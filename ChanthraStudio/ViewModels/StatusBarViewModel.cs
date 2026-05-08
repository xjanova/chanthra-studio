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

    private string _creditsLabel = "Credits 8,420 ☾";
    public string CreditsLabel { get => _creditsLabel; set => SetProperty(ref _creditsLabel, value); }

    private string _versionLabel = "v 2.4.18 canary";
    public string VersionLabel { get => _versionLabel; set => SetProperty(ref _versionLabel, value); }

    private string _autosaveLabel = "Auto-save 2s ago";
    public string AutosaveLabel { get => _autosaveLabel; set => SetProperty(ref _autosaveLabel, value); }
}
