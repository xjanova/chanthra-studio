using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services.Providers;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// Wires the per-clip Post action: looks up the chosen provider, pulls the
/// API key + target id from settings, runs the post, and writes a
/// post_history row regardless of outcome (success rows for tracking,
/// failure rows for diagnosis later).
/// </summary>
public sealed class PostingService
{
    private readonly StudioContext _ctx;

    public event EventHandler<PostingCompletedEventArgs>? Completed;

    public PostingService(StudioContext ctx) { _ctx = ctx; }

    public async Task<PostResult> PostAsync(string providerId, Clip clip, string caption, CancellationToken ct = default)
    {
        var provider = _ctx.Providers.Posting.FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
        {
            var miss = new PostResult(false, null, $"Unknown posting provider: {providerId}");
            WriteHistory(clip.Id, providerId, miss);
            KeepCaptionForRetry(clip.Id, caption, posted: false);
            Completed?.Invoke(this, new PostingCompletedEventArgs(clip.Id, providerId, miss));
            return miss;
        }

        var apiKey = _ctx.Settings[providerId];
        var targetId = providerId switch
        {
            "facebook" => _ctx.Settings.PostFacebookPageId,
            "webhook" => _ctx.Settings.PostWebhookUrl,
            _ => "",
        };

        var req = new PostRequest
        {
            ApiKey = apiKey,
            TargetId = targetId,
            FilePath = clip.FilePath,
            Caption = caption,
        };
        req.Extras["clipId"] = clip.Id;
        req.Extras["shotId"] = clip.ShotId;

        PostResult result;
        try
        {
            result = await provider.PostAsync(req, ct);
        }
        catch (Exception ex)
        {
            result = new PostResult(false, null, ex.Message);
        }

        WriteHistory(clip.Id, providerId, result);
        KeepCaptionForRetry(clip.Id, caption, result.Ok);
        Completed?.Invoke(this, new PostingCompletedEventArgs(clip.Id, providerId, result));
        return result;
    }

    /// <summary>
    /// The caption of a failed post, for the Library's retry to start from.
    /// Auto Pilot and the scheduler compose a full caption with hashtags; the
    /// retry used to offer just the file name, and the caption was gone.
    /// </summary>
    public static string? PendingCaption(string clipId)
    {
        try
        {
            var path = CaptionFile(clipId);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    /// <summary>Drop a kept caption — its clip is gone.</summary>
    public static void ForgetCaption(string clipId) => KeepCaptionForRetry(clipId, "", posted: true);

    private static string CaptionFile(string clipId) =>
        Path.Combine(AppPaths.MediaFolder, "captions", SafeName(clipId) + ".txt");

    private static string SafeName(string id) =>
        string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    private static void KeepCaptionForRetry(string clipId, string caption, bool posted)
    {
        try
        {
            var path = CaptionFile(clipId);
            if (posted || string.IsNullOrWhiteSpace(caption))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, caption);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("posting", "could not keep the caption for a retry: " + ex.Message);
        }
    }

    private void WriteHistory(string clipId, string providerId, PostResult result)
    {
        try
        {
            using var c = _ctx.Db.Open();
            c.Execute("""
                INSERT INTO post_history (clip_id, target, target_id, posted_at, success, response_blob)
                VALUES ($clipId, $target, $targetId, $now, $success, $blob)
                """,
                new
                {
                    clipId,
                    target = providerId,
                    targetId = result.PostId,
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    success = result.Ok ? 1 : 0,
                    blob = result.Error ?? result.PostId ?? "",
                });
            ActivityLog.Info("posting",
                result.Ok ? $"posted clip {clipId} → {providerId} · id={result.PostId}"
                          : $"post FAILED clip {clipId} → {providerId} · {result.Error}");
        }
        catch (Exception ex)
        {
            ActivityLog.Error("posting", $"WriteHistory clip={clipId} provider={providerId}", ex);
        }
    }
}

public sealed class PostingCompletedEventArgs : EventArgs
{
    public string ClipId { get; }
    public string ProviderId { get; }
    public PostResult Result { get; }

    public PostingCompletedEventArgs(string clipId, string providerId, PostResult result)
    {
        ClipId = clipId;
        ProviderId = providerId;
        Result = result;
    }
}
