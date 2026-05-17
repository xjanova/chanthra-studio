using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// One slot on the NLE-lite timeline. Wraps a <see cref="Clip"/> with a
/// per-slot duration override — the NLE's whole point over the simpler
/// RenderFilm dialog is that each slot can hold the screen for a
/// different number of seconds.
/// </summary>
/// <summary>
/// One slot on the overlay (B-roll) track. Unlike TimelineSlot — which
/// is sequential — overlay slots live at an absolute timeline position,
/// so each carries its own StartSec into the master timeline. Multiple
/// overlays can coexist (think: date stamp + tarot card + signature image
/// at three different moments in a 30-second clip).
/// </summary>
public sealed class OverlaySlot : ObservableObject
{
    public Clip Clip { get; init; } = null!;

    private double _startSec;
    public double StartSec { get => _startSec; set => SetProperty(ref _startSec, Math.Max(0, value)); }

    private double _durationSec = 4.0;
    public double DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, Math.Max(0.5, value)); }

    private double _scale = 0.3;
    public double Scale { get => _scale; set => SetProperty(ref _scale, Math.Clamp(value, 0.1, 0.6)); }

    private string _position = "TR";
    public string Position { get => _position; set => SetProperty(ref _position, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public string FileName => Clip.FileName;
    public string FilePath => Clip.FilePath;
    public string EndLabel => $"{StartSec:F1}s → {(StartSec + DurationSec):F1}s · {Position} · {Scale:P0}";
}

public sealed class TimelineSlot : ObservableObject
{
    public Clip Clip { get; init; } = null!;

    private double _durationSec = 3.0;
    public double DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, value); }

    private bool _isSelected;
    /// <summary>True when this slot is the inspector's focus. EditorViewModel
    /// keeps this in sync with its own <c>Selected</c> pointer so the
    /// timeline card can highlight via DataTrigger.</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private bool _isDropTarget;
    /// <summary>True while another slot is being dragged AND the cursor is
    /// over this slot — the timeline shows a gold vertical bar on the left
    /// edge so the user can preview where the drop will land.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => SetProperty(ref _isDropTarget, value); }

    public string FileName => Clip.FileName;
    public string FilePath => Clip.FilePath;
}

/// <summary>
/// NLE-lite. One video track, ordered list of clips with per-clip
/// durations, one optional audio track. Render goes through the existing
/// SlideshowRenderer (which gained <c>Spec.ClipDurations</c> in 7.7) so
/// we don't duplicate the ffmpeg-arg-building logic.
///
/// Drag-and-drop reorder isn't here yet — the up/down + remove buttons
/// give the same outcome with less infra. Drag-drop is a polish pass.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private readonly StudioContext? _ctx;

    public ObservableCollection<Clip> LibraryClips { get; } = new();
    public ObservableCollection<TimelineSlot> Timeline { get; } = new();
    public ObservableCollection<VoiceTake> AudioTakes { get; } = new();

    private TimelineSlot? _selected;
    public TimelineSlot? Selected
    {
        get => _selected;
        set
        {
            var prev = _selected;
            if (SetProperty(ref _selected, value))
            {
                if (prev is not null) prev.IsSelected = false;
                if (_selected is not null) _selected.IsSelected = true;
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }
    public bool HasSelection => _selected is not null;

    private string _outputName = $"film_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
    public string OutputName { get => _outputName; set => SetProperty(ref _outputName, value); }

    private string _audioPath = "";
    /// <summary>Local path to the optional soundtrack. Empty = silent film.</summary>
    public string AudioPath
    {
        get => _audioPath;
        set
        {
            if (SetProperty(ref _audioPath, value))
                OnPropertyChanged(nameof(AudioFileName));
        }
    }
    public string AudioFileName => string.IsNullOrEmpty(_audioPath)
        ? "(silent — no audio track)"
        : System.IO.Path.GetFileName(_audioPath);

    private double _audioVolume = 1.0;
    public double AudioVolume { get => _audioVolume; set => SetProperty(ref _audioVolume, value); }

    private int _fps = 30;
    public int Fps { get => _fps; set => SetProperty(ref _fps, value); }

    /// <summary>Output aspect — drives the renderer's Width/Height + the
    /// crop/letterbox math. Cannonical labels: "16:9" "9:16" "1:1" "21:9".
    /// "16:9" is the legacy default.</summary>
    private string _renderAspect = "16:9";
    public string RenderAspect { get => _renderAspect; set => SetProperty(ref _renderAspect, value); }

    /// <summary>Quality preset. "draft" (1280x720, crf 24, ultrafast),
    /// "full" (1920x1080, crf 20, fast), "hi" (3840x2160, crf 18, slow).
    /// Maps to renderer Width/Height/Fps internally.</summary>
    private string _renderQuality = "full";
    public string RenderQuality { get => _renderQuality; set => SetProperty(ref _renderQuality, value); }

    public IRelayCommand<string> SetRenderAspectCommand { get; private set; } = null!;
    public IRelayCommand<string> SetRenderQualityCommand { get; private set; } = null!;

    private double _crossfadeSec;
    /// <summary>Crossfade duration between adjacent slots, 0 = hard cut.
    /// Passed through to SlideshowRenderer.Spec.CrossfadeSec.</summary>
    public double CrossfadeSec { get => _crossfadeSec; set => SetProperty(ref _crossfadeSec, value); }

    // ---------- Playhead / scrubber (T23) ----------
    private double _playheadSec;
    /// <summary>Current playhead position in seconds from timeline start.
    /// Drives the scrubber bar and the razor-at-playhead command.</summary>
    public double PlayheadSec
    {
        get => _playheadSec;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, TotalDuration));
            if (!SetProperty(ref _playheadSec, clamped)) return;
            OnPropertyChanged(nameof(SlotAtPlayhead));
            OnPropertyChanged(nameof(PlayheadLabel));
            // Promote the slot under the playhead to Selected so the inspector
            // tracks the scrubber.
            var s = SlotAtPlayhead;
            if (s is not null && !ReferenceEquals(Selected, s)) Selected = s;
        }
    }

    public string PlayheadLabel
    {
        get
        {
            var s = (int)System.Math.Floor(_playheadSec);
            var frac = (int)System.Math.Round((_playheadSec - s) * 100);
            return $"{s / 60:D1}:{s % 60:D2}.{frac:D2}";
        }
    }

    /// <summary>Which timeline slot currently contains the playhead (i.e.
    /// the cumulative duration window the playhead falls into). Null if
    /// the timeline is empty.</summary>
    public TimelineSlot? SlotAtPlayhead
    {
        get
        {
            double cursor = 0;
            foreach (var s in Timeline)
            {
                if (_playheadSec < cursor + s.DurationSec) return s;
                cursor += s.DurationSec;
            }
            return Timeline.LastOrDefault();
        }
    }

    /// <summary>Offset within the SlotAtPlayhead where the playhead currently
    /// sits, in seconds. Used by razor-at-playhead to know where in the
    /// slot to cut.</summary>
    public double PlayheadOffsetWithinSlot
    {
        get
        {
            double cursor = 0;
            foreach (var s in Timeline)
            {
                if (_playheadSec < cursor + s.DurationSec) return _playheadSec - cursor;
                cursor += s.DurationSec;
            }
            return 0;
        }
    }

    // ---------- Overlay track (T13 → multi-slot in 7.9 T22) ----------
    /// <summary>Every overlay (B-roll) clip layered onto the master timeline.
    /// Multi-slot replacement for the single OverlayClip from 7.7. Each
    /// slot has its own start time, so overlays can fire at different
    /// moments instead of forcing all of them to share one window.</summary>
    public ObservableCollection<OverlaySlot> OverlayTimeline { get; } = new();

    private OverlaySlot? _selectedOverlay;
    public OverlaySlot? SelectedOverlay
    {
        get => _selectedOverlay;
        set
        {
            var prev = _selectedOverlay;
            if (SetProperty(ref _selectedOverlay, value))
            {
                if (prev is not null) prev.IsSelected = false;
                if (_selectedOverlay is not null) _selectedOverlay.IsSelected = true;
                OnPropertyChanged(nameof(HasOverlay));
                OnPropertyChanged(nameof(HasSelectedOverlay));
            }
        }
    }

    public bool HasOverlay => OverlayTimeline.Count > 0;
    public bool HasSelectedOverlay => _selectedOverlay is not null;

    public IRelayCommand<Clip> AddOverlayFromLibraryCommand { get; private set; } = null!;
    public IRelayCommand<OverlaySlot> RemoveOverlayCommand { get; private set; } = null!;
    public IRelayCommand<OverlaySlot> SelectOverlayCommand { get; private set; } = null!;
    public IRelayCommand<string> SetSelectedOverlayPositionCommand { get; private set; } = null!;

    private double _totalDuration;
    /// <summary>Sum of all slot durations — drives the "0:32 total" stamp.</summary>
    public double TotalDuration { get => _totalDuration; set => SetProperty(ref _totalDuration, value); }

    public string TotalDurationLabel
    {
        get
        {
            var s = (int)System.Math.Round(_totalDuration);
            return $"{s / 60:D1}:{s % 60:D2}";
        }
    }

    private string _toastMessage = "";
    public string ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }

    private string _toastKind = "info";
    public string ToastKind { get => _toastKind; set => SetProperty(ref _toastKind, value); }

    private bool _isRendering;
    public bool IsRendering { get => _isRendering; set => SetProperty(ref _isRendering, value); }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand<Clip> AddClipCommand { get; }
    public IRelayCommand<TimelineSlot> RemoveSlotCommand { get; }
    public IRelayCommand<TimelineSlot> MoveLeftCommand { get; }
    public IRelayCommand<TimelineSlot> MoveRightCommand { get; }
    public IRelayCommand<TimelineSlot> SelectSlotCommand { get; }
    public IRelayCommand<TimelineSlot> SplitSlotCommand { get; }
    public IRelayCommand<TimelineSlot> DuplicateSlotCommand { get; }
    public IRelayCommand RazorAtPlayheadCommand { get; }
    public IRelayCommand UndoCommand { get; private set; } = null!;
    public IRelayCommand RedoCommand { get; private set; } = null!;
    public IRelayCommand SaveProjectCommand { get; private set; } = null!;
    public IRelayCommand LoadProjectCommand { get; private set; } = null!;
    public IRelayCommand DeleteSelectedSlotCommand { get; private set; } = null!;
    public IRelayCommand NavSelectedLeftCommand { get; private set; } = null!;
    public IRelayCommand NavSelectedRightCommand { get; private set; } = null!;
    public IRelayCommand NudgePlayheadBackCommand { get; private set; } = null!;
    public IRelayCommand NudgePlayheadForwardCommand { get; private set; } = null!;
    public IRelayCommand ClearTimelineCommand { get; }
    public IRelayCommand BrowseAudioCommand { get; }
    public IRelayCommand ClearAudioCommand { get; }
    public IRelayCommand<VoiceTake> PickTakeCommand { get; }
    public IAsyncRelayCommand RenderCommand { get; }

    public EditorViewModel() : this(null) { }

    public EditorViewModel(StudioContext? ctx)
    {
        _ctx = ctx;

        RefreshCommand = new RelayCommand(Refresh);
        AddClipCommand = new RelayCommand<Clip>(AddClip);
        RemoveSlotCommand = new RelayCommand<TimelineSlot>(RemoveSlot);
        MoveLeftCommand = new RelayCommand<TimelineSlot>(s => MoveSlot(s, -1));
        MoveRightCommand = new RelayCommand<TimelineSlot>(s => MoveSlot(s, +1));
        SelectSlotCommand = new RelayCommand<TimelineSlot>(s => Selected = s);
        SplitSlotCommand = new RelayCommand<TimelineSlot>(SplitSlot);
        DuplicateSlotCommand = new RelayCommand<TimelineSlot>(DuplicateSlot);
        RazorAtPlayheadCommand = new RelayCommand(RazorAtPlayhead);
        UndoCommand = new RelayCommand(Undo, () => _undoStack.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redoStack.Count > 0);
        SaveProjectCommand = new RelayCommand(SaveProject);
        LoadProjectCommand = new RelayCommand(LoadProject);
        SetRenderAspectCommand = new RelayCommand<string>(s => { if (!string.IsNullOrEmpty(s)) RenderAspect = s; });
        SetRenderQualityCommand = new RelayCommand<string>(s => { if (!string.IsNullOrEmpty(s)) RenderQuality = s; });

        DeleteSelectedSlotCommand = new RelayCommand(() => { if (Selected is not null) RemoveSlot(Selected); });
        NavSelectedLeftCommand = new RelayCommand(() => NavSelected(-1));
        NavSelectedRightCommand = new RelayCommand(() => NavSelected(+1));
        NudgePlayheadBackCommand = new RelayCommand(() => PlayheadSec = Math.Max(0, PlayheadSec - 0.5));
        NudgePlayheadForwardCommand = new RelayCommand(() => PlayheadSec = Math.Min(TotalDuration, PlayheadSec + 0.5));

        AddOverlayFromLibraryCommand = new RelayCommand<Clip>(AddOverlayFromLibrary);
        RemoveOverlayCommand = new RelayCommand<OverlaySlot>(RemoveOverlay);
        SelectOverlayCommand = new RelayCommand<OverlaySlot>(s => SelectedOverlay = s);
        SetSelectedOverlayPositionCommand = new RelayCommand<string>(p =>
        {
            if (!string.IsNullOrEmpty(p) && _selectedOverlay is not null) _selectedOverlay.Position = p!;
        });
        OverlayTimeline.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOverlay));
        ClearTimelineCommand = new RelayCommand(() =>
        {
            if (Timeline.Count == 0 && OverlayTimeline.Count == 0) return;
            PushUndo();
            foreach (var t in Timeline) t.PropertyChanged -= OnSlotChanged;
            Timeline.Clear();
            OverlayTimeline.Clear();
            Selected = null;
            SelectedOverlay = null;
            RecomputeTotal();
        });
        BrowseAudioCommand = new RelayCommand(BrowseAudio);
        ClearAudioCommand = new RelayCommand(() => AudioPath = "");
        PickTakeCommand = new RelayCommand<VoiceTake>(t =>
        {
            if (t is not null) AudioPath = t.FilePath;
        });
        RenderCommand = new AsyncRelayCommand(RenderAsync, () => !IsRendering && Timeline.Count > 0);

        Timeline.CollectionChanged += (_, _) => RecomputeTotal();

        if (_ctx is not null) Refresh();
        else SeedDesignTime();
    }

    private void Refresh()
    {
        if (_ctx is null) return;
        LibraryClips.Clear();
        foreach (var c in _ctx.Clips.RecentClips(100)) LibraryClips.Add(c);

        AudioTakes.Clear();
        foreach (var t in _ctx.VoiceService.ListTakes(15)) AudioTakes.Add(t);
        foreach (var t in _ctx.VoiceService.ListMusicTakes(15)) AudioTakes.Add(t);
    }

    private void AddClip(Clip? clip)
    {
        if (clip is null) return;
        PushUndo();
        var slot = new TimelineSlot { Clip = clip, DurationSec = 3.0 };
        slot.PropertyChanged += OnSlotChanged;
        Timeline.Add(slot);
        Selected = slot;
    }

    private void RemoveSlot(TimelineSlot? slot)
    {
        if (slot is null) return;
        PushUndo();
        slot.PropertyChanged -= OnSlotChanged;
        Timeline.Remove(slot);
        if (ReferenceEquals(Selected, slot)) Selected = Timeline.LastOrDefault();
        RecomputeTotal();
    }

    private void MoveSlot(TimelineSlot? slot, int delta)
    {
        if (slot is null) return;
        var idx = Timeline.IndexOf(slot);
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= Timeline.Count) return;
        PushUndo();
        Timeline.Move(idx, newIdx);
    }

    /// <summary>Drag-drop reorder entry point used by the View. Wrapping the
    /// Move call so the undo stack catches drag operations too — calling
    /// Timeline.Move from the View directly would skip the snapshot.</summary>
    public void MoveSlotByDrag(TimelineSlot src, TimelineSlot dst)
    {
        var srcIdx = Timeline.IndexOf(src);
        var dstIdx = Timeline.IndexOf(dst);
        if (srcIdx < 0 || dstIdx < 0 || srcIdx == dstIdx) return;
        PushUndo();
        Timeline.Move(srcIdx, dstIdx);
        Selected = src;
    }

    /// <summary>Razor split — halve the slot's duration and clone it in place
    /// so the resulting pair plays the same clip back-to-back. With a
    /// crossfade transition set, this becomes a smooth re-statement of the
    /// same image; without it, an intentional hard re-cut.</summary>
    private void SplitSlot(TimelineSlot? slot)
    {
        if (slot is null) return;
        if (slot.DurationSec < 1.0) { ShowToast("Slot too short to split — bump duration first.", "warn"); return; }
        var idx = Timeline.IndexOf(slot);
        if (idx < 0) return;
        PushUndo();
        var half = slot.DurationSec / 2.0;
        slot.DurationSec = half;
        var clone = new TimelineSlot { Clip = slot.Clip, DurationSec = half };
        clone.PropertyChanged += OnSlotChanged;
        Timeline.Insert(idx + 1, clone);
        Selected = clone;
        RecomputeTotal();
        ShowToast($"Split — two {half:F1}s halves", "ok");
    }

    /// <summary>Add a new overlay slot at the current TotalDuration/2 (a
    /// sensible middle-of-timeline default the user can immediately drag).
    /// Inserts at the end of OverlayTimeline and selects it for inspector
    /// editing.</summary>
    private void AddOverlayFromLibrary(Clip? clip)
    {
        if (clip is null) return;
        PushUndo();
        var defaultStart = TotalDuration > 1 ? TotalDuration / 2 : 0;
        var slot = new OverlaySlot
        {
            Clip = clip,
            StartSec = defaultStart,
            DurationSec = 4.0,
            Scale = 0.3,
            Position = "TR",
        };
        OverlayTimeline.Add(slot);
        SelectedOverlay = slot;
        ShowToast($"Overlay added · {clip.FileName} at {defaultStart:F1}s", "ok");
    }

    private void RemoveOverlay(OverlaySlot? slot)
    {
        if (slot is null) return;
        PushUndo();
        OverlayTimeline.Remove(slot);
        if (ReferenceEquals(SelectedOverlay, slot)) SelectedOverlay = OverlayTimeline.LastOrDefault();
    }

    /// <summary>Razor split at the current playhead position. Cuts whichever
    /// slot the playhead is inside at the local offset within that slot,
    /// preserving the cumulative timeline length. Unlike the per-slot ✂
    /// button (which always halves), this respects the user's scrubbed
    /// position.</summary>
    private void RazorAtPlayhead()
    {
        var slot = SlotAtPlayhead;
        if (slot is null) { ShowToast("Nothing on timeline to split.", "warn"); return; }
        var localOffset = PlayheadOffsetWithinSlot;
        if (localOffset < 0.1 || localOffset > slot.DurationSec - 0.1)
        {
            ShowToast("Playhead too close to slot edge — move it inside the slot.", "warn");
            return;
        }
        SplitSlotAt(slot, localOffset);
    }

    /// <summary>Split a slot at a specific local offset (seconds within the
    /// slot). Used by both razor-at-playhead and (legacy) the always-halve
    /// SplitSlot button.</summary>
    private void SplitSlotAt(TimelineSlot slot, double localOffsetSec)
    {
        var idx = Timeline.IndexOf(slot);
        if (idx < 0) return;
        PushUndo();
        var firstDur = localOffsetSec;
        var secondDur = slot.DurationSec - localOffsetSec;
        slot.DurationSec = firstDur;
        var clone = new TimelineSlot { Clip = slot.Clip, DurationSec = secondDur };
        clone.PropertyChanged += OnSlotChanged;
        Timeline.Insert(idx + 1, clone);
        Selected = clone;
        RecomputeTotal();
        ShowToast($"Razor · {firstDur:F1}s | {secondDur:F1}s", "ok");
    }

    /// <summary>Duplicate the slot keeping its duration intact. Useful for
    /// holding the same image across a transition or matching two halves of
    /// a beat in the soundtrack.</summary>
    private void DuplicateSlot(TimelineSlot? slot)
    {
        if (slot is null) return;
        var idx = Timeline.IndexOf(slot);
        if (idx < 0) return;
        PushUndo();
        var clone = new TimelineSlot { Clip = slot.Clip, DurationSec = slot.DurationSec };
        clone.PropertyChanged += OnSlotChanged;
        Timeline.Insert(idx + 1, clone);
        Selected = clone;
        RecomputeTotal();
        ShowToast($"Duplicated {slot.FileName}", "ok");
    }

    private void OnSlotChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineSlot.DurationSec)) RecomputeTotal();
    }

    private void RecomputeTotal()
    {
        TotalDuration = Timeline.Sum(s => s.DurationSec);
        OnPropertyChanged(nameof(TotalDurationLabel));
        // Playhead derived properties depend on the timeline shape too.
        OnPropertyChanged(nameof(SlotAtPlayhead));
        OnPropertyChanged(nameof(PlayheadOffsetWithinSlot));
        // Clamp playhead in case the timeline shrank below it.
        if (_playheadSec > TotalDuration)
        {
            _playheadSec = TotalDuration;
            OnPropertyChanged(nameof(PlayheadSec));
            OnPropertyChanged(nameof(PlayheadLabel));
        }
    }

    private void BrowseAudio()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick an audio track",
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) AudioPath = dlg.FileName;
    }

    /// <summary>Move Selected by delta slots, wrapping at edges. Powers
    /// the arrow-key navigation hotkeys.</summary>
    private void NavSelected(int delta)
    {
        if (Timeline.Count == 0) { Selected = null; return; }
        var idx = Selected is null ? -1 : Timeline.IndexOf(Selected);
        var next = idx + delta;
        if (next < 0) next = 0;
        if (next >= Timeline.Count) next = Timeline.Count - 1;
        Selected = Timeline[next];
    }

    // Helpers for 9:16 rotation — keep total pixel count the same so a 1080p
    // landscape preset gives 1080x1920 vertical (the natural sister format).
    private static int RotateW(int w, int h) => h;
    private static int RotateH(int w, int h) => w;

    private async Task RenderAsync()
    {
        if (_ctx is null) { ShowToast("Editor context unavailable.", "warn"); return; }
        if (Timeline.Count == 0) { ShowToast("Timeline is empty — drop in some clips first.", "warn"); return; }

        IsRendering = true;
        ShowToast("Rendering film…", "info");
        try
        {
            // Resolve aspect + quality preset to concrete WxH. Vertical /
            // square / cinema bend the canvas; quality bumps both axes plus
            // a downstream encoder hint via crf/preset (read in BuildArgList
            // via Spec.Quality).
            var (baseW, baseH) = RenderQuality switch
            {
                "draft" => (1280, 720),
                "hi"    => (3840, 2160),
                _       => (1920, 1080),
            };
            var (w, h) = RenderAspect switch
            {
                "9:16" => (RotateW(baseW, baseH), RotateH(baseW, baseH)),
                "1:1"  => (baseH, baseH),
                "21:9" => (baseW, (int)(baseW * 9.0 / 21.0)),
                _      => (baseW, baseH),
            };

            var spec = new SlideshowRenderer.Spec
            {
                Clips = Timeline.Select(s => s.Clip).ToList(),
                ClipDurations = Timeline.Select(s => s.DurationSec).ToList(),
                SecondsPerClip = 3.0,
                Fps = Fps,
                Width = w,
                Height = h,
                Quality = RenderQuality,
                CrossfadeSec = CrossfadeSec,
                OutputName = OutputName,
                AudioPath = string.IsNullOrEmpty(AudioPath) ? null : AudioPath,
                AudioVolume = AudioVolume,
                OverlayTimeline = OverlayTimeline.Select(o => new SlideshowRenderer.OverlayDescriptor
                {
                    FilePath = o.FilePath,
                    StartSec = o.StartSec,
                    DurationSec = o.DurationSec,
                    Scale = o.Scale,
                    Position = o.Position,
                }).ToList(),
            };
            var progress = new Progress<string>(line =>
            {
                // ffmpeg-progress lines drive the live render pill — same
                // toast channel as the warn/ok messages so the user gets
                // a single source of truth in the inspector.
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
            // Refresh so the new MP4 appears in the library rail.
            Refresh();
        }
        finally
        {
            IsRendering = false;
            RenderCommand.NotifyCanExecuteChanged();
        }
    }

    private void SeedDesignTime()
    {
        // Designer-only sample so the XAML preview shows a meaningful layout.
        for (int i = 1; i <= 3; i++)
        {
            var clip = new Clip
            {
                Id = $"design-{i}",
                ShotId = $"design-{i}",
                FilePath = $"/Assets/Brand/empress-portrait.png",
                CreatedAt = DateTimeOffset.Now,
            };
            LibraryClips.Add(clip);
            var slot = new TimelineSlot { Clip = clip, DurationSec = 3 + i };
            Timeline.Add(slot);
        }
        Selected = Timeline.FirstOrDefault();
        RecomputeTotal();
    }

    // ---------- Project save / load (T25) ----------
    private string _projectName = "Untitled";
    public string ProjectName { get => _projectName; set => SetProperty(ref _projectName, value); }

    private void SaveProject()
    {
        if (Timeline.Count == 0 && OverlayTimeline.Count == 0)
        {
            ShowToast("Nothing to save — timeline is empty.", "warn");
            return;
        }
        var defaultDir = System.IO.Path.Combine(AppPaths.Root, "projects");
        try { System.IO.Directory.CreateDirectory(defaultDir); } catch { }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save NLE project",
            Filter = "Chanthra Studio project (*.chstudio)|*.chstudio|All files|*.*",
            FileName = SafeFile(string.IsNullOrEmpty(_projectName) ? "project" : _projectName) + ".chstudio",
            InitialDirectory = defaultDir,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var file = new NleProjectSerializer.ProjectFile
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                OutputName = OutputName,
                Fps = Fps,
                CrossfadeSec = CrossfadeSec,
                AudioPath = string.IsNullOrEmpty(AudioPath) ? null : AudioPath,
                AudioVolume = AudioVolume,
                Timeline = Timeline.Select(s => new NleProjectSerializer.SlotEntry
                {
                    ShotId = s.Clip.ShotId,
                    FilePath = s.Clip.FilePath,
                    DurationSec = s.DurationSec,
                }).ToList(),
                Overlay = OverlayTimeline.Select(o => new NleProjectSerializer.OverlayEntry
                {
                    ShotId = o.Clip.ShotId,
                    FilePath = o.Clip.FilePath,
                    StartSec = o.StartSec,
                    DurationSec = o.DurationSec,
                    Scale = o.Scale,
                    Position = o.Position,
                }).ToList(),
            };
            NleProjectSerializer.Save(dlg.FileName, file);
            ProjectName = file.Name;
            ShowToast($"Saved · {System.IO.Path.GetFileName(dlg.FileName)}", "ok");
        }
        catch (Exception ex)
        {
            ActivityLog.Error("nle", $"SaveProject {dlg.FileName}", ex);
            ShowToast($"Save failed: {ex.Message}", "err");
        }
    }

    private void LoadProject()
    {
        var defaultDir = System.IO.Path.Combine(AppPaths.Root, "projects");
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open NLE project",
            Filter = "Chanthra Studio project (*.chstudio)|*.chstudio|All files|*.*",
            InitialDirectory = System.IO.Directory.Exists(defaultDir) ? defaultDir : AppPaths.Root,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var file = NleProjectSerializer.Load(dlg.FileName);

            // Rebuild slots — look the Clip up from the in-memory library by
            // FilePath (most stable identifier across sessions). Missing clips
            // are skipped with a warning so a half-loaded project beats none.
            int missing = 0;
            PushUndo();
            foreach (var s in Timeline) s.PropertyChanged -= OnSlotChanged;
            Timeline.Clear();
            OverlayTimeline.Clear();

            foreach (var entry in file.Timeline)
            {
                var clip = ResolveClip(entry.FilePath, entry.ShotId);
                if (clip is null) { missing++; continue; }
                var slot = new TimelineSlot { Clip = clip, DurationSec = entry.DurationSec };
                slot.PropertyChanged += OnSlotChanged;
                Timeline.Add(slot);
            }
            foreach (var entry in file.Overlay)
            {
                var clip = ResolveClip(entry.FilePath, entry.ShotId);
                if (clip is null) { missing++; continue; }
                OverlayTimeline.Add(new OverlaySlot
                {
                    Clip = clip,
                    StartSec = entry.StartSec,
                    DurationSec = entry.DurationSec,
                    Scale = entry.Scale,
                    Position = entry.Position,
                });
            }

            OutputName = file.OutputName;
            Fps = file.Fps;
            CrossfadeSec = file.CrossfadeSec;
            AudioPath = file.AudioPath ?? "";
            AudioVolume = file.AudioVolume;
            ProjectName = file.Name;
            Selected = Timeline.FirstOrDefault();
            SelectedOverlay = OverlayTimeline.FirstOrDefault();
            RecomputeTotal();

            var note = missing == 0
                ? $"Loaded · {Timeline.Count} slots · {OverlayTimeline.Count} overlays"
                : $"Loaded · {Timeline.Count} slots ({missing} clip refs missing from Library — re-import)";
            ShowToast(note, missing == 0 ? "ok" : "warn");
        }
        catch (Exception ex)
        {
            ActivityLog.Error("nle", $"LoadProject {dlg.FileName}", ex);
            ShowToast($"Load failed: {ex.Message}", "err");
        }
    }

    private Models.Clip? ResolveClip(string filePath, string shotId)
    {
        // Prefer exact file-path match — that's what the user actually pointed
        // at when they built the project. Fall back to shotId so a renamed file
        // can still hook up if the shot reference survived.
        var byPath = LibraryClips.FirstOrDefault(c => string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null) return byPath;
        return LibraryClips.FirstOrDefault(c => c.ShotId == shotId);
    }

    private static string SafeFile(string raw)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw) sb.Append(System.Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim();
    }

    // ---------- Undo / redo (T24) ----------
    private sealed record TimelineSnapshot(
        IReadOnlyList<(Clip Clip, double Dur)> Main,
        IReadOnlyList<(Clip Clip, double Start, double Dur, double Scale, string Pos)> Overlay);

    private readonly System.Collections.Generic.Stack<TimelineSnapshot> _undoStack = new();
    private readonly System.Collections.Generic.Stack<TimelineSnapshot> _redoStack = new();
    private const int UndoLimit = 50;

    /// <summary>Capture current timeline state and push onto the undo stack.
    /// Call this BEFORE any topology mutation (add/remove/move/split/clear).
    /// Clears the redo stack — once you fork from a history point, the
    /// future you'd been holding is no longer reachable.</summary>
    private void PushUndo()
    {
        var snap = new TimelineSnapshot(
            Timeline.Select(s => (s.Clip, s.DurationSec)).ToList(),
            OverlayTimeline.Select(o => (o.Clip, o.StartSec, o.DurationSec, o.Scale, o.Position)).ToList());
        _undoStack.Push(snap);
        if (_undoStack.Count > UndoLimit)
        {
            // Drop the oldest entry by rebuilding the stack — not pretty, but
            // 50-entry stacks are tiny and this only fires after long edit
            // sessions.
            var keep = _undoStack.ToArray().Take(UndoLimit).Reverse().ToList();
            _undoStack.Clear();
            foreach (var s in keep) _undoStack.Push(s);
        }
        _redoStack.Clear();
        RefreshUndoRedo();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var current = CaptureSnapshot();
        _redoStack.Push(current);
        var restore = _undoStack.Pop();
        ApplySnapshot(restore);
        RefreshUndoRedo();
        ShowToast("Undo", "info");
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var current = CaptureSnapshot();
        _undoStack.Push(current);
        var restore = _redoStack.Pop();
        ApplySnapshot(restore);
        RefreshUndoRedo();
        ShowToast("Redo", "info");
    }

    private TimelineSnapshot CaptureSnapshot()
        => new(Timeline.Select(s => (s.Clip, s.DurationSec)).ToList(),
               OverlayTimeline.Select(o => (o.Clip, o.StartSec, o.DurationSec, o.Scale, o.Position)).ToList());

    private void ApplySnapshot(TimelineSnapshot snap)
    {
        // Detach handlers from old slots before discarding.
        foreach (var s in Timeline) s.PropertyChanged -= OnSlotChanged;
        Timeline.Clear();
        foreach (var (clip, dur) in snap.Main)
        {
            var slot = new TimelineSlot { Clip = clip, DurationSec = dur };
            slot.PropertyChanged += OnSlotChanged;
            Timeline.Add(slot);
        }
        OverlayTimeline.Clear();
        foreach (var (clip, start, dur, scale, pos) in snap.Overlay)
        {
            OverlayTimeline.Add(new OverlaySlot
            {
                Clip = clip, StartSec = start, DurationSec = dur, Scale = scale, Position = pos,
            });
        }
        Selected = Timeline.LastOrDefault();
        SelectedOverlay = OverlayTimeline.LastOrDefault();
        RecomputeTotal();
    }

    private void RefreshUndoRedo()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private async void ShowToast(string msg, string kind)
    {
        ToastMessage = msg;
        ToastKind = kind;
        try
        {
            await Task.Delay(2800);
            if (ToastMessage == msg) ToastMessage = "";
        }
        catch { }
    }
}
