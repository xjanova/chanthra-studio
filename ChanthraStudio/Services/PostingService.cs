using System;
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
        Completed?.Invoke(this, new PostingCompletedEventArgs(clip.Id, providerId, result));
        return result;
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
        }
        catch
        {
            // DB write best-effort — UI already shows the toast.
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
