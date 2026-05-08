using System;
using System.Threading.Tasks;
using System.Windows;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;
using ChanthraStudio.Views.Dialogs;

namespace ChanthraStudio;

public partial class App : Application
{
    public new static App Current => (App)Application.Current;

    public StudioContext Studio { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Fire and forget — the UI is responsive while the license validates
        // and the update check pings GitHub. The status bar reflects the
        // result via LicenseGuard.LicenseChanged.
        _ = BootAsync();
    }

    private async Task BootAsync()
    {
        var version = UpdateService.CurrentVersion();

        try
        {
            await LicenseGuard.Instance.InitializeAsync(version);
        }
        catch
        {
            // License resolution must never block the app from starting.
        }

        // Update prompt — only when licensed, on a fresh launch, after a small delay.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (!LicenseGuard.Instance.IsLicensed) return;

            var info = await UpdateService.CheckAsync();
            if (info is null || !info.HasUpdate) return;

            await Dispatcher.InvokeAsync(() =>
            {
                var vm = new UpdateViewModel { Info = info };
                vm.Status = $"new version {info.LatestVersion} available · current {info.CurrentVersion}";
                var dlg = new UpdateDialog(vm) { Owner = MainWindow };
                dlg.ShowDialog();
            });
        }
        catch
        {
            // No-op — update check is best effort.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Studio.Settings.Save();
        }
        catch
        {
            // Best-effort save on exit — don't block the shutdown if disk is full.
        }
        base.OnExit(e);
    }
}
