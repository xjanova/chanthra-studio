using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Posting;

/// <summary>
/// Posts photos / videos to a Facebook Page via Graph API v19.0.
///
/// Caller supplies:
///   * <see cref="PostRequest.TargetId"/> = the Page ID
///   * <see cref="PostRequest.ApiKey"/>   = Page Access Token (long-lived
///                                          token for that page; NOT the user
///                                          token)
///   * <see cref="PostRequest.FilePath"/> = local image / video file
///   * <see cref="PostRequest.Caption"/>  = post text (optional)
///
/// We sniff the file extension to decide between /photos and /videos. Text-
/// only posts (no file) hit /feed. Errors are surfaced via the `error` field
/// on Facebook's response, which we extract into PostResult.Error.
/// </summary>
internal sealed class FacebookPostingProvider : IPostingProvider
{
    private const string GraphBase = "https://graph.facebook.com/v19.0/";

    public string Id => "facebook";
    public string DisplayName => "Facebook Page · Graph API";
    public string ApiKeyHint => "Page access token · developers.facebook.com";
    public ProviderKind Kind => ProviderKind.Posting;
    public bool RequiresApiKey => true;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no token", "Paste a Page Access Token in Settings.");

        // /me with the page token returns the page record. Cheap, no rate cost.
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(GraphBase) };
            using var resp = await http.GetAsync($"me?fields=id,name&access_token={Uri.EscapeDataString(apiKey)}", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new ProviderHealth(false, $"HTTP {(int)resp.StatusCode}", ExtractError(body));
            var node = JsonNode.Parse(body);
            var name = node?["name"]?.GetValue<string>();
            return new ProviderHealth(true, name is null ? "ok" : $"page · {name}");
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, "probe failed", ex.Message);
        }
    }

    public async Task<PostResult> PostAsync(PostRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            return new PostResult(false, null, "Page Access Token missing — set it in Settings.");
        if (string.IsNullOrWhiteSpace(req.TargetId))
            return new PostResult(false, null, "Page ID missing — set it in Settings.");

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(GraphBase), Timeout = TimeSpan.FromMinutes(5) };

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(req.ApiKey), "access_token");

            if (!string.IsNullOrEmpty(req.FilePath) && File.Exists(req.FilePath))
            {
                var ext = Path.GetExtension(req.FilePath).ToLowerInvariant();
                var isVideo = ext is ".mp4" or ".mov" or ".webm" or ".avi" or ".mkv";

                var bytes = await File.ReadAllBytesAsync(req.FilePath, ct);
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(ext));
                form.Add(fileContent, "source", Path.GetFileName(req.FilePath));

                // Photos use `caption`, videos use `description`.
                if (!string.IsNullOrEmpty(req.Caption))
                {
                    form.Add(new StringContent(req.Caption), isVideo ? "description" : "caption");
                }

                var endpoint = isVideo ? "videos" : "photos";
                using var resp = await http.PostAsync($"{Uri.EscapeDataString(req.TargetId)}/{endpoint}", form, ct);
                return ParseResponse(await resp.Content.ReadAsStringAsync(ct), resp.IsSuccessStatusCode);
            }
            else
            {
                // Text-only post.
                if (string.IsNullOrEmpty(req.Caption))
                    return new PostResult(false, null, "Nothing to post: no file and no caption.");
                form.Add(new StringContent(req.Caption), "message");
                using var resp = await http.PostAsync($"{Uri.EscapeDataString(req.TargetId)}/feed", form, ct);
                return ParseResponse(await resp.Content.ReadAsStringAsync(ct), resp.IsSuccessStatusCode);
            }
        }
        catch (Exception ex)
        {
            return new PostResult(false, null, ex.Message);
        }
    }

    private static PostResult ParseResponse(string body, bool ok)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (!ok)
            {
                var msg = ExtractError(body) ?? body;
                return new PostResult(false, null, msg);
            }
            // Successful uploads return either {"id":"...","post_id":"..."} (page feed)
            // or {"id":"..."} (videos use a single id).
            var postId = node?["post_id"]?.GetValue<string>()
                       ?? node?["id"]?.GetValue<string>();
            return new PostResult(true, postId, null);
        }
        catch (Exception ex)
        {
            return new PostResult(false, null, $"unparseable response: {ex.Message}");
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var err = node?["error"];
            if (err is null) return null;
            var msg = err["message"]?.GetValue<string>() ?? "";
            var code = err["code"]?.GetValue<int?>();
            var sub = err["error_subcode"]?.GetValue<int?>();
            var parts = new List<string> { msg };
            if (code is int c) parts.Add($"code={c}");
            if (sub is int s) parts.Add($"subcode={s}");
            return string.Join(" · ", parts);
        }
        catch { return null; }
    }

    private static string GuessMime(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream",
    };
}
