using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services.Gpu;
using ChanthraStudio.Services.LocalComfy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>One installable model bundle, as a row in the engine panel.</summary>
public sealed class ModelBundleVm : ObservableObject
{
    public ModelBundleVm(GpuModelProfile profile) => Profile = profile;

    public GpuModelProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;
    public string Description => Profile.Description;
    public string SizeLabel => $"{Profile.TotalWeightsGb:0.0} GB · {Profile.Files.Count} ไฟล์";
    public string WorkflowsLabel => string.Join(" · ", Profile.Workflows);

    private bool _installed;
    public bool Installed { get => _installed; set => SetProperty(ref _installed, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanDownload)); } }

    public bool CanDownload => !_busy && !_installed;

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPct)); }
    }

    /// <summary>0..100 — the shared PercentToWidth converter works in percent.</summary>
    public double ProgressPct => _progress * 100.0;

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public void Refresh()
    {
        Installed = ComfyModelInstaller.IsInstalled(Profile);
        Status = Installed
            ? "ติดตั้งแล้ว"
            : $"ยังไม่มี · ต้องโหลด {ComfyModelInstaller.RemainingGb(Profile):0.0} GB";
        OnPropertyChanged(nameof(CanDownload));
    }
}

/// <summary>
/// The engine half of the ComfyUI panel: install the studio's own ComfyUI, run
/// it, and fill it with models — without the user ever meeting a terminal.
/// </summary>
public sealed partial class ModelsViewModel : IDisposable
{
    private ComfyEngine? _engine;
    private CancellationTokenSource? _installCts;

    public ObservableCollection<ModelBundleVm> Bundles { get; } = new();
    public ObservableCollection<ComfyFlavourOption> Flavours { get; } = new();

    public IRelayCommand InstallEngineCommand { get; private set; } = null!;
    public IRelayCommand CancelInstallCommand { get; private set; } = null!;
    public IRelayCommand UninstallEngineCommand { get; private set; } = null!;
    public IRelayCommand StartEngineCommand { get; private set; } = null!;
    public IRelayCommand StopEngineCommand { get; private set; } = null!;
    public IRelayCommand<ModelBundleVm> DownloadBundleCommand { get; private set; } = null!;

    private void InitEngine()
    {
        InstallEngineCommand = new AsyncRelayCommand(InstallEngineAsync);
        CancelInstallCommand = new RelayCommand(() => _installCts?.Cancel());
        UninstallEngineCommand = new RelayCommand(UninstallEngine);
        StartEngineCommand = new AsyncRelayCommand(StartEngineAsync);
        StopEngineCommand = new RelayCommand(() => _engine?.Stop());
        DownloadBundleCommand = new AsyncRelayCommand<ModelBundleVm>(DownloadBundleAsync);

        foreach (var f in new[] { ComfyFlavour.Nvidia, ComfyFlavour.NvidiaOlderCuda, ComfyFlavour.Amd, ComfyFlavour.Intel })
            Flavours.Add(new ComfyFlavourOption(f));

        foreach (var p in ComfyModelInstaller.Profiles)
        {
            var vm = new ModelBundleVm(p);
            vm.Refresh();
            Bundles.Add(vm);
        }

        if (_ctx is null) return;
        _engine = _ctx.ComfyEngine;
        _engine.Changed += OnEngineChanged;

        Gpu = ComfyEngine.DetectGpu();
        SelectedFlavour = Flavours.FirstOrDefault(f => f.Value == ComfyEngine.SuggestFlavour(Gpu)) ?? Flavours[0];
        OnEngineChanged();
    }

    private void OnEngineChanged()
    {
        // The engine raises this from whichever thread its work is on; every
        // consumer of these properties is a binding, and WPF only tolerates
        // that on the dispatcher.
        var app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(OnEngineChanged));
            return;
        }

        foreach (var n in new[]
                 {
                     nameof(EngineStateLabel), nameof(EngineStateKind), nameof(EngineStateBrushKey), nameof(EngineDetail),
                     nameof(EngineProgress), nameof(EngineProgressPct),
                     nameof(EngineInstalled), nameof(EngineRunning),
                     nameof(EngineBusy), nameof(EngineVersionLabel), nameof(EngineUrlLabel),
                     nameof(CanInstall), nameof(CanStart), nameof(CanStop),
                     nameof(ShowStartButton), nameof(EngineLog),
                 })
            OnPropertyChanged(n);
    }

    // ------------------------------------------------------------------ facts

    private GpuFacts _gpu = new("unknown", "", 0, "");
    public GpuFacts Gpu { get => _gpu; private set => SetProperty(ref _gpu, value); }

    public string GpuLabel => _gpu.Vendor == "unknown"
        ? "ตรวจไม่พบการ์ดจอ — ยังติดตั้งได้ แต่จะเรนเดอร์ด้วย CPU ซึ่งช้ามาก (หรือใช้เส้นทางเช่า GPU แทน)"
        : string.IsNullOrEmpty(_gpu.Driver)
            ? _gpu.Label
            : $"{_gpu.Label} · ไดรเวอร์ {_gpu.Driver}";

    private ComfyFlavourOption? _selectedFlavour;
    public ComfyFlavourOption? SelectedFlavour
    {
        get => _selectedFlavour;
        set => SetProperty(ref _selectedFlavour, value);
    }

    /// <summary>Where the engine will live, and whether that path is safe.</summary>
    public string EngineRootLabel => _engine?.Root ?? ComfyPaths.DefaultRoot();

    public bool EngineRootWarning => !ComfyPaths.IsAsciiSafe(EngineRootLabel);

    // ------------------------------------------------------------------ state

    public bool EngineInstalled => _engine?.IsInstalled ?? false;
    public bool EngineRunning => _engine?.IsRunning ?? false;
    public bool EngineBusy => _engine?.State is ComfyEngineState.Installing or ComfyEngineState.Starting;

    public bool CanInstall => !EngineBusy;
    public bool CanStart => EngineInstalled && !EngineRunning && !EngineBusy;
    public bool CanStop => EngineRunning;

    /// <summary>
    /// Visibility, not just enablement. A greyed "start" button next to
    /// "not installed" invites the user to click the thing that cannot work
    /// instead of the thing that can.
    /// </summary>
    public bool ShowStartButton => EngineInstalled && !EngineRunning;

    public double EngineProgress => _engine?.Progress ?? 0;

    /// <summary>0..100 for the shared PercentToWidth converter.</summary>
    public double EngineProgressPct => (_engine?.Progress ?? 0) * 100.0;

    public string EngineDetail => _engine?.Detail ?? "";

    public string EngineStateLabel => _engine?.State switch
    {
        ComfyEngineState.NotInstalled => "ยังไม่ได้ติดตั้ง",
        ComfyEngineState.Installing => "กำลังติดตั้ง",
        ComfyEngineState.Stopped => "ติดตั้งแล้ว · ปิดอยู่",
        ComfyEngineState.Starting => "กำลังเปิด",
        ComfyEngineState.Running => "กำลังทำงาน",
        ComfyEngineState.Failed => "มีปัญหา",
        _ => "—",
    };

    public string EngineStateKind => _engine?.State switch
    {
        ComfyEngineState.Running => "ok",
        ComfyEngineState.Failed => "err",
        _ => "warn",
    };

    /// <summary>Brush resource name, resolved through the ResourceLookup
    /// converter — the same route GpuViewModel uses for its status pill.</summary>
    public string EngineStateBrushKey => EngineStateKind switch
    {
        "ok" => "BrushOk",
        "err" => "BrushErr",
        _ => "BrushWarn",
    };

    public string EngineVersionLabel => _engine?.Stamp is { } s
        ? $"ComfyUI {s.Tag} · {s.Flavour} · ติดตั้งเมื่อ {s.InstalledAtUtc.ToLocalTime():d MMM yyyy}"
        : "";

    public string EngineUrlLabel => _engine?.IsRunning == true ? _engine.BaseUrl : "—";

    public string EngineLog => string.Join(Environment.NewLine, (_engine?.RecentOutput() ?? Array.Empty<string>()).TakeLast(14));

    // --------------------------------------------------------------- settings

    public bool UseOwnEngine
    {
        get => _engine?.UseOwnEngine ?? true;
        set { if (_engine is null) return; _engine.UseOwnEngine = value; OnPropertyChanged(); }
    }

    public bool AutoStartEngine
    {
        get => _engine?.AutoStart ?? true;
        set { if (_engine is null) return; _engine.AutoStart = value; OnPropertyChanged(); }
    }

    public string EnginePortText
    {
        get => (_engine?.PreferredPort ?? 8188).ToString(CultureInfo.InvariantCulture);
        set { if (_engine is not null && TryInt(value, 1025, 65534, out var v)) _engine.PreferredPort = v; OnPropertyChanged(); }
    }

    public ObservableCollection<string> VramModes { get; } = new()
        { "auto", "highvram", "lowvram", "novram", "cpu" };

    public string VramMode
    {
        get
        {
            var m = _engine?.VramMode ?? "";
            return string.IsNullOrEmpty(m) ? "auto" : m;
        }
        set
        {
            if (_engine is null || value is null) return;
            _engine.VramMode = value == "auto" ? "" : value;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------- operations

    private async Task InstallEngineAsync()
    {
        if (_engine is null || SelectedFlavour is null) return;
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        try
        {
            await _engine.InstallAsync(SelectedFlavour.Value, _installCts.Token);
            ServerStatus = "ติดตั้งเอนจินเรียบร้อย — กดเปิดเพื่อเริ่มใช้งาน";
            StatusKind = "ok";
        }
        catch (OperationCanceledException)
        {
            ServerStatus = "ยกเลิกการติดตั้งแล้ว";
            StatusKind = "warn";
        }
        catch (Exception ex)
        {
            ServerStatus = ex.Message;
            StatusKind = "err";
        }
    }

    private void UninstallEngine()
    {
        _engine?.Uninstall();
        ServerStatus = "ลบเอนจินแล้ว — ไฟล์โมเดลยังอยู่ครบ";
        StatusKind = "warn";
    }

    private async Task StartEngineAsync()
    {
        if (_engine is null) return;
        try
        {
            if (await _engine.StartAsync())
            {
                // Point the panel's own client at the engine we just started,
                // otherwise the model list keeps querying whatever URL the
                // settings page holds and reports "unreachable" next to a
                // running engine.
                ServerUrl = _engine.BaseUrl;
                await RefreshAsync();
            }
            else
            {
                ServerStatus = _engine.LastError;
                StatusKind = "err";
            }
        }
        catch (Exception ex)
        {
            ServerStatus = ex.Message;
            StatusKind = "err";
        }
    }

    private async Task DownloadBundleAsync(ModelBundleVm? bundle)
    {
        if (bundle is null || _ctx is null) return;
        bundle.Busy = true;
        bundle.Status = "กำลังเริ่ม…";
        try
        {
            var hf = _ctx.Settings["huggingface"];
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                bundle.Progress = p.Fraction;
                bundle.Status = p.Message;
            });
            await new ComfyModelInstaller().DownloadAsync(bundle.Profile, hf, progress);
            bundle.Refresh();

            // New files on disk mean a different answer from /object_info.
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            bundle.Status = ex.Message;
        }
        finally
        {
            bundle.Busy = false;
            bundle.Refresh();
        }
    }

    public void Dispose()
    {
        if (_engine is not null) _engine.Changed -= OnEngineChanged;
        _installCts?.Cancel();
        _installCts?.Dispose();
        _installCts = null;
    }
}

/// <summary>Picker row for a ComfyUI portable flavour.</summary>
public sealed class ComfyFlavourOption
{
    public ComfyFlavourOption(ComfyFlavour value) => Value = value;

    public ComfyFlavour Value { get; }
    public string DisplayName => ComfyRelease.DescribeFlavour(Value);

    /// <summary>The app-wide ComboBox template renders the closed state through
    /// ToString(); see Themes/Controls.xaml.</summary>
    public override string ToString() => DisplayName;
}
