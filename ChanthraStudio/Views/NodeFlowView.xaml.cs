using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    // Drag-to-connect state. Set on socket MouseDown, cleared on MouseUp.
    private FlowNode? _wireFromNode;
    private NodeSocket? _wireFromSocket;
    private Point _wireFromPoint;

    public NodeFlowView()
    {
        InitializeComponent();
        // Catch every mouse-up that bubbles up through the user control so
        // we can finish a wire-drag even if the user releases over empty
        // canvas (the Viewport handlers only fire when the down landed there).
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(GlobalMouseLeftButtonUp), handledEventsToo: true);
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

        if (_wireFromSocket is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var mouse = e.GetPosition(WorldCanvas);
            Vm.UpdateGhostWire(_wireFromPoint, mouse);
            return;
        }
        if (_wireFromSocket is not null && e.LeftButton != MouseButtonState.Pressed)
        {
            CancelWireDrag();
        }

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

    /// <summary>
    /// MouseDown handler attached to every socket Border. Starts a drag-to-
    /// connect operation; the source socket and pin position are stashed so
    /// the global MouseUp can resolve the target.
    /// </summary>
    private void Socket_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not Border bd) return;
        if (bd.Tag is not NodeSocket socket) return;
        var node = Vm.FindNodeForSocket(socket);
        if (node is null) return;

        _wireFromNode = node;
        _wireFromSocket = socket;
        // For a clean drag, sockets must originate at an OUTPUT — if the
        // user starts on an INPUT we treat the down as a click and fall
        // through to SocketClickCommand (legacy click-mode wire creation).
        if (socket.IsInput)
        {
            Vm.SocketClickCommand.Execute(socket);
            _wireFromNode = null;
            _wireFromSocket = null;
            e.Handled = true;
            return;
        }

        _wireFromPoint = socket.IsInput ? node.InputPin(socket.Row) : node.OutputPin(socket.Row);
        Vm.UpdateGhostWire(_wireFromPoint, _wireFromPoint);  // zero-length ghost on press
        WorldCanvas.CaptureMouse();
        e.Handled = true;  // suppress the parent Node's header-drag handler
    }

    private void Socket_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border bd && bd.Child is System.Windows.Shapes.Ellipse el)
            el.Stroke = (Brush)FindResource("BrushGoldHi");
    }

    private void Socket_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border bd && bd.Child is System.Windows.Shapes.Ellipse el)
            el.Stroke = (Brush)FindResource("BrushVoid");
    }

    /// <summary>
    /// Global MouseUp — finishes any in-flight wire drag. Walks the visual
    /// tree at the cursor to find a socket Border whose Tag is a NodeSocket
    /// and attempts to wire the source → target through the VM.
    /// </summary>
    private void GlobalMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || _wireFromSocket is null) return;

        // Hit-test where the user released. WPF's VisualTreeHelper.HitTest
        // walks the entire visual tree but we only want elements with the
        // socket Tag. Walk up from whatever was hit until we find one.
        var pos = e.GetPosition(this);
        var hit = VisualTreeHelper.HitTest(this, pos);
        DependencyObject? walk = hit?.VisualHit;
        NodeSocket? targetSocket = null;
        while (walk is not null)
        {
            if (walk is Border b && b.Tag is NodeSocket s) { targetSocket = s; break; }
            walk = VisualTreeHelper.GetParent(walk);
        }

        if (targetSocket is not null && _wireFromNode is not null && _wireFromSocket is not null)
        {
            var targetNode = Vm.FindNodeForSocket(targetSocket);
            if (targetNode is not null)
            {
                Vm.TryAddWire(_wireFromNode, _wireFromSocket, targetNode, targetSocket);
            }
        }
        CancelWireDrag();
    }

    private void CancelWireDrag()
    {
        _wireFromNode = null;
        _wireFromSocket = null;
        Vm?.ClearGhostWire();
        if (WorldCanvas.IsMouseCaptured) WorldCanvas.ReleaseMouseCapture();
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
