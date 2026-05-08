using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class ActivationViewModel : ObservableObject
{
    private string _licenseKey = "";
    public string LicenseKey
    {
        get => _licenseKey;
        set => SetProperty(ref _licenseKey, value);
    }

    public string MachineId => MachineFingerprint.Get();

    public LicenseInfo Current => LicenseGuard.Instance.Current;

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (SetProperty(ref _isBusy, value)) ((RelayCommand)ActivateCommand).NotifyCanExecuteChanged(); } }

    public IRelayCommand ActivateCommand { get; }
    public IRelayCommand DeactivateCommand { get; }
    public IRelayCommand BuyCommand { get; }
    public IRelayCommand CopyMachineIdCommand { get; }
    public IRelayCommand RefreshCommand { get; }

    public ActivationViewModel()
    {
        ActivateCommand = new RelayCommand(async () => await ActivateAsync(), () => !_isBusy && !string.IsNullOrWhiteSpace(_licenseKey));
        DeactivateCommand = new RelayCommand(async () => await DeactivateAsync(), () => !_isBusy && Current.IsValid);
        BuyCommand = new RelayCommand(OpenBuyPage);
        CopyMachineIdCommand = new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(MachineId); StatusMessage = "machine id copied"; } catch { }
        });
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !_isBusy);

        // Pre-fill from saved key so users see what's currently active
        if (Current.HasKey) _licenseKey = Current.LicenseKey;
        LicenseGuard.Instance.LicenseChanged += _ =>
        {
            OnPropertyChanged(nameof(Current));
            ((RelayCommand)DeactivateCommand).NotifyCanExecuteChanged();
        };
    }

    private async Task ActivateAsync()
    {
        IsBusy = true;
        StatusMessage = "contacting xman4289.com...";
        try
        {
            var v = UpdateService.CurrentVersion();
            var result = await LicenseGuard.Instance.ActivateAsync(_licenseKey, v);
            StatusMessage = result.IsValid
                ? $"activated · {result.LicenseType} · {(result.ExpiresAt is null ? "perpetual" : $"expires {result.ExpiresAt:yyyy-MM-dd}")}"
                : (string.IsNullOrEmpty(result.Message) ? "activation failed" : result.Message);
            OnPropertyChanged(nameof(Current));
        }
        finally { IsBusy = false; }
    }

    private async Task DeactivateAsync()
    {
        IsBusy = true;
        StatusMessage = "deactivating...";
        try
        {
            await LicenseGuard.Instance.DeactivateAsync();
            _licenseKey = "";
            OnPropertyChanged(nameof(LicenseKey));
            OnPropertyChanged(nameof(Current));
            StatusMessage = "deactivated";
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "refreshing...";
        try
        {
            await LicenseGuard.Instance.RefreshAsync();
            OnPropertyChanged(nameof(Current));
            StatusMessage = Current.IsValid ? $"valid · {Current.DaysRemaining}d remaining" : (Current.Message ?? "no license");
        }
        finally { IsBusy = false; }
    }

    private static void OpenBuyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://xman4289.com/products/chanthra-studio") { UseShellExecute = true });
        }
        catch { }
    }
}
