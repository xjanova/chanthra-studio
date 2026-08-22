using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Providers.ComfyUI;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>A ready-made graph the user can drop onto the canvas.</summary>
public sealed class GraphTemplateVm
{
    public GraphTemplateVm(WorkflowDescriptor descriptor) => Descriptor = descriptor;

    public WorkflowDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;
    public string Spec => Descriptor.Spec;
    public string Description => Descriptor.Description;
    public string Origin => Descriptor.IsBuiltin ? "มากับสตูดิโอ" : "ของคุณเอง";
}

/// <summary>
/// The palette half of the node editor: every node the engine can run, and a
/// gallery of ready graphs.
///
/// <b>Two halves on purpose.</b> Parity is the palette — a stock engine
/// reports 849 node types, and anything the editor cannot place is a workflow
/// the user has to go and build elsewhere. The advantage is the gallery: most
/// of the time nobody wants to assemble a sampler, a VAE decode and a saver by
/// hand, they want a working graph to start from and then change one thing.
/// </summary>
public sealed partial class NodeFlowViewModel
{
    private ComfyNodeCatalog? _catalog;

    public ObservableCollection<ComfyNodeDef> PaletteResults { get; } = new();
    public ObservableCollection<GraphTemplateVm> Templates { get; } = new();

    public IRelayCommand<ComfyNodeDef> AddCatalogNodeCommand { get; private set; } = null!;
    public IRelayCommand<GraphTemplateVm> LoadTemplateCommand { get; private set; } = null!;
    public IAsyncRelayCommand RefreshCatalogCommand { get; private set; } = null!;

    private void InitPalette()
    {
        AddCatalogNodeCommand = new RelayCommand<ComfyNodeDef>(AddCatalogNode);
        LoadTemplateCommand = new RelayCommand<GraphTemplateVm>(LoadTemplate);
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshSchemaAsync);

        // The cached list means the palette is populated before the engine has
        // ever been started in this session — opening the editor should not
        // require a cold start to show anything.
        _catalog = ComfyNodeCatalog.LoadCached();
        ApplyPaletteFilter();
        LoadTemplates();
    }

    private string _paletteQuery = "";
    public string PaletteQuery
    {
        get => _paletteQuery;
        set { if (SetProperty(ref _paletteQuery, value)) ApplyPaletteFilter(); }
    }

    private string _catalogStatus = "";
    /// <summary>How many nodes are available, and from where.</summary>
    public string CatalogStatus { get => _catalogStatus; private set => SetProperty(ref _catalogStatus, value); }

    /// <summary>
    /// Shown count is capped. Nine hundred rows is not a list anyone reads —
    /// it is a list they search — and saying how many were hidden is better
    /// than silently truncating.
    /// </summary>
    private const int MaxShown = 120;

    private void ApplyPaletteFilter()
    {
        PaletteResults.Clear();
        if (_catalog is null)
        {
            CatalogStatus = "ยังไม่รู้จักโหนดของเอนจิน — เปิดเอนจินแล้วกด ↻ เพื่อดึงรายการ";
            return;
        }

        var matches = _catalog.Search(_paletteQuery).ToList();
        foreach (var n in matches.Take(MaxShown)) PaletteResults.Add(n);

        CatalogStatus = matches.Count > MaxShown
            ? $"{matches.Count} โหนด · แสดง {MaxShown} แรก — พิมพ์ค้นหาให้แคบลง"
            : $"{matches.Count} โหนด จากทั้งหมด {_catalog.Nodes.Count}";
    }

    private void AddCatalogNode(ComfyNodeDef? def)
    {
        if (def is null) return;
        PushUndo();
        var node = NodeFactory.Create(def, 80 - PanX, 80 - PanY,
            $"n{Nodes.Count + 1}_{Guid.NewGuid().ToString("N")[..4]}");
        Nodes.Add(node);
        Selected = node;
        RecomputeWires();
        ShowStatus($"เพิ่ม {def.DisplayName}", "ok");
    }

    // ------------------------------------------------------------ templates

    private void LoadTemplates()
    {
        Templates.Clear();
        var studio = (System.Windows.Application.Current as App)?.Studio;
        if (studio is null) return;
        foreach (var w in studio.Workflows.All) Templates.Add(new GraphTemplateVm(w));
    }

    private void LoadTemplate(GraphTemplateVm? template)
    {
        if (template is null) return;
        try
        {
            var loaded = NodeFlowConverter.LoadFromFile(template.Descriptor.Path);
            PushUndo();
            Nodes.Clear();
            Wires.Clear();
            foreach (var n in loaded.Nodes) Nodes.Add(n);
            foreach (var w in loaded.Wires) Wires.Add(w);
            GraphName = template.Descriptor.Name;
            _pendingFrom = null;

            // Re-derive sockets and pickers from the schema. A file only records
            // the inputs that were wired or set, so a graph loaded raw has no
            // free sockets to connect anything new to.
            ApplySchemaToAll();
            RehydrateSockets();

            Selected = Nodes.FirstOrDefault();
            AutoArrange();
            RecomputeWires();
            ShowStatus($"โหลด {template.DisplayName} · {Nodes.Count} โหนด — แก้ค่าแล้วกด Run ได้เลย", "ok");
        }
        catch (Exception ex)
        {
            ShowStatus($"โหลดเทมเพลตไม่สำเร็จ: {ex.Message}", "err");
        }
    }

    /// <summary>
    /// Give every node the full socket set its class actually has.
    ///
    /// A saved workflow lists only the inputs that carry a value or a wire, so
    /// a node loaded from one arrives missing the sockets nothing happened to
    /// be connected to — and the user cannot wire what is not drawn. The schema
    /// knows the rest.
    /// </summary>
    private void RehydrateSockets()
    {
        if (_catalog is null) return;

        foreach (var node in Nodes)
        {
            var def = _catalog.Find(string.IsNullOrEmpty(node.ClassType)
                ? NodeFlowConverter.ClassTypeFor(node.Kind)
                : node.ClassType);
            if (def is null) continue;

            node.Title = def.DisplayName;

            // Inputs: keep the ones already present (they may carry wires that
            // reference them by id) and append whatever the schema adds.
            var have = new HashSet<string>(node.Inputs.Select(s => s.Id), StringComparer.Ordinal);
            var row = node.Inputs.Count;
            foreach (var input in def.Inputs.Where(i => i.IsLink && !have.Contains(i.Name)))
            {
                node.Inputs.Add(new NodeSocket
                {
                    Id = input.Name,
                    Label = input.Optional ? input.Name + " ?" : input.Name,
                    Type = NodeFactory.MapSocket(input.TypeName),
                    TypeName = input.TypeName,
                    IsInput = true,
                    Row = row++,
                });
            }

            // Outputs are positional — a wire records the index, so the list has
            // to be at least as long as the schema says, with the right labels.
            for (var i = 0; i < def.Outputs.Count; i++)
            {
                if (i < node.Outputs.Count)
                {
                    // Existing placeholder sockets carry no type; adopting the
                    // schema's is what makes the compatibility check work on a
                    // loaded graph.
                    var existing = node.Outputs[i];
                    if (string.IsNullOrEmpty(existing.TypeName))
                    {
                        node.Outputs[i] = new NodeSocket
                        {
                            Id = existing.Id,
                            Label = def.Outputs[i].Name,
                            Type = NodeFactory.MapSocket(def.Outputs[i].TypeName),
                            TypeName = def.Outputs[i].TypeName,
                            IsInput = false,
                            Row = existing.Row,
                        };
                    }
                    continue;
                }
                node.Outputs.Add(new NodeSocket
                {
                    Id = $"out{i}",
                    Label = def.Outputs[i].Name,
                    Type = NodeFactory.MapSocket(def.Outputs[i].TypeName),
                    TypeName = def.Outputs[i].TypeName,
                    IsInput = false,
                    Row = i,
                });
            }
        }
    }
}
