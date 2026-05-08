using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ChanthraStudio.Models;
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

    public IRelayCommand ZoomInCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ZoomResetCommand { get; }
    public IRelayCommand AutoArrangeCommand { get; }
    public IRelayCommand<string> AddNodeCommand { get; }

    public NodeFlowViewModel()
    {
        SeedSampleGraph();

        ZoomInCommand = new RelayCommand(() => Zoom += 0.1);
        ZoomOutCommand = new RelayCommand(() => Zoom -= 0.1);
        ZoomResetCommand = new RelayCommand(() => { Zoom = 1.0; PanX = 0; PanY = 0; });
        AutoArrangeCommand = new RelayCommand(AutoArrange);
        AddNodeCommand = new RelayCommand<string>(AddNodeFromKind);

        RecomputeWires();
    }

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

        var n = new FlowNode
        {
            Id = $"n{Nodes.Count + 1}",
            Kind = kind,
            Title = kind.ToString(),
            AccentKey = "BrushClipPlum",
            X = 80 - PanX,
            Y = 80 - PanY,
            Width = 220,
        };
        // Minimal default sockets so users can wire it up
        n.Inputs.Add(new NodeSocket { Id = "in0", Label = "in", Type = SocketType.Image, IsInput = true, Row = 0 });
        n.Outputs.Add(new NodeSocket { Id = "out0", Label = "OUT", Type = SocketType.Image, IsInput = false, Row = 0 });
        Nodes.Add(n);
        Selected = n;
        RecomputeWires();
    }
}
