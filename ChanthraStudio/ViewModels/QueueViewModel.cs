using System;
using System.Collections.ObjectModel;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class QueueViewModel : ObservableObject, IDisposable
{
    private readonly StudioContext _ctx;

    public ObservableCollection<GenerationJob> Jobs { get; } = new();

    private string _summary = "";
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    private bool _hasJobs;
    public bool HasJobs { get => _hasJobs; set => SetProperty(ref _hasJobs, value); }

    public IRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<GenerationJob> CancelJobCommand { get; }

    public QueueViewModel(StudioContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new RelayCommand(Refresh);
        CancelJobCommand = new AsyncRelayCommand<GenerationJob>(CancelJobAsync);

        // Refresh on terminal events only. Subscribing to every progress frame
        // (multiple per second) was Clear()-ing + reloading the ItemsControl
        // continuously, collapsing user scroll position and flickering pills.
        // The queue rows users care about are status transitions, not the
        // 0–100 progress bar (that lives on the Generate-view shot card).
        // Named handler (not a lambda) so Dispose() can detach it — the
        // ViewSwitcher recreates this VM on every Queue visit while
        // Generation is a long-lived singleton.
        _ctx.Generation.ProgressChanged += OnGenerationProgress;

        Refresh();
    }

    private void OnGenerationProgress(object? sender, GenerationProgressEventArgs e)
    {
        if (e.Status == ShotStatus.Done
            || e.Status == ShotStatus.Error
            || e.Status == ShotStatus.Queue)
        {
            Refresh();
        }
    }

    /// <summary>Detach from the Generation singleton. Called from
    /// QueueView.Unloaded so navigating away doesn't leak the VM.</summary>
    public void Dispose() => _ctx.Generation.ProgressChanged -= OnGenerationProgress;

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

    /// <summary>Cancel an in-flight job. Only meaningful for queued/running
    /// statuses — the GenerationService's _running map has the matching
    /// CancellationTokenSource and (for ComfyUI) ALSO fires /interrupt to
    /// stop the GPU mid-step.</summary>
    private async System.Threading.Tasks.Task CancelJobAsync(GenerationJob? job)
    {
        if (job is null) return;
        if (job.Status != "queued" && job.Status != "running") return;
        await _ctx.Generation.CancelAsync(job.Id);
        Refresh();
    }
}
