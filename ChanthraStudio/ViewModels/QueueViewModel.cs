using System;
using System.Collections.ObjectModel;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class QueueViewModel : ObservableObject
{
    private readonly StudioContext _ctx;

    public ObservableCollection<GenerationJob> Jobs { get; } = new();

    private string _summary = "";
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    private bool _hasJobs;
    public bool HasJobs { get => _hasJobs; set => SetProperty(ref _hasJobs, value); }

    public IRelayCommand RefreshCommand { get; }

    public QueueViewModel(StudioContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new RelayCommand(Refresh);

        // Refresh on terminal events only. Subscribing to every progress frame
        // (multiple per second) was Clear()-ing + reloading the ItemsControl
        // continuously, collapsing user scroll position and flickering pills.
        // The queue rows users care about are status transitions, not the
        // 0–100 progress bar (that lives on the Generate-view shot card).
        _ctx.Generation.ProgressChanged += (_, e) =>
        {
            if (e.Status == ShotStatus.Done
                || e.Status == ShotStatus.Error
                || e.Status == ShotStatus.Queue)
            {
                Refresh();
            }
        };

        Refresh();
    }

    public void Refresh()
    {
        Jobs.Clear();
        var rows = _ctx.Clips.RecentJobs();
        foreach (var j in rows) Jobs.Add(j);
        HasJobs = Jobs.Count > 0;

        var running = Jobs.Count(j => j.Status == "queued" || j.Status == "running");
        var done = Jobs.Count(j => j.Status == "done");
        var error = Jobs.Count(j => j.Status == "error");
        Summary = $"{Jobs.Count} jobs · {running} active · {done} done · {error} failed";
    }
}
