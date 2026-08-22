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

    /// <summary>Coarse bucket used for the wire and socket colour.</summary>
    public SocketType Type { get; init; }

    /// <summary>
    /// The server's own type string — "MODEL", "CONDITIONING", "MASK", or
    /// whatever a custom node pack invents.
    ///
    /// Kept alongside <see cref="Type"/> because the colour bucket is lossy:
    /// IMAGE and MASK share one, and connecting them is not valid. Empty on
    /// sockets that predate the schema-driven palette, which the compatibility
    /// check treats as "allow" so older graphs stay editable.
    /// </summary>
    public string TypeName { get; init; } = "";

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

    /// <summary>
    /// Valid values, read from the server's own node schema.
    ///
    /// <b>Why this is not a hard-coded list.</b> Every parameter in this editor
    /// used to be a free-text box, including checkpoint and LoRA filenames and
    /// sampler names — with defaults like "v1-5-pruned-emaonly.safetensors"
    /// that were a guess about what the user had installed. So building a graph
    /// and pressing Run failed on a typo or a file that was never there, and
    /// the error came back from the server naming a node number. Filled from
    /// /object_info when the engine is reachable; empty means free text, which
    /// is still correct for a prompt.
    /// </summary>
    public ObservableCollection<string> Choices { get; } = new();

    public bool HasChoices => Choices.Count > 0;

    /// <summary>Replace the option list and keep the selection valid.</summary>
    public void SetChoices(System.Collections.Generic.IEnumerable<string> options)
    {
        Choices.Clear();
        foreach (var o in options) Choices.Add(o);

        // A value the server has never heard of would sit in the box looking
        // chosen and fail validation at submit. If the current one is not on
        // the list, take the first that is.
        if (Choices.Count > 0 && !Choices.Contains(Value))
            Value = Choices[0];

        OnPropertyChanged(nameof(HasChoices));
    }
}

public sealed class FlowNode : ObservableObject
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>
    /// Which of the curated palette entries this node is drawn as. Purely
    /// cosmetic now — it picks the header accent and the starter socket
    /// layout. It is NOT what gets written to ComfyUI.
    /// </summary>
    public NodeKind Kind { get; set; }

    /// <summary>
    /// The real ComfyUI <c>class_type</c>, and the only thing the converter
    /// emits.
    ///
    /// <b>Why this exists.</b> The editor used to derive class_type from
    /// <see cref="Kind"/>, an enum of ten curated node types, and the reverse
    /// lookup fell back to <c>LoraLoader</c> for anything it did not
    /// recognise. So opening any real workflow — flux, wan, hunyuan, every
    /// graph this app ships — turned each unfamiliar node into a LoraLoader on
    /// screen, and saving it wrote LoraLoader back out. Loading a workflow
    /// destroyed it. Carrying the original string means an unknown node
    /// round-trips untouched.
    /// </summary>
    public string ClassType { get; set; } = "";
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
