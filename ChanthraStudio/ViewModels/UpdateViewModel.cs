using System;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class UpdateViewModel : ObservableObject
{
    private UpdateInfo? _info;
    public UpdateInfo? Info { get => _info; set { SetProperty(ref _info, value); OnPropertyChanged(nameof(HasInfo)); OnPropertyChanged(nameof(HasUpdate)); } }

    public bool HasInfo => _info is not null;
    public bool HasUpdate => _info is not null && _info.HasUpdate;

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; set => SetProperty(ref _isChecking, value); }

    private bool _isDownloading;
    public bool IsDownloading { get => _isDownloading; set => SetProperty(ref _isDownloading, value); }

    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    private string _progressLabel = "";
    public string ProgressLabel { get => _progressLabel; set => SetProperty(ref _progressLabel, value); }

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private CancellationTokenSource? _cts;

    public IRelayCommand CheckCommand { get; }
    public IRelayCommand DownloadAndApplyCommand { get; }
    public IRelayCommand OpenReleasePageCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public UpdateViewModel()
    {
        CheckCommand = new RelayCommand(async () => await CheckAsync(), () => !_isChecking && !_isDownloading);
        DownloadAndApplyCommand = new RelayCommand(async () => await DownloadAsync(), () => HasUpdate && !_isDownloading && LicenseGuard.Instance.IsLicensed);
        OpenReleasePageCommand = new RelayCommand(UpdateService.OpenReleasePage);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public async Task CheckAsync()
    {
        IsChecking = true;
        Status = "checking github...";
        try
        {
            var info = await UpdateService.CheckAsync();
            Info = info;
            Status = info is null
                ? "could not reach github"
                : info.HasUpdate
                    ? $"new version {info.LatestVersion} available · current {info.CurrentVersion}"
                    : $"up to date · {info.CurrentVersion}";
            ((RelayCommand)DownloadAndApplyCommand).NotifyCanExecuteChanged();
        }
        finally
        {
            IsChecking = false;
            ((RelayCommand)CheckCommand).NotifyCanExecuteChanged();
        }
    }

    private async Task DownloadAsync()
    {
        if (_info is null || !_info.HasUpdate) return;
        if (!LicenseGuard.Instance.IsLicensed)
        {
            Status = "auto-update requires a valid license";
            return;
        }

        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Progress = 0;
        ProgressLabel = "starting download...";
        Status = $"downloading {_info.AssetName} ...";

        var prog = new Progress<(long Downloaded, long Total)>(t =>
        {
            if (t.Total <= 0) { Progress = 0; ProgressLabel = $"{t.Downloaded / 1024.0:F0} KB"; return; }
            Progress = (double)t.Downloaded / t.Total * 100.0;
            ProgressLabel = $"{t.Downloaded / 1_048_576.0:F1} / {t.Total / 1_048_576.0:F1} MB";
        });

        try
        {
            var path = await UpdateService.DownloadAsync(_info, prog, _cts.Token);
            if (string.IsNullOrEmpty(path))
            {
                Status = "download failed";
                return;
            }
            Status = "applying update — app will restart...";
            UpdateService.ApplyAndRestart(path);
            // Give the helper a beat to spawn before we exit.
            await Task.Delay(500);
            System.Windows.Application.Current.Shutdown();
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
