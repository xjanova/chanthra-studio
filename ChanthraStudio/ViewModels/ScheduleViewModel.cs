using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class ScheduleViewModel : ObservableObject, IDisposable
{
    private readonly StudioContext? _ctx;
    private bool _attached;
    private System.Windows.Threading.DispatcherTimer? _clock;

    /// <summary>False for the design-time instance the XAML declares; the
    /// view swaps that for a live one on load.</summary>
    public bool IsLive => _ctx is not null;

    public ObservableCollection<Schedule> Schedules { get; } = new();

    /// <summary>Drives the list's empty state — a first visit showed a blank
    /// panel with nothing saying what goes there.</summary>
    public bool HasNoSchedules => Schedules.Count == 0;

    private Schedule? _selected;
    public Schedule? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public ObservableCollection<string> AvailableWorkflows { get; } = new();
    public ObservableCollection<string> PostingTargets { get; } = new()
    {
        "", "facebook", "webhook",
    };

    private string _toast = "";
    public string Toast { get => _toast; set => SetProperty(ref _toast, value); }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand AddCommand { get; }
    public IRelayCommand<Schedule> ToggleEnabledCommand { get; }
    public IRelayCommand<Schedule> SaveCommand { get; }
    public IRelayCommand<Schedule> DeleteCommand { get; }
    public IRelayCommand<Schedule> RunNowCommand { get; }
    public IRelayCommand<Schedule> CloneCommand { get; private set; } = null!;

    public ScheduleViewModel() : this(null) { }

    public ScheduleViewModel(StudioContext? ctx)
    {
        _ctx = ctx;
        Schedules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSchedules));

        RefreshCommand = new RelayCommand(Refresh);
        AddCommand = new RelayCommand(AddNew);
        ToggleEnabledCommand = new RelayCommand<Schedule>(ToggleEnabled);
        SaveCommand = new RelayCommand<Schedule>(Save);
        DeleteCommand = new RelayCommand<Schedule>(Delete);
        RunNowCommand = new RelayCommand<Schedule>(RunNow);
        CloneCommand = new RelayCommand<Schedule>(Clone);

        if (_ctx is not null)
        {
            foreach (var w in _ctx.Workflows.All) AvailableWorkflows.Add(w.Name);
            Refresh();
            Reattach();
        }
        // (Reattach does not reload the list — see SyncFireTimes.)
        else
        {
            // Design-time placeholder so the XAML preview isn't empty.
            Schedules.Add(new Schedule
            {
                Id = 1, Name = "Daily empress · 08:00 / 18:00",
                PromptTemplate = "the empress beneath a gold halo · {date} · cinematic",
                Workflow = "sdxl_text2img", Route = "comfyui",
                Aspect = AspectRatio.Wide, Camera = CamMode.Push,
                Kind = ScheduleKind.DailySlots, Spec = "08:00,18:00",
                IsEnabled = true,
                NextFireAt = DateTimeOffset.Now.AddHours(2),
            });
        }
    }

    private void Refresh()
    {
        if (_ctx is null) return;
        var selectedId = Selected?.Id;
        Schedules.Clear();
        foreach (var s in _ctx.Schedules.All()) Schedules.Add(s);
        RefreshRunNotes();
        // Keep the editor on the same schedule across a reload.
        if (selectedId is { } id) Selected = Schedules.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Each card's last result — including a failed auto-post, which
    /// used to be recorded only in a table no page showed.</summary>
    private void RefreshRunNotes()
    {
        if (_ctx is null) return;
        IReadOnlyDictionary<long, ScheduleRun> runs;
        try { runs = _ctx.Schedules.LatestRuns(); }
        catch { return; }
        foreach (var s in Schedules)
        {
            if (!runs.TryGetValue(s.Id, out var run)) { s.LastRunNote = ""; continue; }
            var when = run.FiredAt.ToLocalTime().ToString("d MMM HH:mm");
            s.LastRunNote = run.Status switch
            {
                "done" when !string.IsNullOrEmpty(run.ErrorMessage) => $"รอบล่าสุด {when} · เรนเดอร์เสร็จ · {run.ErrorMessage}",
                "done" => $"รอบล่าสุด {when} · สำเร็จ",
                "error" => $"รอบล่าสุด {when} · ล้มเหลว: {run.ErrorMessage}",
                _ => $"รอบล่าสุด {when} · กำลังทำ",
            };
        }
    }

    private void OnRunFinished(long scheduleId)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnRunFinished(scheduleId)));
            return;
        }
        RefreshRunNotes();
    }

    /// <summary>
    /// Listen for fires while the page is on screen.
    ///
    /// The scheduler works on its own copies of the rows. Without this the
    /// cards kept showing the pre-fire next/last times, and toggling a card
    /// afterwards wrote that stale next-fire back — which made the schedule
    /// fire (and auto-post) a second time.
    /// </summary>
    public void Reattach()
    {
        if (_ctx is null || _attached) return;
        _attached = true;
        _ctx.ScheduleService.ScheduleFired += OnScheduleFired;
        _ctx.ScheduleService.RunFinished += OnRunFinished;
        SyncFireTimes();

        _clock ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick -= OnClock;
        _clock.Tick += OnClock;
        _clock.Start();
    }

    /// <summary>
    /// Catch up on fires that happened while the page was away, touching only
    /// the times the scheduler owns — a full reload would throw away edits
    /// the user left unsaved in the editor.
    /// </summary>
    private void SyncFireTimes()
    {
        if (_ctx is null) return;
        IReadOnlyList<Schedule> saved;
        try { saved = _ctx.Schedules.All(); }
        catch { return; }
        foreach (var row in saved)
        {
            var shown = Schedules.FirstOrDefault(s => s.Id == row.Id);
            if (shown is null) { Schedules.Add(row); continue; }
            shown.LastFireAt = row.LastFireAt;
            shown.NextFireAt = row.NextFireAt;
            shown.RefreshTimeLabels();
        }
        RefreshRunNotes();
    }

    public void Detach()
    {
        if (_ctx is not null && _attached)
        {
            _ctx.ScheduleService.ScheduleFired -= OnScheduleFired;
            _ctx.ScheduleService.RunFinished -= OnRunFinished;
        }
        _attached = false;
        _clock?.Stop();
    }

    public void Dispose() => Detach();

    private void OnClock(object? sender, EventArgs e)
    {
        foreach (var s in Schedules) s.RefreshTimeLabels();
    }

    private void OnScheduleFired(Schedule fired)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnScheduleFired(fired)));
            return;
        }
        // Take the scheduler's values onto the object the page is showing,
        // rather than reloading and throwing away unsaved edits in the editor.
        var shown = Schedules.FirstOrDefault(s => s.Id == fired.Id);
        if (shown is null) { Refresh(); return; }
        shown.LastFireAt = fired.LastFireAt;
        shown.NextFireAt = fired.NextFireAt;
        shown.RefreshTimeLabels();
        RefreshRunNotes();
    }

    /// <summary>
    /// The card's switch has already flipped <see cref="Schedule.IsEnabled"/>
    /// through its binding; persist it, moving a stale next-fire forward so
    /// re-enabling does not fire at once for a slot that passed while it was
    /// off.
    /// </summary>
    public void ApplyEnabledChange(Schedule s)
    {
        if (_ctx is null) return;
        if (s.IsEnabled && (s.NextFireAt is null || s.NextFireAt <= DateTimeOffset.UtcNow))
            s.NextFireAt = s.ComputeNextFireAt(DateTimeOffset.UtcNow);
        _ctx.Schedules.Update(s);
        ShowToast($"{s.Name} · {s.EnabledLabel}");
    }

    private void AddNew()
    {
        var s = new Schedule
        {
            Name = "Daily empress",
            PromptTemplate = "the empress in moonlit chamber · {date} · cinematic gold halo · silk crimson veil",
            NegativePrompt = "blurry, low quality, watermark",
            Workflow = "sdxl_text2img",
            Route = "comfyui",
            StyleId = "empress",
            Aspect = AspectRatio.Wide,
            Camera = CamMode.Push,
            DurationSec = 8,
            Motion = 0.7,
            Kind = ScheduleKind.DailySlots,
            Spec = "08:00,18:00",
            IsEnabled = true,
        };
        s.NextFireAt = s.ComputeNextFireAt(DateTimeOffset.UtcNow);

        if (_ctx is null)
        {
            Schedules.Add(s);
            Selected = s;
            return;
        }

        _ctx.Schedules.Insert(s);
        Schedules.Add(s);
        Selected = s;
        ShowToast($"Created schedule · next fire {s.NextFireLabel}");
    }

    private void ToggleEnabled(Schedule? s)
    {
        if (s is null || _ctx is null) return;
        s.IsEnabled = !s.IsEnabled;
        // When re-enabling, recompute next fire from now if the stored
        // next-fire is null or already in the past — otherwise old expired
        // values would trigger immediately on the next 60s tick.
        if (s.IsEnabled && (s.NextFireAt is null || s.NextFireAt <= DateTimeOffset.UtcNow))
            s.NextFireAt = s.ComputeNextFireAt(DateTimeOffset.UtcNow);
        _ctx.Schedules.Update(s);
        ShowToast($"{s.Name} · {(s.IsEnabled ? "ON" : "OFF")}");
    }

    private void Save(Schedule? s)
    {
        if (s is null || _ctx is null) return;
        // A spec that parses to nothing used to save happily and then never
        // fire, with "next fire —" as the only clue.
        if (s.SpecProblem is { } problem)
        {
            ShowToast("ยังไม่ได้บันทึก — " + problem, sticky: true);
            return;
        }
        // Recompute next fire whenever the user edits Spec/Kind so the
        // change takes effect immediately rather than after the current
        // next_fire passes.
        s.NextFireAt = s.ComputeNextFireAt(DateTimeOffset.UtcNow);
        _ctx.Schedules.Update(s);
        ShowToast($"Saved · next fire {s.NextFireLabel}");
    }

    private void Delete(Schedule? s)
    {
        if (s is null || _ctx is null) return;
        _ctx.Schedules.Delete(s.Id);
        Schedules.Remove(s);
        if (ReferenceEquals(Selected, s)) Selected = null;
        ShowToast($"Deleted {s.Name}");
    }

    /// <summary>
    /// Fire once, now. Goes straight to the scheduler rather than faking a
    /// past next-fire and waiting for the due scan: that scan skips disabled
    /// rows, so "run now" on a fresh (disabled) clone toasted "Firing…" and
    /// did nothing — and left a past next-fire that fired again the moment
    /// the schedule was switched on. The regular cadence is left alone.
    /// </summary>
    private async void RunNow(Schedule? s)
    {
        if (s is null || _ctx is null) return;
        if (s.SpecProblem is { } problem) { ShowToast(problem, sticky: true); return; }
        ShowToast($"Firing {s.Name}…");
        try
        {
            var started = await _ctx.ScheduleService.RunNowAsync(s);
            if (!started) ShowToast($"{s.Name} is already being fired — wait for it to be submitted");
        }
        catch (Exception ex)
        {
            ShowToast($"Run now failed: {ex.Message}", sticky: true);
        }
    }

    /// <summary>Clone a schedule with " (copy)" appended to the name and
    /// IsEnabled flipped off — the user almost always wants to tweak the
    /// clone before letting it fire. (T55 · 7.21)</summary>
    private void Clone(Schedule? src)
    {
        if (src is null || _ctx is null) return;
        var copy = new Models.Schedule
        {
            Name = src.Name + " (copy)",
            PromptTemplate = src.PromptTemplate,
            NegativePrompt = src.NegativePrompt,
            Workflow = src.Workflow,
            Route = src.Route,
            StyleId = src.StyleId,
            Aspect = src.Aspect,
            Camera = src.Camera,
            DurationSec = src.DurationSec,
            Motion = src.Motion,
            Kind = src.Kind,
            Spec = src.Spec,
            AutoPost = src.AutoPost,
            PostTarget = src.PostTarget,
            PostCaption = src.PostCaption,
            IsEnabled = false,    // disabled so the clone doesn't fire on next tick
        };
        copy.NextFireAt = copy.ComputeNextFireAt(DateTimeOffset.UtcNow);
        _ctx.Schedules.Insert(copy);
        Schedules.Add(copy);
        Selected = copy;
        ShowToast($"Cloned · {copy.Name} (disabled — enable when ready)");
    }

    private void ShowToast(string msg, bool sticky = false)
    {
        Toast = msg;
        // Errors stay until the next message: a problem that vanishes after
        // three seconds is one the user may never see.
        if (sticky) return;
        // Self-clear via dispatcher delay rather than a Task.Delay because
        // ScheduleViewModel is unit-testable without a Dispatcher.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, e) => { if (Toast == msg) Toast = ""; timer.Stop(); };
        timer.Start();
    }
}
