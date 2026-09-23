using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ChanthraStudio.Services;
using ChanthraStudio.ViewModels;
using ChanthraStudio.Views.Dialogs;

namespace ChanthraStudio;

public partial class App : Application
{
    public new static App Current => (App)Application.Current;

    /// <summary>
    /// Built in <see cref="OnStartup"/>, not as a field initialiser: a
    /// corrupt or locked database threw from the App constructor, before any
    /// handler existed, and the app vanished without a word.
    /// </summary>
    public StudioContext Studio { get; private set; } = null!;

    /// <summary>Held for the life of the process; see <see cref="ClaimSingleInstance"/>.</summary>
    private Mutex? _instanceMutex;

    private int _noticeOpen;
    private DateTime _lastNoticeUtc = DateTime.MinValue;
    private string? _lastNoticeKey;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Global safety net — a stray UI layout/render exception should be
        // LOGGED and RECOVERED, not silently kill the app ("เด้ง"). The full
        // exception lands in the logs folder for diagnosis, and the user is
        // told — swallowing it silently left a button that "did nothing".
        DispatcherUnhandledException += (_, ux) =>
        {
            ux.Handled = true;
            if (_exiting)
            {
                // The window is already gone; a dialog now would only hold
                // up the shutdown.
                try { ActivityLog.Error("app", "unhandled exception during exit", ux.Exception); } catch { }
                return;
            }
            // Before the main window has been shown there is nothing to recover
            // to: an exception here used to leave a windowless process that held
            // the single-instance lock. (After Show, a first-render exception is
            // recovered like any other — the window is there to use.)
            if (!_windowShown)
            {
                try { ActivityLog.Error("app", "unhandled exception before the window was ready", ux.Exception); ActivityLog.Flush(); } catch { }
                FailStartup("หน้าต่างหลักเปิดไม่สำเร็จ", ux.Exception);
                return;
            }
            try { ActivityLog.Error("app", "unhandled UI exception (recovered)", ux.Exception); } catch { }
            NotifyRecoveredError(ux.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ux) =>
        {
            // The process is going down: record everything and flush now,
            // the background writer will not get another turn.
            try
            {
                ActivityLog.Error("app", "fatal unhandled exception", ux.ExceptionObject as Exception);
                ActivityLog.Shutdown();
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ux) =>
        {
            try { ActivityLog.Error("app", "unobserved task exception", ux.Exception); } catch { }
            ux.SetObserved();
        };

        // Both early exits leave before any window exists (the main window is
        // opened at the end of this method, not through StartupUri).
        if (!ClaimSingleInstance())
        {
            Shutdown(0);
            return;
        }

        var studio = CreateStudio();
        if (studio is null)
        {
            Shutdown(1);
            return;
        }
        Studio = studio;

        // Apply the theme override before MainWindow is built: StaticResource
        // lookups in MainWindow.xaml are resolved at construction, so the
        // override has to be in MergedDictionaries by then. (T53 · 7.22)
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
        // All of them, not just rows idle for 10 minutes: nothing resumes a
        // job across a restart, and this is the only copy running.
        var swept = Studio.Shots.SweepStuckGenerations(0);
        if (swept > 0)
            ActivityLog.Info("shots", $"swept {swept} stuck Generating shots → Error");

        // Kick off the nvidia-smi poller so the StatusBar shows real numbers
        // within ~2 seconds of launch. Service detects no-NVIDIA and goes
        // dormant — safe to start unconditionally.
        Studio.GpuTelemetry.Start();

        // Rented-GPU reaper. Starts unconditionally, even when the feature is
        // switched off, because a machine left running by a previous session
        // is still billing and this loop is the only thing that will notice.
        // Its first act is to reconcile any workers the last run left behind.
        Studio.GpuWorkers.Start();

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            // Kept here: WPF has already cleared Application.MainWindow by
            // the time OnExit runs, and the storyboard flush needs this.
            _shell = window.DataContext as MainViewModel;
            window.Closed += (_, _) => _exiting = true;
            window.Show();
            _windowShown = true;
        }
        catch (Exception ex)
        {
            try { ActivityLog.Error("app", "main window failed to open", ex); ActivityLog.Flush(); } catch { }
            FailStartup("หน้าต่างหลักเปิดไม่สำเร็จ", ex);
            return;
        }

        // The auto-schedule scanner starts only once there is a window: it
        // renders and posts on its own, and must not do that behind a startup
        // failure. First tick is 5 s out so license + workflow + db settle.
        Studio.ScheduleService.Start();

        // Fire and forget — the UI is responsive while the license validates
        // and the update check pings GitHub. The status bar reflects the
        // result via LicenseGuard.LicenseChanged.
        _ = BootAsync();
    }

    private bool _failing;
    private bool _windowShown;
    private bool _exiting;
    private MainViewModel? _shell;

    /// <summary>
    /// Say why the app cannot go on, then close it properly — OnExit still
    /// stops the scheduler and releases rented machines.
    /// </summary>
    private void FailStartup(string what, Exception ex)
    {
        if (_failing) return;
        _failing = true;
        // Nothing may render or post while the error box waits for the user.
        try { Studio?.ScheduleService.Stop(); } catch { /* shutting down regardless */ }
        try
        {
            MessageBox.Show($"{what}\n\n{ex.Message}\n\nรายละเอียดบันทึกไว้ในโฟลเดอร์ logs",
                "Chanthra Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* shutting down regardless */ }
        Shutdown(1);
    }

    /// <summary>
    /// One copy per data folder. Two copies share one database, so both fired
    /// every schedule (a Facebook post went out twice), each reaped the other's
    /// rented GPUs as leftovers, and both started ComfyUI on the same port.
    /// A second launch brings the first window forward instead.
    /// </summary>
    private bool ClaimSingleInstance()
    {
        var root = AppPaths.Root.TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(root)))[..16];
        try
        {
            _instanceMutex = new Mutex(true, @"Local\ChanthraStudio." + hash, out var createdNew);
            if (createdNew) return true;
        }
        catch (Exception ex)
        {
            // Cannot tell — run rather than refuse to start.
            ActivityLog.Warn("app", "single-instance check failed: " + ex.Message);
            return true;
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        if (!ActivateOtherInstance())
            MessageBox.Show("Chanthra Studio เปิดอยู่แล้ว — ใช้หน้าต่างที่เปิดอยู่ได้เลย",
                "Chanthra Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static bool ActivateOtherInstance()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    // Restore only a minimised window; SW_RESTORE on a maximised
                    // one shrank it back to its normal size.
                    ShowWindowAsync(p.MainWindowHandle, IsIconic(p.MainWindowHandle) ? 9 /* SW_RESTORE */ : 5 /* SW_SHOW */);
                    SetForegroundWindow(p.MainWindowHandle);
                    return true;
                }
            }
        }
        catch { /* best effort */ }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Build the studio, offering to set a damaged database aside. Null means
    /// the app cannot start; the reason has been shown.
    /// </summary>
    private static StudioContext? CreateStudio()
    {
        try
        {
            return new StudioContext();
        }
        catch (Exception ex)
        {
            try { ActivityLog.Error("app", "startup failed", ex); ActivityLog.Flush(); } catch { }

            var sqlite = ex as Microsoft.Data.Sqlite.SqliteException
                         ?? ex.InnerException as Microsoft.Data.Sqlite.SqliteException;
            // 11 = SQLITE_CORRUPT, 26 = SQLITE_NOTADB
            if (sqlite is { SqliteErrorCode: 11 or 26 })
            {
                var answer = MessageBox.Show(
                    "ไฟล์ฐานข้อมูลของ Chanthra Studio เสียหาย เปิดใช้งานต่อไม่ได้\n\n" +
                    $"{AppPaths.DatabaseFile}\n\n" +
                    "กด Yes เพื่อย้ายไฟล์เดิมไปเก็บไว้ (ไม่ลบ) แล้วเริ่มฐานข้อมูลใหม่ — " +
                    "ช็อต คลิป ตารางโพสต์ และคีย์ API ที่บันทึกไว้จะไม่แสดงในฐานข้อมูลใหม่ " +
                    "แต่ไฟล์วิดีโอในโฟลเดอร์ media ยังอยู่ครบ\n\nกด No เพื่อปิดแอป",
                    "ฐานข้อมูลเสียหาย", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return null;
                try
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    foreach (var suffix in new[] { "", "-wal", "-shm" })
                    {
                        var f = AppPaths.DatabaseFile + suffix;
                        if (File.Exists(f)) File.Move(f, f + ".corrupt-" + stamp);
                    }
                    ActivityLog.Warn("db", "damaged database set aside as *.corrupt-" + stamp);
                    return new StudioContext();
                }
                catch (Exception retry)
                {
                    try { ActivityLog.Error("app", "fresh database failed too", retry); ActivityLog.Flush(); } catch { }
                    MessageBox.Show("เริ่มฐานข้อมูลใหม่ไม่สำเร็จ: " + retry.Message,
                        "Chanthra Studio", MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
            }

            var hint = sqlite is { SqliteErrorCode: 5 or 6 }   // BUSY / LOCKED
                ? "ฐานข้อมูลถูกโปรแกรมอื่นล็อกอยู่ — ปิดโปรแกรมที่เปิดไฟล์ chanthra.db (เช่น DB Browser) แล้วเปิดแอปใหม่"
                : "รายละเอียดอยู่ในโฟลเดอร์ logs";
            MessageBox.Show($"Chanthra Studio เริ่มทำงานไม่ได้\n\n{ex.Message}\n\n{hint}",
                "Chanthra Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// Tell the user an action failed after an exception was recovered. One
    /// dialog at a time and at most one per 20 s, so an exception thrown on
    /// every layout pass cannot bury the window in dialogs.
    /// </summary>
    private void NotifyRecoveredError(Exception ex)
    {
        // The quiet period runs from when the last dialog was CLOSED, and the
        // same error is not repeated for two minutes: an exception thrown on
        // every layout pass otherwise reopened the dialog the moment it shut.
        var key = ex.GetType().FullName + ":" + ex.Message;
        if (key == _lastNoticeKey && DateTime.UtcNow - _lastNoticeUtc < TimeSpan.FromMinutes(2)) return;
        if (DateTime.UtcNow - _lastNoticeUtc < TimeSpan.FromSeconds(20)) return;
        if (Interlocked.Exchange(ref _noticeOpen, 1) == 1) return;
        _lastNoticeKey = key;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    MessageBox.Show(
                        "เกิดข้อผิดพลาดที่ไม่คาดคิด — งานที่เพิ่งทำอาจไม่สำเร็จ แต่แอปยังใช้งานต่อได้\n\n" +
                        ex.Message + "\n\nรายละเอียดบันทึกไว้ในโฟลเดอร์ logs",
                        "Chanthra Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    _lastNoticeUtc = DateTime.UtcNow;
                    Interlocked.Exchange(ref _noticeOpen, 0);
                }
            });
        }
        catch { Interlocked.Exchange(ref _noticeOpen, 0); }
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
        // A second copy, or a start that failed, exits before the studio exists.
        if (Studio is null)
        {
            try { ActivityLog.Shutdown(); } catch { }
            base.OnExit(e);
            return;
        }
        // The storyboard saves on a short debounce; write the last edits out
        // before anything shuts down.
        _exiting = true;
        try { _shell?.Board.FlushSave(); } catch { /* best effort */ }
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
        try { _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
