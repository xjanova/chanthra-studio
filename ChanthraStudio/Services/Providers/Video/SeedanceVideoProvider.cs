using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Video;

/// <summary>
/// Seedance 2.0 (ByteDance) via the official BytePlus ModelArk video API.
/// Async task pattern — POST a task, poll until status=succeeded, read
/// <c>content.video_url</c>. Text-to-video AND image-to-video (the image is an
/// <c>image_url</c> content item). Auth = ModelArk API key (Bearer).
///
/// Contract (docs.byteplus.com/en/docs/ModelArk · verified 2026-06):
///   POST {host}/api/v3/contents/generations/tasks
///     { model, content:[{type:text,text}, {type:image_url,image_url:{url}}?],
///       ratio, resolution, duration, generate_audio, seed? }
///     → { id }
///   GET  {host}/api/v3/contents/generations/tasks/{id}
///     → { status:"queued|running|succeeded|failed|expired|cancelled",
///         content:{ video_url }, error:{ message } }
/// </summary>
public sealed class SeedanceVideoProvider : IVideoProvider
{
    public string Id => "seedance";
    public string DisplayName => "Seedance 2.0 · ByteDance";
    public string ApiKeyHint => "ModelArk key · console.byteplus.com/ark";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    private const string BaseUrl = "https://ark.ap-southeast.bytepluses.com";
    private const string TasksPath = "/api/v3/contents/generations/tasks";

    /// <summary>Default model when <see cref="VideoRequest.Model"/> is empty —
    /// Seedance 2.0 (2026-01-28 snapshot). Fast variant:
    /// <c>dreamina-seedance-2-0-fast-260128</c>.</summary>
    public const string DefaultModel = "dreamina-seedance-2-0-260128";
    public string? DefaultModelId => DefaultModel;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste your ModelArk API key in Settings.");
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{TasksPath}/probe-0000");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(msg, ct);
            if ((int)resp.StatusCode is 401 or 403)
                return new ProviderHealth(false, "auth failed", "Key rejected — check your ModelArk API key.");
            return new ProviderHealth(true, "ok", "credentials accepted");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Seedance (ModelArk) API key missing — set it in Settings.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model;
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = req.Prompt },
        };
        if (!string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath))
        {
            var dataUrl = "data:" + GuessMime(req.ReferenceImagePath) + ";base64," +
                          Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath, ct));
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } });
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["content"] = content,
            ["ratio"] = MapAspect(req.Aspect),
            ["resolution"] = req.Hd4k ? "1080p" : "720p",
            ["duration"] = (int)Math.Clamp(Math.Round(req.DurationSec), 4, 15),
            ["generate_audio"] = req.Audio,
        };
        if (req.Seed is int seed) payload["seed"] = seed;

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{TasksPath}")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Seedance submit failed (HTTP {(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var id = (JsonNode.Parse(body) as JsonObject)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Seedance accepted the request but returned no task id.");
        return new VideoJob { Id = id, Status = "queued", Progress = 0 };
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException("SeedanceVideoProvider.PollAsync needs the api key out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException("Cancel via DELETE {host}/api/v3/contents/generations/tasks/{id} from the orchestrator.");

    /// <summary>Submit + poll + return the output URL — mirrors the other cloud
    /// providers so GenerationService's generic orchestrator can drive it.</summary>
    public async Task<string> SubmitAndWaitAsync(VideoRequest req, IProgress<double>? progress, CancellationToken ct = default)
    {
        var job = await SubmitAsync(req, ct);
        progress?.Report(10);
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { throw; }

            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{TasksPath}/{job.Id}");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) continue;

            var node = JsonNode.Parse(body) as JsonObject;
            var status = node?["status"]?.GetValue<string>() ?? "running";
            if (status == "succeeded")
            {
                var url = node?["content"]?["video_url"]?.GetValue<string>();
                progress?.Report(100);
                return url ?? throw new InvalidOperationException("Seedance succeeded but returned no video_url.");
            }
            if (status is "failed" or "expired" or "cancelled")
                throw new InvalidOperationException(
                    $"Seedance task {status}: {node?["error"]?["message"]?.GetValue<string>() ?? "no detail"}");
            progress?.Report(status == "running" ? 50 : 25);
        }
        throw new TimeoutException("Seedance task did not complete within 15 minutes.");
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
