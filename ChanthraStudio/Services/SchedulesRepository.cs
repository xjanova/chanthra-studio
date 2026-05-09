using System;
using System.Collections.Generic;
using System.Linq;
using ChanthraStudio.Models;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// CRUD + due-detection queries for the auto-schedule tables. Schema
/// lives in <see cref="Database"/> v3.
/// </summary>
public sealed class SchedulesRepository
{
    private readonly Database _db;

    public SchedulesRepository(Database db) { _db = db; }

    public IReadOnlyList<Schedule> All()
    {
        using var c = _db.Open();
        var rows = c.Query<Row>("SELECT * FROM schedules ORDER BY id ASC").ToList();
        return rows.Select(Hydrate).ToList();
    }

    /// <summary>Return enabled rows whose next_fire_at &lt;= now. Used by ScheduleService each tick.</summary>
    public IReadOnlyList<Schedule> Due()
    {
        using var c = _db.Open();
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = c.Query<Row>("""
            SELECT * FROM schedules
            WHERE is_enabled = 1
              AND next_fire_at IS NOT NULL
              AND next_fire_at <= $now
            ORDER BY next_fire_at ASC
            """, new { now = nowSec }).ToList();
        return rows.Select(Hydrate).ToList();
    }

    public long Insert(Schedule s)
    {
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        s.CreatedAt = DateTimeOffset.UtcNow;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.NextFireAt ??= s.ComputeNextFireAt(DateTimeOffset.UtcNow);
        var id = c.ExecuteScalar<long>("""
            INSERT INTO schedules (
                name, prompt_template, negative_prompt, workflow, route, style_id,
                aspect, camera, duration_sec, motion, kind, spec,
                auto_post, post_target, last_fire_at, next_fire_at,
                is_enabled, created_at, updated_at
            ) VALUES (
                $name, $promptTemplate, $negativePrompt, $workflow, $route, $styleId,
                $aspect, $camera, $duration, $motion, $kind, $spec,
                $autoPost, $postTarget, $lastFire, $nextFire,
                $enabled, $now, $now
            );
            SELECT last_insert_rowid();
            """, new
            {
                name = s.Name,
                promptTemplate = s.PromptTemplate,
                negativePrompt = s.NegativePrompt,
                workflow = s.Workflow,
                route = s.Route,
                styleId = s.StyleId,
                aspect = s.Aspect.ToString(),
                camera = s.Camera.ToString(),
                duration = s.DurationSec,
                motion = s.Motion,
                kind = s.Kind == ScheduleKind.DailySlots ? "daily_slots" : "interval",
                spec = s.Spec,
                autoPost = s.AutoPost ? 1 : 0,
                postTarget = s.PostTarget,
                lastFire = s.LastFireAt?.ToUnixTimeSeconds(),
                nextFire = s.NextFireAt?.ToUnixTimeSeconds(),
                enabled = s.IsEnabled ? 1 : 0,
                now,
            });
        s.Id = id;
        return id;
    }

    public void Update(Schedule s)
    {
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        s.UpdatedAt = DateTimeOffset.UtcNow;
        c.Execute("""
            UPDATE schedules SET
                name = $name,
                prompt_template = $promptTemplate,
                negative_prompt = $negativePrompt,
                workflow = $workflow,
                route = $route,
                style_id = $styleId,
                aspect = $aspect,
                camera = $camera,
                duration_sec = $duration,
                motion = $motion,
                kind = $kind,
                spec = $spec,
                auto_post = $autoPost,
                post_target = $postTarget,
                last_fire_at = $lastFire,
                next_fire_at = $nextFire,
                is_enabled = $enabled,
                updated_at = $now
            WHERE id = $id
            """, new
            {
                id = s.Id,
                name = s.Name,
                promptTemplate = s.PromptTemplate,
                negativePrompt = s.NegativePrompt,
                workflow = s.Workflow,
                route = s.Route,
                styleId = s.StyleId,
                aspect = s.Aspect.ToString(),
                camera = s.Camera.ToString(),
                duration = s.DurationSec,
                motion = s.Motion,
                kind = s.Kind == ScheduleKind.DailySlots ? "daily_slots" : "interval",
                spec = s.Spec,
                autoPost = s.AutoPost ? 1 : 0,
                postTarget = s.PostTarget,
                lastFire = s.LastFireAt?.ToUnixTimeSeconds(),
                nextFire = s.NextFireAt?.ToUnixTimeSeconds(),
                enabled = s.IsEnabled ? 1 : 0,
                now,
            });
    }

    public void SetEnabled(long id, bool enabled)
    {
        using var c = _db.Open();
        c.Execute("UPDATE schedules SET is_enabled = $e, updated_at = $now WHERE id = $id",
            new { id, e = enabled ? 1 : 0, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM schedules WHERE id = $id", new { id });
    }

    public long LogRun(long scheduleId, string status, string? jobId, string? error = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO schedule_runs (schedule_id, fired_at, job_id, status, error_message)
            VALUES ($scheduleId, $firedAt, $jobId, $status, $error);
            SELECT last_insert_rowid();
            """, new
            {
                scheduleId,
                firedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                jobId,
                status,
                error,
            });
    }

    public IReadOnlyList<ScheduleRun> RecentRuns(long scheduleId, int limit = 30)
    {
        using var c = _db.Open();
        var rows = c.Query<RunRow>("""
            SELECT id, schedule_id, fired_at, job_id, status, error_message
            FROM schedule_runs
            WHERE schedule_id = $scheduleId
            ORDER BY fired_at DESC LIMIT $limit
            """, new { scheduleId, limit }).ToList();
        return rows.Select(r => new ScheduleRun
        {
            Id = r.Id,
            ScheduleId = r.Schedule_id,
            FiredAt = DateTimeOffset.FromUnixTimeSeconds(r.Fired_at),
            JobId = r.Job_id,
            Status = r.Status,
            ErrorMessage = r.Error_message,
        }).ToList();
    }

    private static Schedule Hydrate(Row r)
    {
        return new Schedule
        {
            Id = r.Id,
            Name = r.Name,
            PromptTemplate = r.Prompt_template,
            NegativePrompt = r.Negative_prompt ?? "",
            Workflow = r.Workflow ?? "",
            Route = r.Route ?? "comfyui",
            StyleId = r.Style_id ?? "",
            Aspect = Enum.TryParse<Models.AspectRatio>(r.Aspect, true, out var a) ? a : Models.AspectRatio.Wide,
            Camera = Enum.TryParse<Models.CamMode>(r.Camera, true, out var cm) ? cm : Models.CamMode.Push,
            DurationSec = r.Duration_sec,
            Motion = r.Motion,
            Kind = r.Kind == "interval" ? ScheduleKind.Interval : ScheduleKind.DailySlots,
            Spec = r.Spec,
            AutoPost = r.Auto_post != 0,
            PostTarget = r.Post_target ?? "",
            LastFireAt = r.Last_fire_at is long lf ? DateTimeOffset.FromUnixTimeSeconds(lf) : null,
            NextFireAt = r.Next_fire_at is long nf ? DateTimeOffset.FromUnixTimeSeconds(nf) : null,
            IsEnabled = r.Is_enabled != 0,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(r.Created_at),
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(r.Updated_at),
        };
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Prompt_template { get; set; } = "";
        public string? Negative_prompt { get; set; }
        public string? Workflow { get; set; }
        public string? Route { get; set; }
        public string? Style_id { get; set; }
        public string Aspect { get; set; } = "Wide";
        public string Camera { get; set; } = "Push";
        public double Duration_sec { get; set; }
        public double Motion { get; set; }
        public string Kind { get; set; } = "daily_slots";
        public string Spec { get; set; } = "";
        public int Auto_post { get; set; }
        public string? Post_target { get; set; }
        public long? Last_fire_at { get; set; }
        public long? Next_fire_at { get; set; }
        public int Is_enabled { get; set; }
        public long Created_at { get; set; }
        public long Updated_at { get; set; }
    }

    private sealed class RunRow
    {
        public long Id { get; set; }
        public long Schedule_id { get; set; }
        public long Fired_at { get; set; }
        public string? Job_id { get; set; }
        public string Status { get; set; } = "";
        public string? Error_message { get; set; }
    }
}
