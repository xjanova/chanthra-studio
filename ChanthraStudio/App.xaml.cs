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

        ActivityLog.Info("app", $"start · v{UpdateService.CurrentVersion()} · data={AppPaths.Root}");

        // One-time diagnostic — verify the SQLite FK pragma is on so writes
        // through generation_jobs / clips / shots stay consistent. If this
        // ever prints false, schema integrity has degraded and the user
        // should clear %APPDATA%/ChanthraStudio/chanthra.db.
        var fk = Studio.Db.ForeignKeysEnforced;
        ActivityLog.Info("db", $"foreign_keys = {(fk ? "ON" : "OFF")}");
        if (!fk) ActivityLog.Warn("db", "FK enforcement is OFF — writes may pass without parent rows");

        // Recover shots that were left in Generating state by a previous
        // session crash or hard-kill. Flip them to Error so the storyboard
        // rebuild shows them with a red dot instead of an animated gold one.
        var swept = Studio.Shots.SweepStuckGenerations();
        if (swept > 0)
            ActivityLog.Info("shots", $"swept {swept} stuck Generating shots → Error");

        // Kick off the nvidia-smi poller so the StatusBar shows real numbers
        // within ~2 seconds of launch. Service detects no-NVIDIA and goes
        // dormant — safe to start unconditionally.
        Studio.GpuTelemetry.Start();

        // Start the auto-schedule scanner. First tick is 5s out so the
        // license + workflow + db bootstrap finishes before we try to
        // fan out concurrent generations.
        Studio.ScheduleService.Start();

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
        try { Studio.GpuTelemetry.Dispose(); } catch { /* dispose is best effort */ }
        try { Studio.ScheduleService.Dispose(); } catch { /* dispose is best effort */ }
        // Drain the activity log buffer before the process exits — the
        // AppDomain.ProcessExit hook fires too late for some shutdown
        // flows (e.g. Application.Current.Shutdown from a menu item).
        try { ActivityLog.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
