using System.Collections.ObjectModel;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.ViewModels;

public sealed class StatusBarViewModel : ObservableObject
{
    // Was a fixed "Connected" with a pulsing green dot, whatever the state of
    // anything. Now it is the studio's own ComfyUI engine, as it really is.
    private string _connectionLabel = "ComfyUI · —";
    public string ConnectionLabel { get => _connectionLabel; set => SetProperty(ref _connectionLabel, value); }

    private bool _connectionOk;
    /// <summary>Drives the dot: lit only while the engine answers.</summary>
    public bool ConnectionOk { get => _connectionOk; set => SetProperty(ref _connectionOk, value); }

    private string _gpuLabel = "GPU detecting...";
    public string GpuLabel { get => _gpuLabel; set => SetProperty(ref _gpuLabel, value); }

    private double _gpuUtilisation;
    public double GpuUtilisation { get => _gpuUtilisation; set => SetProperty(ref _gpuUtilisation, value); }

    private string _gpuUtilLabel = "—";
    public string GpuUtilLabel { get => _gpuUtilLabel; set => SetProperty(ref _gpuUtilLabel, value); }

    private string _vramLabel = "VRAM —";
    public string VramLabel { get => _vramLabel; set => SetProperty(ref _vramLabel, value); }

    private string _tempLabel = "TEMP —";
    public string TempLabel { get => _tempLabel; set => SetProperty(ref _tempLabel, value); }

    /// <summary>"cool" / "warm" / "hot" — drives the temp pill color.</summary>
    private string _tempKind = "cool";
    public string TempKind { get => _tempKind; set => SetProperty(ref _tempKind, value); }

    private string _powerLabel = "POWER —";
    public string PowerLabel { get => _powerLabel; set => SetProperty(ref _powerLabel, value); }

    private string _fanLabel = "FAN —";
    public string FanLabel { get => _fanLabel; set => SetProperty(ref _fanLabel, value); }

    /// <summary>Last 60 utilisation samples — bound to a Sparkline control.</summary>
    public ObservableCollection<double> UtilHistory { get; } = new();
    public ObservableCollection<double> TempHistory { get; } = new();

    private string _queueLabel = "Queue idle";
    public string QueueLabel { get => _queueLabel; set => SetProperty(ref _queueLabel, value); }

    private string _licenseLabel = "Trial";
    public string LicenseLabel { get => _licenseLabel; set => SetProperty(ref _licenseLabel, value); }

    private string _licenseKind = "trial";
    public string LicenseKind { get => _licenseKind; set => SetProperty(ref _licenseKind, value); }

    private string _versionLabel;
    public string VersionLabel { get => _versionLabel; set => SetProperty(ref _versionLabel, value); }

    private string _autosaveLabel = "Auto-save —";
    public string AutosaveLabel { get => _autosaveLabel; set => SetProperty(ref _autosaveLabel, value); }

    private string _budgetLabel = "";
    /// <summary>"฿612 / ฿1,500 · 41%" — visible iff a monthly budget is set
    /// in Settings. Empty = no budget configured, no pill rendered.</summary>
    public string BudgetLabel { get => _budgetLabel; set => SetProperty(ref _budgetLabel, value); }

    private string _budgetKind = "ok";
    /// <summary>"ok" (&lt;75%) / "warn" (75-100%) / "err" (&gt;100%) — drives
    /// the pill colour just like the temp pill.</summary>
    public string BudgetKind { get => _budgetKind; set => SetProperty(ref _budgetKind, value); }

    private System.Windows.Threading.DispatcherTimer? _autosaveTicker;
    /// <summary>Drives the bottom-right "Saved 04s ago" pill. Reads from
    /// AppSettings.LastSavedAt every second once started by the App ctor.</summary>
    public void StartAutosaveTicker()
    {
        if (_autosaveTicker is not null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _autosaveTicker = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _autosaveTicker.Tick += (_, _) =>
        {
            var studio = (System.Windows.Application.Current as App)?.Studio;
            if (studio is null) return;
            var settings = studio.Settings;
            if (settings.LastSavedAt is { } t)
            {
                var ago = DateTime.UtcNow - t;
                if (ago.TotalSeconds < 5) AutosaveLabel = "Saved · just now";
                else if (ago.TotalSeconds < 60) AutosaveLabel = $"Saved · {(int)ago.TotalSeconds}s ago";
                else if (ago.TotalMinutes < 60) AutosaveLabel = $"Saved · {(int)ago.TotalMinutes}m ago";
                else AutosaveLabel = $"Saved · {(int)ago.TotalHours}h ago";
            }
            else
            {
                AutosaveLabel = "Auto-save · not yet";
            }
            // Budget check piggy-backs on the 1-Hz autosave ticker — the
            // usage table is small and the query is indexed, so per-second
            // polling has negligible cost.
            UpdateBudget(studio);
        };
        _autosaveTicker.Start();
    }

    private DateTimeOffset _budgetLastChecked;
    private double _budgetLastSpent;

    /// <summary>Compute month-to-date spending vs the configured budget.
    /// Re-queries SQLite at most once every 5 seconds — even though the
    /// usage table is tiny, hitting it 60×/min for an unchanged number
    /// would be wasteful.</summary>
    private void UpdateBudget(StudioContext studio)
    {
        var budget = studio.Settings.MonthlyBudgetThb;
        if (budget <= 0)
        {
            BudgetLabel = "";
            return;
        }
        if (DateTimeOffset.UtcNow - _budgetLastChecked < TimeSpan.FromSeconds(5))
        {
            // Use the cached value to keep the label fresh between SQL hits.
            RenderBudgetLabel(budget, _budgetLastSpent);
            return;
        }
        try
        {
            var nowLocal = DateTimeOffset.Now;
            var monthStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Offset).ToUniversalTime();
            var spent = studio.Usage.Total(monthStart).Thb;
            _budgetLastSpent = spent;
            _budgetLastChecked = DateTimeOffset.UtcNow;
            RenderBudgetLabel(budget, spent);
        }
        catch
        {
            // Budget pill is non-critical — DB hiccup just keeps the last
            // value on screen.
        }
    }

    private void RenderBudgetLabel(double budget, double spent)
    {
        var pct = budget <= 0 ? 0 : (spent / budget * 100);
        BudgetLabel = $"฿{spent:N0} / ฿{budget:N0} · {pct:F0}%";
        BudgetKind = pct switch
        {
            >= 100 => "err",
            >= 75  => "warn",
            _      => "ok",
        };
    }

    public StatusBarViewModel()
    {
        _versionLabel = "v " + UpdateService.CurrentVersion();
        ApplyLicense(LicenseGuard.Instance.Current);
        LicenseGuard.Instance.LicenseChanged += info =>
        {
            var app = System.Windows.Application.Current;
            if (app is null) { ApplyLicense(info); return; }
            app.Dispatcher.Invoke(() => ApplyLicense(info));
        };

        var telemetry = (System.Windows.Application.Current as App)?.Studio.GpuTelemetry;
        if (telemetry is not null)
        {
            telemetry.SnapshotReceived += OnGpuSnapshot;
        }

        var engine = (System.Windows.Application.Current as App)?.Studio.ComfyEngine;
        if (engine is not null)
        {
            engine.Changed += () =>
            {
                var app = System.Windows.Application.Current;
                if (app is null) return;
                app.Dispatcher.BeginInvoke(() => ApplyEngine(engine));
            };
            ApplyEngine(engine);
        }
    }

    private void ApplyEngine(Services.LocalComfy.ComfyEngine engine)
    {
        ConnectionOk = engine.State == Services.LocalComfy.ComfyEngineState.Running;
        ConnectionLabel = engine.State switch
        {
            Services.LocalComfy.ComfyEngineState.Running => "ComfyUI · พร้อม",
            Services.LocalComfy.ComfyEngineState.Starting => "ComfyUI · กำลังเปิด",
            Services.LocalComfy.ComfyEngineState.Installing => "ComfyUI · กำลังติดตั้ง",
            Services.LocalComfy.ComfyEngineState.Removing => "ComfyUI · กำลังลบ",
            Services.LocalComfy.ComfyEngineState.Failed => "ComfyUI · เปิดไม่สำเร็จ",
            Services.LocalComfy.ComfyEngineState.NotInstalled => "ComfyUI · ยังไม่ติดตั้ง",
            _ => "ComfyUI · ปิดอยู่",
        };
    }

    private void OnGpuSnapshot(GpuSnapshot s)
    {
        var app = System.Windows.Application.Current;
        if (app is null || !app.Dispatcher.CheckAccess())
        {
            app?.Dispatcher.Invoke(() => OnGpuSnapshot(s));
            return;
        }

        GpuLabel = string.IsNullOrEmpty(s.ShortName) ? "GPU" : s.ShortName;
        GpuUtilisation = s.UtilPct < 0 ? 0 : s.UtilPct;
        GpuUtilLabel = s.UtilPct < 0 ? "—" : $"{s.UtilPct}%";

        if (s.VramTotalMb > 0)
            VramLabel = $"VRAM {s.VramUsedGb:F1} / {s.VramTotalGb:F0} GB";
        else
            VramLabel = "VRAM —";

        if (s.TempC >= 0)
        {
            TempLabel = $"{s.TempC}°C";
            TempKind = s.TempC < 65 ? "cool" : s.TempC < 80 ? "warm" : "hot";
        }
        else { TempLabel = "—"; TempKind = "cool"; }

        PowerLabel = s.PowerW > 0 ? $"{s.PowerW:F0} W" : "—";
        FanLabel = s.FanPct >= 0 ? $"{s.FanPct}%" : "—";

        // Append to histories — Sparkline binds these and re-renders.
        UtilHistory.Add(s.UtilPct < 0 ? 0 : s.UtilPct);
        if (UtilHistory.Count > 60) UtilHistory.RemoveAt(0);
        TempHistory.Add(s.TempC < 0 ? 0 : s.TempC);
        if (TempHistory.Count > 60) TempHistory.RemoveAt(0);
    }

    private void ApplyLicense(LicenseInfo info)
    {
        if (!info.IsValid)
        {
            LicenseLabel = "Trial · activate";
            LicenseKind = "warn";
            return;
        }
        LicenseLabel = $"{info.LicenseType.ToUpperInvariant()} ☾";
        LicenseKind = info.DaysRemaining is > 0 and < 14 ? "warn" : "ok";
    }
}
