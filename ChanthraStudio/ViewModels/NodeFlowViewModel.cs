using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Providers.ComfyUI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// In-app visual graph editor. Phase 6 of the chanthra-studio roadmap.
///
/// Loads a small ComfyUI-like preset graph so the canvas is visually
/// populated on first open. Nodes can be dragged; wires re-render
/// automatically because <see cref="RecomputeWires"/> is called from
/// the view's drag handler.
/// </summary>
public sealed class NodeFlowViewModel : ObservableObject
{
    public ObservableCollection<FlowNode> Nodes { get; } = new();
    public ObservableCollection<FlowWire> Wires { get; } = new();

    private FlowNode? _selected;
    public FlowNode? Selected
    {
        get => _selected;
        set
        {
            var prev = _selected;
            if (SetProperty(ref _selected, value))
            {
                if (prev is not null) prev.IsSelected = false;
                if (_selected is not null) _selected.IsSelected = true;
                OnPropertyChanged(nameof(SelectedTitle));
            }
        }
    }

    public string SelectedTitle => _selected?.Title ?? "No node selected";

    private double _zoom = 1.0;
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, 0.4, 2.2);
            SetProperty(ref _zoom, clamped);
        }
    }

    private double _panX;
    public double PanX { get => _panX; set => SetProperty(ref _panX, value); }

    private double _panY;
    public double PanY { get => _panY; set => SetProperty(ref _panY, value); }

    /// <summary>
    /// Bezier path drawn while the user is dragging from a socket. Null when
    /// not actively dragging. The view's MouseMove handler updates this each
    /// frame; the XAML <c>Path</c> binds Data to it.
    /// </summary>
    private System.Windows.Media.Geometry? _ghostWireGeometry;
    public System.Windows.Media.Geometry? GhostWireGeometry
    {
        get => _ghostWireGeometry;
        set => SetProperty(ref _ghostWireGeometry, value);
    }

    /// <summary>
    /// Compute the bezier for a wire-in-flight from socket origin to the
    /// current mouse position. Same control-point math as the committed
    /// wires so the visual transition on drop is seamless.
    /// </summary>
    public void UpdateGhostWire(Point from, Point to)
    {
        double strength = Math.Max(60, Math.Abs(to.X - from.X) * 0.5);
        var p1 = new Point(from.X + strength, from.Y);
        var p2 = new Point(to.X - strength, to.Y);
        var fig = new System.Windows.Media.PathFigure { StartPoint = from, IsFilled = false };
        fig.Segments.Add(new System.Windows.Media.BezierSegment(p1, p2, to, true));
        var geo = new System.Windows.Media.PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        GhostWireGeometry = geo;
    }

    public void ClearGhostWire()
    {
        GhostWireGeometry = null;
    }

    /// <summary>
    /// Public attempt-to-wire used by the drag-drop flow in the code-behind.
    /// Returns true if the wire was created (or already existed); false if
    /// invalid (same node, wrong direction, etc.).
    /// </summary>
    public bool TryAddWire(FlowNode fromNode, NodeSocket fromSocket, FlowNode toNode, NodeSocket toSocket)
    {
        if (fromSocket.IsInput || !toSocket.IsInput) return false;
        if (ReferenceEquals(fromNode, toNode)) return false;
        var dupe = Wires.FirstOrDefault(w =>
            w.FromNodeId == fromNode.Id && w.FromSocketId == fromSocket.Id &&
            w.ToNodeId == toNode.Id && w.ToSocketId == toSocket.Id);
        if (dupe is not null) return true;
        PushUndo();
        // Replace any existing wire into the same input.
        var replaced = Wires.Where(w => w.ToNodeId == toNode.Id && w.ToSocketId == toSocket.Id).ToList();
        foreach (var r in replaced) Wires.Remove(r);
        Wires.Add(new FlowWire
        {
            Id = $"{fromNode.Id}.{fromSocket.Id}->{toNode.Id}.{toSocket.Id}",
            FromNodeId = fromNode.Id, FromSocketId = fromSocket.Id,
            ToNodeId = toNode.Id, ToSocketId = toSocket.Id,
            Type = fromSocket.Type,
        });
        RecomputeWires();
        ShowStatus($"Wired {fromSocket.Label} → {toSocket.Label}", "ok");
        return true;
    }

    /// <summary>
    /// Find the FlowNode that owns the given socket. The socket model itself
    /// doesn't back-reference its parent (POCOs stay clean) so we walk the
    /// node list once per lookup; node count is small enough that the linear
    /// scan beats a maintained reverse-index.
    /// </summary>
    public FlowNode? FindNodeForSocket(NodeSocket socket)
        => Nodes.FirstOrDefault(n => n.Inputs.Contains(socket) || n.Outputs.Contains(socket));

    public IRelayCommand ZoomInCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ZoomResetCommand { get; }
    public IRelayCommand AutoArrangeCommand { get; }
    public IRelayCommand<string> AddNodeCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand<NodeSocket> SocketClickCommand { get; }
    public IRelayCommand<FlowNode> DeleteNodeCommand { get; }
    public IRelayCommand DeleteSelectedCommand { get; }
    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IRelayCommand DuplicateSelectedCommand { get; }
    public IRelayCommand NewGraphCommand { get; }
    public IAsyncRelayCommand AiBuildCommand { get; }
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;
    private readonly UndoStack<GraphSnap> _undo;

    private string _aiPrompt = "";
    /// <summary>Natural-language description the AI turns into a ComfyUI graph.</summary>
    public string AiPrompt { get => _aiPrompt; set => SetProperty(ref _aiPrompt, value); }

    private bool _isAiBuilding;
    public bool IsAiBuilding { get => _isAiBuilding; set => SetProperty(ref _isAiBuilding, value); }

    private string _statusMessage = "";
    /// <summary>Bottom-bar message after a Save/Load/Run action — also drives
    /// a small toast in the inspector.</summary>
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private string _statusKind = "info";
    public string StatusKind { get => _statusKind; set => SetProperty(ref _statusKind, value); }

    private string _graphName = "node_flow";
    /// <summary>File name (no extension) the Save button uses. Editable so
    /// the user can iterate on multiple drafts.</summary>
    public string GraphName { get => _graphName; set => SetProperty(ref _graphName, value); }

    /// <summary>
    /// When the user clicks an output socket first, this holds the pending
    /// "from" endpoint. Clicking an input socket next creates a wire to it.
    /// Clicking another output (or pressing Esc — not yet wired) resets it.
    /// </summary>
    private (FlowNode Node, NodeSocket Socket)? _pendingFrom;

    public NodeFlowViewModel()
    {
        SeedSampleGraph();

        ZoomInCommand = new RelayCommand(() => Zoom += 0.1);
        ZoomOutCommand = new RelayCommand(() => Zoom -= 0.1);
        ZoomResetCommand = new RelayCommand(() => { Zoom = 1.0; PanX = 0; PanY = 0; });
        AutoArrangeCommand = new RelayCommand(AutoArrange);
        AddNodeCommand = new RelayCommand<string>(AddNodeFromKind);
        SaveCommand = new RelayCommand(SaveGraph);
        LoadCommand = new RelayCommand(LoadGraph);
        RunCommand = new AsyncRelayCommand(RunGraphAsync);
        SocketClickCommand = new RelayCommand<NodeSocket>(OnSocketClicked);
        DeleteNodeCommand = new RelayCommand<FlowNode>(DeleteNode);
        DeleteSelectedCommand = new RelayCommand(() => DeleteNode(Selected));

        // Undo / redo — snapshot the whole graph (positions, sockets, params,
        // wires, selection) so every structural edit and drag is reversible.
        _undo = new UndoStack<GraphSnap>(CaptureSnapshot, ApplySnapshot);
        UndoCommand = new RelayCommand(_undo.Undo, () => _undo.CanUndo);
        RedoCommand = new RelayCommand(_undo.Redo, () => _undo.CanRedo);
        DuplicateSelectedCommand = new RelayCommand(DuplicateSelected);
        NewGraphCommand = new RelayCommand(NewGraph);
        AiBuildCommand = new AsyncRelayCommand(AiBuildAsync);
        _undo.Changed += () =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        };

        RecomputeWires();
    }

    private void SaveGraph()
    {
        try
        {
            var graph = new FlowGraph();
            foreach (var n in Nodes) graph.Nodes.Add(n);
            foreach (var w in Wires) graph.Wires.Add(w);
            var path = NodeFlowConverter.SaveToUserWorkflows(graph, GraphName);
            ShowStatus($"Saved · {System.IO.Path.GetFileName(path)} — refresh the Composer's workflow picker to use it", "ok");
        }
        catch (Exception ex)
        {
            ShowStatus($"Save failed: {ex.Message}", "err");
        }
    }

    private void LoadGraph()
    {
        var userDir = System.IO.Path.Combine(AppPaths.Root, "workflows");
        try { System.IO.Directory.CreateDirectory(userDir); } catch { }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open workflow JSON",
            Filter = "ComfyUI workflows (*.json)|*.json|All files|*.*",
            InitialDirectory = userDir,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var loaded = NodeFlowConverter.LoadFromFile(dlg.FileName);
            Nodes.Clear();
            Wires.Clear();
            foreach (var n in loaded.Nodes) Nodes.Add(n);
            foreach (var w in loaded.Wires) Wires.Add(w);
            GraphName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            RecomputeWires();
            Selected = Nodes.FirstOrDefault();
            _undo.Clear();   // fresh history — can't undo past a load
            ShowStatus($"Loaded · {Nodes.Count} nodes · {Wires.Count} wires", "ok");
        }
        catch (Exception ex)
        {
            ShowStatus($"Load failed: {ex.Message}", "err");
        }
    }

    /// <summary>
    /// Submit the current graph to the ComfyUI server (Settings.ComfyUiUrl)
    /// and return immediately — output download is handled the same way the
    /// Composer does it via GenerationService when the workflow is re-picked
    /// from the Composer. Here we just exercise the pipeline and report the
    /// queued prompt id.
    /// </summary>
    private async Task RunGraphAsync()
    {
        var studio = (System.Windows.Application.Current as App)?.Studio;
        if (studio is null)
        {
            ShowStatus("Run requires the app's StudioContext — not available at design time.", "warn");
            return;
        }
        var url = studio.Settings.ComfyUiUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowStatus("ComfyUI URL is empty — set it in Settings.", "warn");
            return;
        }
        try
        {
            ShowStatus("Submitting graph to ComfyUI…", "info");
            var graph = new FlowGraph();
            foreach (var n in Nodes) graph.Nodes.Add(n);
            foreach (var w in Wires) graph.Wires.Add(w);
            var nodesJson = NodeFlowConverter.ToComfyApi(graph);

            using var client = new ComfyUiClient(url);
            var promptId = await client.SubmitPromptAsync(nodesJson);
            ShowStatus($"Queued · prompt_id {promptId[..System.Math.Min(8, promptId.Length)]} — check ComfyUI's output folder", "ok");
        }
        catch (Exception ex)
        {
            ShowStatus($"Run failed: {ex.Message}", "err");
        }
    }

    /// <summary>Ask the active LLM to design a ComfyUI graph from
    /// <see cref="AiPrompt"/>, validate it, and load it into the editor
    /// (replacing the canvas — undoable). The user can tweak it then Run.</summary>
    private async Task AiBuildAsync()
    {
        var ctx = (System.Windows.Application.Current as App)?.Studio;
        if (ctx is null)
        {
            ShowStatus("AI build needs the running app (not available at design time).", "warn");
            return;
        }
        if (string.IsNullOrWhiteSpace(AiPrompt))
        {
            ShowStatus("Describe the workflow first — e.g. \"SDXL portrait with a LoRA, 1024², 30 steps\".", "warn");
            return;
        }

        IsAiBuilding = true;
        ShowStatus("AI is designing the workflow…", "info");
        try
        {
            var spec = await ctx.Llm.BuildComfyWorkflowAsync(AiPrompt);
            var graph = AiWorkflowBuilder.BuildGraph(spec);
            if (graph.Nodes.Count == 0)
            {
                ShowStatus("AI returned an empty graph — try rephrasing.", "err");
                return;
            }

            PushUndo();
            Nodes.Clear();
            Wires.Clear();
            foreach (var n in graph.Nodes) Nodes.Add(n);
            foreach (var w in graph.Wires) Wires.Add(w);
            _pendingFrom = null;
            Selected = Nodes.FirstOrDefault();
            RecomputeWires();
            ShowStatus($"AI built {graph.Nodes.Count} nodes · {graph.Wires.Count} wires — tweak then Run", "ok");
        }
        catch (Exception ex)
        {
            ShowStatus("AI build failed: " + ex.Message, "err");
        }
        finally
        {
            IsAiBuilding = false;
        }
    }

    private void OnSocketClicked(NodeSocket? socket)
    {
        if (socket is null) return;
        var owner = Nodes.FirstOrDefault(n => n.Inputs.Contains(socket) || n.Outputs.Contains(socket));
        if (owner is null) return;

        if (socket.IsInput)
        {
            // Need a pending "from" socket — otherwise just announce the click.
            if (_pendingFrom is null)
            {
                ShowStatus("Click an OUTPUT socket first, then this input — directional rule.", "warn");
                return;
            }
            var from = _pendingFrom.Value;
            // Don't connect a node to itself, and don't duplicate wires.
            if (ReferenceEquals(from.Node, owner))
            {
                ShowStatus("Can't wire a node to itself.", "warn");
                _pendingFrom = null;
                return;
            }
            var dupe = Wires.FirstOrDefault(w =>
                w.FromNodeId == from.Node.Id && w.FromSocketId == from.Socket.Id &&
                w.ToNodeId == owner.Id && w.ToSocketId == socket.Id);
            if (dupe is not null)
            {
                ShowStatus("Wire already exists.", "warn");
                _pendingFrom = null;
                return;
            }
            // Any existing wire INTO the same input gets replaced — most
            // ComfyUI inputs are single-source, multiple wires would crash
            // the server at run time.
            PushUndo();
            var replaced = Wires.Where(w => w.ToNodeId == owner.Id && w.ToSocketId == socket.Id).ToList();
            foreach (var r in replaced) Wires.Remove(r);

            Wires.Add(new FlowWire
            {
                Id = $"{from.Node.Id}.{from.Socket.Id}->{owner.Id}.{socket.Id}",
                FromNodeId = from.Node.Id, FromSocketId = from.Socket.Id,
                ToNodeId = owner.Id, ToSocketId = socket.Id,
                Type = from.Socket.Type,
            });
            _pendingFrom = null;
            RecomputeWires();
            ShowStatus($"Wired {from.Socket.Label} → {socket.Label}", "ok");
        }
        else
        {
            // Output socket — set as the pending source. Click again to cancel.
            if (_pendingFrom is { } cur && ReferenceEquals(cur.Socket, socket))
            {
                _pendingFrom = null;
                ShowStatus("Wire start cancelled.", "info");
                return;
            }
            _pendingFrom = (owner, socket);
            ShowStatus($"From {owner.Title}.{socket.Label} — now click an input socket.", "info");
        }
    }

    private void ShowStatus(string msg, string kind)
    {
        StatusMessage = msg;
        StatusKind = kind;
    }

    /// <summary>Delete a wire (e.g. context menu on a wire path).</summary>
    public void RemoveWire(FlowWire w)
    {
        PushUndo();
        Wires.Remove(w);
        ShowStatus($"Disconnected {w.FromSocketId} → {w.ToSocketId}", "info");
    }

    /// <summary>Delete a node and every wire connected to either of its ends.
    /// Driven by the × on the node card AND the Delete key.</summary>
    public void DeleteNode(FlowNode? node)
    {
        if (node is null) return;
        PushUndo();
        var doomed = Wires.Where(w => w.FromNodeId == node.Id || w.ToNodeId == node.Id).ToList();
        foreach (var w in doomed) Wires.Remove(w);
        Nodes.Remove(node);
        if (_pendingFrom is { } pf && ReferenceEquals(pf.Node, node)) _pendingFrom = null;
        if (ReferenceEquals(Selected, node)) Selected = Nodes.FirstOrDefault();
        RecomputeWires();
        ShowStatus($"Deleted {node.Title} · {doomed.Count} wire(s) removed", "info");
    }

    // ─────────────────────────── Undo / Redo ───────────────────────────

    /// <summary>Capture+push the CURRENT state before a discrete mutation.
    /// Call this immediately BEFORE adding/removing nodes or wires.</summary>
    public void PushUndo() => _undo.Push();

    /// <summary>Push a pre-captured snapshot — used by drag &amp; param edits
    /// that grab the "before" state up front and commit it once changed.</summary>
    public void PushUndoSnapshot(GraphSnap pre) => _undo.PushSnapshot(pre);

    /// <summary>Full-fidelity, immutable clone of the graph for the undo stack.</summary>
    public GraphSnap CaptureSnapshot() => new(
        Nodes.Select(n => new NodeSnap(
            n.Id, n.Title, n.Kind, n.AccentKey, n.X, n.Y, n.Width,
            n.Inputs.Select(s => new SocketSnap(s.Id, s.Label, s.Type, s.IsInput, s.Row)).ToList(),
            n.Outputs.Select(s => new SocketSnap(s.Id, s.Label, s.Type, s.IsInput, s.Row)).ToList(),
            n.Params.Select(p => new ParamSnap(p.Label, p.Value, p.Editor)).ToList())).ToList(),
        Wires.Select(w => new WireSnap(w.Id, w.FromNodeId, w.FromSocketId, w.ToNodeId, w.ToSocketId, w.Type)).ToList(),
        Selected?.Id);

    private void ApplySnapshot(GraphSnap snap)
    {
        Nodes.Clear();
        Wires.Clear();
        foreach (var ns in snap.Nodes)
        {
            var node = new FlowNode
            {
                Id = ns.Id, Title = ns.Title, Kind = ns.Kind,
                AccentKey = ns.AccentKey, X = ns.X, Y = ns.Y, Width = ns.Width,
            };
            foreach (var s in ns.Inputs) node.Inputs.Add(new NodeSocket { Id = s.Id, Label = s.Label, Type = s.Type, IsInput = s.IsInput, Row = s.Row });
            foreach (var s in ns.Outputs) node.Outputs.Add(new NodeSocket { Id = s.Id, Label = s.Label, Type = s.Type, IsInput = s.IsInput, Row = s.Row });
            foreach (var p in ns.Params) node.Params.Add(new NodeParam { Label = p.Label, Value = p.Value, Editor = p.Editor });
            Nodes.Add(node);
        }
        foreach (var ws in snap.Wires)
            Wires.Add(new FlowWire { Id = ws.Id, FromNodeId = ws.FromNodeId, FromSocketId = ws.FromSocketId, ToNodeId = ws.ToNodeId, ToSocketId = ws.ToSocketId, Type = ws.Type });

        _pendingFrom = null;
        Selected = snap.SelectedId is null ? null : Nodes.FirstOrDefault(n => n.Id == snap.SelectedId);
        RecomputeWires();
    }

    /// <summary>Clone the selected node 30px down-right with a fresh id.</summary>
    private void DuplicateSelected()
    {
        if (Selected is null) return;
        PushUndo();
        var src = Selected;
        var copy = new FlowNode
        {
            Id = $"n{Nodes.Count + 1}_{Guid.NewGuid().ToString("N")[..4]}",
            Title = src.Title, Kind = src.Kind, AccentKey = src.AccentKey,
            X = src.X + 30, Y = src.Y + 30, Width = src.Width,
        };
        foreach (var s in src.Inputs) copy.Inputs.Add(new NodeSocket { Id = s.Id, Label = s.Label, Type = s.Type, IsInput = s.IsInput, Row = s.Row });
        foreach (var s in src.Outputs) copy.Outputs.Add(new NodeSocket { Id = s.Id, Label = s.Label, Type = s.Type, IsInput = s.IsInput, Row = s.Row });
        foreach (var p in src.Params) copy.Params.Add(new NodeParam { Label = p.Label, Value = p.Value, Editor = p.Editor });
        Nodes.Add(copy);
        Selected = copy;
        RecomputeWires();
        ShowStatus($"Duplicated {src.Title}", "ok");
    }

    /// <summary>Clear the whole canvas (undoable).</summary>
    private void NewGraph()
    {
        if (Nodes.Count == 0 && Wires.Count == 0) return;
        PushUndo();
        Nodes.Clear();
        Wires.Clear();
        _pendingFrom = null;
        Selected = null;
        ShowStatus("New graph — canvas cleared (Ctrl+Z to restore)", "info");
    }

    /// <summary>Frame + centre the whole graph in the given viewport. The
    /// view passes its live canvas size from the toolbar's Fit button.</summary>
    public void FitToView(double viewportW, double viewportH)
    {
        if (Nodes.Count == 0 || viewportW <= 0 || viewportH <= 0) return;
        double minX = Nodes.Min(n => n.X), minY = Nodes.Min(n => n.Y);
        double maxX = Nodes.Max(n => n.X + n.Width), maxY = Nodes.Max(n => n.Y + n.TotalHeight);
        double cw = Math.Max(1, maxX - minX), ch = Math.Max(1, maxY - minY);
        const double margin = 90;
        Zoom = Math.Clamp(Math.Min((viewportW - margin) / cw, (viewportH - margin) / ch), 0.4, 2.2);
        double ccx = (minX + maxX) / 2, ccy = (minY + maxY) / 2;
        // Render maps point → point*Zoom + Pan (Scale then Translate), so to put
        // the content centre at the viewport centre: Pan = viewportCentre − centre*Zoom.
        PanX = viewportW / 2 - ccx * Zoom;
        PanY = viewportH / 2 - ccy * Zoom;
    }

    // Immutable snapshot value types for the undo stack.
    public sealed record GraphSnap(List<NodeSnap> Nodes, List<WireSnap> Wires, string? SelectedId);
    public sealed record NodeSnap(string Id, string Title, NodeKind Kind, string AccentKey,
        double X, double Y, double Width, List<SocketSnap> Inputs, List<SocketSnap> Outputs, List<ParamSnap> Params);
    public sealed record SocketSnap(string Id, string Label, SocketType Type, bool IsInput, int Row);
    public sealed record ParamSnap(string Label, string Value, string Editor);
    public sealed record WireSnap(string Id, string FromNodeId, string FromSocketId, string ToNodeId, string ToSocketId, SocketType Type);

    private void SeedSampleGraph()
    {
        // Empress preset — standard SDXL-style image flow:
        // LoadCheckpoint → CLIPTextEncode (positive) ─┐
        //                  CLIPTextEncode (negative) ─┼─→ KSampler → VAEDecode → SaveImage
        //                  EmptyLatentImage ──────────┘
        var ckpt = new FlowNode
        {
            Id = "ckpt", Title = "Load Checkpoint", Kind = NodeKind.LoadCheckpoint,
            AccentKey = "BrushClipGold", X = 60, Y = 60, Width = 230,
        };
        ckpt.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, IsInput = false, Row = 0 });
        ckpt.Outputs.Add(new NodeSocket { Id = "clip", Label = "CLIP", Type = SocketType.Clip, IsInput = false, Row = 1 });
        ckpt.Outputs.Add(new NodeSocket { Id = "vae", Label = "VAE", Type = SocketType.Vae, IsInput = false, Row = 2 });
        ckpt.Params.Add(new NodeParam { Label = "ckpt_name", Value = "chanthra-sora-lyra-2.4.safetensors" });

        var posPrompt = new FlowNode
        {
            Id = "pos", Title = "CLIP Text Encode (positive)", Kind = NodeKind.CLIPTextEncode,
            AccentKey = "BrushClipAmber", X = 360, Y = 30, Width = 260,
        };
        posPrompt.Inputs.Add(new NodeSocket { Id = "clip", Label = "clip", Type = SocketType.Clip, IsInput = true, Row = 0 });
        posPrompt.Outputs.Add(new NodeSocket { Id = "cond", Label = "CONDITIONING", Type = SocketType.Conditioning, IsInput = false, Row = 0 });
        posPrompt.Params.Add(new NodeParam { Label = "text", Value = "the empress beneath a gold halo, silk crimson veil, cinematic", Editor = "textarea" });

        var negPrompt = new FlowNode
        {
            Id = "neg", Title = "CLIP Text Encode (negative)", Kind = NodeKind.CLIPTextEncode,
            AccentKey = "BrushClipCrimson", X = 360, Y = 220, Width = 260,
        };
        negPrompt.Inputs.Add(new NodeSocket { Id = "clip", Label = "clip", Type = SocketType.Clip, IsInput = true, Row = 0 });
        negPrompt.Outputs.Add(new NodeSocket { Id = "cond", Label = "CONDITIONING", Type = SocketType.Conditioning, IsInput = false, Row = 0 });
        negPrompt.Params.Add(new NodeParam { Label = "text", Value = "blurry, lowres, deformed", Editor = "textarea" });

        var latent = new FlowNode
        {
            Id = "latent", Title = "Empty Latent Image", Kind = NodeKind.EmptyLatentImage,
            AccentKey = "BrushClipPlum", X = 360, Y = 410, Width = 230,
        };
        latent.Outputs.Add(new NodeSocket { Id = "latent", Label = "LATENT", Type = SocketType.Latent, IsInput = false, Row = 0 });
        latent.Params.Add(new NodeParam { Label = "width", Value = "1024" });
        latent.Params.Add(new NodeParam { Label = "height", Value = "1024" });
        latent.Params.Add(new NodeParam { Label = "batch_size", Value = "1" });

        var sampler = new FlowNode
        {
            Id = "sampler", Title = "K Sampler", Kind = NodeKind.KSampler,
            AccentKey = "BrushClipGold", X = 700, Y = 110, Width = 240,
        };
        sampler.Inputs.Add(new NodeSocket { Id = "model", Label = "model", Type = SocketType.Model, IsInput = true, Row = 0 });
        sampler.Inputs.Add(new NodeSocket { Id = "positive", Label = "positive", Type = SocketType.Conditioning, IsInput = true, Row = 1 });
        sampler.Inputs.Add(new NodeSocket { Id = "negative", Label = "negative", Type = SocketType.Conditioning, IsInput = true, Row = 2 });
        sampler.Inputs.Add(new NodeSocket { Id = "latent", Label = "latent_image", Type = SocketType.Latent, IsInput = true, Row = 3 });
        sampler.Outputs.Add(new NodeSocket { Id = "latent", Label = "LATENT", Type = SocketType.Latent, IsInput = false, Row = 0 });
        sampler.Params.Add(new NodeParam { Label = "seed", Value = "2814" });
        sampler.Params.Add(new NodeParam { Label = "steps", Value = "28" });
        sampler.Params.Add(new NodeParam { Label = "cfg", Value = "7.5" });
        sampler.Params.Add(new NodeParam { Label = "sampler_name", Value = "dpmpp_2m" });
        sampler.Params.Add(new NodeParam { Label = "scheduler", Value = "karras" });

        var vae = new FlowNode
        {
            Id = "vae", Title = "VAE Decode", Kind = NodeKind.VAEDecode,
            AccentKey = "BrushClipAmber", X = 1000, Y = 140, Width = 220,
        };
        vae.Inputs.Add(new NodeSocket { Id = "samples", Label = "samples", Type = SocketType.Latent, IsInput = true, Row = 0 });
        vae.Inputs.Add(new NodeSocket { Id = "vae", Label = "vae", Type = SocketType.Vae, IsInput = true, Row = 1 });
        vae.Outputs.Add(new NodeSocket { Id = "image", Label = "IMAGE", Type = SocketType.Image, IsInput = false, Row = 0 });

        var save = new FlowNode
        {
            Id = "save", Title = "Save Image", Kind = NodeKind.SaveImage,
            AccentKey = "BrushClipCrimson", X = 1280, Y = 160, Width = 220,
        };
        save.Inputs.Add(new NodeSocket { Id = "images", Label = "images", Type = SocketType.Image, IsInput = true, Row = 0 });
        save.Params.Add(new NodeParam { Label = "filename_prefix", Value = "empress" });

        Nodes.Add(ckpt);
        Nodes.Add(posPrompt);
        Nodes.Add(negPrompt);
        Nodes.Add(latent);
        Nodes.Add(sampler);
        Nodes.Add(vae);
        Nodes.Add(save);

        AddWire("ckpt", "model", "sampler", "model", SocketType.Model);
        AddWire("ckpt", "clip", "pos", "clip", SocketType.Clip);
        AddWire("ckpt", "clip", "neg", "clip", SocketType.Clip);
        AddWire("ckpt", "vae", "vae", "vae", SocketType.Vae);
        AddWire("pos", "cond", "sampler", "positive", SocketType.Conditioning);
        AddWire("neg", "cond", "sampler", "negative", SocketType.Conditioning);
        AddWire("latent", "latent", "sampler", "latent", SocketType.Latent);
        AddWire("sampler", "latent", "vae", "samples", SocketType.Latent);
        AddWire("vae", "image", "save", "images", SocketType.Image);

        Selected = sampler;
    }

    private void AddWire(string fromNode, string fromSocket, string toNode, string toSocket, SocketType type)
    {
        Wires.Add(new FlowWire
        {
            Id = $"{fromNode}.{fromSocket}->{toNode}.{toSocket}",
            FromNodeId = fromNode, FromSocketId = fromSocket,
            ToNodeId = toNode, ToSocketId = toSocket, Type = type,
        });
    }

    /// <summary>
    /// Recompute the bezier <see cref="Geometry"/> for every wire based on
    /// the current node positions. Call this from the drag handler after
    /// each mouse move so the wires "snap" to the moved node.
    /// </summary>
    public void RecomputeWires()
    {
        foreach (var w in Wires)
        {
            var src = Nodes.FirstOrDefault(n => n.Id == w.FromNodeId);
            var dst = Nodes.FirstOrDefault(n => n.Id == w.ToNodeId);
            if (src is null || dst is null) continue;

            var srcSocket = src.Outputs.FirstOrDefault(s => s.Id == w.FromSocketId);
            var dstSocket = dst.Inputs.FirstOrDefault(s => s.Id == w.ToSocketId);
            if (srcSocket is null || dstSocket is null) continue;

            var p0 = src.OutputPin(srcSocket.Row);
            var p3 = dst.InputPin(dstSocket.Row);
            // Bezier control points pull horizontally — feels natural for
            // L-to-R flow editors. Strength scales with horizontal distance.
            double strength = Math.Max(60, Math.Abs(p3.X - p0.X) * 0.5);
            var p1 = new Point(p0.X + strength, p0.Y);
            var p2 = new Point(p3.X - strength, p3.Y);

            var fig = new PathFigure { StartPoint = p0, IsFilled = false };
            fig.Segments.Add(new BezierSegment(p1, p2, p3, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            w.Geometry = geo;
        }
    }

    /// <summary>
    /// Topological L→R auto-arrange. Groups nodes by depth from any node
    /// with no incoming wires, then stacks them vertically per column.
    /// </summary>
    private void AutoArrange()
    {
        if (Nodes.Count == 0) return;
        PushUndo();

        var depth = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var n in Nodes) depth[n.Id] = 0;
        // Iterate a few times — graph is small, depth converges quickly.
        for (int iter = 0; iter < 32; iter++)
        {
            bool changed = false;
            foreach (var w in Wires)
            {
                if (depth.TryGetValue(w.FromNodeId, out var fd) &&
                    depth.TryGetValue(w.ToNodeId, out var td) &&
                    td < fd + 1)
                {
                    depth[w.ToNodeId] = fd + 1;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        const double colW = 300, rowH = 220;
        var byCol = Nodes.GroupBy(n => depth[n.Id]).OrderBy(g => g.Key);
        foreach (var col in byCol)
        {
            int idx = 0;
            foreach (var n in col)
            {
                n.X = 60 + col.Key * colW;
                n.Y = 60 + idx * rowH;
                idx++;
            }
        }
        RecomputeWires();
    }

    private void AddNodeFromKind(string? kindKey)
    {
        if (string.IsNullOrEmpty(kindKey)) return;
        if (!Enum.TryParse<NodeKind>(kindKey, out var kind)) return;

        PushUndo();
        var n = new FlowNode
        {
            Id = $"n{Nodes.Count + 1}",
            Kind = kind,
            Title = HumaniseKind(kind),
            AccentKey = AccentForKind(kind),
            X = 80 - PanX,
            Y = 80 - PanY,
            Width = 230,
        };
        SeedSocketsForKind(n, kind);
        Nodes.Add(n);
        Selected = n;
        RecomputeWires();
    }

    /// <summary>
    /// Populate the socket layout to match the real ComfyUI node of the
    /// given <see cref="NodeKind"/>. The socket IDs ARE the ComfyUI input
    /// keys — NodeFlowConverter relies on that to emit valid wire targets.
    /// </summary>
    private static void SeedSocketsForKind(FlowNode n, NodeKind kind)
    {
        switch (kind)
        {
            case NodeKind.LoadCheckpoint:
                n.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "clip",  Label = "CLIP",  Type = SocketType.Clip,  Row = 1 });
                n.Outputs.Add(new NodeSocket { Id = "vae",   Label = "VAE",   Type = SocketType.Vae,   Row = 2 });
                n.Params.Add(new NodeParam { Label = "ckpt_name", Value = "v1-5-pruned-emaonly.safetensors" });
                break;
            case NodeKind.CLIPTextEncode:
                n.Inputs.Add(new NodeSocket { Id = "clip", Label = "clip", Type = SocketType.Clip, IsInput = true, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "cond", Label = "CONDITIONING", Type = SocketType.Conditioning, Row = 0 });
                n.Params.Add(new NodeParam { Label = "text", Value = "", Editor = "textarea" });
                break;
            case NodeKind.KSampler:
                n.Inputs.Add(new NodeSocket { Id = "model",    Label = "model",        Type = SocketType.Model,        IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "positive", Label = "positive",     Type = SocketType.Conditioning, IsInput = true, Row = 1 });
                n.Inputs.Add(new NodeSocket { Id = "negative", Label = "negative",     Type = SocketType.Conditioning, IsInput = true, Row = 2 });
                n.Inputs.Add(new NodeSocket { Id = "latent_image", Label = "latent_image", Type = SocketType.Latent,   IsInput = true, Row = 3 });
                n.Outputs.Add(new NodeSocket { Id = "latent", Label = "LATENT", Type = SocketType.Latent, Row = 0 });
                n.Params.Add(new NodeParam { Label = "seed", Value = "0" });
                n.Params.Add(new NodeParam { Label = "steps", Value = "28" });
                n.Params.Add(new NodeParam { Label = "cfg", Value = "7.5" });
                n.Params.Add(new NodeParam { Label = "sampler_name", Value = "dpmpp_2m" });
                n.Params.Add(new NodeParam { Label = "scheduler", Value = "karras" });
                n.Params.Add(new NodeParam { Label = "denoise", Value = "1.0" });
                break;
            case NodeKind.VAEDecode:
                n.Inputs.Add(new NodeSocket { Id = "samples", Label = "samples", Type = SocketType.Latent, IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "vae",     Label = "vae",     Type = SocketType.Vae,    IsInput = true, Row = 1 });
                n.Outputs.Add(new NodeSocket { Id = "image", Label = "IMAGE", Type = SocketType.Image, Row = 0 });
                break;
            case NodeKind.SaveImage:
                n.Inputs.Add(new NodeSocket { Id = "images", Label = "images", Type = SocketType.Image, IsInput = true, Row = 0 });
                n.Params.Add(new NodeParam { Label = "filename_prefix", Value = "chanthra" });
                break;
            case NodeKind.EmptyLatentImage:
                n.Outputs.Add(new NodeSocket { Id = "latent", Label = "LATENT", Type = SocketType.Latent, Row = 0 });
                n.Params.Add(new NodeParam { Label = "width", Value = "1024" });
                n.Params.Add(new NodeParam { Label = "height", Value = "1024" });
                n.Params.Add(new NodeParam { Label = "batch_size", Value = "1" });
                break;
            case NodeKind.LoadImage:
                n.Outputs.Add(new NodeSocket { Id = "image", Label = "IMAGE", Type = SocketType.Image, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "mask",  Label = "MASK",  Type = SocketType.Image, Row = 1 });
                n.Params.Add(new NodeParam { Label = "image", Value = "reference.png" });
                break;
            case NodeKind.LoraLoader:
                n.Inputs.Add(new NodeSocket { Id = "model", Label = "model", Type = SocketType.Model, IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "clip",  Label = "clip",  Type = SocketType.Clip,  IsInput = true, Row = 1 });
                n.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "clip",  Label = "CLIP",  Type = SocketType.Clip,  Row = 1 });
                n.Params.Add(new NodeParam { Label = "lora_name", Value = "" });
                n.Params.Add(new NodeParam { Label = "strength_model", Value = "1.0" });
                n.Params.Add(new NodeParam { Label = "strength_clip",  Value = "1.0" });
                break;
            case NodeKind.ControlNetApply:
                n.Inputs.Add(new NodeSocket { Id = "conditioning", Label = "conditioning", Type = SocketType.Conditioning, IsInput = true, Row = 0 });
                n.Inputs.Add(new NodeSocket { Id = "control_net",  Label = "control_net",  Type = SocketType.Model,        IsInput = true, Row = 1 });
                n.Inputs.Add(new NodeSocket { Id = "image",        Label = "image",        Type = SocketType.Image,        IsInput = true, Row = 2 });
                n.Outputs.Add(new NodeSocket { Id = "conditioning", Label = "CONDITIONING", Type = SocketType.Conditioning, Row = 0 });
                n.Params.Add(new NodeParam { Label = "strength", Value = "1.0" });
                break;
            case NodeKind.AnimateDiff:
                n.Inputs.Add(new NodeSocket { Id = "model", Label = "model", Type = SocketType.Model, IsInput = true, Row = 0 });
                n.Outputs.Add(new NodeSocket { Id = "model", Label = "MODEL", Type = SocketType.Model, Row = 0 });
                n.Params.Add(new NodeParam { Label = "motion_model", Value = "" });
                n.Params.Add(new NodeParam { Label = "beta_schedule", Value = "autoselect" });
                break;
        }
    }

    private static string HumaniseKind(NodeKind k) => k switch
    {
        NodeKind.LoadCheckpoint  => "Load Checkpoint",
        NodeKind.CLIPTextEncode  => "CLIP Text Encode",
        NodeKind.KSampler        => "K Sampler",
        NodeKind.VAEDecode       => "VAE Decode",
        NodeKind.SaveImage       => "Save Image",
        NodeKind.EmptyLatentImage => "Empty Latent Image",
        NodeKind.LoadImage       => "Load Image",
        NodeKind.LoraLoader      => "Load LoRA",
        NodeKind.ControlNetApply => "ControlNet Apply",
        NodeKind.AnimateDiff     => "AnimateDiff",
        _ => k.ToString(),
    };

    private static string AccentForKind(NodeKind k) => k switch
    {
        NodeKind.LoadCheckpoint or NodeKind.KSampler or NodeKind.LoraLoader => "BrushClipGold",
        NodeKind.CLIPTextEncode or NodeKind.VAEDecode                       => "BrushClipAmber",
        NodeKind.EmptyLatentImage or NodeKind.SaveImage                     => "BrushClipPlum",
        _ => "BrushClipPlum",
    };
}
