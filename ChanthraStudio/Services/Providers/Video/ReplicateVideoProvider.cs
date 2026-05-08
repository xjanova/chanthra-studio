using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Video;

/// <summary>
/// Real Replicate API client. Submits a prediction, polls until done,
/// returns the output URL(s). Default model is the user-supplied
/// <c>VideoRequest.Model</c> as a slug like
/// <c>"black-forest-labs/flux-schnell"</c>; the provider resolves the
/// model's latest version internally.
///
/// Replicate API contract:
///   POST   /v1/models/{owner}/{name}/predictions   { input: { ... } }
///     → { id, status: "starting", urls: { get, cancel }, ... }
///   GET    /v1/predictions/{id}
///     → { status: "succeeded" | "failed" | "processing" | "starting" | "canceled",
///         output: string | string[] | null,
///         error: string? }
/// Auth: <c>Authorization: Bearer r8_...</c>
/// </summary>
public sealed class ReplicateVideoProvider : IVideoProvider
{
    public string Id => "replicate";
    public string DisplayName => "Replicate (Flux · SDXL · Kling · Hunyuan)";
    public string ApiKeyHint => "r8_… · replicate.com/account/api-tokens";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;

    private const string BaseUrl = "https://api.replicate.com/v1";

    /// <summary>Default model slug used when <c>VideoRequest.Model</c> is empty.</summary>
    public const string DefaultModel = "black-forest-labs/flux-schnell";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an r8_… token in Settings.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/account");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return new ProviderHealth(false, $"HTTP {(int)resp.StatusCode}", body);
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json) as JsonObject;
            var username = node?["username"]?.GetValue<string>() ?? "(account)";
            return new ProviderHealth(true, "ok", $"signed in as {username}");
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, "error", ex.Message);
        }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Replicate API key missing — paste it in Settings.");

        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;
        var (owner, name) = ParseSlug(model);

        // Build the input dict per the model's documented schema. We pass a
        // small canonical set; users who want non-default params can switch
        // models or fork the workflow on Replicate's side.
        var input = new JsonObject
        {
            ["prompt"] = req.Prompt,
            ["aspect_ratio"] = MapAspect(req.Aspect),
            ["output_format"] = "mp4",
        };
        if (!string.IsNullOrEmpty(req.NegativePrompt))
            input["negative_prompt"] = req.NegativePrompt;
        if (req.Seed is int seed)
            input["seed"] = seed;
        if (!string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath))
            input["image"] = "data:" + GuessMime(req.ReferenceImagePath) + ";base64," +
                              Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath, ct));

        var payload = new JsonObject { ["input"] = input };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/models/{owner}/{name}/predictions")
        {
            Content = JsonContent.Create((JsonNode)payload),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
        msg.Headers.Add("Prefer", "wait=2");  // server-side fast-poll for short jobs

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Replicate submit failed (HTTP {(int)resp.StatusCode}): {body}");

        return ParseJob(body);
    }

    public async Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
    {
        // Caller passes us the prediction id and is responsible for keeping
        // the API key in some out-of-band place. We expect it through Meta
        // since this method's signature is provider-agnostic — set
        // <c>job.Meta["api_key"]</c> after SubmitAsync.
        // Safer pattern in this codebase: caller orchestrates polling itself.
        // For now this method is a single-shot status check.
        throw new NotSupportedException(
            "ReplicateVideoProvider.PollAsync requires the api key out-of-band; " +
            "use the Submit→Wait variant via the GenerationService orchestrator instead.");
    }

    public async Task CancelAsync(string jobId, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "ReplicateVideoProvider.CancelAsync needs an api key — call Cancel directly via " +
            $"DELETE {BaseUrl}/predictions/{{id}} from your orchestrator.");
    }

    /// <summary>
    /// End-to-end submit + poll + return the first output URL.
    /// Convenience wrapper that owns the api key for the lifetime of the
    /// operation — easier for callers than the IVideoProvider 3-method dance
    /// because Replicate's polling is stateless.
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
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/predictions/{job.Id}");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var parsed = ParseJob(body);
                if (parsed.Progress > 0) progress?.Report(parsed.Progress);
                if (parsed.Status == "done")
                {
                    return parsed.VideoUrl
                        ?? throw new InvalidOperationException("Replicate succeeded but output was empty.");
                }
                if (parsed.Status == "error")
                    throw new InvalidOperationException($"Replicate prediction failed: {parsed.Error}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { throw; }
        }
        throw new TimeoutException("Replicate prediction did not complete within 15 minutes.");
    }

    private static VideoJob ParseJob(string body)
    {
        var node = JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("Replicate returned non-JSON response.");
        var id = node["id"]?.GetValue<string>() ?? "";
        var rawStatus = node["status"]?.GetValue<string>() ?? "starting";
        var status = rawStatus switch
        {
            "starting" or "processing" => "running",
            "succeeded" => "done",
            "failed" or "canceled" => "error",
            _ => "running",
        };
        string? error = node["error"]?.GetValue<string>();

        // output is either string, array of strings, or null. We pick the
        // first URL — chanthra's GenerationService downloads the primary.
        string? videoUrl = node["output"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray a when a.Count > 0 => a[0]?.GetValue<string>(),
            _ => null,
        };

        // Replicate doesn't expose a step-progress field; some models do via
        // logs. We approximate as 0/50/100 based on status alone.
        double progress = status switch
        {
            "done" => 100,
            "running" => 50,
            _ => 0,
        };

        return new VideoJob
        {
            Id = id,
            Status = status,
            Progress = progress,
            VideoUrl = videoUrl,
            Error = error,
        };
    }

    private static (string Owner, string Name) ParseSlug(string slug)
    {
        var trimmed = (slug ?? "").Trim();
        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash == trimmed.Length - 1)
            throw new ArgumentException(
                $"Replicate model must be owner/name — got \"{slug}\". " +
                "Examples: black-forest-labs/flux-schnell, stability-ai/sdxl, minimax/video-01");
        return (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    private static string MapAspect(string a) => a switch
    {
        "9:16" => "9:16",
        "1:1" => "1:1",
        "21:9" => "21:9",
        _ => "16:9",
    };

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
}
