using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ChanthraStudio.Services.LocalComfy;

/// <summary>
/// Owns the ComfyUI python process.
///
/// <b>The thing this class exists to guarantee:</b> when Chanthra Studio is
/// gone, ComfyUI is gone. A plain <c>Process.Kill()</c> is not enough — python
/// spawns its own children, and if the studio crashes rather than exits it
/// never runs any cleanup at all. So the process is put in a Windows job object
/// created with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the kernel closes the
/// job when our process dies for any reason, and everything in it dies with it.
/// A stranded ComfyUI is not merely untidy — it holds the whole GPU's memory and
/// the port the next launch wants.
/// </summary>
public sealed class ComfyProcessHost : IDisposable
{
    private readonly object _lock = new();
    private Process? _process;
    private IntPtr _job = IntPtr.Zero;
    private bool _disposed;

    /// <summary>Recent stdout/stderr, for the engine card's log view. Bounded —
    /// a model load prints thousands of lines and nobody scrolls back that far,
    /// while an unbounded buffer would grow for as long as the app runs.</summary>
    private readonly ConcurrentQueue<string> _tail = new();
    private const int TailLimit = 400;

    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false }; }
    }

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Raised for every line the engine prints.</summary>
    public event Action<string>? Output;

    public string[] RecentOutput() => _tail.ToArray();

    /// <summary>
    /// Launch the engine.
    /// </summary>
    /// <param name="root">Engine root.</param>
    /// <param name="preferredPort">Port to try first; a free one is chosen if
    /// it is taken.</param>
    /// <param name="vramMode">"" · "lowvram" · "novram" · "highvram" · "cpu".</param>
    public void Start(string root, int preferredPort, string vramMode)
    {
        lock (_lock)
        {
            if (_process is { HasExited: false }) return;

            var python = ComfyPaths.PythonExe(root);
            var mainPy = ComfyPaths.MainPy(root);
            if (!File.Exists(python) || !File.Exists(mainPy))
                throw new InvalidOperationException("Engine is not installed — install it from the ComfyUI panel first.");

            Port = FindFreePort(preferredPort);

            var outputDir = Path.Combine(AppPaths.MediaFolder, "comfy-output");
            var inputDir = Path.Combine(AppPaths.MediaFolder, "comfy-input");
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(inputDir);

            var args = new StringBuilder();
            // -s keeps the embedded interpreter from picking up a system-wide
            // site-packages, which is the usual way a portable build starts
            // importing a stranger's half-installed torch.
            args.Append("-s ").Append(Quote(mainPy));
            args.Append(" --listen 127.0.0.1");
            args.Append(" --port ").Append(Port);
            // Without this ComfyUI opens a browser tab on every start.
            args.Append(" --disable-auto-launch");
            // The flag the portable's own launcher passes; it tells ComfyUI it
            // is running from the standalone layout.
            args.Append(" --windows-standalone-build");
            args.Append(" --output-directory ").Append(Quote(outputDir));
            args.Append(" --input-directory ").Append(Quote(inputDir));
            // Models are located by extra_model_paths.yaml rather than
            // --models-directory: the yaml also applies when the user launches
            // the engine's own .bat, so there is one answer to "where are my
            // models" instead of two that can disagree.
            switch (vramMode)
            {
                case "lowvram": args.Append(" --lowvram"); break;
                case "novram": args.Append(" --novram"); break;
                case "highvram": args.Append(" --highvram"); break;
                case "cpu": args.Append(" --cpu"); break;
            }

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = args.ToString(),
                WorkingDirectory = ComfyPaths.PortableDir(root),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Unbuffered, or the first sign of life arrives only when python
            // decides to flush — which on a cold start is after the model load.
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => Line(e.Data);
            proc.ErrorDataReceived += (_, e) => Line(e.Data);

            proc.Start();
            // Assign to the job BEFORE the first read: a process that manages to
            // spawn children in the window between start and assignment would
            // leave those children outside the job, and outside the guarantee.
            AssignToJob(proc);

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _process = proc;

            Line($"[chanthra] started {Path.GetFileName(python)} on port {Port}"
                 + (string.IsNullOrEmpty(vramMode) ? "" : $" ({vramMode})"));
        }
    }

    /// <summary>
    /// Stop the engine. Asks first, then insists.
    /// </summary>
    public void Stop(int graceMs = 4000)
    {
        Process? proc;
        lock (_lock) { proc = _process; _process = null; }
        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                // Kill the whole tree: torch spawns worker processes that keep
                // the GPU allocated even after the parent is gone.
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(graceMs);
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("comfy", "stop failed: " + ex.Message);
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private void Line(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _tail.Enqueue(text!);
        while (_tail.Count > TailLimit) _tail.TryDequeue(out _);
        Output?.Invoke(text!);
    }

    /// <summary>
    /// The port the engine will actually get.
    ///
    /// Binding to test a port and then handing it to another process is a race,
    /// but the alternative — launching on a busy port — fails after the whole
    /// cold start, by which point the user has waited a minute to be told no.
    /// </summary>
    private static int FindFreePort(int preferred)
    {
        if (IsFree(preferred)) return preferred;
        for (var p = preferred + 1; p < preferred + 40; p++)
            if (IsFree(p)) return p;
        throw new InvalidOperationException(
            $"No free port near {preferred} — something is occupying the whole range.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string Quote(string s) => '"' + s.Replace("\"", "\\\"") + '"';

    // ------------------------------------------------------------ job object

    private void AssignToJob(Process proc)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero) return;

                var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                };
                var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(extended, ptr, false);
                    SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            AssignProcessToJobObject(_job, proc.Handle);
        }
        catch (Exception ex)
        {
            // Losing the job object costs the crash-safety guarantee, not the
            // feature. Stop() still works for an orderly exit; say so rather
            // than fail the launch.
            ActivityLog.Warn("comfy",
                "could not create a job object — a crash of the studio may leave ComfyUI running: " + ex.Message);
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }
}
