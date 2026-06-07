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
/// MiniMax (Hailuo) official video API. Async: create task → poll status →
/// retrieve the file's download URL. Text-to-video AND image-to-video (first
/// frame). Auth = MiniMax API key (Bearer). Host = global api.minimax.io.
///
/// Contract (platform.minimax.io/docs/guides/video-generation · verified 2026-06):
///   POST {host}/v1/video_generation  { model, prompt, duration, resolution, first_frame_image? }
///     → { task_id, base_resp:{ status_code, status_msg } }
///   GET  {host}/v1/query/video_generation?task_id=ID
///     → { status:"Queueing|Preparing|Processing|Success|Fail", file_id?, base_resp }
///   GET  {host}/v1/files/retrieve?file_id=ID
///     → { file:{ download_url } }
/// </summary>
public sealed class MinimaxVideoProvider : IVideoProvider
{
    public string Id => "minimax";
    public string DisplayName => "MiniMax · Hailuo";
    public string ApiKeyHint => "MiniMax key · platform.minimax.io";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    private const string BaseUrl = "https://api.minimax.io";
    public const string DefaultModel = "MiniMax-Hailuo-2.3";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste your MiniMax API key in Settings.");
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/query/video_generation?task_id=probe-0000");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(msg, ct);
            if ((int)resp.StatusCode is 401 or 403)
                return new ProviderHealth(false, "auth failed", "Key rejected.");
            var body = await resp.Content.ReadAsStringAsync(ct);
            var code = (JsonNode.Parse(body) as JsonObject)?["base_resp"]?["status_code"]?.GetValue<int>() ?? 0;
            // 1004 = authentication failed; anything else (incl. task-not-found) means the key worked.
            if (code == 1004)
                return new ProviderHealth(false, "auth failed", "Key rejected (1004).");
            return new ProviderHealth(true, "ok", "credentials accepted");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("MiniMax API key missing — set it in Settings.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model;
        var payload = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = req.Prompt,
            ["duration"] = req.DurationSec < 7.5 ? 6 : 10,   // Hailuo outputs 6 or 10 seconds
            ["resolution"] = req.Hd4k ? "1080P" : "768P",
        };
        if (!string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath))
        {
            payload["first_frame_image"] = "data:" + GuessMime(req.ReferenceImagePath) + ";base64," +
                Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath, ct));
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/video_generation")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax submit failed (HTTP {(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body) as JsonObject;
        var code = node?["base_resp"]?["status_code"]?.GetValue<int>() ?? -1;
        if (code != 0)
            throw new InvalidOperationException(
                $"MiniMax rejected the request: {node?["base_resp"]?["status_msg"]?.GetValue<string>() ?? body}");
        var taskId = node?["task_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("MiniMax accepted the request but returned no task_id.");
        return new VideoJob { Id = taskId, Status = "queued", Progress = 0 };
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException("MinimaxVideoProvider.PollAsync needs the api key out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException("MiniMax exposes no cancel endpoint — the orchestrator stops polling via its CancellationToken.");

    /// <summary>Submit → poll → retrieve the file download URL. Mirrors the
    /// other cloud providers so GenerationService can drive it generically.</summary>
    public async Task<string> SubmitAndWaitAsync(VideoRequest req, IProgress<double>? progress, CancellationToken ct = default)
    {
        var job = await SubmitAsync(req, ct);
        progress?.Report(10);
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { throw; }

            using var qmsg = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/v1/query/video_generation?task_id={Uri.EscapeDataString(job.Id)}");
            qmsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
            using var qresp = await _http.SendAsync(qmsg, ct);
            var qbody = await qresp.Content.ReadAsStringAsync(ct);
            if (!qresp.IsSuccessStatusCode) continue;

            var qnode = JsonNode.Parse(qbody) as JsonObject;
            var status = qnode?["status"]?.GetValue<string>() ?? "Processing";
            if (status == "Success")
            {
                var fileId = qnode?["file_id"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("MiniMax reported Success but no file_id.");
                progress?.Report(90);
                return await RetrieveDownloadUrlAsync(req.ApiKey, fileId, ct);
            }
            if (status == "Fail")
                throw new InvalidOperationException(
                    $"MiniMax task failed: {qnode?["base_resp"]?["status_msg"]?.GetValue<string>() ?? "no detail"}");
            progress?.Report(status == "Processing" ? 50 : 25);
        }
        throw new TimeoutException("MiniMax task did not complete within 15 minutes.");
    }

    private async Task<string> RetrieveDownloadUrlAsync(string apiKey, string fileId, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/v1/files/retrieve?file_id={Uri.EscapeDataString(fileId)}");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax file retrieve failed (HTTP {(int)resp.StatusCode}): {body}");
        var url = (JsonNode.Parse(body) as JsonObject)?["file"]?["download_url"]?.GetValue<string>();
        return url ?? throw new InvalidOperationException("MiniMax file had no download_url.");
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
}
