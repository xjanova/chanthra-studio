using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

public enum SocketType
{
    Model, Clip, Vae, Latent, Image, Conditioning, Number, String
}

public enum NodeKind
{
    LoadCheckpoint, CLIPTextEncode, KSampler, VAEDecode, SaveImage,
    EmptyLatentImage, LoadImage, LoraLoader, ControlNetApply, AnimateDiff
}

public sealed class NodeSocket : ObservableObject
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public SocketType Type { get; init; }
    public bool IsInput { get; init; }

    // 0-based index used for vertical layout inside the node card
    public int Row { get; init; }
}

public sealed class NodeParam : ObservableObject
{
    public string Label { get; init; } = "";

    private string _value = "";
    public string Value { get => _value; set => SetProperty(ref _value, value); }

    // Optional hint for the editor (e.g. "textarea", "slider:1-30", "combo:euler|dpmpp_2m")
    public string Editor { get; init; } = "text";
}

public sealed class FlowNode : ObservableObject
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public NodeKind Kind { get; set; }
    // Resource key for header accent ("BrushClipPlum", "BrushClipGold", "BrushClipCrimson", "BrushClipAmber")
    public string AccentKey { get; set; } = "BrushClipPlum";

    private double _x;
    public double X { get => _x; set => SetProperty(ref _x, value); }

    private double _y;
    public double Y { get => _y; set => SetProperty(ref _y, value); }

    public double Width { get; set; } = 220;

    public ObservableCollection<NodeSocket> Inputs { get; } = new();
    public ObservableCollection<NodeSocket> Outputs { get; } = new();
    public ObservableCollection<NodeParam> Params { get; } = new();

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    // Header height + per-row height inside the node card. Used by
    // FlowGraph to compute socket pin positions for wire endpoints.
    public const double HeaderHeight = 32;
    public const double RowHeight = 24;
    public const double ParamsBlockExtra = 8;

    public double TotalHeight
    {
        get
        {
            int rows = System.Math.Max(Inputs.Count, Outputs.Count);
            double paramsExtra = Params.Count > 0 ? Params.Count * RowHeight + ParamsBlockExtra : 0;
            return HeaderHeight + rows * RowHeight + paramsExtra + 12;
        }
    }

    public Point InputPin(int row) =>
        new(X, Y + HeaderHeight + row * RowHeight + RowHeight / 2);

    public Point OutputPin(int row) =>
        new(X + Width, Y + HeaderHeight + row * RowHeight + RowHeight / 2);
}

public sealed class FlowWire : ObservableObject
{
    public string Id { get; set; } = "";
    public string FromNodeId { get; set; } = "";
    public string FromSocketId { get; set; } = "";
    public string ToNodeId { get; set; } = "";
    public string ToSocketId { get; set; } = "";
    public SocketType Type { get; set; }

    private Geometry? _geometry;
    public Geometry? Geometry { get => _geometry; set => SetProperty(ref _geometry, value); }
}

public sealed class FlowGraph
{
    public ObservableCollection<FlowNode> Nodes { get; } = new();
    public ObservableCollection<FlowWire> Wires { get; } = new();
}
