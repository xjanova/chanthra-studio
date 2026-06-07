using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services;

/// <summary>
/// Minimal wrapper around <c>ffmpeg.exe</c>. We don't ship the binary —
/// it's GPL-tainted in some builds and 60+ MB even without docs. Instead:
///
///   1. honour the user-supplied <see cref="AppSettings.FfmpegPath"/> if set
///   2. probe a short list of well-known install locations
///   3. fall back to the system PATH
///
/// Resolution is cached for the lifetime of the AppDomain. If detection
/// fails, callers get a clean <see cref="FfmpegMissingException"/> with an
/// install hint instead of a generic file-not-found.
/// </summary>
public sealed class FFmpegService
{
    private readonly StudioContext _ctx;
    private string? _resolved;

    public FFmpegService(StudioContext ctx) { _ctx = ctx; }

    /// <summary>Returns the resolved ffmpeg.exe path, or null if missing.</summary>
    public string? TryResolve()
    {
        if (_resolved is not null) return _resolved;

        // 1. explicit override
        var overridePath = _ctx.Settings.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return _resolved = overridePath;

        // 2. well-known locations
        var candidates = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe",
                "ffmpeg-8.0.1-full_build", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p)) return _resolved = p;
        }

        // 3. system PATH probe
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return _resolved = p;
            }
            catch { /* skip malformed entries */ }
        }
        return null;
    }

    /// <summary>Forces a re-detection on next <see cref="TryResolve"/>.</summary>
    public void InvalidateCache() => _resolved = null;

    /// <summary>
    /// Resolve the path to <c>ffprobe.exe</c>, the sister binary to ffmpeg.
    /// Lives next to ffmpeg.exe in every standard install (Gyan, BtbN,
    /// scoop, winget), so we just swap the basename. Returns null if
    /// ffmpeg itself isn't installed.
    /// </summary>
    public string? TryResolveFFprobe()
    {
        var ffmpegPath = TryResolve();
        if (ffmpegPath is null) return null;
        var dir = Path.GetDirectoryName(ffmpegPath) ?? "";
        var probe = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(probe) ? probe : null;
    }

    /// <summary>
    /// Probe the duration (in seconds) of an audio or video file by
    /// shelling out to ffprobe. Returns null on any failure — caller
    /// should fall back to a sensible default rather than tank the
    /// pipeline.
    /// </summary>
    /// <remarks>
    /// Implementation uses the <c>format=duration</c> field via
    /// <c>-show_entries</c> + <c>-of csv=p=0</c> for a single-token output.
    /// Adds a 5-second timeout — ffprobe is normally sub-second but a
    /// network-mounted file can hang. Result is cached per process so
    /// repeat probes of the same file (e.g. multiple renders during one
    /// session) don't re-spawn the process.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double?> _durationCache = new();
    public async Task<double?> ProbeDurationSecAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        if (_durationCache.TryGetValue(filePath, out var cached)) return cached;
        var probe = TryResolveFFprobe();
        if (probe is null) { _durationCache[filePath] = null; return null; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var (stdout, _, exit) = await RunAsync(probe,
                new[] { "-v", "error", "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1", filePath },
                capture: true, ct: cts.Token);
            if (exit != 0) { _durationCache[filePath] = null; return null; }
            var trimmed = stdout.Trim();
            if (double.TryParse(trimmed,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                _durationCache[filePath] = sec;
                return sec;
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("ffprobe", $"duration probe failed for {Path.GetFileName(filePath)}: {ex.Message}");
        }
        _durationCache[filePath] = null;
        return null;
    }

    /// <summary>True if the file carries at least one audio stream. Cached
    /// per process. Returns false on any probe failure (treat as silent —
    /// the renderer substitutes silence for that clip's segment).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _hasAudioCache = new();
    public async Task<bool> ProbeHasAudioAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
        if (_hasAudioCache.TryGetValue(filePath, out var cached)) return cached;
        var probe = TryResolveFFprobe();
        if (probe is null) { _hasAudioCache[filePath] = false; return false; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var (stdout, _, exit) = await RunAsync(probe,
                new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index",
                        "-of", "csv=p=0", filePath },
                capture: true, ct: cts.Token);
            var has = exit == 0 && !string.IsNullOrWhiteSpace(stdout);
            _hasAudioCache[filePath] = has;
            return has;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ActivityLog.Warn("ffprobe", $"audio probe failed for {Path.GetFileName(filePath)}: {ex.Message}");
            _hasAudioCache[filePath] = false;
            return false;
        }
    }

    /// <summary>Returns ffmpeg's reported version string, or null.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var path = TryResolve();
        if (path is null) return null;
        try
        {
            var (stdout, _, code) = await RunAsync(path, new[] { "-version" }, capture: true, ct: ct);
            if (code != 0) return null;
            // First line is "ffmpeg version 6.1.1 …"
            var nl = stdout.IndexOf('\n');
            return nl > 0 ? stdout[..nl].Trim() : stdout.Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Runs ffmpeg with the given list of arguments. Each entry becomes a
    /// separate argv slot, so paths containing spaces / quotes / shell
    /// metacharacters are passed verbatim with no shell parsing — safer than
    /// concatenating into a single command-line string.
    /// </summary>
    public async Task<(string stdout, string stderr, int exit)> RunAsync(
        string ffmpegPath, IEnumerable<string> arguments, bool capture = true,
        Action<string>? onStderrLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        if (capture)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderr.AppendLine(e.Data);
                onStderrLine?.Invoke(e.Data);
            };
        }
        if (!proc.Start())
            throw new FfmpegMissingException("ffmpeg failed to start", ffmpegPath);
        if (capture)
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        await proc.WaitForExitAsync(ct);
        return (stdout.ToString(), stderr.ToString(), proc.ExitCode);
    }
}

public sealed class FfmpegMissingException : Exception
{
    public string AttemptedPath { get; }
    public FfmpegMissingException(string message, string attempted) : base(message)
        => AttemptedPath = attempted;
}
