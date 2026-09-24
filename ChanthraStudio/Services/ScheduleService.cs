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

    /// <summary>A run finished (rendered, failed, or its auto-post did).
    /// Raised on a worker or UI thread; listeners marshal.</summary>
    public event Action<long>? RunFinished;

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

    /// <summary>
    /// Force the scheduler to scan + fire any due rows right now, without
    /// waiting for the 60-second timer tick. Called from the Schedule
    /// view's "Run now" button after the UI sets next_fire_at into the past.
    /// </summary>
    public Task ForceTickAsync() => TickAsync();

    /// <summary>Schedule ids between "decided to fire" and "submitted". A
    /// second fire for the same schedule in that window — a double-clicked
    /// Run now, or Run now racing the timer — is refused.</summary>
    private readonly ConcurrentDictionary<long, byte> _firing = new();

    /// <summary>
    /// Fire one schedule now, whether or not it is enabled, without moving its
    /// regular next-fire. Returns false when that schedule is already firing.
    /// </summary>
    public async Task<bool> RunNowAsync(Schedule schedule)
    {
        if (!_firing.TryAdd(schedule.Id, 0)) return false;
        try
        {
            // Fire what is saved, not half-edited fields in the editor.
            var saved = _ctx.Schedules.All().FirstOrDefault(s => s.Id == schedule.Id) ?? schedule;
            saved.LastFireAt = DateTimeOffset.UtcNow;
            try { _ctx.Schedules.Update(saved); } catch { /* best-effort */ }
            ScheduleFired?.Invoke(saved);
            await SubmitAsync(saved);
            return true;
        }
        finally
        {
            _firing.TryRemove(schedule.Id, out _);
        }
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
            if (!_firing.TryAdd(s.Id, 0)) continue;
            try
            {
                // Bump next_fire_at FIRST so a long-running generation can't
                // cause us to fire the same slot twice on consecutive ticks.
                var fired = DateTimeOffset.UtcNow;
                s.LastFireAt = fired;
                s.NextFireAt = s.ComputeNextFireAt(fired);
                try { _ctx.Schedules.Update(s); } catch { /* best-effort */ }

                ScheduleFired?.Invoke(s);
                await SubmitAsync(s);
            }
            finally
            {
                _firing.TryRemove(s.Id, out _);
            }
        }
    }

    /// <summary>Compose and submit one fire. If anything throws, a run row
    /// says so and the loop carries on — one bad schedule must not take the
    /// others down.</summary>
    private async Task SubmitAsync(Schedule s)
    {
        try
        {
            var shot = ComposeShot(s);
            long runId = _ctx.Schedules.LogRun(s.Id, "queued", null);

            // Save the promptId↔scheduleId mapping so OnGenerationProgress
            // can pick up auto-post and update run status.
            // Each schedule carries its own Route + Workflow — honour
            // them per-fire instead of the global Settings.ActiveVideo
            // so two schedules can target different providers at once.
            var promptId = await _ctx.Generation.SubmitAsync(
                shot,
                routeOverride: s.Route,
                workflowOverride: s.Workflow);
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
            ActivityLog.Warn("schedule", $"{s.Name}: fire failed — {ex.Message}");
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
        // Invariant: under th-TH the current culture writes the Buddhist year,
        // so {date} came out as 2569-09-23 while {iso} said 2026.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return template
            .Replace("{date}", now.ToString("yyyy-MM-dd", inv))
            .Replace("{time}", now.ToString("HH:mm", inv))
            .Replace("{hour}", now.ToString("HH", inv))
            .Replace("{minute}", now.ToString("mm", inv))
            .Replace("{dow}", now.DayOfWeek.ToString())
            .Replace("{iso}", now.ToString("o", inv));
    }

    private async void OnGenerationProgress(object? sender, GenerationProgressEventArgs e)
    {
        if (!_jobToSchedule.TryGetValue(e.PromptId, out var scheduleId)) return;

        if (e.Status == ShotStatus.Done)
        {
            _jobToSchedule.TryRemove(e.PromptId, out _);
            string? postProblem = null;

            // Auto-post. We look the schedule up fresh so a recently-edited
            // post target is honoured.
            try
            {
                var schedule = _ctx.Schedules.All().FirstOrDefault(s => s.Id == scheduleId);
                if (schedule is { AutoPost: true } && !string.IsNullOrEmpty(schedule.PostTarget))
                {
                    var clip = _ctx.Clips.RecentClips(20).FirstOrDefault(c => c.ShotId == e.ShotId);
                    if (clip is null)
                    {
                        postProblem = "โพสต์ไม่ได้ — หาไฟล์ของงานนี้ใน Library ไม่เจอ";
                    }
                    else
                    {
                        // The public caption is the schedule's own caption, or
                        // its name. It used to be the generation prompt.
                        var caption = ResolveTemplate(string.IsNullOrWhiteSpace(schedule.PostCaption)
                            ? schedule.Name
                            : schedule.PostCaption);
                        if (caption.Length > 1500) caption = caption[..1500];
                        var result = await _ctx.Posting.PostAsync(schedule.PostTarget, clip, caption);
                        if (!result.Ok) postProblem = "โพสต์ไม่สำเร็จ: " + result.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                postProblem = "โพสต์ไม่สำเร็จ: " + ex.Message;
            }

            // The run is "done" either way — the clip exists — but a failed
            // post is written down where the card can show it, instead of
            // being known only to the post_history table.
            try { _ctx.Schedules.FinishRun(e.PromptId, "done", postProblem); } catch { }
            if (postProblem is not null) ActivityLog.Warn("schedule", postProblem);
            RunFinished?.Invoke(scheduleId);
            return;
        }

        if (e.Status == ShotStatus.Error)
        {
            _jobToSchedule.TryRemove(e.PromptId, out _);
            try { _ctx.Schedules.FinishRun(e.PromptId, "error", e.Error); } catch { }
            RunFinished?.Invoke(scheduleId);
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
