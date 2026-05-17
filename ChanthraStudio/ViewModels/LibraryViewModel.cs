using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class LibraryViewModel : ObservableObject
{
    private readonly StudioContext _ctx;

    /// <summary>Full unfiltered list — Refresh() repopulates this from the DB,
    /// then <see cref="ApplyFilter"/> projects the visible subset into
    /// <see cref="Clips"/> based on the SearchBus query.</summary>
    private readonly System.Collections.Generic.List<Clip> _allClips = new();

    public ObservableCollection<Clip> Clips { get; } = new();

    private bool _hasClips;
    public bool HasClips { get => _hasClips; set => SetProperty(ref _hasClips, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    private string? _toastMessage;
    public string? ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }

    private string _toastKind = "info";
    public string ToastKind { get => _toastKind; set => SetProperty(ref _toastKind, value); }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand<Clip> OpenClipCommand { get; }
    public IRelayCommand<Clip> RevealCommand { get; }
    public IRelayCommand<Clip> CopyPathCommand { get; }
    public IRelayCommand<Clip> DeleteClipCommand { get; }
    public IAsyncRelayCommand<Clip> PostClipCommand { get; }
    public IAsyncRelayCommand RenderFilmCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand DeleteSelectedCommand { get; }
    public IRelayCommand SelectAllCommand { get; }

    private int _selectedCount;
    public int SelectedCount { get => _selectedCount; set => SetProperty(ref _selectedCount, value); }

    private bool _hasSelection;
    public bool HasSelection { get => _hasSelection; set => SetProperty(ref _hasSelection, value); }

    public LibraryViewModel(StudioContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new RelayCommand(Refresh);
        OpenClipCommand = new RelayCommand<Clip>(OpenClip);
        RevealCommand = new RelayCommand<Clip>(RevealInExplorer);
        CopyPathCommand = new RelayCommand<Clip>(CopyPath);
        DeleteClipCommand = new RelayCommand<Clip>(DeleteClip);
        PostClipCommand = new AsyncRelayCommand<Clip>(PostClipAsync);
        RenderFilmCommand = new AsyncRelayCommand(RenderFilmAsync);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (var c in Clips) c.IsSelected = false;
            UpdateSelectionState();
        });
        SelectAllCommand = new RelayCommand(() =>
        {
            foreach (var c in Clips) c.IsSelected = true;
            UpdateSelectionState();
        });
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);

        // Auto-refresh whenever a generation completes — the ProgressChanged
        // event fires on the UI thread already (GenerationService dispatches).
        _ctx.Generation.ProgressChanged += (_, e) =>
        {
            if (e.Status == ShotStatus.Done) Refresh();
        };

        // Title-bar search box re-filters the visible clips live.
        _ctx.Search.PropertyChanged += (_, _) => ApplyFilter();

        Refresh();
    }

    public void Refresh()
    {
        // Capture which clip ids the user had checked — IsSelected lives on
        // the in-memory Clip instances we're about to discard. Without this
        // restore-after-rebuild step a generation finishing mid-batch would
        // wipe whatever the user had marked for "Render film".
        var prevSelected = new HashSet<string>(
            Clips.Where(c => c.IsSelected).Select(c => c.Id));

        // Detach old clips' PropertyChanged handlers before discarding them.
        foreach (var old in _allClips) old.PropertyChanged -= OnClipPropertyChanged;
        _allClips.Clear();

        var rows = _ctx.Clips.RecentClips();
        foreach (var c in rows)
        {
            if (prevSelected.Contains(c.Id)) c.IsSelected = true;
            c.PropertyChanged += OnClipPropertyChanged;
            _allClips.Add(c);
        }
        ApplyFilter();
    }

    /// <summary>Project <see cref="_allClips"/> through the SearchBus query
    /// into the visible <see cref="Clips"/> collection. Match is a case-
    /// insensitive substring against the clip's file name — that's all we
    /// have to filter on without a prompt-history table.</summary>
    private void ApplyFilter()
    {
        var q = _ctx.Search.Query.Trim();
        Clips.Clear();
        foreach (var c in _allClips)
        {
            if (string.IsNullOrEmpty(q) || c.FileName.Contains(q, StringComparison.OrdinalIgnoreCase))
                Clips.Add(c);
        }
        HasClips = Clips.Count > 0;
        var suffix = _allClips.Count == Clips.Count
            ? ""
            : $" of {_allClips.Count}";
        Summary = _allClips.Count == 0
            ? $"No clips yet · {AppPaths.MediaFolder}"
            : $"{Clips.Count}{suffix} clip{(Clips.Count == 1 ? "" : "s")} · {AppPaths.MediaFolder}";
        UpdateSelectionState();
    }

    private void OnClipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Clip.IsSelected)) UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = Clips.Count(c => c.IsSelected);
        HasSelection = SelectedCount > 0;
    }

    private void OpenClip(Clip? clip)
    {
        if (clip is null || !clip.FileExists) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = clip.FilePath, UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast($"Open failed: {ex.Message}", "err"); }
    }

    private void RevealInExplorer(Clip? clip)
    {
        if (clip is null) return;
        try
        {
            // /select, opens Explorer with the file highlighted.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{clip.FilePath}\""));
        }
        catch (Exception ex) { ShowToast($"Reveal failed: {ex.Message}", "err"); }
    }

    private void CopyPath(Clip? clip)
    {
        if (clip is null) return;
        try
        {
            System.Windows.Clipboard.SetText(clip.FilePath);
            ShowToast("Path copied to clipboard", "ok");
        }
        catch (Exception ex) { ShowToast($"Copy failed: {ex.Message}", "err"); }
    }

    private void DeleteClip(Clip? clip)
    {
        if (clip is null) return;
        try
        {
            // Best-effort: drop the file (if present) + the DB row. We don't
            // confirm — the toast undo is on the wishlist for a later phase.
            if (File.Exists(clip.FilePath))
            {
                try { File.Delete(clip.FilePath); } catch { /* read-only / locked, leave it */ }
            }
            _ctx.Clips.DeleteClip(clip.Id);
            Clips.Remove(clip);
            _allClips.Remove(clip);
            HasClips = Clips.Count > 0;
            ShowToast($"Deleted {clip.FileName}", "ok");
        }
        catch (Exception ex) { ShowToast($"Delete failed: {ex.Message}", "err"); }
    }

    /// <summary>Confirm-then-delete every currently-checked clip. Files come
    /// off disk first (best-effort), then DB rows, then in-memory state.
    /// Faster than clicking through each clip's context menu when triaging
    /// a large generation batch.</summary>
    private void DeleteSelected()
    {
        var victims = Clips.Where(c => c.IsSelected).ToList();
        if (victims.Count == 0) { ShowToast("No clips selected.", "warn"); return; }
        var ok = System.Windows.MessageBox.Show(
            $"Delete {victims.Count} selected clip{(victims.Count == 1 ? "" : "s")} and their files?",
            "Confirm bulk delete",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.OK) return;

        int filesDropped = 0;
        foreach (var clip in victims)
        {
            try
            {
                if (File.Exists(clip.FilePath))
                {
                    try { File.Delete(clip.FilePath); filesDropped++; }
                    catch { /* locked / read-only — DB row still goes */ }
                }
                _ctx.Clips.DeleteClip(clip.Id);
                _allClips.Remove(clip);
                Clips.Remove(clip);
            }
            catch { /* per-clip best effort — keep deleting the rest */ }
        }
        HasClips = Clips.Count > 0;
        UpdateSelectionState();
        ShowToast($"Deleted {victims.Count} · {filesDropped} files removed from disk", "ok");
    }

    private async System.Threading.Tasks.Task RenderFilmAsync()
    {
        var selected = Clips.Where(c => c.IsSelected && c.FileExists).ToList();
        if (selected.Count == 0)
        {
            ShowToast("No clips selected.", "warn");
            return;
        }

        var defaultName = $"film_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var dialog = new Views.Dialogs.RenderFilmDialog(_ctx, selected.Count, defaultName)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
        if (!dialog.Confirmed) return;

        ShowToast("Rendering film…", "info");
        var spec = new SlideshowRenderer.Spec
        {
            Clips = selected,
            SecondsPerClip = dialog.SecondsPerClip,
            Fps = dialog.Fps,
            OutputName = dialog.OutputName,
            AudioPath = string.IsNullOrEmpty(dialog.AudioPath) ? null : dialog.AudioPath,
            AudioVolume = dialog.AudioVolume,
        };
        var progress = new Progress<string>(line => { /* could surface frame counts later */ });
        var result = await _ctx.SlideshowRenderer.RenderAsync(spec, progress);
        if (!result.Ok)
        {
            ShowToast(result.Error ?? "render failed", "err");
            return;
        }

        ShowToast($"Film rendered · {System.IO.Path.GetFileName(result.OutputPath)}", "ok");
        // Clear selection + reload so the new MP4 shows up at the top.
        foreach (var c in Clips) c.IsSelected = false;
        Refresh();
    }

    private async System.Threading.Tasks.Task PostClipAsync(Clip? clip)
    {
        if (clip is null) return;
        if (!clip.FileExists)
        {
            ShowToast("File missing on disk — can't post a deleted clip.", "err");
            return;
        }
        var dialog = new Views.Dialogs.PostDialog(_ctx, clip.FileName, BuildDefaultCaption(clip))
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
        if (!dialog.Confirmed || string.IsNullOrEmpty(dialog.ProviderId)) return;

        ShowToast($"Posting to {dialog.ProviderId}…", "info");
        var result = await _ctx.Posting.PostAsync(dialog.ProviderId, clip, dialog.Caption);
        if (result.Ok)
        {
            var idHint = string.IsNullOrEmpty(result.PostId) ? "" : $" · {result.PostId}";
            ShowToast($"Posted ✓{idHint}", "ok");
        }
        else
        {
            ShowToast($"Post failed: {result.Error}", "err");
        }
    }

    private static string BuildDefaultCaption(Clip clip)
    {
        // Sensible starter — user almost always edits this. Includes the
        // brand mark + the shot id so multi-shot threads stay traceable.
        return $"✦ {System.IO.Path.GetFileNameWithoutExtension(clip.FileName)}";
    }

    private async void ShowToast(string message, string kind)
    {
        ToastMessage = message;
        ToastKind = kind;
        try
        {
            await System.Threading.Tasks.Task.Delay(2800);
            if (ToastMessage == message) ToastMessage = null;
        }
        catch { }
    }
}
