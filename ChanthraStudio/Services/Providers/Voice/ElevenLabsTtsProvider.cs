using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Voice;

/// <summary>
/// Text-to-speech via ElevenLabs
/// (<c>POST /v1/text-to-speech/{voice_id}</c>). Authenticates via the
/// <c>xi-api-key</c> header. The voice list bundled here is the canonical
/// "Default" voice library; users with custom-cloned voices can paste a
/// different voice_id later (we'll surface that UI in a follow-up).
/// </summary>
internal sealed class ElevenLabsTtsProvider : IVoiceProvider
{
    private const string Base = "https://api.elevenlabs.io/v1/";
    /// <summary>
    /// Used when no model chip is picked. Eleven v3 is the only ElevenLabs
    /// model with Thai (elevenlabs.io/docs/overview/models, checked
    /// 2026-09-23); the old fixed default, Multilingual v2, has 29
    /// languages and Thai is not one of them.
    /// </summary>
    public const string DefaultModel = "eleven_v3";
    private const int V3MaxChars = 5000;
    private static readonly HttpClient Http = new() { BaseAddress = new Uri(Base) };

    public string Id => "elevenlabs";
    public string? DefaultModelId => DefaultModel;
    public string DisplayName => "ElevenLabs";
    public string ApiKeyHint => "xi-… · elevenlabs.io/app/account";
    public ProviderKind Kind => ProviderKind.Voice;
    public bool RequiresApiKey => true;

    // The default-library voice IDs. They're stable per ElevenLabs docs.
    public IReadOnlyList<VoicePreset> AvailableVoices { get; } = new VoicePreset[]
    {
        new() { Id = "EXAVITQu4vr4xnSDxMaL", DisplayName = "Sarah",   Description = "soft · friendly",    LanguageHint = "EN" },
        new() { Id = "ThT5KcBeYPX3keUQqHPh", DisplayName = "Dorothy", Description = "warm · narrative",   LanguageHint = "EN" },
        new() { Id = "pNInz6obpgDQGcFmaJgB", DisplayName = "Adam",    Description = "deep · cinematic",   LanguageHint = "EN" },
        new() { Id = "VR6AewLTigWG4xSOukaG", DisplayName = "Arnold",  Description = "crisp · authoritative", LanguageHint = "EN" },
        new() { Id = "21m00Tcm4TlvDq8ikWAM", DisplayName = "Rachel",  Description = "calm · clear",       LanguageHint = "EN" },
        new() { Id = "TxGEqnHWrfWFTfGW9XjX", DisplayName = "Josh",    Description = "young · bright",     LanguageHint = "EN" },
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an ElevenLabs API key in Settings.");

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, "user");
            msg.Headers.Add("xi-api-key", apiKey);
            using var resp = await Http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
                return new ProviderHealth(false, $"HTTP {(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync(ct);
            var sub = JsonNode.Parse(body)?["subscription"]?["tier"]?.GetValue<string>();
            return new ProviderHealth(true, sub is null ? "ok" : $"tier · {sub}");
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, "probe failed", ex.Message);
        }
    }

    /// <summary>
    /// Every voice the key can use — its own clones and whatever it added
    /// from the Voice Library. The built-in list above is ElevenLabs' Default
    /// set, which only accounts created before March 2026 can use and which
    /// expires on 2026-12-31, so a new account failed on all six.
    /// </summary>
    public async Task<IReadOnlyList<VoicePreset>> ListVoicesAsync(string apiKey, CancellationToken ct = default)
    {
        var voices = new List<VoicePreset>();
        string? pageToken = null;
        for (var page = 0; page < 20; page++)
        {
            var url = "https://api.elevenlabs.io/v2/voices?page_size=100"
                    + (pageToken is null ? "" : "&next_page_token=" + Uri.EscapeDataString(pageToken));
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("xi-api-key", apiKey);
            using var resp = await Http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"ElevenLabs voice list failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");

            var root = JsonNode.Parse(body);
            if (root?["voices"] is JsonArray arr)
            {
                foreach (var v in arr)
                {
                    var id = v?["voice_id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;
                    var labels = v?["labels"] as JsonObject;
                    string? Label(string k) => labels?[k] is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
                    var traits = new[] { Label("gender"), Label("age"), Label("accent"), Label("descriptive") ?? Label("description") }
                        .Where(s => s is not null);
                    voices.Add(new VoicePreset
                    {
                        Id = id!,
                        DisplayName = v?["name"]?.GetValue<string>() ?? id!,
                        Description = string.Join(" · ", traits.DefaultIfEmpty(v?["category"]?.GetValue<string>() ?? "")),
                        LanguageHint = (Label("language") ?? "").ToUpperInvariant(),
                    });
                }
            }
            if (root?["has_more"]?.GetValue<bool>() != true) break;
            pageToken = root?["next_page_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        return voices;
    }

    public async Task<string> SynthesiseAsync(VoiceRequest req, string destPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("ElevenLabs API key missing — set it in Settings.");
        if (string.IsNullOrWhiteSpace(req.Text))
            throw new InvalidOperationException("Empty script — type something to voice.");
        if (string.IsNullOrWhiteSpace(req.VoiceId))
            throw new InvalidOperationException("No voice picked.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model;
        var v3 = model == "eleven_v3";
        if (v3 && req.Text.Length > V3MaxChars)
            throw new InvalidOperationException(
                $"Eleven v3 รับได้ครั้งละไม่เกิน {V3MaxChars:N0} ตัวอักษร (บทนี้ยาว {req.Text.Length:N0}) — แบ่งบทเป็นหลายช่วงแล้วสร้างทีละช่วง");

        var stability = Math.Clamp(req.Stability, 0.0, 1.0);
        // v3 takes only three stability presets (Creative / Natural / Robust)
        // and answers anything in between with a 400.
        if (v3) stability = Math.Round(stability * 2, MidpointRounding.AwayFromZero) / 2;

        var payload = new JsonObject
        {
            ["text"] = req.Text,
            ["model_id"] = model,
            ["voice_settings"] = new JsonObject
            {
                ["stability"] = stability,
                ["similarity_boost"] = 0.75,
            },
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"text-to-speech/{Uri.EscapeDataString(req.VoiceId)}")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("xi-api-key", req.ApiKey);
        msg.Headers.Accept.ParseAdd("audio/mpeg");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await Http.SendAsync(msg, cts.Token);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new InvalidOperationException(
                $"ElevenLabs synth failed ({(int)resp.StatusCode}): {ExtractError(err) ?? err}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using (var fs = File.Create(destPath))
        {
            await resp.Content.CopyToAsync(fs, cts.Token);
        }
        return destPath;
    }

    private static string? ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            // ElevenLabs returns { "detail": { "status": "...", "message": "..." } }
            return node?["detail"]?["message"]?.GetValue<string>()
                ?? node?["detail"]?.GetValue<string>()
                ?? node?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
