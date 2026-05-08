using System;
using System.Collections.Generic;
using System.Linq;
using ChanthraStudio.Models;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// Read-side queries for the Library + Queue views. Writes still happen
/// inline in <see cref="GenerationService"/>; centralising reads here keeps
/// the view-models thin and the SQL in one place.
/// </summary>
public sealed class ClipsRepository
{
    private readonly Database _db;

    public ClipsRepository(Database db) { _db = db; }

    public IReadOnlyList<Clip> RecentClips(int limit = 200)
    {
        using var c = _db.Open();
        // LEFT JOIN onto post_history so the Library card can show a
        // "✓ Posted" badge without a second query per clip. We aggregate
        // post count + most-recent posted_at in the same row.
        var rows = c.Query<ClipRow>("""
            SELECT
                cl.id, cl.shot_id, cl.duration_ms, cl.file_path, cl.poster_path, cl.created_at,
                COALESCE(ph.post_count, 0)  AS post_count,
                ph.last_posted_at           AS last_posted_at
            FROM clips cl
            LEFT JOIN (
                SELECT clip_id,
                       COUNT(*) AS post_count,
                       MAX(posted_at) AS last_posted_at
                FROM post_history
                WHERE success = 1
                GROUP BY clip_id
            ) ph ON ph.clip_id = cl.id
            ORDER BY cl.created_at DESC, cl.id DESC
            LIMIT $limit
            """, new { limit }).ToList();
        return rows.Select(r => new Clip
        {
            Id = r.Id,
            ShotId = r.Shot_id,
            DurationMs = r.Duration_ms,
            FilePath = r.File_path,
            PosterPath = r.Poster_path,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.Created_at),
            PostCount = r.Post_count,
            LastPostedLabel = r.Last_posted_at is long t
                ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd")
                : "",
        }).ToList();
    }

    public IReadOnlyList<GenerationJob> RecentJobs(int limit = 200)
    {
        using var c = _db.Open();
        var rows = c.Query<JobRow>("""
            SELECT id, shot_id, provider, status, submitted_at, completed_at, error_message
            FROM generation_jobs
            ORDER BY submitted_at DESC, id DESC
            LIMIT $limit
            """, new { limit }).ToList();
        return rows.Select(r => new GenerationJob
        {
            Id = r.Id,
            ShotId = r.Shot_id,
            Provider = r.Provider,
            Status = r.Status,
            SubmittedAt = DateTimeOffset.FromUnixTimeSeconds(r.Submitted_at),
            CompletedAt = r.Completed_at is long t ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
            ErrorMessage = r.Error_message,
        }).ToList();
    }

    public void DeleteClip(string clipId)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM clips WHERE id = $id", new { id = clipId });
    }

    private sealed class ClipRow
    {
        public string Id { get; set; } = "";
        public string Shot_id { get; set; } = "";
        public int Duration_ms { get; set; }
        public string File_path { get; set; } = "";
        public string? Poster_path { get; set; }
        public long Created_at { get; set; }
        public int Post_count { get; set; }
        public long? Last_posted_at { get; set; }
    }

    private sealed class JobRow
    {
        public string Id { get; set; } = "";
        public string Shot_id { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Status { get; set; } = "";
        public long Submitted_at { get; set; }
        public long? Completed_at { get; set; }
        public string? Error_message { get; set; }
    }
}
