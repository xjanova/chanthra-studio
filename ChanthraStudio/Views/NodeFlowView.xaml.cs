using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChanthraStudio.Models;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Views;

public partial class NodeFlowView : UserControl
{
    private FlowNode? _draggingNode;
    private Point _dragStartCanvas;
    private Point _dragStartNode;

    private bool _panning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    public NodeFlowView()
    {
        InitializeComponent();
    }

    private NodeFlowViewModel? Vm => DataContext as NodeFlowViewModel;

    /// <summary>
    /// Click on a node card → select it, and start a drag if the click
    /// landed on the header. The header has Cursor=SizeAll so users can
    /// see where the drag handle is.
    /// </summary>
    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border bd) return;
        if (bd.DataContext is not FlowNode node) return;

        var vm = Vm;
        if (vm is null) return;
        vm.Selected = node;

        // Only start a drag if the click landed on the header strip
        // (top 32px of the card) — body clicks just select.
        var pt = e.GetPosition(bd);
        if (pt.Y > FlowNode.HeaderHeight) return;

        _draggingNode = node;
        _dragStartCanvas = e.GetPosition(WorldCanvas);
        _dragStartNode = new Point(node.X, node.Y);
        bd.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (Vm is null) return;

        if (_draggingNode is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var current = e.GetPosition(WorldCanvas);
            var dx = current.X - _dragStartCanvas.X;
            var dy = current.Y - _dragStartCanvas.Y;
            _draggingNode.X = _dragStartNode.X + dx;
            _draggingNode.Y = _dragStartNode.Y + dy;
            Vm.RecomputeWires();
            return;
        }

        if (_draggingNode is not null && e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
        }

        if (_panning && e.RightButton == MouseButtonState.Pressed)
        {
            var current = e.GetPosition((IInputElement)sender);
            Vm.PanX = _panStartX + (current.X - _panStart.X);
            Vm.PanY = _panStartY + (current.Y - _panStart.Y);
        }
    }

    private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        _panning = true;
        _panStart = e.GetPosition((IInputElement)sender);
        _panStartX = Vm.PanX;
        _panStartY = Vm.PanY;
        ((IInputElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            ((IInputElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }
        EndDrag();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        Vm.Zoom += e.Delta > 0 ? 0.08 : -0.08;
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_draggingNode is null) return;
        _draggingNode = null;
        WorldCanvas.ReleaseMouseCapture();
    }
}
