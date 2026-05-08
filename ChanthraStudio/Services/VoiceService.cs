using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services.Providers;

namespace ChanthraStudio.Services;

/// <summary>
/// Front-of-house for text-to-speech. Resolves the provider, looks up its
/// API key from settings, generates a deterministic filename under
/// <c>media/voice/</c>, and writes the audio there. The Voice view's "takes
/// list" is just a disk scan of that folder — no DB rows needed at this
/// scope. (Phase 5.5 / Sound Atelier full will add a voice_takes table for
/// SSML scripts, multi-line takes, FX chains, etc.)
/// </summary>
public sealed class VoiceService
{
    private readonly StudioContext _ctx;

    public VoiceService(StudioContext ctx) { _ctx = ctx; }

    public string OutputDir
    {
        get
        {
            var p = Path.Combine(AppPaths.MediaFolder, "voice");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public async Task<VoiceTake> GenerateAsync(string providerId, string voiceId, string text,
        double speed = 1.0, double stability = 0.5, CancellationToken ct = default)
    {
        var provider = _ctx.Providers.Voice.FirstOrDefault(p => p.Id == providerId)
            ?? throw new InvalidOperationException($"Unknown voice provider: {providerId}");

        var apiKey = _ctx.Settings[providerId];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No API key for {provider.DisplayName} — set it in Settings.");

        var voiceLabel = provider.AvailableVoices.FirstOrDefault(v => v.Id == voiceId)?.DisplayName ?? voiceId;
        var fileName = $"voice_{DateTime.Now:yyyyMMdd_HHmmss}_{providerId}_{Sanitise(voiceLabel)}.mp3";
        var destPath = Path.Combine(OutputDir, fileName);

        var req = new VoiceRequest
        {
            ApiKey = apiKey,
            VoiceId = voiceId,
            Text = text,
            Speed = speed,
            Stability = stability,
            OutputFormat = "mp3",
        };
        var path = await provider.SynthesiseAsync(req, destPath, ct);

        return new VoiceTake
        {
            FilePath = path,
            ProviderId = providerId,
            ProviderDisplayName = provider.DisplayName,
            VoiceId = voiceId,
            VoiceDisplayName = voiceLabel,
            Text = text,
            CreatedAt = DateTime.Now,
        };
    }

    /// <summary>Lists all .mp3 files under <see cref="OutputDir"/>, newest first.</summary>
    public IReadOnlyList<VoiceTake> ListTakes(int limit = 50)
    {
        if (!Directory.Exists(OutputDir)) return Array.Empty<VoiceTake>();
        return Directory.EnumerateFiles(OutputDir, "*.mp3")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Take(limit)
            .Select(fi => new VoiceTake
            {
                FilePath = fi.FullName,
                CreatedAt = fi.LastWriteTime,
                ProviderId = "",
                ProviderDisplayName = InferProvider(fi.Name),
                VoiceId = "",
                VoiceDisplayName = InferVoice(fi.Name),
                Text = "",  // not stored on disk yet
            })
            .ToList();
    }

    public void DeleteTake(VoiceTake take)
    {
        try { if (File.Exists(take.FilePath)) File.Delete(take.FilePath); }
        catch { /* file in use / read-only — caller surfaces toast */ }
    }

    public string MusicOutputDir
    {
        get
        {
            var p = Path.Combine(AppPaths.MediaFolder, "music");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    /// <summary>
    /// Generate music via the chosen <see cref="IMusicProvider"/>, save the
    /// resulting audio under <c>media/music/</c>, and return a <see cref="VoiceTake"/>
    /// for the Voice view to display alongside speech takes.
    /// </summary>
    public async Task<VoiceTake> GenerateMusicAsync(
        string providerId, string modelSlug, string prompt, double durationSec,
        CancellationToken ct = default)
    {
        var provider = _ctx.Providers.Music.FirstOrDefault(p => p.Id == providerId)
            ?? throw new InvalidOperationException($"Unknown music provider: {providerId}");

        // The Replicate music provider shares the api key with the video
        // provider — they're the same Replicate account. Look up "replicate"
        // first, fall back to the provider's own id (so a future Suno
        // provider with its own key still works).
        var apiKey = _ctx.Settings["replicate"];
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = _ctx.Settings[providerId];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"No API key for {provider.DisplayName} — paste your r8_… token in Settings.");

        var slugSafe = string.IsNullOrEmpty(modelSlug) ? "default" : modelSlug.Replace('/', '_');
        var fileName = $"music_{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitise(slugSafe)}.mp3";
        var destPath = Path.Combine(MusicOutputDir, fileName);

        var req = new MusicRequest
        {
            ApiKey = apiKey,
            Model = modelSlug,
            Prompt = prompt,
            DurationSec = durationSec,
        };
        var path = await provider.GenerateAsync(req, destPath, ct);

        return new VoiceTake
        {
            FilePath = path,
            ProviderId = providerId,
            ProviderDisplayName = provider.DisplayName,
            VoiceId = modelSlug,
            VoiceDisplayName = string.IsNullOrEmpty(modelSlug) ? "music" : modelSlug,
            Text = prompt,
            CreatedAt = DateTime.Now,
        };
    }

    /// <summary>
    /// Lists music files (alongside voice takes) so the Voice view can show
    /// a unified history. Music files live under <c>media/music/</c>.
    /// </summary>
    public IReadOnlyList<VoiceTake> ListMusicTakes(int limit = 50)
    {
        if (!Directory.Exists(MusicOutputDir)) return Array.Empty<VoiceTake>();
        var exts = new[] { "*.mp3", "*.wav", "*.flac", "*.m4a" };
        return exts.SelectMany(e => Directory.EnumerateFiles(MusicOutputDir, e))
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Take(limit)
            .Select(fi => new VoiceTake
            {
                FilePath = fi.FullName,
                CreatedAt = fi.LastWriteTime,
                ProviderId = "replicate-music",
                ProviderDisplayName = "music",
                VoiceId = "",
                VoiceDisplayName = InferMusicSlug(fi.Name),
                Text = "",
            })
            .ToList();
    }

    private static string InferMusicSlug(string fileName)
    {
        // Filename pattern: music_YYYYMMDD_HHMMSS_{owner_name}.mp3
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('_', 4);
        return parts.Length >= 4 ? parts[3].Replace('_', '/') : "?";
    }

    private static string Sanitise(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return sb.ToString();
    }

    /// <summary>Filename pattern: <c>voice_YYYYMMDD_HHMMSS_{providerId}_{voice}.mp3</c>.</summary>
    private static string InferProvider(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        return parts.Length >= 4 ? parts[3] : "?";
    }

    private static string InferVoice(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        return parts.Length >= 5 ? string.Join('_', parts[4..]) : "?";
    }
}

public sealed class VoiceTake
{
    public string FilePath { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderDisplayName { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string VoiceDisplayName { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public string FileName => Path.GetFileName(FilePath);
    public string DurationLabel
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return "—";
                var bytes = new FileInfo(FilePath).Length;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
                return $"{bytes / 1024.0 / 1024.0:F1} MB";
            }
            catch { return "—"; }
        }
    }
}
