using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Video;

/// <summary>
/// Real Runway Gen-3 client. Image-to-video only — the public Runway API
/// (developer.runwayml.com, v2024-11-06) doesn't expose pure text-to-video,
/// so callers MUST provide a reference image. The provider rejects requests
/// without one with a clear error rather than failing silently at the API.
///
/// API contract:
///   POST   /v1/image_to_video   { promptImage, promptText?, model, duration, ratio, seed?, watermark }
///     → { id }
///   GET    /v1/tasks/{id}
///     → { status: "PENDING|RUNNING|SUCCEEDED|FAILED", output: ["url", ...], failure? }
///
/// Auth + versioning:
///   Authorization: Bearer key_…
///   X-Runway-Version: 2024-11-06   ← required, otherwise 400
/// </summary>
public sealed class RunwayVideoProvider : IVideoProvider
{
    public string Id => "runway";
    public string DisplayName => "Runway · Gen-3";
    public string ApiKeyHint => "key_… · dev.runwayml.com/account/api-tokens";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    private const string BaseUrl = "https://api.dev.runwayml.com/v1";
    private const string ApiVersion = "2024-11-06";

    /// <summary>Default model slug used when <see cref="VideoRequest.Model"/> is empty.</summary>
    public const string DefaultModel = "gen3a_turbo";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste a key_… token in Settings.");
        try
        {
            // Runway doesn't expose a /account ping; a HEAD against the
            // tasks list with a known-bad id is the cheapest auth check.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/tasks/00000000-0000-0000-0000-000000000000");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Add("X-Runway-Version", ApiVersion);
            using var resp = await _http.SendAsync(req, ct);
            // 401 means auth failed; 404 means auth passed but the id is unknown — that's what we want.
            if ((int)resp.StatusCode == 401)
                return new ProviderHealth(false, "auth failed", "Token rejected — paste a fresh key_… from dev.runwayml.com.");
            return new ProviderHealth(true, "ok", "credentials accepted");
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, "probe failed", ex.Message);
        }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Runway API key missing — paste a key_… token in Settings.");
        if (string.IsNullOrEmpty(req.ReferenceImagePath))
            throw new InvalidOperationException(
                "Runway Gen-3 requires a reference image (image-to-video only). " +
                "Drop one onto the Composer's REFERENCE IMAGE panel before submitting.");
        if (!File.Exists(req.ReferenceImagePath))
            throw new InvalidOperationException(
                $"Reference image not found on disk: {req.ReferenceImagePath}");

        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;
        // Runway only supports 5 or 10 second outputs at the moment.
        var duration = req.DurationSec < 7.5 ? 5 : 10;

        var imageDataUrl = "data:" + GuessMime(req.ReferenceImagePath) + ";base64," +
                           Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath, ct));

        var payload = new JsonObject
        {
            ["promptImage"] = imageDataUrl,
            ["model"] = model,
            ["duration"] = duration,
            ["ratio"] = MapAspect(req.Aspect),
            ["watermark"] = false,
        };
        if (!string.IsNullOrEmpty(req.Prompt))
            payload["promptText"] = req.Prompt;
        if (req.Seed is int seed)
            payload["seed"] = seed;

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/image_to_video")
        {
            Content = JsonContent.Create((JsonNode)payload),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
        msg.Headers.Add("X-Runway-Version", ApiVersion);

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Runway submit failed (HTTP {(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var node = JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("Runway returned non-JSON response.");
        var id = node["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Runway accepted the request but returned no task id.");

        return new VideoJob
        {
            Id = id,
            Status = "queued",
            Progress = 0,
        };
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "RunwayVideoProvider.PollAsync needs the api key out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"Cancel via DELETE {BaseUrl}/tasks/{{id}} from the orchestrator — the IVideoProvider " +
            "interface doesn't carry the api key into Cancel.");

    /// <summary>
    /// End-to-end submit + poll + return the first output URL. Mirrors
    /// ReplicateVideoProvider.SubmitAndWaitAsync — used by GenerationService
    /// when routing through Runway so the api key stays in scope.
    /// </summary>
    public async Task<string> SubmitAndWaitAsync(
        VideoRequest req,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var job = await SubmitAsync(req, ct);
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/tasks/{job.Id}");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
            msg.Headers.Add("X-Runway-Version", ApiVersion);

            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var parsed = JsonNode.Parse(body) as JsonObject;
                var status = parsed?["status"]?.GetValue<string>() ?? "PENDING";
                if (status == "SUCCEEDED")
                {
                    var outputUrl = parsed?["output"] switch
                    {
                        JsonArray a when a.Count > 0 => a[0]?.GetValue<string>(),
                        JsonValue v when v.TryGetValue<string>(out var s) => s,
                        _ => null,
                    };
                    progress?.Report(100);
                    return outputUrl
                        ?? throw new InvalidOperationException("Runway succeeded but no output URL.");
                }
                if (status == "FAILED")
                {
                    var failure = parsed?["failure"]?.GetValue<string>() ?? "no detail";
                    throw new InvalidOperationException($"Runway task failed: {failure}");
                }
                if (status == "RUNNING") progress?.Report(50);
                else progress?.Report(10);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { throw; }
        }
        throw new TimeoutException("Runway task did not complete within 15 minutes.");
    }

    /// <summary>Runway's <c>ratio</c> field accepts a fixed set of values —
    /// map our four AspectRatios to the closest supported one. Cinema (21:9)
    /// isn't supported, so it falls back to 16:9.</summary>
    private static string MapAspect(string a) => a switch
    {
        "9:16" => "9:16",
        "1:1"  => "16:9",   // Runway has no square — pick the closest
        "21:9" => "16:9",
        _      => "16:9",
    };

    private static string? ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body) as JsonObject;
            return node?["error"]?.GetValue<string>()
                ?? node?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png"  => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif"  => "image/gif",
        _       => "image/png",
    };
}
