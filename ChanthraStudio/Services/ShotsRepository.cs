using System;
using System.Collections.Generic;
using System.Linq;
using ChanthraStudio.Models;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// Reads + writes the <c>shots</c> table. The schema has existed since v1
/// but nothing wrote to it until phase 7.4 — clips and generation_jobs
/// just stored shot_id with no parent row. With FK enforcement off (the
/// connection sets PRAGMA foreign_keys=ON but in practice the
/// configuration hasn't surfaced violations) this didn't crash anything,
/// but it meant a relaunch lost prompts entirely — only the clip files
/// on disk survived.
///
/// All shot writes go under the default project/sequence created by
/// migration v5 ("default"/"default") — phase 7.4 is single-sequence by
/// design, multi-sequence comes when the NLE editor needs it.
/// </summary>
public sealed class ShotsRepository
{
    private const string DefaultSequenceId = "default";
    private readonly Database _db;

    public ShotsRepository(Database db) { _db = db; }

    /// <summary>Persist a shot to the <c>shots</c> table. Best-effort — the
    /// generation pipeline must not error out because the metadata write
    /// failed (the user still gets their clip).</summary>
    public void Insert(Shot shot)
    {
        if (string.IsNullOrEmpty(shot.Id)) return;
        try
        {
            using var c = _db.Open();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            c.Execute("""
                INSERT OR REPLACE INTO shots
                (id, sequence_id, number, title, description, prompt, style_id,
                 model_id, aspect, duration_sec, motion, seed_a, seed_b,
                 hd4k, audio, cam_mode, status, progress, thumb_path,
                 video_path, created_at, updated_at)
                VALUES
                ($id, $seq, $number, $title, $description, $prompt, $style,
                 $model, $aspect, $dur, $motion, $seedA, $seedB,
                 $hd, $audio, $cam, $status, $progress, $thumb,
                 $video, $now, $now)
                """,
                new
                {
                    id = shot.Id,
                    seq = DefaultSequenceId,
                    number = shot.Number,
                    title = shot.Title,
                    description = shot.Description,
                    prompt = shot.Prompt,
                    style = shot.StyleId,
                    model = shot.ModelId,
                    aspect = shot.Aspect.ToString(),
                    dur = shot.DurationSec,
                    motion = shot.Motion,
                    seedA = shot.Seed.A,
                    seedB = shot.Seed.B,
                    hd = shot.Hd4k ? 1 : 0,
                    audio = shot.Audio ? 1 : 0,
                    cam = shot.Cam.ToString(),
                    status = shot.Status.ToString(),
                    progress = shot.Progress,
                    thumb = shot.ThumbUrl,
                    video = shot.VideoUrl,
                    now,
                });
        }
        catch (Exception ex)
        {
            // Metadata write is non-critical — never block the actual
            // generation. The clip file on disk is the source of truth.
            ActivityLog.Error("shots", $"Insert {shot.Id}", ex);
        }
    }

    /// <summary>Update a shot's status + progress + media paths as it moves
    /// through the pipeline. Called from <see cref="GenerationService"/>'s
    /// progress dispatcher.</summary>
    public void UpdateStatus(string shotId, ShotStatus status, double progress, string? thumbPath, string? videoPath)
    {
        if (string.IsNullOrEmpty(shotId)) return;
        try
        {
            using var c = _db.Open();
            c.Execute("""
                UPDATE shots
                SET status = $status,
                    progress = $progress,
                    thumb_path = COALESCE($thumb, thumb_path),
                    video_path = COALESCE($video, video_path),
                    updated_at = $now
                WHERE id = $id
                """,
                new
                {
                    id = shotId,
                    status = status.ToString(),
                    progress,
                    thumb = thumbPath,
                    video = videoPath,
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
        }
        catch (Exception ex)
        {
            ActivityLog.Error("shots", $"UpdateStatus {shotId} → {status}", ex);
        }
    }

    /// <summary>Return the most-recent <paramref name="limit"/> shots, newest
    /// first. Used by GenerateViewModel to repopulate the storyboard on
    /// launch with full prompt/style/seed context.</summary>
    public IReadOnlyList<Shot> Recent(int limit = 24)
    {
        try
        {
            using var c = _db.Open();
            var rows = c.Query<ShotRow>("""
                SELECT id, number, title, description, prompt, style_id, model_id,
                       aspect, duration_sec, motion, seed_a, seed_b,
                       hd4k, audio, cam_mode, status, progress,
                       thumb_path, video_path
                FROM shots
                WHERE id NOT LIKE 'board-%'
                ORDER BY created_at DESC, id DESC
                LIMIT $limit
                """, new { limit }).ToList();
            return rows.Select(Map).ToList();
        }
        catch
        {
            return Array.Empty<Shot>();
        }
    }

    /// <summary>
    /// Mark any shot that's been in <c>Generating</c> status for more than
    /// <paramref name="staleAfterMinutes"/> as <c>Error</c>. Called once at
    /// app launch — if a previous session crashed mid-render or the user
    /// quit while a long video was in flight, the row would otherwise stay
    /// forever-Generating in the storyboard rebuild.
    /// </summary>
    public int SweepStuckGenerations(int staleAfterMinutes = 10)
    {
        try
        {
            using var c = _db.Open();
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-staleAfterMinutes).ToUnixTimeSeconds();
            // Queue counts too: a row is only written at submit time, so a
            // Queue row is a submitted job the dead session never started.
            return c.Execute("""
                UPDATE shots
                SET status = 'Error', updated_at = $now
                WHERE status IN ('Generating', 'Queue') AND updated_at <= $cutoff
                """,
                new { now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cutoff });
        }
        catch { return 0; }
    }

    private static Shot Map(ShotRow r)
    {
        Enum.TryParse<AspectRatio>(r.Aspect, out var aspect);
        Enum.TryParse<CamMode>(r.Cam_mode, out var cam);
        Enum.TryParse<ShotStatus>(r.Status, out var status);
        return new Shot
        {
            Id = r.Id,
            Number = r.Number,
            Title = r.Title,
            Description = r.Description ?? "",
            Prompt = r.Prompt ?? "",
            StyleId = r.Style_id ?? "empress",
            ModelId = r.Model_id ?? "",
            Aspect = aspect,
            DurationSec = r.Duration_sec,
            DurationLabel = $"{r.Duration_sec:F1}s",
            Motion = r.Motion,
            Seed = (r.Seed_a ?? 0, r.Seed_b ?? 0),
            Hd4k = r.Hd4k != 0,
            Audio = r.Audio != 0,
            Cam = cam,
            Status = status,
            Progress = r.Progress,
            ThumbUrl = r.Thumb_path,
            VideoUrl = r.Video_path,
        };
    }

    private sealed class ShotRow
    {
        public string Id { get; set; } = "";
        public string Number { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? Prompt { get; set; }
        public string? Style_id { get; set; }
        public string? Model_id { get; set; }
        public string Aspect { get; set; } = "Wide";
        public double Duration_sec { get; set; }
        public double Motion { get; set; }
        public int? Seed_a { get; set; }
        public int? Seed_b { get; set; }
        public int Hd4k { get; set; }
        public int Audio { get; set; }
        public string Cam_mode { get; set; } = "Locked";
        public string Status { get; set; } = "Queue";
        public double Progress { get; set; }
        public string? Thumb_path { get; set; }
        public string? Video_path { get; set; }
    }
}
