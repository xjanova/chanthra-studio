using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class EditorView : UserControl
{
    // Drag-reorder state. _dragOrigin is the press point so we can defer
    // the drag start until the user actually moved past the system minimum
    // distance — otherwise a single-click selection accidentally fires
    // DoDragDrop.
    private Point? _dragOrigin;
    private TimelineSlot? _dragSlot;

    // Preview-playback bookkeeping.
    private static readonly System.Collections.Generic.HashSet<string> _videoExt = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v",
    };
    private DispatcherTimer? _previewTick;
    private bool _previewLoaded;

    // Timeline-play bookkeeping (T36). When _isTimelinePlaying is true, a
    // DispatcherTimer drives PlayheadSec forward at 1× wall-clock; the
    // MediaElement is paused and its Position follows the playhead via the
    // T35 scrub-sync handler. The existing per-slot ▶ keeps audio playback.
    private DispatcherTimer? _timelineTick;
    private bool _isTimelinePlaying;
    private DateTime _timelineLastTickAt;

    public EditorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Force our own VM — the ViewSwitcher leaves DataContext inheriting
            // MainViewModel, so a plain `is null` guard never fires.
            if (DataContext is not EditorViewModel)
                DataContext = new EditorViewModel(App.Current.Studio);
            // Resume autosave; Unloaded pauses it.
            (DataContext as EditorViewModel)?.StartAutosaveTimer();

            // Subscribe to VM.Selected changes so the MediaElement reloads its
            // source whenever the user clicks a different timeline slot.
            if (DataContext is INotifyPropertyChanged inpc)
            {
                inpc.PropertyChanged += OnVmPropertyChanged;
                // Initial load — apply whatever slot is selected at first paint.
                LoadPreviewFromSelected();
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is INotifyPropertyChanged inpc)
                inpc.PropertyChanged -= OnVmPropertyChanged;
            // Tear down the VM's autosave DispatcherTimer too — otherwise
            // navigating away from Edit leaves a 60s timer ticking on the
            // orphaned VM, and every revisit stacks another one. (7.17
            // fix · review LOGIC #1)
            if (DataContext is EditorViewModel evm) evm.StopAutosaveTimer();
            StopPreviewTicker();
            StopTimelinePlay();
            try { PreviewMedia.Stop(); PreviewMedia.Close(); } catch { }
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Selected))
            LoadPreviewFromSelected();
        else if (e.PropertyName == nameof(EditorViewModel.PlayheadSec))
            SyncMediaElementToPlayhead();
    }

    /// <summary>
    /// Scrubber → MediaElement frame. When the user moves the playhead AND
    /// the per-slot play button is paused (so we're not fighting active
    /// playback), seek the MediaElement to the corresponding offset within
    /// the current slot. ScrubbingEnabled=True on the element means a
    /// frame is decoded for the new position even while paused.
    /// </summary>
    private void SyncMediaElementToPlayhead()
    {
        if (DataContext is not EditorViewModel vm) return;
        if (!_previewLoaded) return;
        // Don't fight per-slot ▶ playback — its audio cadence drives Position
        // already. We only seek when paused, which is the scrub case or the
        // timeline-play case (where we explicitly pause the element first).
        if (PreviewPlayGlyph.Text == "⏸") return;
        try
        {
            var offset = TimeSpan.FromSeconds(System.Math.Max(0, vm.PlayheadOffsetWithinSlot));
            // Clamp to NaturalDuration if it's known — past-end seeks render
            // a blank frame on some codecs.
            if (PreviewMedia.NaturalDuration.HasTimeSpan
                && offset > PreviewMedia.NaturalDuration.TimeSpan)
                offset = PreviewMedia.NaturalDuration.TimeSpan;
            PreviewMedia.Position = offset;
        }
        catch { }
    }

    /// <summary>
    /// Inspect <see cref="EditorViewModel.Selected"/>, load its file path into
    /// the MediaElement if the clip is a video, hide the transport bar
    /// otherwise. Called on every Selected change so the preview always
    /// matches the inspector's focus.
    /// </summary>
    private void LoadPreviewFromSelected()
    {
        if (DataContext is not EditorViewModel vm) return;
        StopPreviewTicker();
        try { PreviewMedia.Stop(); } catch { }

        if (vm.Selected is null)
        {
            PreviewMedia.Source = null;
            TransportBar.Visibility = Visibility.Collapsed;
            _previewLoaded = false;
            return;
        }

        var path = vm.Selected.FilePath ?? "";
        var ext = Path.GetExtension(path);
        if (!_videoExt.Contains(ext) || !File.Exists(path))
        {
            // Image or missing-on-disk → MediaElement stays blank, Image
            // converter handles the still preview behind it.
            PreviewMedia.Source = null;
            TransportBar.Visibility = Visibility.Collapsed;
            _previewLoaded = false;
            return;
        }

        try
        {
            PreviewMedia.Source = new Uri(path, UriKind.Absolute);
            TransportBar.Visibility = Visibility.Visible;
            PreviewPlayGlyph.Text = "▶";
            _previewLoaded = true;
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("preview", $"failed to load {Path.GetFileName(path)}: {ex.Message}");
            PreviewMedia.Source = null;
            TransportBar.Visibility = Visibility.Collapsed;
        }
    }

    private void PreviewPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewLoaded) return;
        if (PreviewPlayGlyph.Text == "▶")
        {
            try { PreviewMedia.Play(); } catch { return; }
            PreviewPlayGlyph.Text = "⏸";
            StartPreviewTicker();
        }
        else
        {
            try { PreviewMedia.Pause(); } catch { }
            PreviewPlayGlyph.Text = "▶";
            StopPreviewTicker();
        }
    }

    private void PreviewRewind_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewLoaded) return;
        try
        {
            PreviewMedia.Position = TimeSpan.Zero;
            PreviewTimecode.Text = "0:00";
        }
        catch { }
    }

    private void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewLoaded) return;
        PreviewMedia.IsMuted = !PreviewMedia.IsMuted;
        PreviewMuteGlyph.Text = PreviewMedia.IsMuted ? "🔇" : "🔊";
    }

    private void PreviewMedia_Ended(object sender, RoutedEventArgs e)
    {
        // Loop back to start so the user can re-watch without clicking
        // rewind — most NLE preview panes do this by default.
        try
        {
            PreviewMedia.Position = TimeSpan.Zero;
            PreviewMedia.Play();
        }
        catch { }
    }

    private void PreviewMedia_Opened(object sender, RoutedEventArgs e)
    {
        StartPreviewTicker();
        // After the source loads, jump to wherever the playhead sits so
        // scrubbing across a slot boundary lands on the right frame
        // instead of always at 0:00 of the next slot.
        if (DataContext is EditorViewModel vm)
        {
            try
            {
                var offset = TimeSpan.FromSeconds(System.Math.Max(0, vm.PlayheadOffsetWithinSlot));
                PreviewMedia.Position = offset;
            }
            catch { }
        }
    }

    private void PreviewMedia_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        ActivityLog.Warn("preview", $"MediaFailed: {e.ErrorException?.Message ?? "unknown"}");
        TransportBar.Visibility = Visibility.Collapsed;
        _previewLoaded = false;
        StopPreviewTicker();
    }

    private void StartPreviewTicker()
    {
        if (_previewTick is not null) return;
        _previewTick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _previewTick.Tick += (_, _) =>
        {
            try
            {
                var p = PreviewMedia.Position;
                PreviewTimecode.Text = $"{(int)p.TotalMinutes}:{p.Seconds:D2}";
            }
            catch { }
        };
        _previewTick.Start();
    }

    private void StopPreviewTicker()
    {
        _previewTick?.Stop();
        _previewTick = null;
    }

    /// <summary>
    /// Editor-wide preview hotkeys that need code-behind plumbing rather
    /// than VM commands (because the MediaElement instance lives in XAML
    /// and isn't exposed through the VM). Space toggles per-slot play/pause
    /// (with audio); Shift+Space toggles timeline-play (silent, sequential).
    /// Skipped when the focused element is a TextBox so typing into the
    /// output-name / brief / prompt fields keeps working normally.
    /// </summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Keys typed into a field belong to the field — on any keyboard
        // layout, since e.Key is the physical key.
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
            || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
            || e.OriginalSource is ComboBox { IsEditable: true })
            return;

        if (e.Key == Key.Space)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                ToggleTimelinePlay();
            else
                PreviewPlayPause_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None || DataContext is not EditorViewModel vm) return;

        // Arrows stay with a focused slider or list, where they already mean something.
        var arrowsTaken = e.OriginalSource is System.Windows.Controls.Primitives.RangeBase
                          or System.Windows.Controls.Primitives.Selector
                          or ListBoxItem;
        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Delete or Key.Back => vm.DeleteSelectedSlotCommand,
            Key.Left when !arrowsTaken => vm.NavSelectedLeftCommand,
            Key.Right when !arrowsTaken => vm.NavSelectedRightCommand,
            Key.J => vm.NudgePlayheadBackCommand,
            Key.L => vm.NudgePlayheadForwardCommand,
            Key.C => vm.RazorAtPlayheadCommand,
            _ => null,
        };
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    // --------- T36: Timeline play mode ---------

    private void TimelinePlay_Click(object sender, RoutedEventArgs e) => ToggleTimelinePlay();

    /// <summary>
    /// Toggle silent timeline preview. While active, a DispatcherTimer
    /// advances <see cref="EditorViewModel.PlayheadSec"/> at wall-clock
    /// speed. The scrub-sync handler (T35) seeks MediaElement to the right
    /// frame on each tick; when the playhead crosses a slot boundary the
    /// VM's SlotAtPlayhead → Selected promotion auto-reloads the source.
    /// </summary>
    private void ToggleTimelinePlay()
    {
        if (DataContext is not EditorViewModel vm) return;

        if (_isTimelinePlaying)
        {
            StopTimelinePlay();
            return;
        }
        if (vm.Timeline.Count == 0)
        {
            ActivityLog.Info("nle", "timeline play ignored — no slots");
            return;
        }
        // Pause the per-slot MediaElement so we're not double-driving Position.
        try { PreviewMedia.Pause(); } catch { }
        PreviewPlayGlyph.Text = "▶";

        // If the playhead is already at end-of-timeline, restart from 0 so
        // hitting ▶▶ Timeline feels intuitive.
        if (vm.PlayheadSec >= vm.TotalDuration - 0.05) vm.PlayheadSec = 0;

        _isTimelinePlaying = true;
        _timelineLastTickAt = DateTime.UtcNow;
        if (_timelineTick is null)
        {
            _timelineTick = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(33),  // ~30 fps preview cadence
            };
            _timelineTick.Tick += TimelineTick_Tick;
        }
        _timelineTick.Start();
        TimelinePlayGlyph.Text = "⏸";
    }

    private void StopTimelinePlay()
    {
        _isTimelinePlaying = false;
        _timelineTick?.Stop();
        try { TimelinePlayGlyph.Text = "▶▶"; } catch { }
    }

    private void TimelineTick_Tick(object? sender, EventArgs e)
    {
        if (DataContext is not EditorViewModel vm) { StopTimelinePlay(); return; }
        var now = DateTime.UtcNow;
        var delta = (now - _timelineLastTickAt).TotalSeconds;
        _timelineLastTickAt = now;
        // Defensive clamp: a paused-then-resumed Dispatcher can deliver a
        // huge delta on first tick; cap to one frame so the playhead
        // doesn't leap past five slots after a context-switch.
        if (delta > 0.5) delta = 0.033;

        var next = vm.PlayheadSec + delta;
        if (next >= vm.TotalDuration)
        {
            vm.PlayheadSec = vm.TotalDuration;
            StopTimelinePlay();
            return;
        }
        vm.PlayheadSec = next;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        // Wire the Recent ▾ button to its own context menu — clicking the
        // button itself shows the dropdown, instead of requiring a right-click
        // (the default ContextMenu trigger).
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            // Re-bind DataContext so the menu inherits the editor VM (and
            // therefore its RecentProjects ObservableCollection).
            btn.ContextMenu.DataContext = btn.DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void Fps_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (int.TryParse(tag, out var fps)) vm.Fps = fps;
    }

    private void AudioTake_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        if (sender is not ComboBox cb) return;
        if (cb.SelectedItem is VoiceTake t) vm.AudioPath = t.FilePath;
    }

    // --------- drag-reorder ---------

    private void Slot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TimelineSlot slot) return;
        _dragOrigin = e.GetPosition(this);
        _dragSlot = slot;
        // Don't set e.Handled — we still want the slot's click→select to fire
        // when the user just clicks without dragging.
    }

    private void Slot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is null || _dragSlot is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragOrigin = null; _dragSlot = null; return; }
        var current = e.GetPosition(this);
        var dx = System.Math.Abs(current.X - _dragOrigin.Value.X);
        var dy = System.Math.Abs(current.Y - _dragOrigin.Value.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;

        if (sender is not FrameworkElement fe) return;
        var data = new DataObject("ChanthraStudio.TimelineSlot", _dragSlot);
        // Capture the slot ref before DoDragDrop blocks — clearing afterwards.
        var slot = _dragSlot;
        _dragOrigin = null;
        _dragSlot = null;
        DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
        _ = slot;  // (slot var kept for clarity; not used after blocking call)
    }

    private void Slot_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ChanthraStudio.TimelineSlot")) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        if (sender is FrameworkElement fe && fe.DataContext is TimelineSlot slot)
            slot.IsDropTarget = true;
    }

    private void Slot_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TimelineSlot slot)
            slot.IsDropTarget = false;
    }

    private void Slot_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe1 && fe1.DataContext is TimelineSlot s1) s1.IsDropTarget = false;
        if (!e.Data.GetDataPresent("ChanthraStudio.TimelineSlot")) return;
        if (e.Data.GetData("ChanthraStudio.TimelineSlot") is not TimelineSlot src) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not TimelineSlot dst) return;
        if (DataContext is not EditorViewModel vm) return;
        if (ReferenceEquals(src, dst)) return;

        // Clear any lingering drop-target flags — a fast drag can leave them
        // stuck because DragLeave wasn't reliably called.
        foreach (var s in vm.Timeline) s.IsDropTarget = false;
        // Goes through VM so the undo stack catches the reorder.
        vm.MoveSlotByDrag(src, dst);
        e.Handled = true;
    }

    // --------- Library → overlay drag source ---------

    private Point? _libDragOrigin;
    private Clip? _libDragClip;

    private void LibraryClip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Clip clip) return;
        _libDragOrigin = e.GetPosition(this);
        _libDragClip = clip;
    }

    private void LibraryClip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_libDragOrigin is null || _libDragClip is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _libDragOrigin = null; _libDragClip = null; return; }
        var current = e.GetPosition(this);
        var dx = System.Math.Abs(current.X - _libDragOrigin.Value.X);
        var dy = System.Math.Abs(current.Y - _libDragOrigin.Value.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;

        if (sender is not FrameworkElement fe) return;
        var data = new DataObject("ChanthraStudio.LibraryClip", _libDragClip);
        _libDragOrigin = null;
        _libDragClip = null;
        DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
    }

    // --------- Overlay-target drop handlers ---------

    private void Overlay_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ChanthraStudio.LibraryClip")) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        if (sender is Border bd) bd.BorderBrush = (Brush)FindResource("BrushGoldHi");
    }

    private void Overlay_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border bd) bd.ClearValue(Border.BorderBrushProperty);
    }

    private void Overlay_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border bd) bd.ClearValue(Border.BorderBrushProperty);
        if (!e.Data.GetDataPresent("ChanthraStudio.LibraryClip")) return;
        if (e.Data.GetData("ChanthraStudio.LibraryClip") is not Clip clip) return;
        if (DataContext is not EditorViewModel vm) return;
        vm.AddOverlayFromLibraryCommand.Execute(clip);
        e.Handled = true;
    }
}
