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
/// Pika Labs developer API client. Pika's public API surface has evolved
/// quickly — this client targets the generation-engine endpoints documented
/// at <c>pikapikapika.io/docs</c> (the dev-portal name for the Pika 2.x
/// platform API as of 2026-05).
///
///   POST   https://api.pikapikapika.io/v1/generate
///     body: { "promptText": "...", "options": { "aspectRatio": "16:9", "duration": 5 } }
///     → { "video": { "id": "…" }  }   (or { "id": "…" } depending on tier)
///   GET    https://api.pikapikapika.io/v1/jobs/{id}
///     → { "status": "queued|pending|finished|failed", "videos": [{ "resultUrl": "..." }] }
///
/// Auth: <c>Authorization: Bearer pk_…</c>
///
/// <para>
/// <b>⚠ SPECULATIVE ENDPOINT</b> — Pika hasn't published an OpenAPI spec
/// publicly as of writing. The path/shape above is reconstructed from
/// scattered dev-tier documentation and may not match what's live for
/// every account tier. Untested against a real developer key.
/// </para>
///
/// <para>
/// If submits return 404 / wrong-shape errors, the actual endpoints
/// likely live under a different subdomain (e.g. <c>api.pika.art</c>,
/// <c>generate.pika.art</c>) or a different version (<c>/v2/</c>).
/// To override without a code change: subclass and override
/// <c>BaseUrl</c>, OR ask in the GitHub Issues thread for a pinned
/// endpoint constant. Replicate's <c>flux-pika-1.5</c> Replicate-hosted
/// model is a working fallback while Pika's dev tier stabilises.
/// </para>
///
/// <para>
/// Pika's API surface isn't fully open to all accounts — if the user's key
/// belongs to the consumer tier, this will return 403. The provider
/// surfaces the upstream error verbatim so users can tell the difference
/// between "wrong endpoint" and "wrong key tier".
/// </para>
/// </summary>
public sealed class PikaVideoProvider : IVideoProvider
{
    public string Id => "pika";
    public string DisplayName => "Pika Labs · 2.x";
    public string ApiKeyHint => "pk_… · pika.art (developer tier required)";
    public ProviderKind Kind => ProviderKind.Video;
    public bool RequiresApiKey => true;

    // Hidden (2026-06 audit): Pika's OFFICIAL API is now served through fal.ai
    // (endpoints like pika/v2.2/text-to-video, FAL_KEY auth) — the standalone
    // api.pikapikapika.io endpoint below was always speculative/untested and
    // very likely 404s. Rather than surface a route that errors the moment a
    // user pastes a key, we hide it: run Pika via the Fal route with a
    // "fal-ai/pika/v2.2/text-to-video" model slug instead. The HTTP client is
    // retained in case Pika ships a real first-party REST API later.
    public bool IsImplemented => false;

    private const string BaseUrl = "https://api.pikapikapika.io/v1";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste a pk_… token in Settings.");
        try
        {
            // Pika doesn't expose a /me; probe against a known-bad job id.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/jobs/00000000-0000-0000-0000-000000000000");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 401)
                return new ProviderHealth(false, "auth failed", "Token rejected — paste a fresh pk_… developer key.");
            if ((int)resp.StatusCode == 403)
                return new ProviderHealth(false, "developer tier required",
                    "Your Pika account lacks developer-API access. Upgrade at pika.art.");
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
            throw new InvalidOperationException("Pika API key missing — paste your pk_… token in Settings.");

        var options = new JsonObject
        {
            ["aspectRatio"] = MapAspect(req.Aspect),
            ["duration"] = (int)Math.Clamp(req.DurationSec, 3, 10),
        };
        if (req.Seed is int seed) options["seed"] = seed;
        if (!string.IsNullOrEmpty(req.NegativePrompt)) options["negativePrompt"] = req.NegativePrompt;

        var payload = new JsonObject
        {
            ["promptText"] = req.Prompt,
            ["options"] = options,
        };
        // Image-to-video — Pika takes a base64 data URL in `image`.
        if (!string.IsNullOrEmpty(req.ReferenceImagePath) && File.Exists(req.ReferenceImagePath))
        {
            payload["image"] = "data:" + GuessMime(req.ReferenceImagePath) + ";base64," +
                               Convert.ToBase64String(await File.ReadAllBytesAsync(req.ReferenceImagePath, ct));
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/generate")
        {
            Content = JsonContent.Create((JsonNode)payload),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Pika submit failed (HTTP {(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var node = JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("Pika returned non-JSON.");
        // Newer Pika tiers wrap the id under "video"; older return the bare "id".
        var id = node["video"]?["id"]?.GetValue<string>()
              ?? node["id"]?.GetValue<string>()
              ?? throw new InvalidOperationException("Pika accepted the request but returned no job id.");

        return new VideoJob
        {
            Id = id,
            Status = "queued",
            Progress = 0,
        };
    }

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "PikaVideoProvider.PollAsync needs the api key out-of-band; use SubmitAndWaitAsync.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Pika doesn't expose a cancel endpoint as of 2026-05 — jobs run to completion or fail.");

    public async Task<string> SubmitAndWaitAsync(
        VideoRequest req,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var job = await SubmitAsync(req, ct);
        var deadline = DateTime.UtcNow.AddMinutes(15);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/jobs/{job.Id}");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
            using var resp = await _http.SendAsync(poll, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(body) as JsonObject;
                var status = node?["status"]?.GetValue<string>()?.ToLowerInvariant() ?? "queued";
                if (status == "finished")
                {
                    var url = node?["videos"]?[0]?["resultUrl"]?.GetValue<string>()
                           ?? node?["result"]?["url"]?.GetValue<string>()
                           ?? node?["video"]?["resultUrl"]?.GetValue<string>();
                    progress?.Report(100);
                    if (string.IsNullOrEmpty(url))
                        throw new InvalidOperationException("Pika finished but no resultUrl in response.");
                    return url;
                }
                if (status == "failed" || status == "error")
                {
                    var err = node?["error"]?.GetValue<string>() ?? "no detail";
                    throw new InvalidOperationException($"Pika job failed: {err}");
                }
                if (status == "pending" || status == "in_progress") progress?.Report(50);
                else progress?.Report(10);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { throw; }
        }
        throw new TimeoutException("Pika job did not complete within 15 minutes.");
    }

    private static string MapAspect(string a) => a switch
    {
        "9:16" => "9:16",
        "1:1"  => "1:1",
        "21:9" => "16:9",   // Pika doesn't support 21:9 — fall back to 16:9
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
