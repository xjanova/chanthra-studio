using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Background scheduler. Ticks every 60 seconds, fetches due schedules
/// from the SQLite store, fires generation through the existing
/// GenerationService, and (optionally) posts the resulting clip via
/// PostingService.
///
/// Lifecycle: <see cref="Start"/> from App.OnStartup, <see cref="Stop"/>
/// from App.OnExit. The timer thread is fire-and-forget per tick — long
/// jobs don't block subsequent ticks because each due schedule kicks
/// off an awaitable Task that the service tracks internally.
/// </summary>
public sealed class ScheduleService : IDisposable
{
    private readonly StudioContext _ctx;
    private Timer? _timer;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>jobId → scheduleId mapping so the GenerationService Done event can route auto-post back to the right schedule.</summary>
    private readonly ConcurrentDictionary<string, long> _jobToSchedule = new();

    public event Action<Schedule>? ScheduleFired;

    public ScheduleService(StudioContext ctx)
    {
        _ctx = ctx;
        _ctx.Generation.ProgressChanged += OnGenerationProgress;
    }

    public void Start()
    {
        if (_timer is not null) return;
        // First tick after 5s gives the rest of the app time to settle —
        // license validate, GPU probe, workflow scan, etc. — before we
        // start fanning out concurrent generations.
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task TickAsync()
    {
        // Re-entrancy guard: the previous tick's fired generations may
        // still be running, but THIS tick's job is just "find anyone
        // overdue" — that's fast and idempotent. Lock just on the
        // due-fetch + per-row updates, NOT on the generation submission.
        Schedule[] due;
        lock (_gate)
        {
            try { due = _ctx.Schedules.Due().ToArray(); }
            catch { due = Array.Empty<Schedule>(); }
        }

        foreach (var s in due)
        {
            // Bump next_fire_at FIRST so a long-running generation can't
            // cause us to fire the same slot twice on consecutive ticks.
            var fired = DateTimeOffset.UtcNow;
            s.LastFireAt = fired;
            s.NextFireAt = s.ComputeNextFireAt(fired);
            try { _ctx.Schedules.Update(s); } catch { /* best-effort */ }

            ScheduleFired?.Invoke(s);

            // Compose + submit. If anything throws, log a "skipped" run
            // and continue — one bad schedule shouldn't take the loop down.
            try
            {
                var shot = ComposeShot(s);
                long runId = _ctx.Schedules.LogRun(s.Id, "queued", null);

                // Save the promptId↔scheduleId mapping so OnGenerationProgress
                // can pick up auto-post and update run status.
                var promptId = await _ctx.Generation.SubmitAsync(shot);
                _jobToSchedule[promptId] = s.Id;

                // Patch the run row with the real prompt id (best-effort).
                try
                {
                    using var c = _ctx.Db.Open();
                    Dapper.SqlMapper.Execute(c,
                        "UPDATE schedule_runs SET job_id = $j WHERE id = $i",
                        new { j = promptId, i = runId });
                }
                catch { }
            }
            catch (Exception ex)
            {
                _ctx.Schedules.LogRun(s.Id, "error", null, ex.Message);
            }
        }
    }

    private Shot ComposeShot(Schedule s)
    {
        var rng = new Random();
        return new Shot
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            Number = $"S{s.Id:D2}",
            Title = string.IsNullOrEmpty(s.Name) ? "Scheduled" : s.Name,
            Description = s.PromptTemplate.Length > 140 ? s.PromptTemplate[..140] + "…" : s.PromptTemplate,
            Prompt = ResolveTemplate(s.PromptTemplate),
            NegativePrompt = string.IsNullOrEmpty(s.NegativePrompt)
                ? "blurry, low quality, watermark"
                : s.NegativePrompt,
            StyleId = s.StyleId,
            Aspect = s.Aspect,
            Cam = s.Camera,
            DurationSec = s.DurationSec,
            DurationLabel = $"{s.DurationSec:F1}s",
            Motion = s.Motion,
            // Fresh seed each fire so a daily schedule produces a varied
            // batch rather than the same image over and over.
            Seed = (rng.Next(1000, 9999), rng.Next(1000, 9999)),
            Hd4k = true,
            Audio = false,
            Status = ShotStatus.Queue,
            ThumbUrl = "/Assets/Brand/empress-portrait.png",
        };
    }

    /// <summary>
    /// Replaces a small set of moustache-style placeholders so users can
    /// build prompts that vary per fire without a full templating engine.
    /// Supported: {date}, {time}, {dow}, {hour}, {minute}, {iso}.
    /// </summary>
    public static string ResolveTemplate(string template)
    {
        if (string.IsNullOrEmpty(template)) return template;
        var now = DateTimeOffset.Now;
        return template
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{hour}", now.ToString("HH"))
            .Replace("{minute}", now.ToString("mm"))
            .Replace("{dow}", now.DayOfWeek.ToString())
            .Replace("{iso}", now.ToString("o"));
    }

    private async void OnGenerationProgress(object? sender, GenerationProgressEventArgs e)
    {
        if (!_jobToSchedule.TryGetValue(e.PromptId, out var scheduleId)) return;

        if (e.Status == ShotStatus.Done)
        {
            _jobToSchedule.TryRemove(e.PromptId, out _);

            try
            {
                using var c = _ctx.Db.Open();
                Dapper.SqlMapper.Execute(c,
                    "UPDATE schedule_runs SET status = 'done' WHERE job_id = $j",
                    new { j = e.PromptId });
            }
            catch { }

            // Auto-post — fire and forget. We look the schedule up fresh
            // so a recently-edited post target is honoured.
            try
            {
                var schedule = _ctx.Schedules.All().FirstOrDefault(s => s.Id == scheduleId);
                if (schedule is null || !schedule.AutoPost) return;
                if (string.IsNullOrEmpty(schedule.PostTarget)) return;

                var clip = _ctx.Clips.RecentClips(20)
                    .FirstOrDefault(c => c.ShotId == e.ShotId);
                if (clip is null) return;

                var caption = ResolveTemplate(schedule.PromptTemplate);
                if (caption.Length > 1500) caption = caption[..1500];
                await _ctx.Posting.PostAsync(schedule.PostTarget, clip, caption);
            }
            catch
            {
                // Posting failures are noted in the post_history table by
                // PostingService itself; the schedule run still counts as
                // "done" because the clip exists.
            }
            return;
        }

        if (e.Status == ShotStatus.Error)
        {
            _jobToSchedule.TryRemove(e.PromptId, out _);
            try
            {
                using var c = _ctx.Db.Open();
                Dapper.SqlMapper.Execute(c,
                    "UPDATE schedule_runs SET status = 'error', error_message = $err WHERE job_id = $j",
                    new { j = e.PromptId, err = e.Error });
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.Generation.ProgressChanged -= OnGenerationProgress;
        Stop();
    }
}
