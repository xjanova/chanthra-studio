using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Video;

/// <summary>
/// Kling AI (Kuaishou) direct video API — text-to-video AND image-to-video.
///
/// Auth is a short-lived HS256 JWT signed from an Access Key + Secret Key pair
/// (console at klingai.com → API). The studio stores ONE key string per
/// provider, so paste the pair as <c>AccessKey:SecretKey</c> — we split on the
/// first ':' and mint a fresh JWT per request (valid 30 min).
///
/// Contract:
///   POST /v1/videos/text2video   { model_name, prompt, negative_prompt?, cfg_scale, mode, aspect_ratio, duration }
///   POST /v1/videos/image2video  { model_name, image(base64), prompt?, cfg_scale, mode, duration }
///     → { code:0, data:{ task_id, task_status:"submitted" } }
///   GET  /v1/videos/{text2video|image2video}/{task_id}
///     → { code:0, data:{ task_status:"submitted|processing|succeed|failed",
///                          task_status_msg, task_result:{ videos:[{ url }] } } }
/// </summary>
public sealed class KlingVideoProvider : IVideoProvider
{
    public string Id => "kling";
    public string DisplayName => "Kling AI · Kuaishou";
    public string ApiKeyHint => "AccessKey:SecretKey · klingai.com → API";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    private const string BaseUrl = "https://api-singapore.klingai.com";

    /// <summary>Default model when <see cref="VideoRequest.Model"/> is empty.</summary>
    public const string DefaultModel = "kling-v1-6";
    public string? DefaultModelId => DefaultModel;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste AccessKey:SecretKey in Settings.");
        if (!TrySplit(apiKey, out var ak, out var sk))
            return new ProviderHealth(false, "bad format", "Expected AccessKey:SecretKey (two parts split by ':').");
        try
        {
            // No dedicated ping endpoint — query a bogus task id. Valid auth
            // returns 200 with a non-zero code (task not found); bad auth → 401/403.
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/videos/text2video/probe-0000");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MakeJwt(ak, sk));
            using var resp = await _http.SendAsync(msg, ct);
            if ((int)resp.StatusCode is 401 or 403)
                return new ProviderHealth(false, "auth failed", "Keys rejected — check AccessKey:SecretKey.");
            return new ProviderHealth(true, "ok", "credentials accepted");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (!TrySplit(req.ApiKey, out var ak, out var sk))
            throw new InvalidOperationException(
                "Kling key must be AccessKey:SecretKey — paste both (split by ':') in Settings.");

        var model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model;
        var mode = req.Hd4k ? "pro" : "std";
        // Kling only outputs 5 or 10 second clips.
        var duration = req.DurationSec < 7.5 ? "5" : "10";
        var hasImage = !string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath);

        JsonObject body;
        string path;
        if (hasImage)
        {
            path = "/v1/videos/image2video";
            body = new JsonObject
            {
                ["model_name"] = model,
                ["image"] = Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath!, ct)),
                ["cfg_scale"] = 0.5,
                ["mode"] = mode,
                ["duration"] = duration,
            };
            if (!string.IsNullOrEmpty(req.Prompt)) body["prompt"] = req.Prompt;
        }
        else
        {
            path = "/v1/videos/text2video";
            body = new JsonObject
            {
                ["model_name"] = model,
                ["prompt"] = req.Prompt,
                ["cfg_scale"] = 0.5,
                ["mode"] = mode,
                ["aspect_ratio"] = MapAspect(req.Aspect),
                ["duration"] = duration,
            };
            if (!string.IsNullOrEmpty(req.NegativePrompt)) body["negative_prompt"] = req.NegativePrompt;
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MakeJwt(ak, sk));

        using var resp = await _http.SendAsync(msg, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kling submit failed (HTTP {(int)resp.StatusCode}): {respBody}");

        var node = JsonNode.Parse(respBody) as JsonObject;
        var code = node?["code"]?.GetValue<int>() ?? -1;
        if (code != 0)
            throw new InvalidOperationException(
                $"Kling rejected the request: {node?["message"]?.GetValue<string>() ?? respBody}");
        var taskId = node?["data"]?["task_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Kling accepted the request but returned no task id.");

        var job = new VideoJob { Id = taskId, Status = "queued", Progress = 0 };
        job.Meta["endpoint"] = path;  // remember which query endpoint to poll
        return job;
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "KlingVideoProvider.PollAsync needs the api key out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Kling exposes no cancel endpoint — the orchestrator stops polling via its CancellationToken.");

    /// <summary>
    /// End-to-end submit + poll + return the first output URL. Mirrors
    /// ReplicateVideoProvider / RunwayVideoProvider — used by GenerationService
    /// so the keys stay in scope for the polling loop.
    /// </summary>
    public async Task<string> SubmitAndWaitAsync(VideoRequest req, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!TrySplit(req.ApiKey, out var ak, out var sk))
            throw new InvalidOperationException("Kling key must be AccessKey:SecretKey.");

        var job = await SubmitAsync(req, ct);
        var endpoint = job.Meta.TryGetValue("endpoint", out var ep) && ep is string s
            ? s : "/v1/videos/text2video";
        progress?.Report(10);

        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { throw; }

            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{endpoint}/{job.Id}");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MakeJwt(ak, sk));
            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) continue;  // transient — keep polling

            var data = (JsonNode.Parse(body) as JsonObject)?["data"] as JsonObject;
            var status = data?["task_status"]?.GetValue<string>() ?? "processing";
            if (status == "succeed")
            {
                var url = (data?["task_result"]?["videos"] as JsonArray)?[0]?["url"]?.GetValue<string>();
                progress?.Report(100);
                return url ?? throw new InvalidOperationException("Kling succeeded but returned no video url.");
            }
            if (status == "failed")
                throw new InvalidOperationException(
                    $"Kling task failed: {data?["task_status_msg"]?.GetValue<string>() ?? "no detail"}");
            progress?.Report(status == "processing" ? 50 : 25);
        }
        throw new TimeoutException("Kling task did not complete within 15 minutes.");
    }

    // ---- helpers ----------------------------------------------------------

    private static bool TrySplit(string combined, out string accessKey, out string secretKey)
    {
        accessKey = ""; secretKey = "";
        if (string.IsNullOrWhiteSpace(combined)) return false;
        var idx = combined.IndexOf(':');
        if (idx <= 0 || idx >= combined.Length - 1) return false;
        accessKey = combined[..idx].Trim();
        secretKey = combined[(idx + 1)..].Trim();
        return accessKey.Length > 0 && secretKey.Length > 0;
    }

    /// <summary>Mint a 30-minute HS256 JWT — header.payload.signature, each
    /// segment base64url-encoded — exactly what Kling's gateway expects.</summary>
    private static string MakeJwt(string accessKey, string secretKey)
    {
        var header = B64Url(Encoding.UTF8.GetBytes(
            new JsonObject { ["alg"] = "HS256", ["typ"] = "JWT" }.ToJsonString()));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = B64Url(Encoding.UTF8.GetBytes(
            new JsonObject { ["iss"] = accessKey, ["exp"] = now + 1800, ["nbf"] = now - 5 }.ToJsonString()));
        var signingInput = header + "." + payload;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var sig = B64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + sig;
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MapAspect(string a) => a switch
    {
        "9:16" => "9:16",
        "1:1" => "1:1",
        _ => "16:9",   // Kling has no 21:9 — closest is 16:9
    };
}
