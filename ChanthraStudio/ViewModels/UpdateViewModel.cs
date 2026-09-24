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
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            // Install stayed clickable mid-download; a second click raced the
            // first over the same .part file.
            ((RelayCommand)DownloadAndApplyCommand).NotifyCanExecuteChanged();
            ((RelayCommand)CheckCommand).NotifyCanExecuteChanged();
        }
    }

    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    private string _progressLabel = "";
    public string ProgressLabel { get => _progressLabel; set => SetProperty(ref _progressLabel, value); }

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private CancellationTokenSource? _cts;

    public IRelayCommand CheckCommand { get; }
    public IRelayCommand DownloadAndApplyCommand { get; }
    public IRelayCommand OpenDownloadPageCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand SkipVersionCommand { get; }

    public UpdateViewModel()
    {
        CheckCommand = new RelayCommand(async () => await CheckAsync(), () => !_isChecking && !_isDownloading);
        DownloadAndApplyCommand = new RelayCommand(async () => await DownloadAsync(), () => HasUpdate && !_isDownloading && LicenseGuard.Instance.IsLicensed);
        OpenDownloadPageCommand = new RelayCommand(UpdateService.OpenDownloadPage);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        SkipVersionCommand = new RelayCommand(SkipVersion, () => HasUpdate);
    }

    /// <summary>Stash the currently-offered version in
    /// <see cref="AppSettings.SkippedUpdateVersion"/> so the periodic
    /// poller stops re-popping the same dialog. A later release
    /// (higher CompareSemver) supersedes the skip automatically.</summary>
    /// <summary>
    /// Stop a download in flight. The dialog calls this whenever it closes —
    /// "later", "skip this version" and the X all used to close it while the
    /// download ran on, then installed and restarted the app anyway.
    /// </summary>
    public void CancelPending() => _cts?.Cancel();

    private void SkipVersion()
    {
        if (_info is null || !_info.HasUpdate) return;
        CancelPending();
        try
        {
            var settings = ((App)System.Windows.Application.Current).Studio.Settings;
            settings.SkippedUpdateVersion = _info.LatestVersion;
            settings.Save();
            ActivityLog.Info("update", $"user skipped version {_info.LatestVersion}");
            Status = $"skipped · we won't ask again until a newer release ships";
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("update", "skip-version persist failed: " + ex.Message);
            Status = "skip failed — check Settings storage";
        }
    }

    public async Task CheckAsync()
    {
        IsChecking = true;
        Status = "checking for updates...";
        try
        {
            var info = await UpdateService.CheckAsync();
            Info = info;
            Status = info is null
                ? "could not check for updates — xman4289.com did not answer"
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
        if (_info is null || !_info.HasUpdate || _isDownloading) return;
        if (!LicenseGuard.Instance.IsLicensed)
        {
            Status = "auto-update requires a valid license";
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
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
            string? path;
            try
            {
                path = await UpdateService.DownloadAsync(_info, prog, token);
            }
            catch (System.IO.InvalidDataException ex)
            {
                Status = ex.Message;
                return;
            }
            if (token.IsCancellationRequested)
            {
                Status = "ยกเลิกการอัปเดตแล้ว";
                return;
            }
            if (string.IsNullOrEmpty(path))
            {
                Status = "download failed";
                return;
            }
            ActivityLog.Info("update", $"downloaded {_info.AssetName} ({_info.AssetSizeBytes:N0} bytes, " +
                (string.IsNullOrEmpty(_info.AssetSha256) ? "no digest published" : "sha256 verified") + ") — installing");
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
