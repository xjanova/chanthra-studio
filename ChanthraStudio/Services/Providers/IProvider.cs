using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers;

public enum ProviderKind { Llm, Video, Posting, Voice }

public interface IProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderKind Kind { get; }
    bool RequiresApiKey { get; }
    string ApiKeyHint { get; }

    /// <summary>Quick liveness probe. Should not consume credits.</summary>
    Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default);
}

public sealed record ProviderHealth(bool Ok, string Status, string? Detail = null);

public interface ILlmProvider : IProvider
{
    Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default);
}

public sealed class LlmRequest
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string System { get; set; } = "";
    public string Prompt { get; set; } = "";
    public double Temperature { get; set; } = 0.8;
    public int MaxTokens { get; set; } = 2048;
}

public interface IVideoProvider : IProvider
{
    Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default);
    Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default);
    Task CancelAsync(string jobId, CancellationToken ct = default);
}

public sealed class VideoRequest
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? NegativePrompt { get; set; }
    public string? ReferenceImagePath { get; set; }
    public string Aspect { get; set; } = "16:9";
    public double DurationSec { get; set; } = 8.0;
    public double Motion { get; set; } = 0.7;
    public int? Seed { get; set; }
    public bool Hd4k { get; set; } = true;
    public bool Audio { get; set; } = true;
    public string CamMode { get; set; } = "locked";
}

public sealed class VideoJob
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "queued";  // queued | running | done | error
    public double Progress { get; set; }
    public string? VideoUrl { get; set; }
    public string? ThumbUrl { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object?> Meta { get; } = new();
}

public interface IVoiceProvider : IProvider
{
    /// <summary>The voice presets this provider exposes (e.g. OpenAI's "alloy/echo/fable…").</summary>
    IReadOnlyList<VoicePreset> AvailableVoices { get; }

    /// <summary>
    /// Synthesise speech and write the resulting audio bytes to <paramref name="destPath"/>.
    /// Returns the absolute output path on success; throws on failure.
    /// </summary>
    Task<string> SynthesiseAsync(VoiceRequest req, string destPath, CancellationToken ct = default);
}

public sealed class VoicePreset
{
    public string Id { get; init; } = "";          // e.g. "alloy", "EXAVITQu4vr4xnSDxMaL"
    public string DisplayName { get; init; } = ""; // human-readable label
    public string Description { get; init; } = ""; // tone hint ("warm · bright")
    public string LanguageHint { get; init; } = "EN";
}

public sealed class VoiceRequest
{
    public string ApiKey { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string Text { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    public double Stability { get; set; } = 0.5;     // ElevenLabs only — ignored elsewhere
    public string OutputFormat { get; set; } = "mp3";
}

public interface IPostingProvider : IProvider
{
    Task<PostResult> PostAsync(PostRequest req, CancellationToken ct = default);
}

public sealed class PostRequest
{
    public string ApiKey { get; set; } = "";
    public string TargetId { get; set; } = "";    // page id, group id, channel id, etc.
    public string FilePath { get; set; } = "";
    public string Caption { get; set; } = "";
    public Dictionary<string, string> Extras { get; } = new();
}

public sealed record PostResult(bool Ok, string? PostId, string? Error);
