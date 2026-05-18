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

    public EditorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is null)
                DataContext = new EditorViewModel(App.Current.Studio);

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
            StopPreviewTicker();
            try { PreviewMedia.Stop(); PreviewMedia.Close(); } catch { }
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Selected))
            LoadPreviewFromSelected();
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
    /// and isn't exposed through the VM). Space toggles play/pause.
    /// Skipped when the focused element is a TextBox so typing into the
    /// output-name / brief / prompt fields keeps working normally.
    /// </summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Space)
        {
            PreviewPlayPause_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
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
