using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// Stitches a list of image clips into a single MP4 via ffmpeg. Uses
/// <c>filter_complex</c> with per-input scale + setsar + concat so mixed
/// aspect ratios collapse into a uniform output frame (letterboxed).
///
/// The resulting file is inserted into the <c>clips</c> table so it shows
/// up in Library next to the source images.
/// </summary>
public sealed class SlideshowRenderer
{
    private readonly StudioContext _ctx;

    public SlideshowRenderer(StudioContext ctx) { _ctx = ctx; }

    public sealed class Spec
    {
        public IReadOnlyList<Clip> Clips { get; init; } = Array.Empty<Clip>();
        public double SecondsPerClip { get; init; } = 3.0;
        public int Fps { get; init; } = 30;
        public int Width { get; init; } = 1920;
        public int Height { get; init; } = 1080;
        public string OutputName { get; init; } = "";

        /// <summary>Optional audio track. Empty / missing file → silent video.</summary>
        public string? AudioPath { get; init; }

        /// <summary>0.0 – 1.0 multiplier. Mapped to ffmpeg's volume filter.</summary>
        public double AudioVolume { get; init; } = 1.0;

        /// <summary>
        /// Per-clip duration overrides. When supplied (and the same length as
        /// <see cref="Clips"/>), each input gets its own <c>-t</c> seconds so
        /// the NLE timeline can show different durations per slot. Falls back
        /// to the global <see cref="SecondsPerClip"/> when null/empty/mismatched.
        /// </summary>
        public IReadOnlyList<double>? ClipDurations { get; init; }

        /// <summary>
        /// Crossfade duration in seconds between adjacent clips. Zero (the
        /// default) is the legacy hard-cut concat. When &gt; 0, ffmpeg
        /// xfade chains each pair so the timeline reads as one continuous
        /// dissolve — typical values are 0.3–0.8s for cinematic, 1.5–2.0s
        /// for dreamy fortune-content vibes. Must stay shorter than the
        /// SMALLEST clip duration or xfade returns "offset is negative".
        /// </summary>
        public double CrossfadeSec { get; init; } = 0;

        /// <summary>
        /// Optional picture-in-picture overlay clip layered on top of the
        /// main track. Null = no overlay. When set, ffmpeg adds it as an
        /// extra input with <c>-loop 1 -t {duration}</c> and an
        /// <c>overlay</c> filter at the chosen corner gated by an
        /// <c>enable='between(t,start,start+dur)'</c> expression.
        /// </summary>
        public Clip? OverlayClip { get; init; }
        public double OverlayStartSec { get; init; }
        public double OverlayDurationSec { get; init; } = 4.0;
        /// <summary>0.1–0.6, fraction of main frame width.</summary>
        public double OverlayScale { get; init; } = 0.3;
        /// <summary>"TL" | "TR" | "BL" | "BR" | "C".</summary>
        public string OverlayPosition { get; init; } = "TR";
    }

    public async Task<RenderResult> RenderAsync(Spec spec, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (spec.Clips.Count == 0)
            return RenderResult.Failure("No clips selected.");

        var ff = new FFmpegService(_ctx);
        var ffmpegPath = ff.TryResolve();
        if (ffmpegPath is null)
        {
            return RenderResult.Failure(
                "ffmpeg.exe not found. Install via `winget install Gyan.FFmpeg` or " +
                "set the path manually in Settings → ffmpeg.");
        }

        var outDir = AppPaths.MediaFolder;
        var safeName = SafeFilename(string.IsNullOrWhiteSpace(spec.OutputName)
            ? $"film_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
            : spec.OutputName);
        var outputPath = Path.Combine(outDir, safeName + ".mp4");

        var args = BuildArgList(spec, outputPath);
        progress?.Report("Rendering film…");

        var (_, stderr, exit) = await ff.RunAsync(ffmpegPath, args,
            onStderrLine: line =>
            {
                // ffmpeg emits "frame=  47 fps=30 …" — surface only the frame count
                // through the progress callback to avoid spamming the UI thread.
                if (line.StartsWith("frame=", StringComparison.Ordinal))
                    progress?.Report(line);
            },
            ct: ct);

        if (exit != 0 || !File.Exists(outputPath))
        {
            // Pull last 4 lines of stderr — that's where ffmpeg puts the actual error.
            var tail = TailLines(stderr, 4);
            return RenderResult.Failure($"ffmpeg failed (exit {exit}): {tail}");
        }

        // Persist as a clip — shotId is the first source clip's shot for now.
        var firstShotId = spec.Clips.First().ShotId;
        var clipId = Guid.NewGuid().ToString("N");
        try
        {
            using var c = _ctx.Db.Open();
            c.Execute("""
                INSERT INTO clips (id, shot_id, duration_ms, file_path, created_at)
                VALUES ($id, $shotId, $dur, $path, $now)
                """,
                new
                {
                    id = clipId,
                    shotId = firstShotId,
                    dur = (int)(spec.SecondsPerClip * 1000 * spec.Clips.Count),
                    path = outputPath,
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
        }
        catch
        {
            // DB write best-effort — file still exists on disk.
        }

        return RenderResult.Success(outputPath, clipId);
    }

    /// <summary>
    /// Builds the ffmpeg argument list as separate tokens so
    /// <see cref="ProcessStartInfo.ArgumentList"/> can pass each one verbatim.
    /// Filenames with spaces / quotes / shell metacharacters are safe — there
    /// is no shell parsing in this path.
    /// </summary>
    private static List<string> BuildArgList(Spec spec, string outputPath)
    {
        var hasAudio = !string.IsNullOrWhiteSpace(spec.AudioPath) && File.Exists(spec.AudioPath);
        var hasOverlay = spec.OverlayClip is not null && File.Exists(spec.OverlayClip.FilePath);
        var args = new List<string> { "-y" };

        var perClipOverride = spec.ClipDurations is { Count: > 0 } durs && durs.Count == spec.Clips.Count
            ? durs
            : null;
        for (int i = 0; i < spec.Clips.Count; i++)
        {
            var dur = perClipOverride?[i] ?? spec.SecondsPerClip;
            args.Add("-loop");      args.Add("1");
            args.Add("-t");         args.Add(dur.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            args.Add("-i");         args.Add(spec.Clips[i].FilePath);
        }
        // Overlay slot in the input list comes BEFORE the audio so audio
        // (when present) is always the last input — keeps filter labels
        // consistent regardless of overlay presence.
        var overlayIndex = -1;
        if (hasOverlay)
        {
            overlayIndex = spec.Clips.Count;
            args.Add("-loop");      args.Add("1");
            args.Add("-t");         args.Add(spec.OverlayDurationSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            args.Add("-i");         args.Add(spec.OverlayClip!.FilePath);
        }
        var audioIndex = spec.Clips.Count + (hasOverlay ? 1 : 0);
        if (hasAudio)
        {
            args.Add("-i");
            args.Add(spec.AudioPath!);
        }

        // filter_complex value is a single argv slot — internal commas and
        // semicolons are fine, ffmpeg parses them inside the filter language.
        var filter = new StringBuilder();
        for (int i = 0; i < spec.Clips.Count; i++)
        {
            filter.Append($"[{i}:v]scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=decrease,");
            filter.Append($"pad={spec.Width}:{spec.Height}:(ow-iw)/2:(oh-ih)/2:color=#060409,setsar=1[v{i}];");
        }

        // Two render paths: hard-cut concat (default) vs. xfade chain when
        // CrossfadeSec > 0. xfade composes pairwise so we walk left-to-right,
        // computing the running offset = combined-stream length so far - xfade.
        if (spec.CrossfadeSec > 0 && spec.Clips.Count >= 2)
        {
            double fade = spec.CrossfadeSec;
            // Clamp fade to slightly less than the smallest clip duration so
            // ffmpeg doesn't reject the filter with "offset must be non-negative".
            double minDur = double.MaxValue;
            for (int i = 0; i < spec.Clips.Count; i++)
            {
                var d = perClipOverride?[i] ?? spec.SecondsPerClip;
                if (d < minDur) minDur = d;
            }
            if (fade >= minDur) fade = Math.Max(0.2, minDur - 0.1);

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string lastLabel = "v0";
            double runLen = perClipOverride?[0] ?? spec.SecondsPerClip;
            for (int i = 1; i < spec.Clips.Count; i++)
            {
                var nextDur = perClipOverride?[i] ?? spec.SecondsPerClip;
                var offset = runLen - fade;
                var outLabel = i == spec.Clips.Count - 1 ? "out" : $"x{i}";
                filter.Append(
                    $"[{lastLabel}][v{i}]xfade=transition=fade:" +
                    $"duration={fade.ToString("F3", inv)}:" +
                    $"offset={offset.ToString("F3", inv)}[{outLabel}];");
                lastLabel = outLabel;
                runLen = runLen + nextDur - fade;
            }
            // Strip trailing semicolon and audio mix below appends with its own ;
            if (filter[^1] == ';') filter.Length -= 1;
        }
        else
        {
            for (int i = 0; i < spec.Clips.Count; i++) filter.Append($"[v{i}]");
            filter.Append($"concat=n={spec.Clips.Count}:v=1:a=0[out]");
        }

        // Picture-in-picture overlay (T13). Builds on the [out] label
        // produced by the concat / xfade chain above; remaps to a NEW [out]
        // label so the downstream -map still resolves.
        if (hasOverlay)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var overlayW = (int)(spec.Width * Math.Clamp(spec.OverlayScale, 0.1, 0.6));
            // overlay height comes from scale's source aspect — let ffmpeg keep it via -1
            var (xExpr, yExpr) = spec.OverlayPosition switch
            {
                "TL" => ("20",          "20"),
                "BL" => ("20",          "H-h-20"),
                "BR" => ("W-w-20",      "H-h-20"),
                "C"  => ("(W-w)/2",     "(H-h)/2"),
                _    => ("W-w-20",      "20"),    // TR / default
            };
            var startStr = spec.OverlayStartSec.ToString("F2", inv);
            var endStr = (spec.OverlayStartSec + spec.OverlayDurationSec).ToString("F2", inv);

            filter.Append($";[{overlayIndex}:v]scale={overlayW}:-1,setsar=1,format=yuva420p[ov];");
            filter.Append($"[out][ov]overlay={xExpr}:{yExpr}:enable='between(t,{startStr},{endStr})'[outpip]");
        }
        if (hasAudio)
        {
            var vol = Math.Clamp(spec.AudioVolume, 0.0, 2.0);
            filter.Append($";[{audioIndex}:a]volume={vol.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[a]");
        }

        args.Add("-filter_complex");
        args.Add(filter.ToString());

        // When the overlay filter ran, the final video label is [outpip], not [out].
        args.Add("-map");           args.Add(hasOverlay ? "[outpip]" : "[out]");
        if (hasAudio)
        {
            args.Add("-map");       args.Add("[a]");
            args.Add("-c:a");       args.Add("aac");
            args.Add("-b:a");       args.Add("192k");
            args.Add("-shortest");
        }
        args.Add("-r");             args.Add(spec.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("-c:v");           args.Add("libx264");
        args.Add("-preset");        args.Add("fast");
        args.Add("-crf");           args.Add("20");
        args.Add("-pix_fmt");       args.Add("yuv420p");
        args.Add(outputPath);
        return args;
    }

    private static string TailLines(string text, int n)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var skip = Math.Max(0, lines.Length - n);
        return string.Join(" · ", lines.Skip(skip).Select(l => l.Trim()));
    }

    private static string SafeFilename(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }
}

public sealed record RenderResult(bool Ok, string? OutputPath, string? ClipId, string? Error)
{
    public static RenderResult Success(string path, string clipId) => new(true, path, clipId, null);
    public static RenderResult Failure(string error) => new(false, null, null, error);
}
