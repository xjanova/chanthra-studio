using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public EditorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is null)
                DataContext = new EditorViewModel(App.Current.Studio);
        };
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
