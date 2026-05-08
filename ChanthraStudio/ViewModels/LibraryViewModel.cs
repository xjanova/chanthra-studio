using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class LibraryViewModel : ObservableObject
{
    private readonly StudioContext _ctx;

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

    public LibraryViewModel(StudioContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new RelayCommand(Refresh);
        OpenClipCommand = new RelayCommand<Clip>(OpenClip);
        RevealCommand = new RelayCommand<Clip>(RevealInExplorer);
        CopyPathCommand = new RelayCommand<Clip>(CopyPath);
        DeleteClipCommand = new RelayCommand<Clip>(DeleteClip);
        PostClipCommand = new AsyncRelayCommand<Clip>(PostClipAsync);

        // Auto-refresh whenever a generation completes — the ProgressChanged
        // event fires on the UI thread already (GenerationService dispatches).
        _ctx.Generation.ProgressChanged += (_, e) =>
        {
            if (e.Status == ShotStatus.Done) Refresh();
        };

        Refresh();
    }

    public void Refresh()
    {
        Clips.Clear();
        var rows = _ctx.Clips.RecentClips();
        foreach (var c in rows) Clips.Add(c);
        HasClips = Clips.Count > 0;
        Summary = HasClips
            ? $"{Clips.Count} clip{(Clips.Count == 1 ? "" : "s")} · {AppPaths.MediaFolder}"
            : $"No clips yet · {AppPaths.MediaFolder}";
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
            HasClips = Clips.Count > 0;
            ShowToast($"Deleted {clip.FileName}", "ok");
        }
        catch (Exception ex) { ShowToast($"Delete failed: {ex.Message}", "err"); }
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
