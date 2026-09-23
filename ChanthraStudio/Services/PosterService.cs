using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Still frames for video clips.
///
/// WPF's Image control cannot decode an .mp4, so every video card — which, now
/// that ComfyUI renders save as H.264, is every video the studio makes — was an
/// empty box. A poster is one frame pulled out with ffmpeg, cached next to the
/// media and recorded on the clip row so it is made once.
/// </summary>
public sealed class PosterService
{
    private readonly StudioContext _ctx;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _one = new(1, 1);

    public PosterService(StudioContext ctx) => _ctx = ctx;

    public static string PosterFolder
    {
        get
        {
            var p = Path.Combine(AppPaths.MediaFolder, "posters");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    /// <summary>
    /// Make posters for the video clips that lack one, in the background, one
    /// ffmpeg at a time. Each clip's <see cref="Clip.PosterPath"/> is set on
    /// the UI thread as its frame lands, so cards fill in as they go.
    /// </summary>
    public void EnsurePosters(IEnumerable<Clip> clips)
    {
        var todo = clips
            .Where(c => MediaKind.RendersAsVideo(c.FilePath)
                        && (string.IsNullOrEmpty(c.PosterPath) || !File.Exists(c.PosterPath))
                        && File.Exists(c.FilePath))
            .Where(c => _inFlight.TryAdd(c.Id, 0))
            .ToList();
        if (todo.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var clip in todo)
            {
                try { await MakeAsync(clip); }
                catch (Exception ex) { ActivityLog.Warn("poster", $"{Path.GetFileName(clip.FilePath)}: {ex.Message}"); }
                finally { _inFlight.TryRemove(clip.Id, out _); }
            }
        });
    }

    private async Task MakeAsync(Clip clip)
    {
        var ff = new FFmpegService(_ctx);
        var ffmpeg = ff.TryResolve();
        if (ffmpeg is null) return;   // no ffmpeg: cards keep their placeholder

        await _one.WaitAsync();
        try
        {
            var dest = Path.Combine(PosterFolder, SafeName(string.IsNullOrEmpty(clip.Id) ? Path.GetFileNameWithoutExtension(clip.FilePath) : clip.Id) + ".jpg");
            // Half a second in skips the black first frame many encoders
            // write; a clip shorter than that falls back to its first frame.
            foreach (var seek in new[] { "0.5", "0" })
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var (_, _, exit) = await ff.RunAsync(ffmpeg,
                    new[] { "-y", "-v", "error", "-ss", seek, "-i", clip.FilePath, "-frames:v", "1", "-vf", "scale=480:-2", dest },
                    ct: cts.Token);
                if (exit == 0 && File.Exists(dest) && new FileInfo(dest).Length > 0) break;
            }
            if (!File.Exists(dest) || new FileInfo(dest).Length == 0) return;

            if (!string.IsNullOrEmpty(clip.Id))
            {
                try { _ctx.Clips.SetPoster(clip.Id, dest); } catch { /* the file still helps this session */ }
            }
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui is null || ui.CheckAccess()) clip.PosterPath = dest;
            else await ui.InvokeAsync(() => clip.PosterPath = dest);
        }
        finally
        {
            _one.Release();
        }
    }

    private static string SafeName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(raw.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray());
    }
}
