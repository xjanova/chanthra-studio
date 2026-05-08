using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Posting;

/// <summary>
/// Generic webhook poster — multipart form POST to any URL the user
/// configures in Settings (<see cref="AppSettings.PostWebhookUrl"/>).
///
/// Fields uploaded:
///   * file      — the clip binary (clip's filename + sniffed MIME)
///   * metadata  — JSON string with caption · clip / shot / job ids · timestamp
///
/// Optional bearer token sourced from <see cref="PostRequest.ApiKey"/> via
/// <c>Authorization: Bearer {token}</c>. Empty key → unauthenticated.
///
/// Endpoints behind the URL are expected to return any 2xx status to be
/// treated as success; the response body is ignored unless it parses to JSON
/// containing an "id" field, which we surface as <see cref="PostResult.PostId"/>.
/// </summary>
internal sealed class WebhookPostingProvider : IPostingProvider
{
    private static readonly HttpClient Http = new();

    public string Id => "webhook";
    public string DisplayName => "Generic webhook";
    public string ApiKeyHint => "Optional bearer/secret — empty for public endpoints";
    public ProviderKind Kind => ProviderKind.Posting;
    public bool RequiresApiKey => false;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        // The provider is configured per-clip via Settings.PostWebhookUrl, not
        // here, so we can't actually probe it without a target. Return OK so
        // the Settings UI doesn't redbox the row.
        await Task.Yield();
        return new ProviderHealth(true, "ready", "Probe runs at first post — no setup check available.");
    }

    public async Task<PostResult> PostAsync(PostRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.TargetId))
            return new PostResult(false, null, "Webhook URL missing — set it in Settings → Posting.");
        if (!Uri.TryCreate(req.TargetId, UriKind.Absolute, out var url))
            return new PostResult(false, null, $"Invalid webhook URL: {req.TargetId}");
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            return new PostResult(false, null, $"Webhook URL must be http(s) — got {url.Scheme}://");

        try
        {
            using var form = new MultipartFormDataContent();

            if (!string.IsNullOrEmpty(req.FilePath) && File.Exists(req.FilePath))
            {
                var bytes = await File.ReadAllBytesAsync(req.FilePath, ct);
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(Path.GetExtension(req.FilePath)));
                form.Add(fileContent, "file", Path.GetFileName(req.FilePath));
            }

            var meta = new WebhookMeta
            {
                Caption = req.Caption,
                ClipId = req.Extras.TryGetValue("clipId", out var c) ? c : null,
                ShotId = req.Extras.TryGetValue("shotId", out var s) ? s : null,
                JobId = req.Extras.TryGetValue("jobId", out var j) ? j : null,
                FileName = string.IsNullOrEmpty(req.FilePath) ? null : Path.GetFileName(req.FilePath),
                PostedAt = DateTimeOffset.UtcNow,
            };
            var metaJson = JsonSerializer.Serialize(meta, JsonOpts);
            form.Add(new StringContent(metaJson, System.Text.Encoding.UTF8, "application/json"), "metadata");

            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            if (!string.IsNullOrEmpty(req.ApiKey))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadCts.CancelAfter(TimeSpan.FromMinutes(5));
            using var resp = await Http.SendAsync(msg, uploadCts.Token);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var trimmed = body.Length > 240 ? body[..240] + "…" : body;
                return new PostResult(false, null, $"HTTP {(int)resp.StatusCode} — {trimmed}");
            }

            // Best-effort id extraction from the response.
            string? postId = null;
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                postId = node?["id"]?.GetValue<string>()
                       ?? node?["post_id"]?.GetValue<string>()
                       ?? node?["uuid"]?.GetValue<string>();
            }
            catch { /* response wasn't JSON — fine */ }

            return new PostResult(true, postId, null);
        }
        catch (Exception ex)
        {
            return new PostResult(false, null, ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class WebhookMeta
    {
        public string? Caption { get; set; }
        public string? ClipId { get; set; }
        public string? ShotId { get; set; }
        public string? JobId { get; set; }
        public string? FileName { get; set; }
        public DateTimeOffset PostedAt { get; set; }
    }

    private static string GuessMime(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };
}
