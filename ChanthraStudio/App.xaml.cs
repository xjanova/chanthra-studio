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
        // Global safety net — a stray UI layout/render exception should be
        // LOGGED and RECOVERED, not silently kill the app ("เด้ง"). The full
        // exception lands in %APPDATA%/ChanthraStudio/logs for diagnosis.
        DispatcherUnhandledException += (_, ux) =>
        {
            try { ActivityLog.Error("app", "unhandled UI exception (recovered)", ux.Exception); } catch { }
            ux.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ux) =>
        {
            try { ActivityLog.Warn("app", "unhandled domain exception: " + ((ux.ExceptionObject as Exception)?.Message ?? "unknown")); } catch { }
        };

        // Apply theme override BEFORE the base call — base.OnStartup
        // honours StartupUri which instantiates MainWindow, and any
        // StaticResource lookups in MainWindow.xaml are resolved at
        // construction. Adding the alternate dict to MergedDictionaries
        // here ensures the override values are in scope when those
        // lookups run. (T53 · 7.22)
        ApplyThemeOverride(Studio.Settings.Theme);

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

        // Rented-GPU reaper. Starts unconditionally, even when the feature is
        // switched off, because a machine left running by a previous session
        // is still billing and this loop is the only thing that will notice.
        // Its first act is to reconcile any workers the last run left behind.
        Studio.GpuWorkers.Start();

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

        // Initial update check + start the recurring poller (T65 / 7.23).
        // The recurring loop respects Settings.AutoCheckUpdates so a user
        // who opted out gets a clean app — no background HTTP, no banner.
        _ = RunUpdatePollerAsync();
    }

    /// <summary>
    /// Background loop that re-checks GitHub Releases every
    /// <see cref="AppSettings.UpdateCheckIntervalHours"/>. First poll is
    /// 3s out so license validate settles first; subsequent polls fire on
    /// the configured cadence (clamped 1..168 hrs by AppSettings).
    /// </summary>
    private async Task RunUpdatePollerAsync()
    {
        // Initial 3s delay matches the previous BootAsync behaviour.
        try { await Task.Delay(TimeSpan.FromSeconds(3)); } catch { return; }

        while (true)
        {
            try
            {
                await PollForUpdateOnceAsync();
            }
            catch (Exception ex)
            {
                ActivityLog.Warn("update", "poller iteration failed: " + ex.Message);
            }

            // Re-read interval each tick so changing it in Settings takes
            // effect on the NEXT cycle (no restart needed).
            var hours = Math.Clamp(Studio.Settings.UpdateCheckIntervalHours, 1, 168);
            try { await Task.Delay(TimeSpan.FromHours(hours)); }
            catch { return; }
        }
    }

    /// <summary>One poll iteration. Skips when unlicensed, when the user
    /// opted out, when GitHub returns nothing, or when the latest version
    /// matches <see cref="AppSettings.SkippedUpdateVersion"/>.</summary>
    private async Task PollForUpdateOnceAsync()
    {
        if (!LicenseGuard.Instance.IsLicensed) return;
        if (!Studio.Settings.AutoCheckUpdates) return;

        var info = await UpdateService.CheckAsync();
        if (info is null || !info.HasUpdate) return;

        // If the user already declined this exact version, stay quiet.
        // A higher version released later supersedes the skip.
        var skipped = Studio.Settings.SkippedUpdateVersion ?? "";
        if (!string.IsNullOrEmpty(skipped)
            && UpdateService.CompareSemver(info.LatestVersion, skipped) <= 0)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            // Don't stack dialogs — if one is already open from a previous
            // poll, leave the user with the existing prompt.
            foreach (Window w in Windows)
                if (w is UpdateDialog) return;

            var vm = new UpdateViewModel { Info = info };
            vm.Status = $"new version {info.LatestVersion} available · current {info.CurrentVersion}";
            var dlg = new UpdateDialog(vm) { Owner = MainWindow };
            dlg.ShowDialog();
        });
    }

    /// <summary>
    /// Merge a theme-override ResourceDictionary into
    /// <see cref="Application.Resources"/>'s MergedDictionaries when the
    /// user's saved Theme isn't the default "lunar". MergedDictionaries
    /// resolve later entries first, so any keys defined in the override
    /// shadow the canonical Colors.xaml values without modifying the
    /// source file. (T53 · 7.22)
    /// </summary>
    private static void ApplyThemeOverride(string? theme)
    {
        if (string.IsNullOrEmpty(theme) || theme.Equals("lunar", StringComparison.OrdinalIgnoreCase))
            return;
        // Known alternate themes — extend this map when new variants ship.
        var uri = theme.ToLowerInvariant() switch
        {
            "dawn" => new Uri("pack://application:,,,/Themes/Colors-Dawn.xaml", UriKind.Absolute),
            _      => null,
        };
        if (uri is null) return;
        try
        {
            var rd = new System.Windows.ResourceDictionary { Source = uri };
            // Append (not insert at 0) so the override values beat the
            // canonical ones for same-key lookups.
            Application.Current.Resources.MergedDictionaries.Add(rd);
            Services.ActivityLog.Info("app", $"theme override applied · {theme}");
        }
        catch (Exception ex)
        {
            Services.ActivityLog.Warn("app", $"theme override failed for '{theme}': {ex.Message}");
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
        // Release rented machines BEFORE disposing anything else. This one
        // blocks (up to ~25s) on purpose: once this process is gone nothing
        // of ours can stop the meter, so a slow exit is strictly better than
        // a card that bills all night. Skipped when the user has turned
        // terminate-on-exit off, or when nothing is running.
        try { Studio.GpuWorkers.TerminateAllOnExit(); }
        catch { /* the rows stay marked live, so the next launch will retry */ }
        try { Studio.GpuWorkers.Dispose(); } catch { /* dispose is best effort */ }
        // Stop our own ComfyUI. The job object would kill it anyway when this
        // process dies, but an orderly stop lets torch release the GPU instead
        // of the driver having to reclaim it, and it keeps the log readable.
        try { Studio.ComfyEngine.Dispose(); } catch { /* dispose is best effort */ }
        try { Studio.GpuTelemetry.Dispose(); } catch { /* dispose is best effort */ }
        try { Studio.ScheduleService.Dispose(); } catch { /* dispose is best effort */ }
        // Drain the activity log buffer before the process exits — the
        // AppDomain.ProcessExit hook fires too late for some shutdown
        // flows (e.g. Application.Current.Shutdown from a menu item).
        try { ActivityLog.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
