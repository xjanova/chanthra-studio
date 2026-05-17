using System;
using System.IO;
using System.Threading;

namespace ChanthraStudio.Services;

/// <summary>
/// File-backed activity log. Catches what the codebase used to silently
/// swallow (DB writes, posting failures, license probes, generation
/// teardown) so reproducing user-reported bugs doesn't require running
/// the app under a debugger. Append-only, daily-rolled.
///
/// Path: <c>{AppPaths.LogsFolder}/chanthra-{yyyy-MM-dd}.log</c>.
///
/// <para>
/// All methods are thread-safe via a single lock — log volume is tiny
/// (a few dozen lines per session), so the lock contention is irrelevant.
/// The file is opened, written, flushed, and closed per call so a hard
/// kill doesn't lose the last few entries.
/// </para>
/// </summary>
public static class ActivityLog
{
    private static readonly object _gate = new();

    public enum Level { Info, Warn, Error }

    public static void Info(string area, string message) => Write(Level.Info, area, message, null);
    public static void Warn(string area, string message) => Write(Level.Warn, area, message, null);
    public static void Error(string area, string message, Exception? ex = null)
        => Write(Level.Error, area, message, ex);

    private static void Write(Level level, string area, string message, Exception? ex)
    {
        try
        {
            var line = FormatLine(level, area, message, ex);
            var path = Path.Combine(AppPaths.LogsFolder, $"chanthra-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            // Mirror to the debug stream so Visual Studio's Output pane catches
            // it during dev runs without having to tail the file.
            System.Diagnostics.Debug.WriteLine($"[chanthra:{level}] {area}: {message}");
        }
        catch
        {
            // Logging must never escalate — if the log file is locked or the
            // disk is full, eat the error rather than tank the operation
            // that was being logged.
        }
    }

    private static string FormatLine(Level level, string area, string message, Exception? ex)
    {
        var levelTag = level switch
        {
            Level.Info  => "INFO ",
            Level.Warn  => "WARN ",
            Level.Error => "ERROR",
            _ => "?",
        };
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var threadId = Thread.CurrentThread.ManagedThreadId;
        var sb = new System.Text.StringBuilder();
        sb.Append(timestamp).Append(' ').Append(levelTag).Append(" [t").Append(threadId).Append("] ")
          .Append(area).Append(": ").Append(message);
        if (ex is not null)
        {
            sb.Append(" — ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            // Single-line stack trace so the log stays readable; the full
            // multi-line trace can come later if we ever need it.
            if (ex.StackTrace is not null)
            {
                var firstLine = ex.StackTrace.Split('\n', 2)[0].Trim();
                sb.Append(" @ ").Append(firstLine);
            }
        }
        return sb.ToString();
    }
}
