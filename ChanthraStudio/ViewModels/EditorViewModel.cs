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
        ClearTimelineCommand = new RelayCommand(() =>
        {
            foreach (var t in Timeline) t.PropertyChanged -= OnSlotChanged;
            Timeline.Clear();
            Selected = null;
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
        var slot = new TimelineSlot { Clip = clip, DurationSec = 3.0 };
        slot.PropertyChanged += OnSlotChanged;
        Timeline.Add(slot);
        Selected = slot;
    }

    private void RemoveSlot(TimelineSlot? slot)
    {
        if (slot is null) return;
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
        Timeline.Move(idx, newIdx);
    }

    private void OnSlotChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineSlot.DurationSec)) RecomputeTotal();
    }

    private void RecomputeTotal()
    {
        TotalDuration = Timeline.Sum(s => s.DurationSec);
        OnPropertyChanged(nameof(TotalDurationLabel));
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

    private async Task RenderAsync()
    {
        if (_ctx is null) { ShowToast("Editor context unavailable.", "warn"); return; }
        if (Timeline.Count == 0) { ShowToast("Timeline is empty — drop in some clips first.", "warn"); return; }

        IsRendering = true;
        ShowToast("Rendering film…", "info");
        try
        {
            var spec = new SlideshowRenderer.Spec
            {
                Clips = Timeline.Select(s => s.Clip).ToList(),
                ClipDurations = Timeline.Select(s => s.DurationSec).ToList(),
                SecondsPerClip = 3.0,
                Fps = Fps,
                OutputName = OutputName,
                AudioPath = string.IsNullOrEmpty(AudioPath) ? null : AudioPath,
                AudioVolume = AudioVolume,
            };
            var result = await _ctx.SlideshowRenderer.RenderAsync(spec);
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
