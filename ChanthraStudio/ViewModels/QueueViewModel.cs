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

        // Live update: any progress change refreshes the queue. Cheap because
        // it's just a SELECT — a few hundred rows max.
        _ctx.Generation.ProgressChanged += (_, _) => Refresh();

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
