using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Providers.ComfyUI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// Inventory view of every model file ComfyUI has installed: checkpoints,
/// LoRAs, UNETs (Flux/Hunyuan/WAN), VAEs, CLIP encoders, CLIP-Vision.
/// Drives the <c>ModelsView</c> sidebar.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly StudioContext? _ctx;

    /// <summary>
    /// False for the design-time instance the XAML constructs. The view uses
    /// this to decide whether to replace its DataContext — testing "has this
    /// been populated yet" instead let seeded demo data masquerade as a live
    /// ViewModel forever.
    /// </summary>
    public bool IsLive => _ctx is not null;

    public ObservableCollection<ModelGroup> Groups { get; } = new();

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) ApplyFilter(); }
    }

    private string _serverUrl = "—";
    public string ServerUrl { get => _serverUrl; set => SetProperty(ref _serverUrl, value); }

    private string _serverStatus = "checking...";
    public string ServerStatus { get => _serverStatus; set => SetProperty(ref _serverStatus, value); }

    /// <summary>"ok" / "warn" / "err" — drives the status pill colour.</summary>
    private string _statusKind = "warn";
    public string StatusKind { get => _statusKind; set => SetProperty(ref _statusKind, value); }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (SetProperty(ref _isRefreshing, value)) ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged(); }
    }

    private string _totalLabel = "—";
    public string TotalLabel { get => _totalLabel; set => SetProperty(ref _totalLabel, value); }

    /// <summary>False when the server listed nothing — for either reason.</summary>
    public bool HasModels => Groups.Any(g => g.TotalCount > 0);

    /// <summary>
    /// Which kind of empty this is. "No models" and "no server" look identical
    /// on screen and have completely different fixes, so the panel says which
    /// one it is rather than leaving the user to guess.
    /// </summary>
    public string EmptyModelsHint => StatusKind == "err"
        ? "ต่อกับเซิร์ฟเวอร์ ComfyUI ไม่ได้ — เปิด ComfyUI แล้วกด Refresh หรือแก้ URL ในหน้า Settings "
          + "(ถ้ายังไม่มีเครื่อง ใช้เส้นทาง Rented GPU ได้ สตูดิโอจะติดตั้งและโหลดโมเดลให้เอง)"
        : "ต่อเซิร์ฟเวอร์ได้แล้ว แต่ยังไม่มีไฟล์โมเดลอยู่ในเครื่อง — วางไฟล์ไว้ใน ComfyUI/models/ แล้วกด Refresh";

    public IRelayCommand RefreshCommand { get; }

    // Cache of the unfiltered lists so re-filtering is cheap.
    private readonly Dictionary<string, List<string>> _raw = new();

    public ModelsViewModel() : this(null) { }

    public ModelsViewModel(StudioContext? ctx)
    {
        _ctx = ctx;
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !_isRefreshing);
        InitComfyCommands();
        InitEngine();
        if (_ctx is null)
        {
            // Design-time placeholders so the XAML preview isn't empty.
            SeedDesignTime();
        }
        else
        {
            ServerUrl = _ctx.Settings.ComfyUiUrl;
            // Settings load from the database, not from the server, so they
            // must be on screen whether or not ComfyUI is reachable.
            LoadComfySettings();
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (_ctx is null) return;
        IsRefreshing = true;
        ServerStatus = "querying ComfyUI...";
        StatusKind = "warn";
        try
        {
            using var client = new ComfyUiClient(_ctx.Settings.ComfyUiUrl);
            var probe = await client.ProbeAsync();
            if (!probe.Ok)
            {
                // "Target machine actively refused it" is Windows for "nothing
                // is listening": say that, and where, in words a user can act on.
                var refused = probe.Status?.Contains("refused", StringComparison.OrdinalIgnoreCase) == true;
                ServerStatus = refused
                    ? $"ComfyUI ยังไม่ได้เปิดที่ {_ctx.Settings.ComfyUiUrl} — ติดตั้งหรือเปิดเอนจินด้านล่าง หรือแก้ URL ในหน้า Settings"
                    : $"ติดต่อ ComfyUI ที่ {_ctx.Settings.ComfyUiUrl} ไม่ได้ · {probe.Status}";
                StatusKind = "err";
                // Not just "0 queued": this early return used to skip the queue
                // read entirely, leaving the card showing its "—" placeholder
                // as though the number were still loading.
                QueueLabel = "unreachable";
                _raw.Clear();
                ApplyFilter();
                return;
            }
            ServerStatus = $"ok · {probe.Device ?? "GPU"}  ·  VRAM {probe.VramFree / 1_073_741_824.0:F1} / {probe.VramTotal / 1_073_741_824.0:F1} GB";
            StatusKind = "ok";

            _raw["Checkpoints"] = await client.GetAvailableCheckpointsAsync();
            _raw["UNet (Flux / Hunyuan / WAN)"] = await client.GetAvailableUnetsAsync();
            _raw["LoRA"] = await client.GetAvailableLorasAsync();
            _raw["VAE"] = await client.GetAvailableVaesAsync();
            _raw["CLIP encoders"] = await client.GetAvailableClipsAsync();
            _raw["CLIP Vision"] = await client.GetAvailableClipVisionAsync();

            // Populate the pickers from what this server actually accepts.
            var (samplers, schedulers) = await client.GetSamplerOptionsAsync();
            Replace(Samplers, samplers);
            Replace(Schedulers, schedulers);
            Replace(CheckpointChoices, _raw["Checkpoints"]);
            Replace(LoraChoices, _raw["LoRA"]);

            // A saved value the server has never heard of would sit in the
            // combo box looking selected while failing validation at submit.
            if (samplers.Count > 0 && !samplers.Contains(SamplerName)) SamplerName = samplers[0];
            if (schedulers.Count > 0 && !schedulers.Contains(Scheduler)) Scheduler = schedulers[0];

            await RefreshQueueAsync();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ServerStatus = "error · " + ex.Message;
            StatusKind = "err";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Refill an observable collection in place — rebinding the whole
    /// collection would drop the ComboBox's current selection.</summary>
    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void ApplyFilter()
    {
        Groups.Clear();
        var f = (_filter ?? "").Trim();
        int total = 0;
        foreach (var (name, items) in _raw)
        {
            var filtered = string.IsNullOrEmpty(f)
                ? items
                : items.Where(i => i.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            total += filtered.Count;
            var group = new ModelGroup
            {
                Name = name,
                Count = filtered.Count,
                TotalCount = items.Count,
            };
            foreach (var i in filtered) group.Items.Add(i);
            Groups.Add(group);
        }
        TotalLabel = $"{total} model{(total == 1 ? "" : "s")} installed";
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(EmptyModelsHint));
    }

    private void SeedDesignTime()
    {
        ServerUrl = "http://127.0.0.1:8188";
        ServerStatus = "ok · NVIDIA RTX 4090  ·  VRAM 22.4 / 24.0 GB";
        StatusKind = "ok";
        var demo = new (string name, string[] items)[]
        {
            ("Checkpoints", new[] { "sd_xl_base_1.0.safetensors", "juggernautXL_v9.safetensors", "v1-5-pruned-emaonly-fp16.safetensors" }),
            ("UNet (Flux / Hunyuan / WAN)", new[] { "flux1-dev-fp8.safetensors", "hunyuan_video_t2v_720p_bf16.safetensors" }),
            ("LoRA", new[] { "empress_style.safetensors", "cinematic_lighting.safetensors" }),
            ("VAE", new[] { "ae.safetensors", "wan_2.1_vae.safetensors" }),
            ("CLIP encoders", new[] { "t5xxl_fp8_e4m3fn.safetensors", "clip_l.safetensors" }),
            ("CLIP Vision", new[] { "clip_vision_h.safetensors" }),
        };
        foreach (var (name, items) in demo)
        {
            var g = new ModelGroup { Name = name, Count = items.Length, TotalCount = items.Length };
            foreach (var i in items) g.Items.Add(i);
            Groups.Add(g);
        }
        TotalLabel = "demo data";
    }
}

public sealed class ModelGroup : ObservableObject
{
    public string Name { get; init; } = "";
    public int Count { get; set; }
    public int TotalCount { get; set; }
    public ObservableCollection<string> Items { get; } = new();
}
