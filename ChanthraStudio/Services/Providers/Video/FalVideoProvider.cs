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
/// Real fal.ai client. fal hosts hundreds of models under owner/name slugs;
/// the queue API is the canonical async path:
///
///   POST  https://queue.fal.run/{model-slug}  → { request_id }
///   GET   https://queue.fal.run/{model-slug}/requests/{id}/status → { status }
///   GET   https://queue.fal.run/{model-slug}/requests/{id}        → { video: { url } | image: { url } }
///
/// Auth header: <c>Authorization: Key fal_…</c> — note "Key" not "Bearer".
///
/// <para>
/// Model schema varies wildly by slug — text-to-video models accept
/// <c>prompt</c>; image-to-video models also require <c>image_url</c>;
/// some accept <c>seed</c>, <c>aspect_ratio</c>, <c>duration</c>. We pass
/// the conservative superset; fal ignores unknown keys silently (verified
/// against ltx-video, kling-video, hunyuan-video as of 2026-05).
/// </para>
/// </summary>
public sealed class FalVideoProvider : IVideoProvider
{
    public string Id => "fal";
    public string DisplayName => "fal.ai (LTX · Kling · Hunyuan · MiniMax)";
    public string ApiKeyHint => "fal-… · fal.ai/dashboard/keys";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    /// <summary>Default video model slug. ltx-video is fast + cheap and
    /// accepts plain text prompts, making it a sensible "click run" default.</summary>
    public const string DefaultModel = "fal-ai/ltx-video";

    private const string QueueBase = "https://queue.fal.run";
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste a fal_… token in Settings.");
        try
        {
            // fal doesn't expose a /me endpoint — a HEAD against the queue
            // with a known-bad request id is the cheapest auth check. 401
            // means bad key; 404 means auth passed.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{QueueBase}/{DefaultModel}/requests/00000000/status");
            req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 401)
                return new ProviderHealth(false, "auth failed", "Token rejected — paste a fresh fal_… from fal.ai/dashboard.");
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
            throw new InvalidOperationException("fal API key missing — paste your fal_… token in Settings.");

        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;
        // fal expects slugs like "fal-ai/ltx-video", "fal-ai/kling-video", etc.
        // The Composer doubles its workflow picker as the slug source, so a
        // user-typed slug without an owner triggers a clearer error here.
        if (!model.Contains('/'))
            throw new ArgumentException(
                $"fal model must be owner/name — got \"{model}\". " +
                "Examples: fal-ai/ltx-video, fal-ai/kling-video, fal-ai/hunyuan-video.");

        var input = new JsonObject
        {
            ["prompt"] = req.Prompt,
            ["aspect_ratio"] = MapAspect(req.Aspect),
            ["duration"] = (int)Math.Clamp(req.DurationSec, 2, 30),
        };
        if (!string.IsNullOrEmpty(req.NegativePrompt))
            input["negative_prompt"] = req.NegativePrompt;
        if (req.Seed is int seed)
            input["seed"] = seed;
        // Image-to-video models read image_url instead of image. We pass
        // a base64 data URL which fal accepts everywhere (no separate upload).
        if (!string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath))
        {
            var dataUrl = "data:" + GuessMime(req.ReferenceImagePath) + ";base64," +
                          Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath, ct));
            input["image_url"] = dataUrl;
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{QueueBase}/{model}")
        {
            Content = JsonContent.Create((JsonNode)input),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Key", req.ApiKey);

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"fal submit failed (HTTP {(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var node = JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("fal returned non-JSON.");
        var requestId = node["request_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("fal accepted the request but returned no request_id.");

        return new VideoJob
        {
            Id = requestId,
            Status = "queued",
            Progress = 0,
            // Stash the model slug — PollAsync would need it, but our
            // orchestration uses SubmitAndWaitAsync which has it in scope.
            Meta = { ["model"] = model },
        };
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "FalVideoProvider.PollAsync needs both api key and model slug out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "fal cancellation isn't supported through the queue API as of 2026-05 — submissions run to completion.");

    /// <summary>End-to-end submit + status-poll + result-fetch. Same shape
    /// as ReplicateVideoProvider.SubmitAndWaitAsync and RunwayVideoProvider's.</summary>
    public async Task<string> SubmitAndWaitAsync(
        VideoRequest req,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var job = await SubmitAsync(req, ct);
        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;
        var deadline = DateTime.UtcNow.AddMinutes(15);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            using var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{QueueBase}/{model}/requests/{job.Id}/status");
            statusReq.Headers.Authorization = new AuthenticationHeaderValue("Key", req.ApiKey);
            using var statusResp = await _http.SendAsync(statusReq, ct);
            var statusBody = await statusResp.Content.ReadAsStringAsync(ct);
            if (statusResp.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(statusBody) as JsonObject;
                var status = node?["status"]?.GetValue<string>() ?? "IN_QUEUE";
                if (status == "COMPLETED")
                {
                    // Fetch the response payload.
                    using var outReq = new HttpRequestMessage(HttpMethod.Get, $"{QueueBase}/{model}/requests/{job.Id}");
                    outReq.Headers.Authorization = new AuthenticationHeaderValue("Key", req.ApiKey);
                    using var outResp = await _http.SendAsync(outReq, ct);
                    var outBody = await outResp.Content.ReadAsStringAsync(ct);
                    var outNode = JsonNode.Parse(outBody) as JsonObject;
                    // Common shapes:
                    //   { video: { url: "..." } }
                    //   { videos: [{ url: "..." }] }
                    //   { image: { url: "..." } }
                    var url = outNode?["video"]?["url"]?.GetValue<string>()
                           ?? outNode?["videos"]?[0]?["url"]?.GetValue<string>()
                           ?? outNode?["image"]?["url"]?.GetValue<string>()
                           ?? outNode?["images"]?[0]?["url"]?.GetValue<string>()
                           ?? outNode?["output"]?["url"]?.GetValue<string>();
                    progress?.Report(100);
                    if (string.IsNullOrEmpty(url))
                        throw new InvalidOperationException("fal completed but the response had no output URL.");
                    return url;
                }
                if (status == "FAILED" || status == "ERROR")
                {
                    var err = node?["error"]?.GetValue<string>() ?? "no detail";
                    throw new InvalidOperationException($"fal task failed: {err}");
                }
                if (status == "IN_PROGRESS") progress?.Report(50);
                else progress?.Report(10);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { throw; }
        }
        throw new TimeoutException("fal task did not complete within 15 minutes.");
    }

    private static string MapAspect(string a) => a switch
    {
        "9:16" => "9:16",
        "1:1"  => "1:1",
        "21:9" => "21:9",
        _      => "16:9",
    };

    private static string? ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body) as JsonObject;
            return node?["detail"]?.GetValue<string>()
                ?? node?["error"]?.GetValue<string>()
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
