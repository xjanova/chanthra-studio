using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Video;

/// <summary>
/// Google <b>Veo 3.1</b> via the Gemini API (generativelanguage.googleapis.com)
/// — the "omni" video model: text-to-video AND image-to-video with <b>natively
/// generated audio + spoken dialogue + lip-sync</b>, which is exactly what the
/// brand's talking-head fortune-teller clips need.
///
/// Long-running task pattern (ai.google.dev/gemini-api/docs/video · 2026-06):
///   POST {base}/models/{model}:predictLongRunning
///        { instances:[{ prompt, image?:{inlineData:{mimeType,data}} }],
///          parameters:{ aspectRatio, resolution, durationSeconds,
///                       personGeneration, negativePrompt?, numberOfVideos } }
///     → { name:"models/…/operations/…" }
///   GET  {base}/{operation.name}
///     → { done:bool, response:{ generateVideoResponse:{ generatedSamples:[
///         { video:{ uri } } ] } }, error?:{ message } }
///   GET  {video.uri}            (needs the api key — see note below)
///
/// Auth: the same Gemini key as the LLM provider (AIzaSy…), passed via the
/// <c>x-goog-api-key</c> header on submit + poll. The returned <c>video.uri</c>
/// also requires the key; we append it as a <c>?key=</c> query param so the
/// generic downloader in <see cref="GenerationService"/> can fetch it without
/// a custom header. The key is never logged or persisted with the file.
/// </summary>
public sealed class GeminiVeoVideoProvider : IVideoProvider
{
    public string Id => "veo";
    public string DisplayName => "Google Veo 3.1 (omni)";
    public string ApiKeyHint => "AIzaSy… (Gemini key) · aistudio.google.com/apikey";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    private const string Base = "https://generativelanguage.googleapis.com/v1beta/";

    /// <summary>Latest "omni" preview. Stable GA fallback is
    /// <c>veo-3.0-generate-001</c>; quality preview is
    /// <c>veo-3.1-generate-preview</c> — both selectable from the Settings chips.</summary>
    public const string DefaultModel = "veo-3.1-fast-generate-preview";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste your Gemini API key (AIzaSy…) in Settings.");
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{Base}models?key={Uri.EscapeDataString(apiKey)}");
            using var resp = await _http.SendAsync(msg, ct);
            if ((int)resp.StatusCode is 401 or 403)
                return new ProviderHealth(false, "auth failed", "Key rejected — check your Gemini API key.");
            return new ProviderHealth(resp.IsSuccessStatusCode,
                resp.IsSuccessStatusCode ? "ok" : $"HTTP {(int)resp.StatusCode}",
                resp.IsSuccessStatusCode ? "credentials accepted" : null);
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Veo (Gemini) API key missing — set it in Settings.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model;

        var hasImage = !string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath);
        var instance = new JsonObject { ["prompt"] = req.Prompt };
        if (hasImage)
        {
            instance["image"] = new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = GuessMime(req.ReferenceImagePath!),
                    ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath!, ct)),
                },
            };
        }

        var dur = ClampDuration(req.DurationSec);
        // 1080p is only valid at the full 8s length; otherwise fall back to 720p.
        var resolution = req.Hd4k && dur == 8 ? "1080p" : "720p";

        var parameters = new JsonObject
        {
            ["aspectRatio"] = MapAspect(req.Aspect),
            ["resolution"] = resolution,
            // String, not number: protojson accepts a quoted number for either a
            // string- or int-typed field, so "8" is safe whatever the field is.
            ["durationSeconds"] = dur.ToString(),
            // Per the Gemini API docs these are mode-specific enums: text-to-video
            // accepts only "allow_all", image-to-video only "allow_adult". Sending
            // the wrong one for the mode gets the request rejected.
            ["personGeneration"] = hasImage ? "allow_adult" : "allow_all",
        };
        if (!string.IsNullOrWhiteSpace(req.NegativePrompt))
            parameters["negativePrompt"] = req.NegativePrompt;

        var payload = new JsonObject
        {
            ["instances"] = new JsonArray { instance },
            ["parameters"] = parameters,
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{Base}models/{Uri.EscapeDataString(model)}:predictLongRunning")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-goog-api-key", req.ApiKey);

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Veo submit failed (HTTP {(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var name = (JsonNode.Parse(body) as JsonObject)?["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Veo accepted the request but returned no operation name.");
        return new VideoJob { Id = name, Status = "queued", Progress = 0 };
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException("GeminiVeoVideoProvider.PollAsync needs the api key out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException("Veo exposes no cancel endpoint — the orchestrator stops polling via its CancellationToken.");

    /// <summary>Submit → poll the operation → return the video URI with the api
    /// key appended so the generic cloud orchestrator can download it. Mirrors
    /// the other cloud providers' SubmitAndWaitAsync shape.</summary>
    public async Task<string> SubmitAndWaitAsync(VideoRequest req, IProgress<double>? progress, CancellationToken ct = default)
    {
        var job = await SubmitAsync(req, ct);
        progress?.Report(12);

        // Veo runs minutes, not seconds (11s floor, up to ~6 min at peak).
        var deadline = DateTime.UtcNow.AddMinutes(8);
        var tick = 0;
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(8), ct); }
            catch (OperationCanceledException) { throw; }

            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{Base}{job.Id}");
            msg.Headers.Add("x-goog-api-key", req.ApiKey);
            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) continue;   // transient — keep polling

            var node = JsonNode.Parse(body) as JsonObject;
            if (node?["error"] is JsonObject err)
                throw new InvalidOperationException(
                    $"Veo task failed: {err["message"]?.GetValue<string>() ?? "no detail"}");

            if (node?["done"]?.GetValue<bool>() == true)
            {
                var uri = ExtractVideoUri(node)
                    ?? throw new InvalidOperationException("Veo finished but returned no video uri.");
                progress?.Report(100);
                var sep = uri.Contains('?') ? '&' : '?';
                return $"{uri}{sep}key={Uri.EscapeDataString(req.ApiKey)}";
            }

            progress?.Report(Math.Min(92, 15 + (++tick) * 7));
        }
        throw new TimeoutException("Veo task did not complete within 8 minutes.");
    }

    private static string? ExtractVideoUri(JsonObject op)
    {
        var response = op["response"] as JsonObject;
        // Primary documented path.
        var uri = (response?["generateVideoResponse"]?["generatedSamples"] as JsonArray)?
            .FirstOrDefault()?["video"]?["uri"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri)) return uri;
        // Tolerated variants seen across SDK versions.
        uri = (response?["generateVideoResponse"]?["samples"] as JsonArray)?
            .FirstOrDefault()?["video"]?["uri"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri)) return uri;
        uri = (response?["generatedVideos"] as JsonArray)?
            .FirstOrDefault()?["video"]?["uri"]?.GetValue<string>();
        return uri;
    }

    private static string MapAspect(string a) => a switch
    {
        "9:16" => "9:16",      // Veo supports only 16:9 and 9:16
        _ => "16:9",
    };

    private static int ClampDuration(double sec)
    {
        var s = (int)Math.Round(sec);
        // Veo 3.x supports 4, 6 or 8 seconds.
        if (s <= 4) return 4;
        if (s <= 6) return 6;
        return 8;
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };

    private static string? ExtractError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?["message"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
