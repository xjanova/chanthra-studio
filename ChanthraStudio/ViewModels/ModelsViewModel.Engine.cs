using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Gpu;
using ChanthraStudio.Services.LocalComfy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>One installable model bundle, as a row in the engine panel.</summary>
public sealed class ModelBundleVm : ObservableObject, IDisposable
{
    public ModelBundleVm(GpuModelProfile profile) => Profile = profile;

    public GpuModelProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;
    public string Description => Profile.Description;
    public string SizeLabel => $"{Profile.TotalWeightsGb:0.0} GB · {Profile.Files.Count} ไฟล์ · การ์ด {Profile.MinVramGb} GB ขึ้นไป";
    public string WorkflowsLabel => string.Join(" · ", Profile.Workflows);

    private double _localVramGb;
    /// <summary>VRAM of this machine's card; 0 when unknown.</summary>
    public double LocalVramGb
    {
        get => _localVramGb;
        set
        {
            if (!SetProperty(ref _localVramGb, value)) return;
            OnPropertyChanged(nameof(VramWarning));
            OnPropertyChanged(nameof(HasVramWarning));
        }
    }

    /// <summary>
    /// Said before the download, not discovered after it: the 14B video
    /// bundles are 20–30 GB and will not render on an 8 GB card, and nothing
    /// on this page used to mention that.
    /// </summary>
    public string VramWarning => _localVramGb > 0 && _localVramGb + 0.5 < Profile.MinVramGb
        ? $"การ์ดเครื่องนี้มี {_localVramGb:0.#} GB แต่ชุดนี้ต้องการ {Profile.MinVramGb} GB — เรนเดอร์บนเครื่องนี้ไม่ไหว ใช้เส้นทางเช่า GPU แทน"
        : "";

    public bool HasVramWarning => VramWarning.Length > 0;

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set { SetProperty(ref _installed, value); OnPropertyChanged(nameof(CanDownload)); }
    }

    private bool _busy;
    public bool Busy { get => _busy; set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanDownload)); } }

    public bool CanDownload => !_busy && !_installed;

    private ModelDownloadJob? _job;

    /// <summary>Raised on the UI thread once a followed download ends.</summary>
    public event Action<ModelBundleVm>? Finished;

    /// <summary>Follow a download — one this row started, or one that was
    /// already running when the page was opened.</summary>
    public void Attach(ModelDownloadJob job)
    {
        if (ReferenceEquals(_job, job)) return;
        Detach();
        _job = job;
        Busy = true;
        if (job.Last is { } last) Show(last);
        else Status = "กำลังเริ่ม…";
        job.Progressed += OnProgress;
        _ = WatchAsync(job);
    }

    public void CancelDownload()
    {
        if (_job is null) return;
        _job.Cancel();
        Status = "กำลังยกเลิก…";
    }

    private async Task WatchAsync(ModelDownloadJob job)
    {
        string? error = null;
        try { await job.Completion; }
        catch (OperationCanceledException) { error = "ยกเลิกแล้ว — กดโหลดอีกครั้งเพื่อโหลดต่อจากที่ค้างไว้"; }
        catch (Exception ex) { error = ex.Message; }

        if (!ReferenceEquals(_job, job)) return;
        Detach();
        Busy = false;
        Refresh();
        if (error is not null) Status = error;
        Finished?.Invoke(this);
    }

    private void OnProgress(ModelDownloadProgress p)
    {
        var app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => OnProgress(p)));
            return;
        }
        if (_job is not null && !_job.IsCancellationRequested) Show(p);
    }

    private void Show(ModelDownloadProgress p)
    {
        Progress = p.Fraction;
        Status = p.Message;
    }

    private void Detach()
    {
        if (_job is not null) _job.Progressed -= OnProgress;
        _job = null;
    }

    public void Dispose() => Detach();

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

    public ObservableCollection<ModelBundleVm> Bundles { get; } = new();
    public ObservableCollection<ComfyFlavourOption> Flavours { get; } = new();

    public IRelayCommand InstallEngineCommand { get; private set; } = null!;
    public IRelayCommand CancelInstallCommand { get; private set; } = null!;
    public IRelayCommand UninstallEngineCommand { get; private set; } = null!;
    public IRelayCommand StartEngineCommand { get; private set; } = null!;
    public IRelayCommand StopEngineCommand { get; private set; } = null!;
    public IRelayCommand<ModelBundleVm> DownloadBundleCommand { get; private set; } = null!;
    public IRelayCommand<ModelBundleVm> CancelBundleCommand { get; private set; } = null!;

    private void InitEngine()
    {
        InstallEngineCommand = new AsyncRelayCommand(InstallEngineAsync);
        // The engine owns the install's cancellation, not this page: the page
        // is rebuilt on every visit, and a cancel button wired to a token only
        // the previous page knew about did nothing.
        CancelInstallCommand = new RelayCommand(() => _engine?.CancelInstall());
        UninstallEngineCommand = new AsyncRelayCommand(UninstallEngineAsync);
        StartEngineCommand = new AsyncRelayCommand(StartEngineAsync);
        StopEngineCommand = new RelayCommand(() => _engine?.Stop());
        DownloadBundleCommand = new RelayCommand<ModelBundleVm>(DownloadBundle);
        CancelBundleCommand = new RelayCommand<ModelBundleVm>(b => b?.CancelDownload());

        foreach (var f in new[] { ComfyFlavour.Nvidia, ComfyFlavour.NvidiaOlderCuda, ComfyFlavour.Amd, ComfyFlavour.Intel })
            Flavours.Add(new ComfyFlavourOption(f));

        foreach (var p in ComfyModelInstaller.Profiles)
        {
            var vm = new ModelBundleVm(p);
            vm.Refresh();
            vm.Finished += OnBundleFinished;
            // Pick up a download started on an earlier visit to this page.
            if (ComfyModelInstaller.ActiveJob(p.Key) is { } running) vm.Attach(running);
            Bundles.Add(vm);
        }

        if (_ctx is null) return;
        _engine = _ctx.ComfyEngine;
        _engine.Changed += OnEngineChanged;
        OnEngineChanged();
        _ = DetectGpuAsync();
    }

    /// <summary>
    /// nvidia-smi — and PowerShell when it is missing — takes seconds to
    /// answer. Asking on the UI thread froze the page every time it opened.
    /// </summary>
    private async Task DetectGpuAsync()
    {
        var gpu = await Task.Run(ComfyEngine.DetectGpuCached);
        Gpu = gpu;
        SelectedFlavour ??= Flavours.FirstOrDefault(f => f.Value == ComfyEngine.SuggestFlavour(gpu)) ?? Flavours[0];
        foreach (var b in Bundles) b.LocalVramGb = gpu.VramGb;
        OnPropertyChanged(nameof(GpuLabel));
    }

    private async void OnBundleFinished(ModelBundleVm bundle)
    {
        // New files on disk mean a different answer from /object_info.
        try { await RefreshAsync(); }
        catch (Exception ex) { ActivityLog.Warn("comfy", "model list refresh after download failed: " + ex.Message); }
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
                     nameof(EngineRootText), nameof(ModelsRootText),
                     nameof(EngineRootLabel), nameof(EngineRootWarning),
                     nameof(EngineSpaceLabel), nameof(ModelsSpaceLabel),
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
        set { if (SetProperty(ref _selectedFlavour, value)) OnPropertyChanged(nameof(CanInstall)); }
    }

    /// <summary>Where the engine will live, and whether that path is safe.</summary>
    public string EngineRootLabel => _engine?.Root ?? ComfyPaths.DefaultRoot();

    public bool EngineRootWarning => !ComfyPaths.IsAsciiSafe(EngineRootLabel);

    /// <summary>Editable engine folder. Changing it after an install points
    /// the studio at a different folder rather than moving the old one.</summary>
    public string EngineRootText
    {
        get => _engine?.Root ?? ComfyPaths.DefaultRoot();
        set
        {
            if (_engine is null || string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
            _engine.SetRoot(value.Trim());
            OnEngineChanged();
        }
    }

    public string ModelsRootText
    {
        get => _engine?.ModelsRoot ?? "";
        set
        {
            if (_engine is null || string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
            _engine.SetModelsRoot(value.Trim());
            foreach (var b in Bundles) b.Refresh();
            OnEngineChanged();
        }
    }

    /// <summary>
    /// Free space on each target drive.
    ///
    /// Shown because the two folders routinely belong on different drives and
    /// the consequence of getting it wrong is discovered 20 GB into a
    /// download. The engine needs roughly 9 GB while it unpacks; a single
    /// video profile is 33 GB.
    /// </summary>
    public string EngineSpaceLabel => SpaceLabel(EngineRootText, 9);
    public string ModelsSpaceLabel => SpaceLabel(ModelsRootText, 15);

    private static string SpaceLabel(string path, double wantGb)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var free = ComfyEngine.FreeGb(path);
        if (free < 0) return "วัดพื้นที่ว่างไม่ได้";
        return free < wantGb
            ? $"เหลือ {free:0.0} GB — น้อยไป ควรมีอย่างน้อย {wantGb:0} GB"
            : $"เหลือ {free:0.0} GB";
    }

    // ------------------------------------------------------------------ state

    public bool EngineInstalled => _engine?.IsInstalled ?? false;
    public bool EngineRunning => _engine?.IsRunning ?? false;
    public bool EngineBusy => _engine?.State is ComfyEngineState.Installing or ComfyEngineState.Starting or ComfyEngineState.Removing;

    // Not before the GPU check has picked a build: pressing Install in that
    // first second used to do nothing at all.
    public bool CanInstall => !EngineBusy && _selectedFlavour is not null;
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
        ComfyEngineState.Removing => "กำลังลบ",
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
        try
        {
            await _engine.InstallAsync(SelectedFlavour.Value);
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

    private async Task UninstallEngineAsync()
    {
        if (_engine is null || EngineBusy) return;
        var answer = System.Windows.MessageBox.Show(
            $"ลบเอนจิน ComfyUI ที่\n{_engine.Root}\n\nไฟล์โมเดลใน {_engine.ModelsRoot} จะไม่ถูกลบ\nติดตั้งใหม่ภายหลังต้องดาวน์โหลดราว 2 GB อีกครั้ง",
            "ยืนยันการลบเอนจิน",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        await _engine.UninstallAsync();
        if (_engine.IsInstalled || _engine.State == ComfyEngineState.Failed)
        {
            ServerStatus = "ลบเอนจินไม่สำเร็จ: " + _engine.LastError;
            StatusKind = "err";
            return;
        }
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

    private void DownloadBundle(ModelBundleVm? bundle)
    {
        if (bundle is null || _ctx is null || !bundle.CanDownload) return;
        // The job belongs to the installer, not this page, so it keeps going —
        // and stays cancellable — when the user leaves and comes back. The old
        // version also reset the row straight after a failure, so the error
        // message was replaced by "not installed" before anyone could read it.
        var job = ComfyModelInstaller.StartDownload(bundle.Profile, _ctx.Settings["huggingface"]);
        bundle.Attach(job);
    }

    public void Dispose()
    {
        if (_engine is not null) _engine.Changed -= OnEngineChanged;
        foreach (var b in Bundles)
        {
            b.Finished -= OnBundleFinished;
            b.Dispose();   // stops following; the download itself carries on
        }
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
