using System;
using System.Collections.ObjectModel;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class ScheduleViewModel : ObservableObject
{
    private readonly StudioContext? _ctx;

    public ObservableCollection<Schedule> Schedules { get; } = new();

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

    public ScheduleViewModel() : this(null) { }

    public ScheduleViewModel(StudioContext? ctx)
    {
        _ctx = ctx;

        RefreshCommand = new RelayCommand(Refresh);
        AddCommand = new RelayCommand(AddNew);
        ToggleEnabledCommand = new RelayCommand<Schedule>(ToggleEnabled);
        SaveCommand = new RelayCommand<Schedule>(Save);
        DeleteCommand = new RelayCommand<Schedule>(Delete);
        RunNowCommand = new RelayCommand<Schedule>(RunNow);

        if (_ctx is not null)
        {
            foreach (var w in _ctx.Workflows.All) AvailableWorkflows.Add(w.Name);
            Refresh();
        }
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
        Schedules.Clear();
        foreach (var s in _ctx.Schedules.All()) Schedules.Add(s);
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

    /// <summary>Force a fire right now — bumps next_fire_at to now so the
    /// next ScheduleService tick picks it up. Useful for testing without
    /// waiting an hour.</summary>
    private void RunNow(Schedule? s)
    {
        if (s is null || _ctx is null) return;
        s.NextFireAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        _ctx.Schedules.Update(s);
        ShowToast($"Forced · {s.Name} runs on next 60s tick");
    }

    private void ShowToast(string msg)
    {
        Toast = msg;
        // Self-clear via dispatcher delay rather than a Task.Delay because
        // ScheduleViewModel is unit-testable without a Dispatcher.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, e) => { if (Toast == msg) Toast = ""; timer.Stop(); };
        timer.Start();
    }
}
