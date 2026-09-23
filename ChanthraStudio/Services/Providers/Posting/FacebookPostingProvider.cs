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
    // v19/v20 are being retired in 2026 (v19 on 2026-05-21). Pinned to a
    // current, long-support version per the 2026-06 provider audit.
    private const string GraphBase = "https://graph.facebook.com/v24.0/";

    // Video bytes go to the dedicated upload host (Video API → Publishing
    // shows graph-video.facebook.com/{version}/{page-id}/videos); the general
    // host is for everything else.
    private const string GraphVideoBase = "https://graph-video.facebook.com/v24.0/";

    // Single shared HttpClient — per-call construction was leaking sockets
    // into TIME_WAIT under repeated posting. Timeout set per-request via
    // CancellationToken / Task.WhenAny rather than the instance-wide timeout
    // so one long upload doesn't throttle a small probe.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri(GraphBase) };

    // Uploads are bounded by the per-call token below, not the 100 s default.
    private static readonly HttpClient UploadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

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
            using var probe = new HttpRequestMessage(HttpMethod.Get, "me?fields=id,name");
            probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(probe, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new ProviderHealth(false, $"HTTP {(int)resp.StatusCode}", ExtractError(body));
            var node = JsonNode.Parse(body);
            var name = node?["name"]?.GetValue<string>();
            var id = node?["id"]?.GetValue<string>();
            // The id is shown so it can be compared with the Page ID setting —
            // a user token passes this probe too, and then every post fails.
            return new ProviderHealth(true, name is null ? "ok" : $"{name} · id {id}");
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
        // A pasted id often carries a space; it was compared trimmed but posted as-is.
        var pageId = req.TargetId.Trim();
        // A clip whose file has gone is a failure, not a text post: this used
        // to publish the caption alone and report success.
        if (!string.IsNullOrEmpty(req.FilePath) && !File.Exists(req.FilePath))
            return new PostResult(false, null, $"ไม่พบไฟล์ที่จะโพสต์: {req.FilePath}");

        try
        {
            // A token for another page, or a personal user token, is the
            // commonest setup mistake; Facebook's own reply to the upload is
            // an opaque permissions error after the whole file has been sent.
            var (pageToken, problem) = await ResolvePageTokenAsync(req.ApiKey, pageId, ct);
            if (problem is not null) return new PostResult(false, null, problem);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(pageToken), "access_token");

            if (!string.IsNullOrEmpty(req.FilePath))
            {
                var ext = Path.GetExtension(req.FilePath).ToLowerInvariant();
                // The studio's one list of video types (.ts, .wmv, .mpg… went to /photos).
                var isVideo = MediaKind.IsVideo(req.FilePath);

                // Streamed: a finished film is hundreds of MB, and reading it
                // whole into memory first was the slowest part of a post.
                var size = new FileInfo(req.FilePath).Length;
                await using var stream = new FileStream(req.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                var fileContent = new StreamContent(stream, 1 << 16);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(ext));
                form.Add(fileContent, "source", Path.GetFileName(req.FilePath));

                // Photos use `caption`, videos use `description`.
                if (!string.IsNullOrEmpty(req.Caption))
                {
                    form.Add(new StringContent(req.Caption), isVideo ? "description" : "caption");
                }

                var url = (isVideo ? GraphVideoBase : GraphBase)
                          + $"{Uri.EscapeDataString(pageId)}/{(isVideo ? "videos" : "photos")}";
                // Five minutes flat cut off a large film on a home uplink
                // part-way; allow for ~1 Mbit/s with five minutes on top.
                using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                uploadCts.CancelAfter(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(size / 125_000.0));
                try
                {
                    using var resp = await UploadHttp.PostAsync(url, form, uploadCts.Token);
                    return ParseResponse(await resp.Content.ReadAsStringAsync(ct), resp.IsSuccessStatusCode);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new PostResult(false, null,
                        $"อัปโหลดไป Facebook ไม่ทันเวลา ({size / 1_048_576.0:0.#} MB) — ตรวจการเชื่อมต่อแล้วลองใหม่ " +
                        "และเช็คในเพจก่อนว่าโพสต์ไม่ได้ขึ้นไปแล้ว");
                }
            }
            else
            {
                // Text-only post.
                if (string.IsNullOrEmpty(req.Caption))
                    return new PostResult(false, null, "Nothing to post: no file and no caption.");
                form.Add(new StringContent(req.Caption), "message");
                using var resp = await Http.PostAsync($"{Uri.EscapeDataString(pageId)}/feed", form, ct);
                return ParseResponse(await resp.Content.ReadAsStringAsync(ct), resp.IsSuccessStatusCode);
            }
        }
        catch (Exception ex)
        {
            return new PostResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// The token to post with. A Page token for <paramref name="pageId"/> is
    /// used as-is. A personal or system-user token — the commonest setup
    /// mistake — is exchanged for that page's own token when the account
    /// manages the page. A token for some other page is refused with a Thai
    /// explanation. A lookup that fails for any other reason is not treated
    /// as a mismatch: the post then reports Facebook's real error.
    /// </summary>
    private static async Task<(string Token, string? Problem)> ResolvePageTokenAsync(
        string token, string pageId, CancellationToken ct)
    {
        pageId = pageId.Trim();
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(30));

            // Bearer header rather than ?access_token=: URLs end up in proxy logs.
            using var meReq = new HttpRequestMessage(HttpMethod.Get, "me?fields=id,name");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(meReq, probeCts.Token);
            var body = await resp.Content.ReadAsStringAsync(probeCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = ExtractError(body) ?? $"HTTP {(int)resp.StatusCode}";
                return err.Contains("code=190")
                    ? (token, "Facebook token หมดอายุหรือถูกยกเลิก (code 190) — สร้าง token ใหม่แล้ววางใน Settings → Posting")
                    : (token, null);
            }
            var me = JsonNode.Parse(body);
            var id = me?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || id == pageId) return (token, null);
            var name = me?["name"]?.GetValue<string>() ?? "?";

            // Not the page itself: ask for the page's token through this account.
            using var pageReq = new HttpRequestMessage(HttpMethod.Get, $"{Uri.EscapeDataString(pageId)}?fields=access_token");
            pageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var pageResp = await Http.SendAsync(pageReq, probeCts.Token);
            var pageBody = await pageResp.Content.ReadAsStringAsync(probeCts.Token);
            // Rate limits and server errors say nothing about who owns the
            // token; let the post itself run and report Facebook's answer.
            if ((int)pageResp.StatusCode == 429 || (int)pageResp.StatusCode >= 500)
                return (token, null);
            var derived = pageResp.IsSuccessStatusCode
                ? JsonNode.Parse(pageBody)?["access_token"]?.GetValue<string>()
                : null;
            if (!string.IsNullOrEmpty(derived))
            {
                ActivityLog.Info("posting", $"token belongs to {name}; posting with page {pageId}'s own token");
                return (derived, null);
            }
            return (token,
                $"Token นี้เป็นของ \"{name}\" (id {id}) ไม่ใช่ของเพจ {pageId} และขอ token ของเพจนั้นไม่ได้ — " +
                "ใช้ Page Access Token ของเพจที่จะโพสต์ (บัญชีต้องเป็นผู้ดูแลเพจและให้สิทธิ์ pages_manage_posts) หรือแก้ Page ID ใน Settings → Posting");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (token, null);
        }
        catch (HttpRequestException)
        {
            return (token, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (token, null);
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
