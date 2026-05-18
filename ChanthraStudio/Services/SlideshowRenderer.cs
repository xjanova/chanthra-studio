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

        /// <summary>Quality tier — drives ffmpeg's <c>-crf</c> + <c>-preset</c>.
        /// "draft" prioritises speed (crf 24, ultrafast — bigger file but
        /// renders 3-5× faster on the same machine), "full" is the legacy
        /// balanced default (crf 20, fast), "hi" maxes quality for archival
        /// renders (crf 18, slow).</summary>
        public string Quality { get; init; } = "full";

        /// <summary>Optional single audio track (legacy single-track API).
        /// Empty / missing file → silent video. Ignored when
        /// <see cref="AudioTracks"/> has entries — the multi-track mixer
        /// takes precedence.</summary>
        public string? AudioPath { get; init; }

        /// <summary>0.0 – 1.0 multiplier for the legacy single-track. Mapped
        /// to ffmpeg's volume filter.</summary>
        public double AudioVolume { get; init; } = 1.0;

        /// <summary>Multi-track audio mixer (T34). Each track plays from its
        /// own StartSec, scaled by Volume, then mixed via amix. Empty =
        /// fall back to the legacy single-track AudioPath/AudioVolume above.</summary>
        public IReadOnlyList<AudioTrackDescriptor> AudioTracks { get; init; } = Array.Empty<AudioTrackDescriptor>();

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
        /// Every picture-in-picture overlay to layer onto the main track.
        /// Each entry becomes an extra ffmpeg input with <c>-loop 1 -t {dur}</c>,
        /// then chains an <c>overlay</c> filter gated by
        /// <c>enable='between(t,start,start+dur)'</c>. Empty = no overlay.
        /// </summary>
        public IReadOnlyList<OverlayDescriptor> OverlayTimeline { get; init; } = Array.Empty<OverlayDescriptor>();

        /// <summary>
        /// Text title cards layered via ffmpeg's <c>drawtext</c> filter. No
        /// extra inputs — each title becomes a filter chain stage. Gated by
        /// the same between(t,start,end) expression as image overlays.
        /// </summary>
        public IReadOnlyList<TitleDescriptor> Titles { get; init; } = Array.Empty<TitleDescriptor>();
    }

    /// <summary>One audio track on the multi-audio mixer. ffmpeg renders
    /// these via per-input <c>adelay</c> (to honour StartSec) + <c>volume</c>
    /// + a single <c>amix</c> sum. Missing files are silently skipped.</summary>
    public sealed class AudioTrackDescriptor
    {
        public string FilePath { get; init; } = "";
        public double StartSec { get; init; }
        public double Volume { get; init; } = 1.0;
    }

    /// <summary>One text title overlaid via ffmpeg's <c>drawtext</c> filter.
    /// drawtext needs a fontfile path on Windows builds — we default to
    /// Segoe UI Black which ships with Windows; users can drop a custom
    /// font into the Settings later.</summary>
    public sealed class TitleDescriptor
    {
        public string Text { get; init; } = "";
        public double StartSec { get; init; }
        public double DurationSec { get; init; } = 3.0;
        /// <summary>"TL" | "TR" | "BL" | "BR" | "C".</summary>
        public string Position { get; init; } = "C";
        /// <summary>Font size in output pixels. Defaults to 64 (large but
        /// readable on 1080p output).</summary>
        public int FontSize { get; init; } = 64;
        /// <summary>ffmpeg colour spec — name ("gold") or hex ("0xD4A76A").
        /// Empty falls back to gold.</summary>
        public string Color { get; init; } = "0xD4A76A";
        /// <summary>Absolute path to a font file. Empty = use the built-in
        /// fallback (Windows Segoe UI Black).</summary>
        public string? FontFile { get; init; }
    }

    /// <summary>One picture-in-picture overlay layered on the master video.
    /// File path (rather than the full Clip) keeps SlideshowRenderer free of
    /// any dependency on the NLE view-model classes.</summary>
    public sealed class OverlayDescriptor
    {
        public string FilePath { get; init; } = "";
        public double StartSec { get; init; }
        public double DurationSec { get; init; } = 4.0;
        /// <summary>0.1–0.6, fraction of main frame width.</summary>
        public double Scale { get; init; } = 0.3;
        /// <summary>"TL" | "TR" | "BL" | "BR" | "C".</summary>
        public string Position { get; init; } = "TR";
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

        // Total expected frames so we can convert "frame=NNN" into "% done"
        // and an ETA. Includes both the main timeline and any crossfade
        // overshoot from chained xfades; close enough for a status pill.
        var perClipOverride = spec.ClipDurations is { Count: > 0 } durs && durs.Count == spec.Clips.Count
            ? durs : null;
        double totalSec = 0;
        for (int i = 0; i < spec.Clips.Count; i++)
            totalSec += perClipOverride?[i] ?? spec.SecondsPerClip;
        if (spec.CrossfadeSec > 0 && spec.Clips.Count > 1)
            totalSec -= spec.CrossfadeSec * (spec.Clips.Count - 1);
        var totalFrames = Math.Max(1, (int)Math.Round(totalSec * spec.Fps));
        var startedAt = DateTime.UtcNow;
        progress?.Report($"Rendering · 0%");

        var (_, stderr, exit) = await ff.RunAsync(ffmpegPath, args,
            onStderrLine: line =>
            {
                // ffmpeg emits "frame=  47 fps=30 …" — extract the frame count
                // and turn it into a "Rendering · 41% · 12s left" pill. Falls
                // back to the raw line if the parse fails so the user always
                // sees SOME progress signal.
                if (!line.StartsWith("frame=", StringComparison.Ordinal))
                    return;
                var pretty = FormatProgress(line, totalFrames, startedAt);
                progress?.Report(pretty);
            },
            ct: ct);

        if (exit != 0 || !File.Exists(outputPath))
        {
            // Pull last 4 lines of stderr — that's where ffmpeg puts the actual error.
            var tail = TailLines(stderr, 4);
            return RenderResult.Failure($"ffmpeg failed (exit {exit}): {tail}");
        }

        // Persist as a clip — shotId is the first source clip's shot for now.
        // Honour per-slot durations + subtract crossfade overshoot so the
        // Library row's duration_ms matches the actual MP4 runtime (the
        // legacy "SecondsPerClip * Count" math was off by both knobs).
        var actualSec = totalSec;  // computed above for the frame estimator
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
                    dur = (int)(actualSec * 1000),
                    path = outputPath,
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
        }
        catch (Exception ex)
        {
            // DB write best-effort — file still exists on disk.
            ActivityLog.Warn("renderer", "clip row insert failed: " + ex.Message);
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
        // Multi-track audio takes precedence over the legacy single-track.
        // Filter both lists down to entries that still exist on disk.
        var audioTracks = spec.AudioTracks
            .Where(a => !string.IsNullOrEmpty(a.FilePath) && File.Exists(a.FilePath))
            .ToList();
        var hasMultiAudio = audioTracks.Count > 0;
        var hasLegacyAudio = !hasMultiAudio
            && !string.IsNullOrWhiteSpace(spec.AudioPath)
            && File.Exists(spec.AudioPath);
        var hasAudio = hasMultiAudio || hasLegacyAudio;
        // Filter the overlay descriptors down to those whose file still exists
        // on disk — a clip deleted from Library while sitting on the overlay
        // track shouldn't tank the whole render.
        var overlays = spec.OverlayTimeline.Where(o => !string.IsNullOrEmpty(o.FilePath) && File.Exists(o.FilePath)).ToList();
        var hasOverlay = overlays.Count > 0;
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
        // Overlay slots in the input list come BEFORE the audio so audio
        // (when present) is always the last input — keeps filter labels
        // consistent regardless of overlay presence.
        var firstOverlayIndex = spec.Clips.Count;
        foreach (var o in overlays)
        {
            args.Add("-loop");      args.Add("1");
            args.Add("-t");         args.Add(o.DurationSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            args.Add("-i");         args.Add(o.FilePath);
        }
        var audioIndex = spec.Clips.Count + overlays.Count;
        if (hasLegacyAudio)
        {
            args.Add("-i");
            args.Add(spec.AudioPath!);
        }
        else if (hasMultiAudio)
        {
            // One -i per track. Indices start at audioIndex, in track order.
            foreach (var t in audioTracks)
            {
                args.Add("-i");
                args.Add(t.FilePath);
            }
        }

        // filter_complex value is a single argv slot — internal commas and
        // semicolons are fine, ffmpeg parses them inside the filter language.
        // Five stages chained on a shared StringBuilder; helper methods keep
        // each stage's bookkeeping local so the section-by-section meaning
        // stays readable. Refactored from a 200-line inline build in 7.14.
        var titles = spec.Titles.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
        var hasTitles = titles.Count > 0;
        var filter = new StringBuilder();
        AppendScalePadStage(filter, spec);
        AppendConcatOrXfadeStage(filter, spec, perClipOverride);
        if (hasOverlay) AppendOverlayStage(filter, overlays, firstOverlayIndex, spec.Width, hasTitles);
        if (hasTitles) AppendTitleStage(filter, titles, hasOverlay, overlays.Count);
        if (hasLegacyAudio)
            AppendLegacyAudioStage(filter, audioIndex, spec.AudioVolume);
        else if (hasMultiAudio)
            AppendMultiAudioStage(filter, audioTracks, audioIndex);

        args.Add("-filter_complex");
        args.Add(filter.ToString());

        // When the overlay filter ran, the final video label is [outpip], not [out].
        args.Add("-map");           args.Add((hasOverlay || hasTitles) ? "[outpip]" : "[out]");
        if (hasAudio)
        {
            args.Add("-map");       args.Add("[a]");
            args.Add("-c:a");       args.Add("aac");
            args.Add("-b:a");       args.Add("192k");
            args.Add("-shortest");
        }
        args.Add("-r");             args.Add(spec.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("-c:v");           args.Add("libx264");
        // Quality tier → ffmpeg preset/crf. Draft trades file size + visual
        // fidelity for render-wall-time; hi does the opposite.
        var (preset, crf) = spec.Quality switch
        {
            "draft" => ("ultrafast", "24"),
            "hi"    => ("slow",      "18"),
            _       => ("fast",      "20"),
        };
        args.Add("-preset");        args.Add(preset);
        args.Add("-crf");           args.Add(crf);
        args.Add("-pix_fmt");       args.Add("yuv420p");
        args.Add(outputPath);
        return args;
    }

    // ============================================================
    // Filter-chain stages — extracted in 7.14 from the inline build.
    // Each method appends one logical stage to the shared filter
    // StringBuilder and returns nothing; the chain's video label
    // bookkeeping is fixed-name ([out] → [outpip]) so we don't need
    // to thread it through return values.
    //
    // Terminator contract: stage 1 (scale-pad) emits a TRAILING ';' per
    // clip. Stage 2's concat branch leaves NO trailing terminator; stage
    // 2's xfade branch trims its own trailing ';'. Stages 3-5 (overlay /
    // title / audio) each begin with a LEADING ';'. Order matters — if
    // you insert a new stage, follow the leading-';' convention so the
    // concat branch's lack-of-terminator stays compatible.
    // ============================================================

    /// <summary>Stage 1: scale + letterbox-pad each main-track clip into
    /// a uniform output frame. Emits <c>[i:v]scale=…pad=…[vN]</c> per clip.</summary>
    private static void AppendScalePadStage(StringBuilder filter, Spec spec)
    {
        for (int i = 0; i < spec.Clips.Count; i++)
        {
            filter.Append($"[{i}:v]scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=decrease,");
            filter.Append($"pad={spec.Width}:{spec.Height}:(ow-iw)/2:(oh-ih)/2:color=#060409,setsar=1[v{i}];");
        }
    }

    /// <summary>Stage 2: combine the [vN] clips into a single [out] video
    /// stream. Hard-cut concat unless <see cref="Spec.CrossfadeSec"/> &gt; 0
    /// and there are 2+ clips, in which case an xfade chain walks
    /// pairwise with a running offset.</summary>
    private static void AppendConcatOrXfadeStage(StringBuilder filter, Spec spec, IReadOnlyList<double>? perClipOverride)
    {
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
            // Strip the trailing semicolon — the next stage prepends its own.
            if (filter[^1] == ';') filter.Length -= 1;
        }
        else
        {
            for (int i = 0; i < spec.Clips.Count; i++) filter.Append($"[v{i}]");
            filter.Append($"concat=n={spec.Clips.Count}:v=1:a=0[out]");
        }
    }

    /// <summary>Stage 3: chain N picture-in-picture overlays. Each input is
    /// scaled and labelled <c>[ovN]</c>, then a chain of overlay filters
    /// feeds <c>[pipN]</c> labels into each other. Final stage labels its
    /// output [outpip] UNLESS a title stage will run after.</summary>
    private static void AppendOverlayStage(StringBuilder filter, List<OverlayDescriptor> overlays, int firstOverlayInputIndex, int frameWidth, bool titlesWillFollow)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Prep each overlay input as a scaled labelled stream.
        for (int oi = 0; oi < overlays.Count; oi++)
        {
            var o = overlays[oi];
            var inputIdx = firstOverlayInputIndex + oi;
            var w = (int)(frameWidth * Math.Clamp(o.Scale, 0.1, 0.6));
            filter.Append($";[{inputIdx}:v]scale={w}:-1,setsar=1,format=yuva420p[ov{oi}]");
        }
        // Chain overlay filters; each output feeds the next.
        string lastLabel = "out";
        for (int oi = 0; oi < overlays.Count; oi++)
        {
            var o = overlays[oi];
            var (xExpr, yExpr) = o.Position switch
            {
                "TL" => ("20",          "20"),
                "BL" => ("20",          "H-h-20"),
                "BR" => ("W-w-20",      "H-h-20"),
                "C"  => ("(W-w)/2",     "(H-h)/2"),
                _    => ("W-w-20",      "20"),    // TR / default
            };
            var startStr = o.StartSec.ToString("F2", inv);
            var endStr = (o.StartSec + o.DurationSec).ToString("F2", inv);
            // Last overlay labels itself [outpip] UNLESS titles will run
            // after — then it labels [pip{N-1}] and the title stage takes
            // over as the final stage.
            var lastStage = oi == overlays.Count - 1 && !titlesWillFollow;
            var outLabel = lastStage ? "outpip" : $"pip{oi}";
            filter.Append($";[{lastLabel}][ov{oi}]overlay={xExpr}:{yExpr}:enable='between(t,{startStr},{endStr})'[{outLabel}]");
            lastLabel = outLabel;
        }
    }

    /// <summary>Stage 4: drawtext title cards. Each title becomes a filter
    /// stage chained onto whatever the last video label was — so titles
    /// always sit ON TOP of image overlays. The last title's output is
    /// labelled [outpip].</summary>
    private static void AppendTitleStage(StringBuilder filter, List<TitleDescriptor> titles, bool hadOverlay, int overlayCount)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string lastLabel = hadOverlay && overlayCount > 0 ? $"pip{overlayCount - 1}" : "out";
        var defaultFont = ResolveDefaultFontFile();
        for (int ti = 0; ti < titles.Count; ti++)
        {
            var t = titles[ti];
            var fontFile = string.IsNullOrEmpty(t.FontFile) ? defaultFont : t.FontFile;
            var color = string.IsNullOrEmpty(t.Color) ? "0xD4A76A" : t.Color;
            var (xExpr, yExpr) = t.Position switch
            {
                "TL" => ("40",                  "40"),
                "BL" => ("40",                  "h-th-40"),
                "BR" => ("w-tw-40",             "h-th-40"),
                "C"  => ("(w-tw)/2",            "(h-th)/2"),
                _    => ("w-tw-40",             "40"),  // TR / default
            };
            var startStr = t.StartSec.ToString("F2", inv);
            var endStr = (t.StartSec + t.DurationSec).ToString("F2", inv);
            var safeText = EscapeDrawtext(t.Text);
            var fontArg = string.IsNullOrEmpty(fontFile) ? "" : $"fontfile='{EscapePathForFilter(fontFile)}':";
            var lastStage = ti == titles.Count - 1;
            var outLabel = lastStage ? "outpip" : $"tt{ti}";
            filter.Append($";[{lastLabel}]drawtext={fontArg}text='{safeText}':fontsize={t.FontSize}:fontcolor={color}:x={xExpr}:y={yExpr}:enable='between(t,{startStr},{endStr})'[{outLabel}]");
            lastLabel = outLabel;
        }
    }

    /// <summary>Stage 5a: legacy single-track audio — just volume scale
    /// the one input and label it [a].</summary>
    private static void AppendLegacyAudioStage(StringBuilder filter, int audioInputIndex, double volume)
    {
        var vol = Math.Clamp(volume, 0.0, 2.0);
        filter.Append($";[{audioInputIndex}:a]volume={vol.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[a]");
    }

    /// <summary>Stage 5b: multi-track audio mixer. Per-track adelay + volume,
    /// then a single amix that produces [a]. dropout_transition=0 prevents
    /// amix from auto-ducking when shorter inputs end (would otherwise
    /// sound like a level dip).</summary>
    private static void AppendMultiAudioStage(StringBuilder filter, List<AudioTrackDescriptor> audioTracks, int firstAudioInputIndex)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int ai = 0; ai < audioTracks.Count; ai++)
        {
            var t = audioTracks[ai];
            var input = firstAudioInputIndex + ai;
            var startMs = (int)Math.Round(Math.Max(0, t.StartSec) * 1000);
            var vol = Math.Clamp(t.Volume, 0.0, 2.0);
            if (startMs > 0)
                filter.Append($";[{input}:a]adelay={startMs}|{startMs},volume={vol.ToString("F2", inv)}[a{ai}]");
            else
                filter.Append($";[{input}:a]volume={vol.ToString("F2", inv)}[a{ai}]");
        }
        filter.Append(";");
        for (int ai = 0; ai < audioTracks.Count; ai++) filter.Append($"[a{ai}]");
        filter.Append($"amix=inputs={audioTracks.Count}:duration=longest:dropout_transition=0[a]");
    }

    /// <summary>Parse ffmpeg's per-frame progress line into a "Rendering ·
    /// 41% · ~12s left" pill. Returns the raw line if the parse can't find
    /// the frame number so the user always sees something.</summary>
    private static string FormatProgress(string ffmpegLine, int totalFrames, DateTime startedAt)
    {
        // Line looks like: "frame=  47 fps=30 q=28.0 size=N/A time=00:00:01.55 ..."
        try
        {
            var fIdx = ffmpegLine.IndexOf("frame=", StringComparison.Ordinal);
            if (fIdx < 0) return ffmpegLine;
            var rest = ffmpegLine[(fIdx + 6)..].TrimStart();
            var spaceIdx = rest.IndexOf(' ');
            if (spaceIdx < 0) return ffmpegLine;
            if (!int.TryParse(rest[..spaceIdx], out var frame)) return ffmpegLine;

            var pct = Math.Min(100, frame * 100.0 / totalFrames);
            var elapsed = DateTime.UtcNow - startedAt;
            if (frame > 0 && pct > 0.5)
            {
                var totalEst = elapsed.TotalSeconds * (totalFrames / (double)frame);
                var remain = Math.Max(0, totalEst - elapsed.TotalSeconds);
                return $"Rendering · {pct:F0}% · ~{remain:F0}s left";
            }
            return $"Rendering · {pct:F0}%";
        }
        catch
        {
            return ffmpegLine;
        }
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

    /// <summary>
    /// Escape a string for inclusion in an ffmpeg <c>drawtext=text='X'</c>
    /// argument. ffmpeg's filter grammar uses single quotes as the
    /// delimiter and backslash-escapes a small set of meta characters.
    /// Newlines become explicit <c>\n</c> so multi-line titles render
    /// correctly.
    /// </summary>
    private static string EscapeDrawtext(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length + 8);
        foreach (var ch in raw)
        {
            switch (ch)
            {
                // ffmpeg filter language: ' \ : , % \n need escaping inside text=''.
                case '\'': sb.Append(@"\'"); break;
                case '\\': sb.Append(@"\\"); break;
                case ':':  sb.Append(@"\:"); break;
                case ',':  sb.Append(@"\,"); break;
                case '%':  sb.Append(@"\%"); break;
                case '\n': sb.Append(@"\n"); break;
                default:   sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Escape a Windows path for use inside a filter argument —
    /// turns the drive colon into a doubled backslash form ffmpeg's
    /// fontfile= recognises.</summary>
    private static string EscapePathForFilter(string path)
        => path.Replace(@"\", "/").Replace(":", @"\:");

    private static string ResolveDefaultFontFile()
    {
        // Windows ships Segoe UI Black in System32\Fonts. Cormorant Garamond
        // (the brand display font) isn't a system font, so we don't try it
        // — users wanting Cormorant titles should drop the .ttf path in
        // TitleDescriptor.FontFile explicitly.
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(win, "Fonts", "seguibl.ttf"),  // Segoe UI Black
            Path.Combine(win, "Fonts", "segoeuib.ttf"), // Segoe UI Bold
            Path.Combine(win, "Fonts", "arialbd.ttf"),  // Arial Bold
            Path.Combine(win, "Fonts", "arial.ttf"),    // last resort
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return "";  // drawtext will error gracefully; ActivityLog catches it.
    }
}

public sealed record RenderResult(bool Ok, string? OutputPath, string? ClipId, string? Error)
{
    public static RenderResult Success(string path, string clipId) => new(true, path, clipId, null);
    public static RenderResult Failure(string error) => new(false, null, null, error);
}
