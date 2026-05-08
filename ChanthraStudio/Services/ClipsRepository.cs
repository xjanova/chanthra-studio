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
        var rows = c.Query<ClipRow>("""
            SELECT id, shot_id, duration_ms, file_path, poster_path, created_at
            FROM clips
            ORDER BY created_at DESC, id DESC
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
