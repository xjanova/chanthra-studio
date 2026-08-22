using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services.Providers.ComfyUI;

namespace ChanthraStudio.Services.LocalComfy;

public enum ComfyEngineState
{
    NotInstalled,
    Installing,
    Stopped,
    Starting,
    Running,
    Failed,
}

/// <summary>What the machine's GPU looks like, as far as we can tell.</summary>
/// <param name="Vendor">"nvidia" · "amd" · "intel" · "unknown".</param>
/// <param name="Name">Marketing name, or empty.</param>
/// <param name="VramGb">0 when it could not be read.</param>
/// <param name="Driver">Driver version string, or empty.</param>
public sealed record GpuFacts(string Vendor, string Name, double VramGb, string Driver)
{
    public string Label => string.IsNullOrEmpty(Name)
        ? "ไม่พบการ์ดจอที่รองรับ"
        : VramGb > 0 ? $"{Name} · {VramGb:0.#} GB" : Name;
}

/// <summary>
/// Chanthra Studio's own ComfyUI: installed by the app, run by the app, owned
/// by the app.
///
/// <b>Why the studio ships an engine instead of asking for a URL.</b> The
/// previous arrangement assumed the user had already installed ComfyUI, knew
/// what a server URL was, and would keep it running while they worked. That is
/// a reasonable ask of an engineer and an unreasonable one of everybody else —
/// and the failure looked like the studio being broken rather than a server
/// being off. Now the local route means one button.
///
/// The external-URL route is kept, not replaced: someone with a tuned ComfyUI
/// full of custom nodes should be able to point at it.
/// </summary>
public sealed class ComfyEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ComfyProcessHost _host = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ComfyEngine(AppSettings settings)
    {
        _settings = settings;
        Stamp = ComfyInstaller.ReadStamp(Root);
        State = Stamp is null ? ComfyEngineState.NotInstalled : ComfyEngineState.Stopped;
        _host.Output += line => Output?.Invoke(line);
    }

    // --------------------------------------------------------------- settings

    public string Root => ComfyPaths.Root(_settings);

    public int PreferredPort
    {
        get => int.TryParse(_settings.GetSetting("comfy:port"), out var p) && p is > 1024 and < 65535 ? p : 8188;
        set { _settings.SetSetting("comfy:port", value.ToString(CultureInfo.InvariantCulture)); _settings.Save(); }
    }

    /// <summary>"" (auto) · lowvram · novram · highvram · cpu.</summary>
    public string VramMode
    {
        get => _settings.GetSetting("comfy:vramMode") ?? "";
        set { _settings.SetSetting("comfy:vramMode", value ?? ""); _settings.Save(); }
    }

    /// <summary>
    /// Use the studio's engine for the local ComfyUI route. Off means the
    /// route talks to whatever <see cref="AppSettings.ComfyUiUrl"/> points at.
    /// </summary>
    public bool UseOwnEngine
    {
        get
        {
            var v = _settings.GetSetting("comfy:useOwnEngine");
            // Default follows reality: on when we have an engine to use.
            return string.IsNullOrEmpty(v) ? Stamp is not null : v != "0";
        }
        set { _settings.SetSetting("comfy:useOwnEngine", value ? "1" : "0"); _settings.Save(); }
    }

    /// <summary>Start the engine automatically when a render needs it.</summary>
    public bool AutoStart
    {
        get { var v = _settings.GetSetting("comfy:autoStart"); return string.IsNullOrEmpty(v) || v != "0"; }
        set { _settings.SetSetting("comfy:autoStart", value ? "1" : "0"); _settings.Save(); }
    }

    // ------------------------------------------------------------------ state

    public ComfyEngineState State { get; private set; }
    public ComfyInstallStamp? Stamp { get; private set; }
    public string Stage { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public double Progress { get; private set; }
    public string LastError { get; private set; } = "";

    public bool IsInstalled => Stamp is not null;
    public bool IsRunning => State == ComfyEngineState.Running && _host.IsRunning;

    /// <summary>Where the running engine answers. Empty when it isn't up.</summary>
    public string BaseUrl => _host.IsRunning ? _host.BaseUrl : "";

    /// <summary>
    /// The URL the ComfyUI route should use right now: our engine when it is
    /// the chosen one and it is up, otherwise the configured external server.
    /// </summary>
    public string EffectiveUrl
        => UseOwnEngine && _host.IsRunning ? _host.BaseUrl : _settings.ComfyUiUrl;

    public event Action? Changed;
    public event Action<string>? Output;

    public string[] RecentOutput() => _host.RecentOutput();

    private void Set(ComfyEngineState state, string stage = "", string detail = "", double progress = -1)
    {
        State = state;
        if (stage.Length > 0) Stage = stage;
        Detail = detail;
        if (progress >= 0) Progress = progress;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- install

    public async Task InstallAsync(ComfyFlavour flavour, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Installing over a running engine would replace files the process
            // has open, which on Windows fails in the middle rather than at the
            // start.
            if (_host.IsRunning) StopInternal();

            LastError = "";
            Set(ComfyEngineState.Installing, "install", "เริ่มติดตั้ง…", 0);

            var progress = new Progress<ComfyInstallProgress>(p =>
                Set(ComfyEngineState.Installing, p.Stage, p.Message, p.Fraction));

            Stamp = await new ComfyInstaller().InstallAsync(Root, flavour, progress, ct);
            UseOwnEngine = true;
            Set(ComfyEngineState.Stopped, "installed", $"ComfyUI {Stamp.Tag}", 1);
            ActivityLog.Info("comfy", $"engine installed: {Stamp.Tag} ({Stamp.Flavour})");
        }
        catch (OperationCanceledException)
        {
            Set(IsInstalled ? ComfyEngineState.Stopped : ComfyEngineState.NotInstalled, "cancelled", "ยกเลิกการติดตั้ง", 0);
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Set(ComfyEngineState.Failed, "failed", ex.Message, 0);
            ActivityLog.Error("comfy", "install failed", ex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Delete the engine. Weights are untouched — they live elsewhere
    /// precisely so this is a cheap thing to do.</summary>
    public void Uninstall()
    {
        StopInternal();
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            Stamp = null;
            Set(ComfyEngineState.NotInstalled, "removed", "ลบเอนจินแล้ว (โมเดลยังอยู่ครบ)", 0);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Set(ComfyEngineState.Failed, "failed", ex.Message);
        }
    }

    // ------------------------------------------------------------------- run

    /// <summary>
    /// Start the engine and wait until it actually answers.
    /// </summary>
    /// <remarks>
    /// "Started" and "ready" are minutes apart on a cold start — python has to
    /// import torch and probe the GPU before it binds the port. Returning as
    /// soon as the process exists would hand callers a server that refuses
    /// every request for the next two minutes.
    /// </remarks>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_host.IsRunning && State == ComfyEngineState.Running) return true;
            if (!IsInstalled) throw new InvalidOperationException("ยังไม่ได้ติดตั้งเอนจิน — กดติดตั้งในหน้า ComfyUI ก่อน");

            LastError = "";
            Set(ComfyEngineState.Starting, "starting", "กำลังเปิดเอนจิน…", 0.05);
            _host.Start(Root, PreferredPort, VramMode);

            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // A dead process will never answer; noticing that immediately
                // turns a five-minute wait into an error with the log attached.
                if (!_host.IsRunning)
                {
                    var tail = string.Join(Environment.NewLine, _host.RecentOutput().TakeLast(6));
                    LastError = "เอนจินปิดตัวเองระหว่างเปิด\n" + tail;
                    Set(ComfyEngineState.Failed, "failed", LastError, 0);
                    return false;
                }

                try
                {
                    using var client = new ComfyUiClient(_host.BaseUrl);
                    var probe = await client.ProbeAsync(ct);
                    if (probe.Ok)
                    {
                        Set(ComfyEngineState.Running, "running",
                            $"{probe.Device} · VRAM {probe.VramFree / 1_073_741_824.0:0.0} / {probe.VramTotal / 1_073_741_824.0:0.0} GB", 1);
                        ActivityLog.Info("comfy", $"engine ready on {_host.BaseUrl}");
                        return true;
                    }
                }
                catch { /* not up yet */ }

                Set(ComfyEngineState.Starting, "starting", "กำลังโหลด torch และตรวจการ์ดจอ…", 0.4);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            LastError = "เอนจินไม่ตอบภายใน 5 นาที — ดู log ในการ์ดนี้";
            Set(ComfyEngineState.Failed, "failed", LastError, 0);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        StopInternal();
        Set(IsInstalled ? ComfyEngineState.Stopped : ComfyEngineState.NotInstalled, "stopped", "หยุดแล้ว", 0);
    }

    private void StopInternal()
    {
        _host.Stop();
    }

    /// <summary>
    /// What a render calls: make sure something is listening, starting our own
    /// engine if that is the configured route and it is merely stopped.
    /// Returns the URL to use.
    /// </summary>
    public async Task<string> ResolveUrlForRenderAsync(CancellationToken ct = default)
    {
        if (!UseOwnEngine || !IsInstalled) return _settings.ComfyUiUrl;
        if (_host.IsRunning) return _host.BaseUrl;
        if (!AutoStart)
            throw new InvalidOperationException(
                "เอนจิน ComfyUI ของสตูดิโอยังไม่ได้เปิด — กดเปิดในหน้า ComfyUI หรือเปิดโหมดเริ่มอัตโนมัติ");

        await StartAsync(ct);
        if (!_host.IsRunning)
            throw new InvalidOperationException(LastError.Length > 0 ? LastError : "เปิดเอนจินไม่สำเร็จ");
        return _host.BaseUrl;
    }

    // ---------------------------------------------------------- gpu detection

    /// <summary>
    /// What is in this machine, best effort.
    ///
    /// nvidia-smi first because it is the only source that reports VRAM and
    /// driver version reliably; the display-adapter list is the fallback and
    /// tells us the vendor but little else. Everything here is advisory — it
    /// picks a default in the installer dropdown, and the user can override it.
    /// </summary>
    public static GpuFacts DetectGpu()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);
                var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    var parts = first.Split(',', StringSplitOptions.TrimEntries);
                    var name = parts.ElementAtOrDefault(0) ?? "NVIDIA GPU";
                    var mb = double.TryParse(parts.ElementAtOrDefault(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
                    var drv = parts.ElementAtOrDefault(2) ?? "";
                    return new GpuFacts("nvidia", name, mb / 1024.0, drv);
                }
            }
        }
        catch { /* no nvidia-smi — not an NVIDIA machine, or the driver is absent */ }

        try
        {
            // WMI via the management objects would pull another dependency;
            // this reads the same table through a tool every Windows has.
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_VideoController | Select-Object -First 1 -ExpandProperty Name)\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var name = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(6000);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "nvidia"
                               : name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "amd"
                               : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel"
                               : "unknown";
                    return new GpuFacts(vendor, name, 0, "");
                }
            }
        }
        catch { }

        return new GpuFacts("unknown", "", 0, "");
    }

    /// <summary>
    /// Which portable build suits the detected hardware.
    ///
    /// <b>The NVIDIA choice is not one build.</b> ComfyUI ships two, and the
    /// README is explicit in both directions: the cu126 build "Supports Nvidia
    /// 10 series and older GPUs, DO NOT USE THIS ON NEWER 20 SERIES AND ABOVE
    /// GPUS", while the default build needs 20-series or newer. Handing every
    /// NVIDIA machine the default would install a two-gigabyte engine that
    /// cannot start on a GTX 1070 — and the symptom is a CUDA error at first
    /// render, long after the download the user waited for.
    /// </summary>
    public static ComfyFlavour SuggestFlavour(GpuFacts gpu) => gpu.Vendor switch
    {
        "nvidia" => IsPreTuring(gpu.Name) ? ComfyFlavour.NvidiaOlderCuda : ComfyFlavour.Nvidia,
        "amd" => ComfyFlavour.Amd,
        "intel" => ComfyFlavour.Intel,
        // Unknown hardware gets the newest NVIDIA build: a machine whose GPU we
        // cannot read is most often one where nvidia-smi is simply not on PATH,
        // and this build still runs on the CPU via --cpu.
        _ => ComfyFlavour.Nvidia,
    };

    /// <summary>
    /// True for Pascal and older — the cards the cu126 build exists for.
    ///
    /// Decided on the model number, which is the only thing the driver reliably
    /// reports. GTX 16xx is the exception that makes a plain "1000-series"
    /// test wrong: it is Turing, the same generation as the RTX 20 series.
    /// </summary>
    internal static bool IsPreTuring(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var upper = name.ToUpperInvariant();

        // Every RTX card is Turing or later.
        if (upper.Contains("RTX")) return false;

        var digits = new string(upper.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var model))
        {
            // Named cards with no model number at all — TITAN X, TITAN Xp — are
            // Maxwell/Pascal. A TITAN RTX would have been caught above.
            return upper.Contains("TITAN");
        }

        // 1650 / 1660 and their Super/Ti variants are Turing despite the 16xx
        // number; everything else in the 1000s is Pascal.
        if (model is >= 1600 and < 1700) return false;

        // 900-series and 1000-series (and the odd 700-series still in service)
        // are Maxwell/Pascal. Four-digit numbers at 2000 and above are Turing+.
        return model < 1600;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Dispose();
        _gate.Dispose();
    }
}
