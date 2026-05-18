using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
/// 7.13 — switched from synchronous "open · write · close per line" to a
/// buffered writer. Producers drop lines into a thread-safe queue and
/// return immediately; a single background flusher drains the queue and
/// writes a batch every 250ms (or sooner if the queue tops 256 lines).
/// Under a generation burst this turns N file IOs into 1 batch IO.
/// </para>
///
/// <para>
/// Process shutdown: an AppDomain ProcessExit hook flushes the remaining
/// queue synchronously so we don't lose the last few lines on Ctrl+C /
/// task-kill / regular exit. The flush is best-effort — if the file is
/// locked at shutdown time we drop the queue rather than hang.
/// </para>
/// </summary>
public static class ActivityLog
{
    public enum Level { Info, Warn, Error }

    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly object _flushGate = new();
    private static readonly CancellationTokenSource _shutdownCts = new();
    private static Task? _flusher;
    private const int FlushIntervalMs = 250;
    private const int QueueDrainTrigger = 256;
    /// <summary>Hold-over for the last filesystem error so we don't log
    /// the same "disk full" once per drain cycle.</summary>
    private static string? _lastFlushError;

    static ActivityLog()
    {
        // Kick the background flusher once per process. The Task self-loops
        // until the shutdown CTS cancels.
        _flusher = Task.Run(FlushLoopAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public static void Info(string area, string message) => Enqueue(Level.Info, area, message, null);
    public static void Warn(string area, string message) => Enqueue(Level.Warn, area, message, null);
    public static void Error(string area, string message, Exception? ex = null)
        => Enqueue(Level.Error, area, message, ex);

    private static void Enqueue(Level level, string area, string message, Exception? ex)
    {
        try
        {
            var line = FormatLine(level, area, message, ex);
            _queue.Enqueue(line);
            // Mirror to the debug stream so Visual Studio's Output pane catches
            // it during dev runs without having to tail the file.
            System.Diagnostics.Debug.WriteLine($"[chanthra:{level}] {area}: {message}");
        }
        catch
        {
            // Logging must never escalate — if FormatLine throws (e.g. due
            // to an exception with a nasty ToString), drop the line.
        }
    }

    private static async Task FlushLoopAsync()
    {
        var ct = _shutdownCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wake up either on the timer or earlier if the queue has
                // grown past the drain trigger.
                for (var elapsed = 0; elapsed < FlushIntervalMs && _queue.Count < QueueDrainTrigger; elapsed += 25)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
                FlushOnce();
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch
            {
                // FlushOnce already swallows its own errors; a top-level
                // catch here protects the loop itself from terminating.
            }
        }
        // Final drain on shutdown.
        FlushOnce();
    }

    private static void FlushOnce()
    {
        if (_queue.IsEmpty) return;
        lock (_flushGate)
        {
            try
            {
                var path = Path.Combine(AppPaths.LogsFolder, $"chanthra-{DateTime.Now:yyyy-MM-dd}.log");
                // Single open per batch. FileShare.Read so a user tailing the
                // log doesn't lock us out.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);
                while (_queue.TryDequeue(out var line))
                    writer.WriteLine(line);
                writer.Flush();
                _lastFlushError = null;
            }
            catch (Exception ex)
            {
                // Dedupe noisy errors — if the disk is full we'd otherwise
                // spam the Debug stream once per flush cycle.
                var msg = ex.Message;
                if (msg != _lastFlushError)
                {
                    System.Diagnostics.Debug.WriteLine($"[chanthra:ActivityLog] flush failed: {msg}");
                    _lastFlushError = msg;
                }
            }
        }
    }

    /// <summary>
    /// Synchronous drain on process exit. Called from the ProcessExit hook
    /// so the trailing lines survive a normal app shutdown.
    /// </summary>
    public static void Shutdown()
    {
        try { _shutdownCts.Cancel(); } catch { }
        // Give the background loop a brief window to drain before the
        // process really exits. Worst case: we drop a handful of lines.
        try { _flusher?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        FlushOnce();
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
