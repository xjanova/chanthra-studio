using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services;

/// <summary>
/// Polls <c>nvidia-smi</c> every 2 seconds and broadcasts the result as
/// <see cref="GpuSnapshot"/> events. The service tolerates non-NVIDIA
/// machines (AMD, Intel, no GPU) by detecting <c>nvidia-smi</c> absence
/// once and going dormant — <see cref="HasNvidia"/> stays false and
/// <see cref="History"/> stays empty.
///
/// nvidia-smi is shipped with every NVIDIA driver, so users with a
/// CUDA-capable card don't need to install anything extra. We avoid
/// NVAPI / NVML native interop because cold-bootstrapping P/Invoke from
/// a portable WPF .exe is more brittle than running a process.
/// </summary>
public sealed class GpuTelemetryService : IDisposable
{
    public bool HasNvidia { get; private set; }
    public string? UnavailableReason { get; private set; }

    private readonly ConcurrentQueue<GpuSnapshot> _history = new();
    private const int HistoryCapacity = 60;

    public IReadOnlyList<GpuSnapshot> History => _history.ToArray();
    public GpuSnapshot? Latest { get; private set; }

    public event Action<GpuSnapshot>? SnapshotReceived;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // First call is a probe — if nvidia-smi isn't on PATH the service
        // marks itself dormant and stops without spinning.
        var first = await ReadOnceAsync(ct);
        if (first is null)
        {
            HasNvidia = false;
            return;
        }
        HasNvidia = true;
        Push(first);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }

            var snap = await ReadOnceAsync(ct);
            if (snap is not null) Push(snap);
        }
    }

    private void Push(GpuSnapshot snap)
    {
        Latest = snap;
        _history.Enqueue(snap);
        while (_history.Count > HistoryCapacity && _history.TryDequeue(out _)) { }
        SnapshotReceived?.Invoke(snap);
    }

    /// <summary>
    /// Run nvidia-smi once and parse the CSV. Returns null if the binary
    /// isn't installed, the process times out, or the output doesn't parse.
    /// </summary>
    private async Task<GpuSnapshot?> ReadOnceAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,fan.speed,power.draw --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watchdog.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await proc.WaitForExitAsync(watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                UnavailableReason = "nvidia-smi timed out";
                return null;
            }

            if (proc.ExitCode != 0)
            {
                UnavailableReason = $"nvidia-smi exit {proc.ExitCode}";
                return null;
            }

            var text = await stdoutTask;
            var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(firstLine)) return null;

            return Parse(firstLine);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            UnavailableReason = "nvidia-smi not found on PATH";
            return null;
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            return null;
        }
    }

    private static GpuSnapshot? Parse(string csv)
    {
        var parts = csv.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 7) return null;

        // Some readings can be "[N/A]" if the field isn't supported on
        // a given card (e.g. server cards without a fan). Treat any
        // unparseable value as -1 / 0 rather than failing the whole row.
        int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : -1;
        long ParseLong(string s) =>
            long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        double ParseDouble(string s) =>
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

        return new GpuSnapshot(
            Name: parts[0],
            TempC: ParseInt(parts[1]),
            UtilPct: ParseInt(parts[2]),
            VramUsedMb: ParseLong(parts[3]),
            VramTotalMb: ParseLong(parts[4]),
            FanPct: ParseInt(parts[5]),
            PowerW: ParseDouble(parts[6]),
            At: DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
    }
}

/// <summary>
/// One nvidia-smi sample. Negative values mean "not available on this card".
/// </summary>
public sealed record GpuSnapshot(
    string Name,
    int TempC,
    int UtilPct,
    long VramUsedMb,
    long VramTotalMb,
    int FanPct,
    double PowerW,
    DateTimeOffset At)
{
    public double VramUsedGb => VramUsedMb / 1024.0;
    public double VramTotalGb => VramTotalMb / 1024.0;
    public double VramFraction => VramTotalMb > 0 ? (double)VramUsedMb / VramTotalMb : 0;
    public string ShortName
    {
        get
        {
            var n = Name ?? "";
            // "NVIDIA GeForce RTX 4090" → "RTX 4090"
            n = n.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase);
            n = n.Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase);
            return n.Trim();
        }
    }
}
