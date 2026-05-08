using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Voice;

/// <summary>
/// Text-to-speech via OpenAI's audio.speech endpoint
/// (<c>POST /v1/audio/speech</c>). Six built-in voices, single model
/// (<c>tts-1</c>) by default — the "tts-1-hd" variant is twice the price
/// for marginal quality and not exposed yet. Audio comes back as binary
/// MP3 which we stream straight to disk.
/// </summary>
internal sealed class OpenAiTtsProvider : IVoiceProvider
{
    private const string Endpoint = "https://api.openai.com/v1/audio/speech";
    private static readonly HttpClient Http = new();

    public string Id => "openai-tts";
    public string DisplayName => "OpenAI TTS";
    public string ApiKeyHint => "sk-… · platform.openai.com/api-keys";
    public ProviderKind Kind => ProviderKind.Voice;
    public bool RequiresApiKey => true;

    public IReadOnlyList<VoicePreset> AvailableVoices { get; } = new VoicePreset[]
    {
        new() { Id = "alloy",   DisplayName = "Alloy",   Description = "neutral · balanced", LanguageHint = "EN" },
        new() { Id = "echo",    DisplayName = "Echo",    Description = "warm · resonant",     LanguageHint = "EN" },
        new() { Id = "fable",   DisplayName = "Fable",   Description = "soft · narrative",    LanguageHint = "EN" },
        new() { Id = "onyx",    DisplayName = "Onyx",    Description = "deep · grounded",     LanguageHint = "EN" },
        new() { Id = "nova",    DisplayName = "Nova",    Description = "bright · modern",     LanguageHint = "EN" },
        new() { Id = "shimmer", DisplayName = "Shimmer", Description = "luminous · soft",     LanguageHint = "EN" },
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an OpenAI API key in Settings.");

        // /v1/models is cheap + confirms auth + reachability.
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(msg, ct);
            return new ProviderHealth(resp.IsSuccessStatusCode,
                resp.IsSuccessStatusCode ? "ok" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, "probe failed", ex.Message);
        }
    }

    public async Task<string> SynthesiseAsync(VoiceRequest req, string destPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("OpenAI API key missing — set it in Settings.");
        if (string.IsNullOrWhiteSpace(req.Text))
            throw new InvalidOperationException("Empty script — type something to voice.");

        var payload = new JsonObject
        {
            ["model"] = "tts-1",
            ["input"] = req.Text,
            ["voice"] = string.IsNullOrEmpty(req.VoiceId) ? "alloy" : req.VoiceId,
            ["speed"] = Math.Clamp(req.Speed, 0.25, 4.0),
            ["response_format"] = req.OutputFormat == "wav" ? "wav" : "mp3",
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await Http.SendAsync(msg, cts.Token);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new InvalidOperationException($"OpenAI TTS failed ({(int)resp.StatusCode}): {ExtractError(err) ?? err}");
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
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
