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

        // H.264 in yuv420p needs even dimensions; 21:9 at 3840 wide worked
        // out to 1645 high and libx264 refused the whole render.
        private readonly int _width = 1920, _height = 1080;
        public int Width { get => _width; init => _width = Math.Max(2, value & ~1); }
        public int Height { get => _height; init => _height = Math.Max(2, value & ~1); }

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

        /// <summary>Per-clip in-point (seconds) into the source — the renderer
        /// seeks here before taking the slot duration. Parallel to
        /// <see cref="Clips"/>; null/short → no trim (start at 0). Video only.</summary>
        public IReadOnlyList<double>? ClipTrimStarts { get; init; }

        /// <summary>
        /// Per-clip Ken Burns + color grading. Same-length list parallel to
        /// <see cref="Clips"/> (and <see cref="ClipDurations"/>). Slots with
        /// identity values (no zoom, no grade) skip the extra filter
        /// emission. (T47 + T48 / 7.18)
        /// </summary>
        public IReadOnlyList<SlotDescriptor>? SlotMeta { get; init; }

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

    /// <summary>Per-clip Ken Burns + color grading. Defaults are identity
    /// (no zoom · no grade) so an unset descriptor produces a no-op
    /// filter chain. (T47 + T48 / 7.18 · T50 / 7.19 added pan)</summary>
    public sealed class SlotDescriptor
    {
        /// <summary>Zoom percent at slot start (100 = no zoom).</summary>
        public double ZoomStartPct { get; init; } = 100;
        /// <summary>Zoom percent at slot end (100 = no zoom).</summary>
        public double ZoomEndPct { get; init; } = 100;
        /// <summary>Pan target X at slot start (0..1, 0.5 = centre).</summary>
        public double PanStartX { get; init; } = 0.5;
        public double PanStartY { get; init; } = 0.5;
        public double PanEndX { get; init; } = 0.5;
        public double PanEndY { get; init; } = 0.5;
        /// <summary>ffmpeg eq.brightness — -0.5..+0.5, 0 = identity.</summary>
        public double Brightness { get; init; }
        /// <summary>ffmpeg eq.contrast — 0.5..2.0, 1.0 = identity.</summary>
        public double Contrast { get; init; } = 1.0;
        /// <summary>ffmpeg eq.saturation — 0..3, 1.0 = identity.</summary>
        public double Saturation { get; init; } = 1.0;

        // Pan WITHOUT a zoom > 100% is a visual no-op because zoompan's
        // visible window equals the source frame, so pan can't shift
        // anything. Gate on actual zoom motion only — UI tells the user
        // "pan needs zoom > 100%". (7.20 fix — review LOGIC #4)
        public bool HasZoom =>
            Math.Abs(ZoomStartPct - ZoomEndPct) > 0.5 || ZoomStartPct > 100.5;
        public bool HasGrade =>
            Math.Abs(Brightness) > 0.001 ||
            Math.Abs(Contrast - 1.0) > 0.001 ||
            Math.Abs(Saturation - 1.0) > 0.001;
    }

    /// <summary>One audio track on the multi-audio mixer. ffmpeg renders
    /// these via per-input <c>adelay</c> (to honour StartSec) + <c>volume</c>
    /// + a single <c>amix</c> sum. Missing files are silently skipped.</summary>
    public sealed class AudioTrackDescriptor
    {
        public string FilePath { get; init; } = "";
        public double StartSec { get; init; }
        public double Volume { get; init; } = 1.0;
        /// <summary>Linear fade-in length at the start of this track (sec).
        /// 0 = hard onset. ffmpeg renders as <c>afade=t=in:st=0:d=N</c>.</summary>
        public double FadeInSec { get; init; }
        /// <summary>Linear fade-out length at the END of this track (sec).
        /// The fade-out start time is anchored to <see cref="NaturalDurationSec"/>
        /// when supplied — set by the renderer via ffprobe before filter-
        /// build (T51 / 7.20). Falls back to a 60s anchor when probe fails.</summary>
        public double FadeOutSec { get; init; }
        /// <summary>Probed natural duration of the source audio in seconds.
        /// Null = couldn't probe; the renderer falls back to a 60s
        /// fade-out anchor.</summary>
        public double? NaturalDurationSec { get; set; }
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

        // ffmpeg's WebP decoder cannot read animation: it skips the frames,
        // finds no image data and — looped as a still — never finishes.
        var animated = spec.Clips.Select(c => c.FilePath)
            .Concat(spec.OverlayTimeline.Select(o => o.FilePath))
            .FirstOrDefault(MediaKind.IsAnimatedWebp);
        if (animated is not null)
            return RenderResult.Failure(
                $"ไฟล์ {Path.GetFileName(animated)} เป็น .webp แบบเคลื่อนไหว ซึ่ง ffmpeg อ่านไม่ได้ — "
                + "เอาออกจากไทม์ไลน์ หรือเรนเดอร์ใหม่ด้วยเวิร์กโฟลว์ที่บันทึกเป็น .mp4");

        var outDir = AppPaths.MediaFolder;
        var inv0 = System.Globalization.CultureInfo.InvariantCulture;
        var safeName = SafeFilename(string.IsNullOrWhiteSpace(spec.OutputName)
            ? $"film_{DateTime.Now.ToString("yyyyMMdd_HHmmss", inv0)}"
            : spec.OutputName);
        // Never write over an earlier film: it may already be a Library card,
        // and a second render under the same name replaced its file — or,
        // when the second render failed, left a broken file in its place.
        var outputPath = Path.Combine(outDir, safeName + ".mp4");
        for (var n = 2; File.Exists(outputPath); n++)
            outputPath = Path.Combine(outDir, $"{safeName} ({n}).mp4");
        // ffmpeg writes here; the file moves into place only on success.
        var workingPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(outputPath) + ".rendering.mp4");
        string? titleDir = null;

        // Probe natural duration of every audio track that has a fade-out
        // configured — feeds AppendMultiAudioStage's afade=out anchor.
        // (T51 / 7.20). Reuses the FFmpegService already constructed above.
        // OperationCanceledException is re-thrown so a user cancel during
        // probe doesn't silently complete the render (7.20 fix · review SMELL #10).
        foreach (var t in spec.AudioTracks)
        {
            if (t.FadeOutSec <= 0.01) continue;
            if (t.NaturalDurationSec is not null) continue;
            try { t.NaturalDurationSec = await ff.ProbeDurationSecAsync(t.FilePath, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ActivityLog.Warn("renderer", $"probe failed for {System.IO.Path.GetFileName(t.FilePath)}: {ex.Message}");
            }
        }

        // Probe each VIDEO clip — does it carry audio (keep its sound), and
        // what's its true length (a silent segment must match the clip exactly
        // or the concat desyncs). Images need neither. ffprobe is cached.
        var trims = spec.ClipTrimStarts is { Count: > 0 } ts && ts.Count == spec.Clips.Count
            ? ts.ToArray() : new double[spec.Clips.Count];
        var slotDurs = spec.ClipDurations is { Count: > 0 } sd && sd.Count == spec.Clips.Count ? sd : null;
        var clipHasAudio = new bool[spec.Clips.Count];
        // effDur is the SLOT: what the timeline shows and what titles, overlays
        // and music are timed against. srcDur is how much of a video clip is
        // actually read; when the clip is shorter than its slot its last
        // frame is held instead of the whole film quietly getting shorter.
        var effDur = new double[spec.Clips.Count];
        var srcDur = new double[spec.Clips.Count];
        for (int i = 0; i < spec.Clips.Count; i++)
        {
            var slotDur = Math.Max(0.1, slotDurs?[i] ?? spec.SecondsPerClip);
            effDur[i] = slotDur;
            srcDur[i] = slotDur;
            if (!MediaKind.RendersAsVideo(spec.Clips[i].FilePath)) continue;

            clipHasAudio[i] = await ff.ProbeHasAudioAsync(spec.Clips[i].FilePath, ct);
            if (await ff.ProbeDurationSecAsync(spec.Clips[i].FilePath, ct) is not double clipLen) continue;

            // An in-point past the end of the clip read nothing, and the slot
            // vanished from the film without a word.
            if (trims[i] > clipLen - 0.1)
            {
                ActivityLog.Warn("renderer", $"{Path.GetFileName(spec.Clips[i].FilePath)}: trim {trims[i]:0.##}s is past the clip's {clipLen:0.##}s — starting from 0");
                trims[i] = 0;
            }
            srcDur[i] = Math.Max(0.1, Math.Min(slotDur, clipLen - trims[i]));
        }

        double totalSec = 0;
        for (int i = 0; i < spec.Clips.Count; i++) totalSec += effDur[i];
        if (spec.CrossfadeSec > 0 && spec.Clips.Count > 1)
            totalSec -= EffectiveFade(spec, effDur) * (spec.Clips.Count - 1);

        List<string> args;
        try
        {
            // Title text files are quoted into the filtergraph; a path holding
            // an apostrophe cannot be, so such a temp folder is avoided.
            var titleName = "chanthra-titles-" + Guid.NewGuid().ToString("N")[..8];
            titleDir = Path.Combine(Path.GetTempPath(), titleName);
            if (titleDir.Contains('\''))
                titleDir = Path.Combine(Path.GetPathRoot(Path.GetFullPath(titleDir)) ?? @"C:\", "ChanthraTemp", titleName);
            args = BuildArgList(spec, workingPath, effDur, srcDur, trims, clipHasAudio, totalSec, titleDir);
        }
        catch (Exception ex)
        {
            TryDeleteDir(titleDir);
            return RenderResult.Failure("เตรียมคำสั่ง render ไม่สำเร็จ: " + ex.Message);
        }

        // Total expected frames → "% done" + ETA for the progress pill.
        var totalFrames = Math.Max(1, (int)Math.Round(totalSec * spec.Fps));
        var startedAt = DateTime.UtcNow;
        progress?.Report($"Rendering · 0%");

        string stderr;
        int exit;
        try
        {
            (_, stderr, exit) = await ff.RunAsync(ffmpegPath, args,
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
        }
        catch
        {
            // Cancelled: ffmpeg has been killed; leave nothing half-written.
            TryDeleteFile(workingPath);
            throw;
        }
        finally
        {
            TryDeleteDir(titleDir);
        }

        if (exit != 0 || !File.Exists(workingPath))
        {
            TryDeleteFile(workingPath);
            ActivityLog.Warn("renderer", $"ffmpeg exit {exit}: {TailLines(stderr, 12)}");
            return RenderResult.Failure(ExplainFailure(stderr, exit));
        }

        try
        {
            File.Move(workingPath, outputPath);
        }
        catch (Exception ex)
        {
            return RenderResult.Failure($"render เสร็จแต่ย้ายไฟล์ไปที่ {outputPath} ไม่ได้: {ex.Message} (ไฟล์อยู่ที่ {workingPath})");
        }

        // "Succeeded" used to mean ffmpeg exited 0 — which it also does when
        // an audio stream ends the film early. Measure what was written.
        if (await ff.ProbeDurationSecAsync(outputPath, ct) is double written
            && Math.Abs(written - totalSec) > Math.Max(1.0, totalSec * 0.1))
            ActivityLog.Warn("renderer", $"{Path.GetFileName(outputPath)} is {written:0.0}s, expected {totalSec:0.0}s");
        progress?.Report("Rendering · 100%");

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
            // Imported clips carry an empty/foreign ShotId — the clips.shot_id FK
            // would reject the render-output row (so the film renders to disk but
            // never shows in Library). Fall back to a sentinel shot under the
            // migration-seeded 'default' sequence so it lands in Library.
            var shotId = firstShotId;
            if (string.IsNullOrEmpty(shotId)
                || c.ExecuteScalar<long>("SELECT COUNT(1) FROM shots WHERE id = $id", new { id = shotId }) == 0)
            {
                shotId = "editor-render";
                c.Execute("""
                    INSERT OR IGNORE INTO shots (id, sequence_id, number, title, created_at, updated_at)
                    VALUES ($id, 'default', '—', 'Editor render', $now, $now)
                    """, new { id = shotId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            }
            c.Execute("""
                INSERT INTO clips (id, shot_id, duration_ms, file_path, created_at)
                VALUES ($id, $shotId, $dur, $path, $now)
                """,
                new
                {
                    id = clipId,
                    shotId,
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
    private static List<string> BuildArgList(Spec spec, string outputPath, double[] effDur, double[] srcDur,
        double[] trims, bool[] clipHasAudio, double totalSec, string titleDir)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
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
        var perSlotMeta = spec.SlotMeta is { Count: > 0 } metas && metas.Count == spec.Clips.Count ? metas : null;

        for (int i = 0; i < spec.Clips.Count; i++)
        {
            // VIDEO clips play their OWN frames — NEVER -loop (looping froze the
            // first frame, the old "slideshow pretending to be a video editor"
            // bug). -ss seeks to the trim in-point; -t takes what we read.
            if (MediaKind.RendersAsVideo(spec.Clips[i].FilePath))
            {
                if (trims[i] > 0.01) { args.Add("-ss"); args.Add(trims[i].ToString("F2", inv)); }
                args.Add("-t");     args.Add(srcDur[i].ToString("F2", inv));
                args.Add("-i");     args.Add(spec.Clips[i].FilePath);
            }
            else if (perSlotMeta?[i] is { HasZoom: true })
            {
                // Ken Burns reads ONE frame. zoompan emits d frames for every
                // frame it is given, so a looped still (25 frames a second)
                // came out 25×d frames long — a 2 s slot rendered as 100 s.
                args.Add("-i");     args.Add(spec.Clips[i].FilePath);
            }
            else
            {
                args.Add("-loop");  args.Add("1");
                args.Add("-t");     args.Add(effDur[i].ToString("F2", inv));
                args.Add("-i");     args.Add(spec.Clips[i].FilePath);
            }
        }
        // Overlay slots in the input list come BEFORE the audio so audio
        // (when present) is always the last input — keeps filter labels
        // consistent regardless of overlay presence.
        var firstOverlayIndex = spec.Clips.Count;
        foreach (var o in overlays)
        {
            // -loop is an image-demuxer option; on an .mp4 it failed the whole
            // render with "Option loop not found".
            if (!MediaKind.RendersAsVideo(o.FilePath)) { args.Add("-loop"); args.Add("1"); }
            args.Add("-t");         args.Add(o.DurationSec.ToString("F2", inv));
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
        // Carry each VIDEO clip's OWN audio into the render (the gap that made
        // this feel like a silent slideshow). Works on BOTH the hard-cut concat
        // AND the crossfade path — the clip audio is combined the SAME way the
        // video is (plain concat vs acrossfade) so it stays perfectly in sync.
        var anyClipAudio = clipHasAudio.Any(x => x);
        var useClipAudio = anyClipAudio;
        var finalHasAudio = useClipAudio || hasAudio;

        var filter = new StringBuilder();
        AppendScalePadStage(filter, spec, effDur, srcDur);
        // Video → [out] (hard-cut concat OR xfade dissolve).
        AppendConcatOrXfadeStage(filter, spec, effDur);
        if (useClipAudio)
        {
            // Build each clip's audio, then combine to MATCH the video → [acat].
            AppendClipAudioStage(filter, spec, effDur, clipHasAudio);
            AppendClipAudioCombineStage(filter, spec, effDur);
        }
        if (hasOverlay) AppendOverlayStage(filter, overlays, firstOverlayIndex, spec.Width, hasTitles);
        if (hasTitles) AppendTitleStage(filter, titles, hasOverlay, overlays.Count, titleDir);

        // Audio mix → [mix], then held to exactly the film's length → [a].
        // The old "-shortest" let whichever stream ended first end the film:
        // a 3 s jingle under a 9 s slideshow produced a 3 s film.
        //
        // amix is told not to normalise: by default it divides every input
        // by the input count, so adding music quietly halved the clips' own
        // sound. A limiter catches the peaks the plain sum can produce.
        const string mixTail = ":dropout_transition=0:normalize=0,alimiter=limit=0.95[mix]";
        if (useClipAudio)
        {
            // Mix the clips' own audio with any external music / voice tracks.
            if (hasMultiAudio) { AppendMultiAudioStage(filter, audioTracks, audioIndex, "ext"); filter.Append($";[acat][ext]amix=inputs=2:duration=first{mixTail}"); }
            else if (hasLegacyAudio) { AppendLegacyAudioStage(filter, audioIndex, spec.AudioVolume, "ext"); filter.Append($";[acat][ext]amix=inputs=2:duration=first{mixTail}"); }
            else filter.Append(";[acat]anull[mix]");
        }
        else if (hasLegacyAudio) AppendLegacyAudioStage(filter, audioIndex, spec.AudioVolume, "mix");
        else if (hasMultiAudio) AppendMultiAudioStage(filter, audioTracks, audioIndex, "mix");
        if (finalHasAudio)
            filter.Append($";[mix]apad,atrim=0:{totalSec.ToString("F3", inv)},asetpts=N/SR/TB[a]");

        args.Add("-filter_complex");
        args.Add(filter.ToString());

        // When the overlay filter ran, the final video label is [outpip], not [out].
        args.Add("-map");           args.Add((hasOverlay || hasTitles) ? "[outpip]" : "[out]");
        if (finalHasAudio)
        {
            args.Add("-map");       args.Add("[a]");
            args.Add("-c:a");       args.Add("aac");
            args.Add("-b:a");       args.Add("192k");
        }
        // The film is as long as the timeline, whatever any one stream says.
        args.Add("-t");             args.Add(totalSec.ToString("F3", inv));
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
    /// a uniform output frame. Emits <c>[i:v]scale=…pad=…[vN]</c> per clip
    /// with optional eq (color grade) and zoompan (Ken Burns) stages
    /// appended when SlotMeta supplies non-identity values. (7.18)</summary>
    private static void AppendScalePadStage(StringBuilder filter, Spec spec, double[] effDur, double[] srcDur)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var perSlotMeta = spec.SlotMeta is { Count: > 0 } metas && metas.Count == spec.Clips.Count
            ? metas : null;

        for (int i = 0; i < spec.Clips.Count; i++)
        {
            var isVideo = MediaKind.RendersAsVideo(spec.Clips[i].FilePath);
            // Base stage: fit-and-letterbox into output frame.
            filter.Append($"[{i}:v]scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=decrease,");
            filter.Append($"pad={spec.Width}:{spec.Height}:(ow-iw)/2:(oh-ih)/2:color=#060409,setsar=1");

            var meta = perSlotMeta?[i];
            // Color grade (eq) — applied AFTER scale/pad so values map to
            // final output luma without source-aspect oddities.
            if (meta is not null && meta.HasGrade)
            {
                filter.Append(",eq=")
                      .Append("brightness=").Append(meta.Brightness.ToString("F3", inv))
                      .Append(":contrast=").Append(meta.Contrast.ToString("F3", inv))
                      .Append(":saturation=").Append(meta.Saturation.ToString("F3", inv));
            }

            // Ken Burns (zoompan) — applied LAST so the zoom interp doesn't
            // re-letterbox. d= is total output frames, computed from this
            // slot's duration × output fps. Both zoom AND pan target
            // interpolate linearly (T50 / 7.19) — pan stays at 0.5/0.5 on
            // both ends for the legacy centre-anchored behaviour.
            // Ken Burns is a STILLS effect — only for images; video clips carry
            // their own motion. Either branch normalises to the output fps so
            // concat / xfade get a uniform timebase across mixed video + stills.
            if (!isVideo && meta is not null && meta.HasZoom)
            {
                var dur = effDur[i];
                var frames = Math.Max(1, (int)System.Math.Round(dur * spec.Fps));
                var z0 = (meta.ZoomStartPct / 100.0).ToString("F3", inv);
                var z1 = (meta.ZoomEndPct / 100.0).ToString("F3", inv);
                var dMinus1 = Math.Max(1, frames - 1);

                // Pan interp: panNow = p0 + (p1−p0) × on / (d−1) for each axis.
                // Visible-window CENTRE = iw × panNow, so the top-left corner
                // ffmpeg actually wants is `iw × panNow − iw/zoom/2`.
                var px0 = meta.PanStartX.ToString("F3", inv);
                var px1 = meta.PanEndX.ToString("F3", inv);
                var py0 = meta.PanStartY.ToString("F3", inv);
                var py1 = meta.PanEndY.ToString("F3", inv);
                var xExpr = $"iw*({px0}+({px1}-{px0})*on/{dMinus1})-iw/zoom/2";
                var yExpr = $"ih*({py0}+({py1}-{py0})*on/{dMinus1})-ih/zoom/2";

                filter.Append($",zoompan=z='{z0}+({z1}-{z0})*on/{dMinus1}'")
                      .Append($":x='{xExpr}':y='{yExpr}'")
                      .Append($":d={frames}:s={spec.Width}x{spec.Height}:fps={spec.Fps}");
            }
            else
            {
                filter.Append($",fps={spec.Fps}");
                // A clip shorter than its slot holds its last frame for the
                // rest of the slot. Letting the slot shrink instead moved every
                // title, overlay and music cue after it out of place.
                var hold = effDur[i] - srcDur[i];
                if (isVideo && hold > 0.04)
                    filter.Append($",tpad=stop_mode=clone:stop_duration={hold.ToString("F3", inv)}");
            }

            filter.Append($"[v{i}];");
        }
    }

    /// <summary>Stage 2: combine the [vN] clips into a single [out] video
    /// stream. Hard-cut concat unless <see cref="Spec.CrossfadeSec"/> &gt; 0
    /// and there are 2+ clips, in which case an xfade chain walks
    /// pairwise with a running offset.</summary>
    /// <summary>The crossfade actually used: clamped to slightly less than the
    /// shortest slot so ffmpeg doesn't reject it with "offset must be
    /// non-negative". Shared by the video, the audio and the length maths.</summary>
    private static double EffectiveFade(Spec spec, double[] effDur)
    {
        if (spec.CrossfadeSec <= 0 || spec.Clips.Count < 2) return 0;
        var fade = spec.CrossfadeSec;
        var minDur = effDur.Min();
        if (fade >= minDur) fade = Math.Max(0.2, minDur - 0.1);
        return fade;
    }

    private static void AppendConcatOrXfadeStage(StringBuilder filter, Spec spec, double[] effDur)
    {
        if (spec.CrossfadeSec > 0 && spec.Clips.Count >= 2)
        {
            double fade = EffectiveFade(spec, effDur);

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string lastLabel = "v0";
            double runLen = effDur[0];
            for (int i = 1; i < spec.Clips.Count; i++)
            {
                var nextDur = effDur[i];
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
            var w = (int)(frameWidth * Math.Clamp(o.Scale, 0.1, 0.6)) & ~1;
            // Shift the overlay's own clock to its start time, so a video
            // overlay begins at its first frame when it appears instead of
            // having played unseen since 0:00.
            var shift = Math.Max(0, o.StartSec).ToString("F3", inv);
            filter.Append($";[{inputIdx}:v]setpts=PTS-STARTPTS+{shift}/TB,scale={w}:-2,setsar=1,format=yuva420p[ov{oi}]");
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
    private static void AppendTitleStage(StringBuilder filter, List<TitleDescriptor> titles, bool hadOverlay, int overlayCount, string titleDir)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string lastLabel = hadOverlay && overlayCount > 0 ? $"pip{overlayCount - 1}" : "out";
        var defaultFont = ResolveDefaultFontFile();
        var thaiFont = ResolveThaiFontFile();

        // The text goes to a file drawtext reads with textfile=, rather than
        // being escaped into the filter: inline, an apostrophe broke the whole
        // filtergraph ("No such filter"), a % made the title vanish, and a
        // typed line break came out as the letter n.
        Directory.CreateDirectory(titleDir);

        for (int ti = 0; ti < titles.Count; ti++)
        {
            var t = titles[ti];
            // Segoe UI has no Thai glyphs: a Thai title rendered as boxes.
            var fontFile = !string.IsNullOrEmpty(t.FontFile) ? t.FontFile
                : ContainsThai(t.Text) && thaiFont.Length > 0 ? thaiFont
                : defaultFont;
            var color = string.IsNullOrEmpty(t.Color) ? "0xD4A76A" : t.Color;
            // Vertical placement is per line (below); only x depends on the
            // line's own width.
            var xExpr = t.Position switch
            {
                "TL" or "BL" => "40",
                "C" => "(w-tw)/2",
                _ => "w-tw-40",   // TR / BR / default
            };
            var startStr = t.StartSec.ToString("F2", inv);
            var endStr = (t.StartSec + t.DurationSec).ToString("F2", inv);
            var fontArg = string.IsNullOrEmpty(fontFile) ? "" : $"fontfile='{EscapePathForFilter(fontFile)}':";
            var lastStage = ti == titles.Count - 1;
            var outLabel = lastStage ? "outpip" : $"tt{ti}";

            // One drawtext per line: drawtext draws a newline character as a
            // box on the line it ends, so a two-line title came out "line one□".
            var lines = t.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lineH = (int)Math.Round(t.FontSize * 1.3);
            var blockH = lineH * lines.Length;
            var yBase = t.Position switch
            {
                "BL" or "BR" => $"h-40-{blockH}",
                "C" => $"(h-{blockH})/2",
                _ => "40",
            };
            for (var li = 0; li < lines.Length; li++)
            {
                var textFile = Path.Combine(titleDir, $"t{ti}_{li}.txt");
                File.WriteAllText(textFile, lines[li], new UTF8Encoding(false));
                var lineOut = li == lines.Length - 1 ? outLabel : $"tt{ti}l{li}";
                filter.Append($";[{lastLabel}]drawtext={fontArg}textfile='{EscapePathForFilter(textFile)}':expansion=none:fontsize={t.FontSize}:fontcolor={color}:x={xExpr}:y={yBase}+{li * lineH}:enable='between(t,{startStr},{endStr})'[{lineOut}]");
                lastLabel = lineOut;
            }
        }
    }

    private static bool ContainsThai(string text)
    {
        foreach (var ch in text) if (ch is >= '฀' and <= '๿') return true;
        return false;
    }

    private static string ResolveThaiFontFile()
    {
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (var name in new[] { "LeelaUIb.ttf", "leelawdb.ttf", "tahomabd.ttf", "LeelawUI.ttf", "tahoma.ttf" })
        {
            var p = Path.Combine(fonts, name);
            if (File.Exists(p)) return p;
        }
        return "";
    }

    /// <summary>What went wrong, in words the user can act on; the full
    /// stderr goes to the activity log.</summary>
    private static string ExplainFailure(string stderr, int exit)
    {
        var tail = TailLines(stderr, 3);
        if (stderr.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
            return "render ไม่ได้ — มีไฟล์บนไทม์ไลน์ที่ถูกลบหรือย้ายไปแล้ว ตรวจช่องที่ขึ้นว่าไม่พบไฟล์ · " + tail;
        if (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            return "render ไม่ได้ — ไฟล์ถูกโปรแกรมอื่นเปิดค้างอยู่ ปิดแล้วลองใหม่ · " + tail;
        if (stderr.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
            return "render ไม่ได้ — มีไฟล์ที่ ffmpeg อ่านไม่ได้ (ไฟล์เสียหรือชนิดไม่รองรับ) · " + tail;
        if (stderr.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            return "render ไม่ได้ — ดิสก์เต็ม · " + tail;
        return $"ffmpeg ล้มเหลว (exit {exit}) · {tail}";
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDir(string? dir)
    {
        try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Stage 4c: per-clip NATIVE audio for the hard-cut concat path.
    /// Clips WITH audio pass their (already -ss/-t-trimmed) stream through a
    /// resample/format normalise; images + silent clips get an exact-length
    /// silence so the parallel <c>concat=a=1</c> stays in sync. Each clip is
    /// labelled [aclipN] and consumed by the video+audio concat.</summary>
    private static void AppendClipAudioStage(StringBuilder filter, Spec spec, double[] effDur, bool[] hasAudio)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < spec.Clips.Count; i++)
        {
            var slot = effDur[i].ToString("F3", inv);
            // Leading ';' (this runs AFTER the video [out] which has no trailing ';').
            // Every segment is padded or cut to exactly its slot: a clip whose
            // sound ran shorter than its picture made every later clip's sound
            // start early, and the film ended where the sound ran out.
            if (hasAudio[i])
                filter.Append($";[{i}:a]aresample=44100,aformat=channel_layouts=stereo,apad,atrim=0:{slot},asetpts=N/SR/TB[aclip{i}]");
            else
                filter.Append($";anullsrc=r=44100:cl=stereo,atrim=duration={slot},asetpts=N/SR/TB[aclip{i}]");
        }
    }

    /// <summary>Combine the per-clip [aclipN] streams into [acat], the SAME way
    /// the video is combined: a plain audio concat for hard cuts, or an
    /// acrossfade chain whose overlaps match the video xfade so audio stays in
    /// sync (each overlap shortens the total by the fade, exactly like xfade).</summary>
    private static void AppendClipAudioCombineStage(StringBuilder filter, Spec spec, double[] effDur)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        filter.Append(";");
        if (spec.CrossfadeSec > 0 && spec.Clips.Count >= 2)
        {
            var fadeStr = EffectiveFade(spec, effDur).ToString("F3", inv);
            string last = "aclip0";
            for (int i = 1; i < spec.Clips.Count; i++)
            {
                var outL = i == spec.Clips.Count - 1 ? "acat" : $"ax{i}";
                filter.Append($"[{last}][aclip{i}]acrossfade=d={fadeStr}[{outL}]");
                if (i < spec.Clips.Count - 1) filter.Append(";");
                last = outL;
            }
        }
        else
        {
            for (int i = 0; i < spec.Clips.Count; i++) filter.Append($"[aclip{i}]");
            filter.Append($"concat=n={spec.Clips.Count}:v=0:a=1[acat]");
        }
    }

    /// <summary>Stage 5a: legacy single-track external audio — volume scale +
    /// normalise, labelled [outLabel].</summary>
    private static void AppendLegacyAudioStage(StringBuilder filter, int audioInputIndex, double volume, string outLabel)
    {
        var vol = Math.Clamp(volume, 0.0, 2.0);
        filter.Append($";[{audioInputIndex}:a]volume={vol.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},aresample=44100,aformat=channel_layouts=stereo[{outLabel}]");
    }

    /// <summary>Stage 5b: multi-track audio mixer. Per-track adelay + volume
    /// + optional afade (in / out), then a single amix that produces [a].
    /// dropout_transition=0 prevents amix from auto-ducking when shorter
    /// inputs end (would otherwise sound like a level dip).
    ///
    /// Fade-out anchoring: afade=t=out needs a start time. We pick the
    /// fade window as <c>[StartSec + ProbedDurationSec − FadeOutSec, end]</c>,
    /// but the audio duration isn't trivial to probe from the renderer
    /// without ffprobe. As a pragmatic compromise we anchor fade-out to
    /// 60s into the track — long enough that legitimate audio finishes
    /// playing, short enough that loops don't drone on. (T42 / 7.16)
    /// </summary>
    private static void AppendMultiAudioStage(StringBuilder filter, List<AudioTrackDescriptor> audioTracks, int firstAudioInputIndex, string outLabel)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int ai = 0; ai < audioTracks.Count; ai++)
        {
            var t = audioTracks[ai];
            var input = firstAudioInputIndex + ai;
            var startMs = (int)Math.Round(Math.Max(0, t.StartSec) * 1000);
            var vol = Math.Clamp(t.Volume, 0.0, 2.0);

            // Build the per-track filter chain. Order: adelay → volume → afade(in) → afade(out)
            // adelay is the pivot — it pushes the entire stream right on the
            // timeline; the fades that follow are timed relative to the
            // DELAYED stream so afade=t=in:st=0 means "at the moment this
            // track first becomes audible", not "0 on the master timeline".
            filter.Append($";[{input}:a]");
            var parts = new List<string> { "aresample=44100", "aformat=channel_layouts=stereo" };
            if (startMs > 0) parts.Add($"adelay={startMs}|{startMs}");
            parts.Add($"volume={vol.ToString("F2", inv)}");
            if (t.FadeInSec > 0.01)
                parts.Add($"afade=t=in:st=0:d={t.FadeInSec.ToString("F2", inv)}");
            if (t.FadeOutSec > 0.01)
            {
                // Anchor fade-out at natural duration when ffprobe could
                // resolve it (T51 / 7.20); otherwise fall back to the
                // legacy 60s constant. The start time is RELATIVE to the
                // delayed stream so adelay doesn't shift it.
                var anchorSec = t.NaturalDurationSec ?? 60.0;
                var fadeStart = Math.Max(0.5, anchorSec - t.FadeOutSec);
                parts.Add($"afade=t=out:st={fadeStart.ToString("F2", inv)}:d={t.FadeOutSec.ToString("F2", inv)}");
            }
            filter.Append(string.Join(",", parts));
            filter.Append($"[a{ai}]");
        }
        filter.Append(";");
        for (int ai = 0; ai < audioTracks.Count; ai++) filter.Append($"[a{ai}]");
        // normalize=0: each track keeps the volume its slider says, instead of
        // being divided by the number of tracks.
        filter.Append($"amix=inputs={audioTracks.Count}:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95[{outLabel}]");
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
