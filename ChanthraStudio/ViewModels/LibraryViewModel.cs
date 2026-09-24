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

public sealed class LibraryViewModel : ObservableObject, IDisposable
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
    public IRelayCommand<string> SetLimitCommand { get; }

    /// <summary>"50" | "200" | "all" — how many recent clips to pull from
    /// the DB. The default "200" matches ClipsRepository's default; "all"
    /// caps at 1000 so we never realistically OOM the WrapPanel.</summary>
    private string _limitMode = "200";
    public string LimitMode
    {
        get => _limitMode;
        set
        {
            if (SetProperty(ref _limitMode, value)) Refresh();
        }
    }

    private int ResolveLimit() => _limitMode switch
    {
        "50"  => 50,
        "all" => 1000,
        _     => 200,
    };

    /// <summary>"all" | "image" | "video" — what kinds of clips to display.
    /// Filename extension is the source of truth; missing-on-disk clips
    /// fall through to "all" so dead refs stay visible for cleanup.</summary>
    private string _kindFilter = "all";
    public string KindFilter
    {
        get => _kindFilter;
        set { if (SetProperty(ref _kindFilter, value ?? "all")) ApplyFilter(); }
    }

    public IRelayCommand<string> SetKindFilterCommand { get; private set; } = null!;

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
        SetLimitCommand = new RelayCommand<string>(m => { if (!string.IsNullOrEmpty(m)) LimitMode = m!; });
        SetKindFilterCommand = new RelayCommand<string>(k => { if (!string.IsNullOrEmpty(k)) KindFilter = k!; });

        // Auto-refresh whenever a generation completes + re-filter on search.
        // Named handlers (not lambdas) so Dispose() can detach them — the
        // ViewSwitcher recreates this VM on every Library visit, but
        // Generation + Search are long-lived singletons that would otherwise
        // keep a zombie VM alive firing Refresh()/ApplyFilter() forever.
        _ctx.Generation.ProgressChanged += OnGenerationProgress;
        _ctx.Search.PropertyChanged += OnSearchChanged;

        Refresh();
    }

    private void OnGenerationProgress(object? sender, GenerationProgressEventArgs e)
    {
        if (e.Status == ShotStatus.Done) Refresh();
    }

    private void OnSearchChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyFilter();

    /// <summary>Detach from singletons + per-clip handlers. Called from
    /// LibraryView.Unloaded so navigating away doesn't leave a live VM.</summary>
    public void Dispose()
    {
        _ctx.Generation.ProgressChanged -= OnGenerationProgress;
        _ctx.Search.PropertyChanged -= OnSearchChanged;
        foreach (var c in _allClips) c.PropertyChanged -= OnClipPropertyChanged;
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

        var rows = _ctx.Clips.RecentClips(ResolveLimit());
        foreach (var c in rows)
        {
            if (prevSelected.Contains(c.Id)) c.IsSelected = true;
            c.PropertyChanged += OnClipPropertyChanged;
            _allClips.Add(c);
        }
        ApplyFilter();
        // Video cards have nothing to draw until a still is pulled out of them.
        _ctx.Posters.EnsurePosters(_allClips);
    }

    /// <summary>Project <see cref="_allClips"/> through the SearchBus query
    /// AND the KindFilter into the visible <see cref="Clips"/> collection.
    /// Match is a case-insensitive substring against the clip's file name
    /// (no prompt-history table). Kind is decided by extension.</summary>
    private void ApplyFilter()
    {
        var q = _ctx.Search.Query.Trim();
        Clips.Clear();
        foreach (var c in _allClips)
        {
            if (!string.IsNullOrEmpty(q) && !c.FileName.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!MatchesKind(c.FileName)) continue;
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

    private bool MatchesKind(string fileName)
    {
        if (_kindFilter == "all") return true;
        // One classifier for the whole app (MediaKind): the chips used their
        // own lists, and .ts / .mpg / .wmv fell into neither.
        return _kindFilter switch
        {
            "image" => MediaKind.IsImage(fileName),
            "video" => MediaKind.IsVideo(fileName),
            _       => true,
        };
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
        var ok = System.Windows.MessageBox.Show(
            $"ลบ {clip.FileName} และไฟล์บนดิสก์?",
            "ยืนยันการลบ",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.OK) return;
        try
        {
            // The file goes first, and the row only if it did: a locked file
            // (open in the editor preview, or being rendered) used to lose its
            // row anyway and sit on disk where the app could no longer see it.
            if (File.Exists(clip.FilePath))
            {
                try { File.Delete(clip.FilePath); }
                catch (Exception ex)
                {
                    ShowToast($"ลบไม่ได้ — ไฟล์ถูกเปิดใช้อยู่ ({ex.Message})", "err");
                    return;
                }
            }
            if (!string.IsNullOrEmpty(clip.PosterPath))
            {
                try { if (File.Exists(clip.PosterPath)) File.Delete(clip.PosterPath); } catch { /* cache file */ }
            }
            PostingService.ForgetCaption(clip.Id);
            _ctx.Clips.DeleteClip(clip.Id);
            Clips.Remove(clip);
            _allClips.Remove(clip);
            HasClips = Clips.Count > 0;
            ShowToast($"ลบ {clip.FileName} แล้ว", "ok");
        }
        catch (Exception ex) { ShowToast($"ลบไม่สำเร็จ: {ex.Message}", "err"); }
    }

    /// <summary>Confirm-then-delete every currently-checked clip. Files come
    /// off disk first (best-effort), then DB rows, then in-memory state.
    /// Faster than clicking through each clip's context menu when triaging
    /// a large generation batch.</summary>
    private void DeleteSelected()
    {
        var victims = Clips.Where(c => c.IsSelected).ToList();
        if (victims.Count == 0) { ShowToast("ยังไม่ได้เลือกคลิป", "warn"); return; }
        var ok = System.Windows.MessageBox.Show(
            $"ลบ {victims.Count} คลิปที่เลือก พร้อมไฟล์บนดิสก์?",
            "ยืนยันการลบหลายคลิป",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.OK) return;

        int deleted = 0, locked = 0;
        foreach (var clip in victims)
        {
            try
            {
                // Same rule as the single delete: the row goes only with its
                // file. A locked file used to lose its row and linger on disk
                // where the Library could no longer show it.
                if (File.Exists(clip.FilePath))
                {
                    try { File.Delete(clip.FilePath); }
                    catch { locked++; continue; }
                }
                if (!string.IsNullOrEmpty(clip.PosterPath))
                {
                    try { if (File.Exists(clip.PosterPath)) File.Delete(clip.PosterPath); } catch { /* cache file */ }
                }
                PostingService.ForgetCaption(clip.Id);
                _ctx.Clips.DeleteClip(clip.Id);
                _allClips.Remove(clip);
                Clips.Remove(clip);
                deleted++;
            }
            catch { /* per-clip best effort — keep deleting the rest */ }
        }
        HasClips = Clips.Count > 0;
        UpdateSelectionState();
        ShowToast(locked == 0
            ? $"ลบแล้ว {deleted} คลิป"
            : $"ลบแล้ว {deleted} คลิป · อีก {locked} คลิปลบไม่ได้เพราะไฟล์ถูกเปิดใช้อยู่", locked == 0 ? "ok" : "warn");
    }

    private async System.Threading.Tasks.Task RenderFilmAsync()
    {
        var selected = Clips.Where(c => c.IsSelected && c.FileExists).ToList();
        if (selected.Count == 0)
        {
            ShowToast("No clips selected.", "warn");
            return;
        }

        var defaultName = $"film_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
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
        // Live render-pill — SlideshowRenderer emits "Rendering · 41% · ~12s left"
        // lines from ffmpeg progress; surface them through the same toast.
        var progress = new Progress<string>(line =>
        {
            ToastMessage = line;
            ToastKind = "info";
        });
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

        ShowToast($"กำลังโพสต์ไป {dialog.ProviderId}…", "info");
        var result = await _ctx.Posting.PostAsync(dialog.ProviderId, clip, dialog.Caption);
        if (result.Ok)
        {
            var idHint = string.IsNullOrEmpty(result.PostId) ? "" : $" · {result.PostId}";
            ShowToast($"โพสต์แล้ว ✓{idHint}", "ok");
        }
        else
        {
            ShowToast($"โพสต์ไม่สำเร็จ: {result.Error}", "err");
        }
    }

    private static string BuildDefaultCaption(Clip clip)
    {
        // A post that failed earlier (Auto Pilot, a schedule, or here) left
        // its caption behind — retrying should not lose the hashtags.
        var pending = PostingService.PendingCaption(clip.Id);
        if (!string.IsNullOrWhiteSpace(pending)) return pending;

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
            await System.Threading.Tasks.Task.Delay(kind switch { "err" => 15000, "warn" => 6000, _ => 2800 });  // errors stay long enough to read
            if (ToastMessage == message) ToastMessage = null;
        }
        catch { }
    }
}
