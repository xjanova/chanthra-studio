using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services.Providers.ComfyUI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// One LoRA row in the stack editor.
///
/// A view-model rather than a bare <see cref="ComfyLoraEntry"/> because the
/// list is edited live: strengths move on sliders and the row has to raise
/// change notifications and tell the panel to recompute its preview.
/// </summary>
public sealed class LoraSlotVm : ObservableObject
{
    private readonly Action _changed;

    public LoraSlotVm(ComfyLoraEntry entry, Action changed)
    {
        Entry = entry;
        _changed = changed;
    }

    public ComfyLoraEntry Entry { get; }

    public string Name
    {
        get => Entry.Name;
        set { if (Entry.Name == value) return; Entry.Name = value ?? ""; OnPropertyChanged(); _changed(); }
    }

    public bool Enabled
    {
        get => Entry.Enabled;
        set { if (Entry.Enabled == value) return; Entry.Enabled = value; OnPropertyChanged(); _changed(); }
    }

    public double ModelStrength
    {
        get => Entry.ModelStrength;
        set
        {
            var v = Math.Round(Math.Clamp(value, -2, 3), 2);
            if (Math.Abs(Entry.ModelStrength - v) < 0.001) return;
            Entry.ModelStrength = v; OnPropertyChanged(); _changed();
        }
    }

    public double ClipStrength
    {
        get => Entry.ClipStrength;
        set
        {
            var v = Math.Round(Math.Clamp(value, -2, 3), 2);
            if (Math.Abs(Entry.ClipStrength - v) < 0.001) return;
            Entry.ClipStrength = v; OnPropertyChanged(); _changed();
        }
    }
}

/// <summary>
/// The ComfyUI control room: everything the studio is allowed to change inside
/// a workflow, plus the server controls that used to require leaving the app.
///
/// <b>Design rule for this whole panel.</b> Every switch here is checked
/// against the selected workflow's real capabilities before it is offered, and
/// the preview list at the bottom shows the exact values that will be written
/// into the graph. A settings page whose controls quietly do nothing on the
/// workflow you happen to have chosen is worse than no settings page: it makes
/// every subsequent bad render ambiguous.
/// </summary>
public sealed partial class ModelsViewModel
{
    private ComfyRenderSettings _comfy = new();

    /// <summary>Set while the settings are being loaded into the UI, so the
    /// property setters don't write each incoming value straight back to the
    /// database and recompute the preview once per field.</summary>
    private bool _loadingComfy;

    public ObservableCollection<string> Samplers { get; } = new();
    public ObservableCollection<string> Schedulers { get; } = new();
    public ObservableCollection<string> CheckpointChoices { get; } = new();
    public ObservableCollection<string> LoraChoices { get; } = new();
    public ObservableCollection<WorkflowDescriptor> WorkflowChoices { get; } = new();
    public ObservableCollection<LoraSlotVm> LoraStack { get; } = new();
    public ObservableCollection<ComfyPatchLine> PatchPreview { get; } = new();

    public IRelayCommand InterruptCommand { get; private set; } = null!;
    public IRelayCommand FreeMemoryCommand { get; private set; } = null!;
    public IRelayCommand AddLoraCommand { get; private set; } = null!;
    public IRelayCommand<LoraSlotVm> RemoveLoraCommand { get; private set; } = null!;
    public IRelayCommand ResetSamplerCommand { get; private set; } = null!;
    public IRelayCommand RandomiseSeedCommand { get; private set; } = null!;

    private void InitComfyCommands()
    {
        InterruptCommand = new AsyncRelayCommand(InterruptAsync);
        FreeMemoryCommand = new AsyncRelayCommand(FreeMemoryAsync);
        AddLoraCommand = new RelayCommand(AddLora);
        RemoveLoraCommand = new RelayCommand<LoraSlotVm>(RemoveLora);
        ResetSamplerCommand = new RelayCommand(ResetSampler);
        RandomiseSeedCommand = new RelayCommand(() =>
        {
            _comfy.LastSeed = Random.Shared.NextInt64(1, 0xFFFF_FFFFL);
            Persist();
            OnPropertyChanged(nameof(SeedText));
        });
    }

    // ------------------------------------------------------------ server card

    private string _queueLabel = "—";
    public string QueueLabel { get => _queueLabel; set => SetProperty(ref _queueLabel, value); }

    private async Task InterruptAsync()
    {
        if (_ctx is null) return;
        try
        {
            using var client = new ComfyUiClient(_ctx.Settings.ComfyUiUrl);
            await client.InterruptAsync();
            await RefreshQueueAsync();
        }
        catch (Exception ex) { ServerStatus = "interrupt failed · " + ex.Message; StatusKind = "err"; }
    }

    private async Task FreeMemoryAsync()
    {
        if (_ctx is null) return;
        try
        {
            using var client = new ComfyUiClient(_ctx.Settings.ComfyUiUrl);
            var ok = await client.FreeMemoryAsync();
            // Re-probe rather than claim success: the point of the button is
            // the VRAM number, so show the new one instead of a toast.
            var probe = await client.ProbeAsync();
            ServerStatus = ok && probe.Ok
                ? $"models unloaded · VRAM {probe.VramFree / 1_073_741_824.0:F1} / {probe.VramTotal / 1_073_741_824.0:F1} GB free"
                : "server declined the unload request";
            StatusKind = ok ? "ok" : "warn";
        }
        catch (Exception ex) { ServerStatus = "unload failed · " + ex.Message; StatusKind = "err"; }
    }

    private async Task RefreshQueueAsync()
    {
        if (_ctx is null) return;
        try
        {
            using var client = new ComfyUiClient(_ctx.Settings.ComfyUiUrl);
            var (running, pending) = await client.GetQueueAsync();
            // -1 means the call failed. Rendering that as "0 running" would be
            // a reassuring lie about a server that is not answering at all.
            QueueLabel = running < 0 ? "unreachable" : $"{running} running · {pending} queued";
        }
        catch { QueueLabel = "unreachable"; }
    }

    // ------------------------------------------------------- workflow context

    private WorkflowDescriptor? _selectedWorkflow;
    public WorkflowDescriptor? SelectedWorkflow
    {
        get => _selectedWorkflow;
        set
        {
            if (!SetProperty(ref _selectedWorkflow, value)) return;
            if (!_loadingComfy && _ctx is not null && value is not null)
            {
                // Choosing a workflow here is the same act as choosing it in
                // the composer — two different "active workflow" values would
                // be a trap the moment the user rendered from Generate.
                _ctx.Settings.ActiveWorkflow = value.Name;
                _ctx.Settings.Save();
            }
            ProbeWorkflow();
        }
    }

    private ComfyWorkflowCapabilities? _caps;
    public ComfyWorkflowCapabilities? Caps { get => _caps; private set => SetProperty(ref _caps, value); }

    public bool CanSample => _caps?.CanSample ?? true;
    public bool CanSize => _caps?.CanSize ?? true;
    public bool CanCheckpoint => _caps?.CanCheckpoint ?? true;
    public bool CanClipSkip => _caps?.CanClipSkip ?? false;
    public bool CanLora => _caps?.CanLora ?? false;
    public bool CanVideo => _caps?.CanVideo ?? false;

    private string _capsLabel = "";
    public string CapsLabel { get => _capsLabel; private set => SetProperty(ref _capsLabel, value); }

    /// <summary>
    /// Read the selected workflow, record what it can accept, and rebuild the
    /// preview.
    /// </summary>
    private void ProbeWorkflow()
    {
        try
        {
            var descriptor = _selectedWorkflow;
            if (descriptor is null) { Caps = null; CapsLabel = ""; PatchPreview.Clear(); return; }

            var wf = Workflow.LoadFromPath(descriptor.Path);
            Caps = wf.Probe();

            var parts = new List<string>();
            parts.Add(_caps!.CanSample ? $"{_caps.Samplers} sampler node(s)" : "no sampler node");
            if (_caps.CanSize) parts.Add("empty latent");
            if (_caps.CanCheckpoint) parts.Add("checkpoint loader");
            if (_caps.CanLora) parts.Add($"{_caps.LoraSlots} LoRA slot(s)");
            if (_caps.CanClipSkip) parts.Add("clip skip");
            if (_caps.CanVideo) parts.Add("video length/fps");
            CapsLabel = string.Join(" · ", parts);

            foreach (var n in new[] { nameof(CanSample), nameof(CanSize), nameof(CanCheckpoint),
                                      nameof(CanClipSkip), nameof(CanLora), nameof(CanVideo) })
                OnPropertyChanged(n);

            RebuildPreview();
        }
        catch (Exception ex)
        {
            Caps = null;
            CapsLabel = "could not read this workflow · " + ex.Message;
            PatchPreview.Clear();
        }
    }

    /// <summary>
    /// Show exactly what a submit would write. Runs against a freshly loaded
    /// copy so the preview never mutates anything the render will use.
    /// </summary>
    private void RebuildPreview()
    {
        PatchPreview.Clear();
        var descriptor = _selectedWorkflow;
        if (descriptor is null) return;
        try
        {
            var wf = Workflow.LoadFromPath(descriptor.Path);
            // 1024x576 stands in for the composer's aspect + HD derivation,
            // which is per-shot and not known here.
            foreach (var line in wf.Apply(_comfy, _comfy.NextSeed(_comfy.LastSeed), 1024, 576))
                PatchPreview.Add(line);
        }
        catch { /* the caps line already explained why it can't be read */ }
    }

    // ---------------------------------------------------------- sampler group

    public bool OverrideSampler
    {
        get => _comfy.OverrideSampler;
        set { if (_comfy.OverrideSampler == value) return; _comfy.OverrideSampler = value; Persist(); }
    }

    public string SamplerName
    {
        get => _comfy.SamplerName;
        set { if (_comfy.SamplerName == value || value is null) return; _comfy.SamplerName = value; Persist(); }
    }

    public string Scheduler
    {
        get => _comfy.Scheduler;
        set { if (_comfy.Scheduler == value || value is null) return; _comfy.Scheduler = value; Persist(); }
    }

    public double Steps
    {
        get => _comfy.Steps;
        set { var v = (int)Math.Clamp(value, 1, 200); if (_comfy.Steps == v) return; _comfy.Steps = v; Persist(); }
    }

    public double Cfg
    {
        get => _comfy.Cfg;
        set { var v = Math.Round(Math.Clamp(value, 0, 30), 1); if (Math.Abs(_comfy.Cfg - v) < 0.01) return; _comfy.Cfg = v; Persist(); }
    }

    public double Denoise
    {
        get => _comfy.Denoise;
        set { var v = Math.Round(Math.Clamp(value, 0, 1), 2); if (Math.Abs(_comfy.Denoise - v) < 0.001) return; _comfy.Denoise = v; Persist(); }
    }

    private void ResetSampler()
    {
        _comfy.Steps = 20;
        _comfy.Cfg = 7.0;
        _comfy.Denoise = 1.0;
        _comfy.SamplerName = Samplers.Contains("euler") ? "euler" : Samplers.FirstOrDefault() ?? "euler";
        _comfy.Scheduler = Schedulers.Contains("normal") ? "normal" : Schedulers.FirstOrDefault() ?? "normal";
        Persist();
        foreach (var n in new[] { nameof(Steps), nameof(Cfg), nameof(Denoise), nameof(SamplerName), nameof(Scheduler) })
            OnPropertyChanged(n);
    }

    // ------------------------------------------------------------- size group

    public bool OverrideSize
    {
        get => _comfy.OverrideSize;
        set { if (_comfy.OverrideSize == value) return; _comfy.OverrideSize = value; Persist(); }
    }

    public string WidthText
    {
        get => _comfy.Width.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 64, 4096, out var v)) { _comfy.Width = v; Persist(); } OnPropertyChanged(); }
    }

    public string HeightText
    {
        get => _comfy.Height.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 64, 4096, out var v)) { _comfy.Height = v; Persist(); } OnPropertyChanged(); }
    }

    public double BatchSize
    {
        get => _comfy.BatchSize;
        set { var v = (int)Math.Clamp(value, 1, 16); if (_comfy.BatchSize == v) return; _comfy.BatchSize = v; Persist(); }
    }

    /// <summary>
    /// Megapixels at the chosen size, with a warning band. SD-era checkpoints
    /// fall apart well before they run out of VRAM, and "why does my 2048×2048
    /// image have three heads" is the single most common self-inflicted wound
    /// in a settings panel like this one.
    /// </summary>
    public string SizeNote
    {
        get
        {
            var mp = _comfy.Width * (double)_comfy.Height / 1_000_000.0;
            var note = $"{mp:0.00} MP";
            if (_comfy.Width % 8 != 0 || _comfy.Height % 8 != 0)
                note += " · ต้องหารด้วย 8 ลงตัว ไม่งั้น ComfyUI จะปัดให้เอง";
            else if (mp > 1.6) note += " · ใหญ่กว่าที่ SDXL ถูกเทรนมา อาจได้ภาพซ้อน";
            return note;
        }
    }

    // ------------------------------------------------------------------- seed

    public bool SeedFixed
    {
        get => _comfy.SeedMode == ComfySeedMode.Fixed;
        set { if (value) SetSeedMode(ComfySeedMode.Fixed); }
    }

    public bool SeedRandom
    {
        get => _comfy.SeedMode == ComfySeedMode.Randomize;
        set { if (value) SetSeedMode(ComfySeedMode.Randomize); }
    }

    public bool SeedIncrement
    {
        get => _comfy.SeedMode == ComfySeedMode.Increment;
        set { if (value) SetSeedMode(ComfySeedMode.Increment); }
    }

    private void SetSeedMode(ComfySeedMode mode)
    {
        if (_comfy.SeedMode == mode) return;
        _comfy.SeedMode = mode;
        Persist();
        foreach (var n in new[] { nameof(SeedFixed), nameof(SeedRandom), nameof(SeedIncrement), nameof(SeedText) })
            OnPropertyChanged(n);
    }

    /// <summary>The seed the last render used — the thing you need to
    /// reproduce it.</summary>
    public string SeedText
    {
        get => _comfy.LastSeed.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { OnPropertyChanged(); return; }
            _comfy.LastSeed = Math.Abs(v);
            Persist();
        }
    }

    // ------------------------------------------------------------- checkpoint

    public bool OverrideCheckpoint
    {
        get => _comfy.OverrideCheckpoint;
        set { if (_comfy.OverrideCheckpoint == value) return; _comfy.OverrideCheckpoint = value; Persist(); }
    }

    public string CheckpointName
    {
        get => _comfy.CheckpointName;
        set { if (_comfy.CheckpointName == value || value is null) return; _comfy.CheckpointName = value; Persist(); }
    }

    // -------------------------------------------------------------- clip skip

    public bool OverrideClipSkip
    {
        get => _comfy.OverrideClipSkip;
        set { if (_comfy.OverrideClipSkip == value) return; _comfy.OverrideClipSkip = value; Persist(); }
    }

    /// <summary>Shown as a positive count because that is how everyone talks
    /// about it; stored negative because that is what the node wants.</summary>
    public double ClipSkip
    {
        get => -_comfy.ClipSkip;
        set { var v = -(int)Math.Clamp(value, 1, 12); if (_comfy.ClipSkip == v) return; _comfy.ClipSkip = v; Persist(); }
    }

    // ------------------------------------------------------------------ loras

    public bool OverrideLoras
    {
        get => _comfy.OverrideLoras;
        set { if (_comfy.OverrideLoras == value) return; _comfy.OverrideLoras = value; Persist(); }
    }

    private void AddLora()
    {
        var entry = new ComfyLoraEntry { Name = LoraChoices.FirstOrDefault() ?? "", ModelStrength = 1.0, ClipStrength = 1.0 };
        _comfy.Loras.Add(entry);
        LoraStack.Add(new LoraSlotVm(entry, Persist));
        Persist();
    }

    private void RemoveLora(LoraSlotVm? slot)
    {
        if (slot is null) return;
        _comfy.Loras.Remove(slot.Entry);
        LoraStack.Remove(slot);
        Persist();
    }

    // ------------------------------------------------------------------ video

    public bool OverrideVideo
    {
        get => _comfy.OverrideVideo;
        set { if (_comfy.OverrideVideo == value) return; _comfy.OverrideVideo = value; Persist(); }
    }

    public double VideoFrames
    {
        get => _comfy.VideoFrames;
        set { var v = (int)Math.Clamp(value, 1, 1000); if (_comfy.VideoFrames == v) return; _comfy.VideoFrames = v; Persist(); }
    }

    public double VideoFps
    {
        get => _comfy.VideoFps;
        set { var v = (int)Math.Clamp(value, 1, 120); if (_comfy.VideoFps == v) return; _comfy.VideoFps = v; Persist(); }
    }

    public string VideoNote
    {
        get
        {
            var sec = _comfy.VideoFrames / (double)Math.Max(1, _comfy.VideoFps);
            return $"{sec:0.0}s ที่ {_comfy.VideoFps} fps";
        }
    }

    // ------------------------------------------------------------- plumbing

    /// <summary>
    /// Write the settings and refresh everything derived from them. Called from
    /// every setter rather than from a Save button: a settings panel with an
    /// unsaved-changes state is a settings panel that loses changes.
    /// </summary>
    private void Persist()
    {
        if (_loadingComfy || _ctx is null) return;
        _comfy.SaveTo(_ctx.Settings);
        OnPropertyChanged(string.Empty);   // cheap: the panel is small and static
        RebuildPreview();
    }

    private void LoadComfySettings()
    {
        if (_ctx is null) return;
        _loadingComfy = true;
        try
        {
            _comfy = ComfyRenderSettings.Load(_ctx.Settings);

            LoraStack.Clear();
            foreach (var e in _comfy.Loras) LoraStack.Add(new LoraSlotVm(e, Persist));

            WorkflowChoices.Clear();
            foreach (var w in _ctx.Workflows.All) WorkflowChoices.Add(w);
            _selectedWorkflow = _ctx.Workflows.FindByName(_ctx.Settings.ActiveWorkflow)
                             ?? _ctx.Workflows.Default();
            OnPropertyChanged(nameof(SelectedWorkflow));
        }
        finally
        {
            _loadingComfy = false;
        }
        ProbeWorkflow();
        OnPropertyChanged(string.Empty);
    }

    private static bool TryInt(string? text, int min, int max, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
           && value >= min && value <= max;
}
