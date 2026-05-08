using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.ViewModels;

public sealed class StatusBarViewModel : ObservableObject
{
    private string _connectionLabel = "Connected";
    public string ConnectionLabel { get => _connectionLabel; set => SetProperty(ref _connectionLabel, value); }

    private string _gpuLabel = "GPU H100 ×4";
    public string GpuLabel { get => _gpuLabel; set => SetProperty(ref _gpuLabel, value); }

    private double _gpuUtilisation = 0.73;
    public double GpuUtilisation { get => _gpuUtilisation; set => SetProperty(ref _gpuUtilisation, value); }

    private string _vramLabel = "VRAM 61.3 / 80 GB";
    public string VramLabel { get => _vramLabel; set => SetProperty(ref _vramLabel, value); }

    private string _queueLabel = "Queue 3 shots · ETA 2:14";
    public string QueueLabel { get => _queueLabel; set => SetProperty(ref _queueLabel, value); }

    private string _licenseLabel = "Trial";
    public string LicenseLabel { get => _licenseLabel; set => SetProperty(ref _licenseLabel, value); }

    /// <summary>"ok" / "warn" / "err" / "trial" — drives the badge color in StatusBar.xaml.</summary>
    private string _licenseKind = "trial";
    public string LicenseKind { get => _licenseKind; set => SetProperty(ref _licenseKind, value); }

    private string _versionLabel;
    public string VersionLabel { get => _versionLabel; set => SetProperty(ref _versionLabel, value); }

    private string _autosaveLabel = "Auto-save 2s ago";
    public string AutosaveLabel { get => _autosaveLabel; set => SetProperty(ref _autosaveLabel, value); }

    public StatusBarViewModel()
    {
        _versionLabel = "v " + UpdateService.CurrentVersion();
        ApplyLicense(LicenseGuard.Instance.Current);
        LicenseGuard.Instance.LicenseChanged += info =>
        {
            // Marshal to UI thread because LicenseChanged fires from async tasks
            var app = System.Windows.Application.Current;
            if (app is null) { ApplyLicense(info); return; }
            app.Dispatcher.Invoke(() => ApplyLicense(info));
        };
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
