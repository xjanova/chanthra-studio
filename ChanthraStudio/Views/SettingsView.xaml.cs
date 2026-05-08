using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;
using ChanthraStudio.Views.Dialogs;

namespace ChanthraStudio.Views;

public partial class SettingsView : UserControl
{
    private UpdateViewModel? _updateVm;

    public SettingsView()
    {
        InitializeComponent();
        // ViewModel created lazily so design-time DataContext doesn't trigger DB bootstrap.
        Loaded += (_, _) =>
        {
            if (DataContext is null)
            {
                var s = App.Current.Studio;
                DataContext = new SettingsViewModel(s.Settings, s.Providers);
            }

            UpdateLicenseBadge(LicenseGuard.Instance.Current);
            VersionLabel.Text = "v " + UpdateService.CurrentVersion();
            LicenseGuard.Instance.LicenseChanged += UpdateLicenseBadge;
        };
        Unloaded += (_, _) =>
        {
            LicenseGuard.Instance.LicenseChanged -= UpdateLicenseBadge;
        };
    }

    private void UpdateLicenseBadge(LicenseInfo info)
    {
        // Marshal to UI thread because LicenseChanged fires from async tasks
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateLicenseBadge(info));
            return;
        }

        if (info.IsValid)
        {
            LicenseBadge.Background = (Brush)FindResource("BrushOk");
            LicenseBadge.BorderBrush = (Brush)FindResource("BrushOk");
            LicenseBadgeLabel.Text = info.LicenseType.ToUpperInvariant();
            LicenseBadgeLabel.Foreground = (Brush)FindResource("BrushVoid");
            LicenseDetail.Text = info.DisplayLine;
        }
        else
        {
            LicenseBadge.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#1FE0B056")!;
            LicenseBadge.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#66E0B056")!;
            LicenseBadgeLabel.Text = "not activated";
            LicenseBadgeLabel.Foreground = (Brush)FindResource("BrushText1");
            LicenseDetail.Text = string.IsNullOrEmpty(info.Message)
                ? "Activate to unlock auto-update from GitHub Releases."
                : info.Message;
        }
    }

    private void ManageLicense_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ActivationDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        _updateVm ??= new UpdateViewModel();
        UpdateStatus.Text = "checking github...";
        await _updateVm.CheckAsync();
        UpdateStatus.Text = _updateVm.Status;
        if (_updateVm.HasUpdate)
        {
            var dlg = new UpdateDialog(_updateVm) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }
    }

    private void ProviderKey_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Prime the box once with the decrypted key — PasswordBox doesn't accept Bindings
        // on Password (by design), so we sync it manually on first appearance.
        if (sender is PasswordBox pb && pb.Tag is ProviderRow row && pb.Password != row.ApiKeyDraft)
            pb.Password = row.ApiKeyDraft;
    }

    private void ProviderKey_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.Tag is ProviderRow row)
        {
            if (pb.Password != row.ApiKeyDraft)
                row.ApiKeyDraft = pb.Password;
        }
    }
}
