using System.Collections.ObjectModel;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.ViewModels;

public sealed class StatusBarViewModel : ObservableObject
{
    private string _connectionLabel = "Connected";
    public string ConnectionLabel { get => _connectionLabel; set => SetProperty(ref _connectionLabel, value); }

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
