using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// The one-button "prompt → clips → final film → Facebook" pipeline.
///
/// Given a generated <see cref="StoryboardSpec"/>, Auto Pilot:
///   1. submits every clip that isn't already rendered (same
///      <see cref="StoryboardBuilder.BuildShot"/> path as the card's Queue
///      button) and waits for the whole batch to finish,
///   2. normalises + concatenates the per-clip outputs into a single
///      H.264/AAC MP4 via ffmpeg (mixed engines produce mixed codecs /
///      resolutions / silent tracks — everything is conformed to the board's
///      aspect at 30 fps with a stereo track, images become timed stills),
///   3. writes the film into the Library (shots + clips rows), and
///   4. optionally posts it to the configured Facebook Page with the board's
///      caption + hashtags through <see cref="PostingService"/>.
///
/// The service holds no UI references — the Storyboard view-model subscribes
/// to <see cref="IProgress{T}"/> updates, so a run keeps going while the user
/// navigates elsewhere (the VM lives on MainViewModel for the same reason).
/// </summary>
public sealed class StoryboardAutoPilot
{
    private readonly StudioContext _ctx;
    private int _running;   // 0/1 — Interlocked guard against double-starts

    public StoryboardAutoPilot(StudioContext ctx) { _ctx = ctx; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public enum AutoPilotStep { Generating, Assembling, Saving, Posting, Done, Failed }

    public sealed record AutoPilotProgress(AutoPilotStep Step, string Message, int DoneClips, int TotalClips);

    public sealed record AutoPilotResult(bool Ok, string? FinalPath, string? FacebookPostId, string? Error)
    {
        public static AutoPilotResult Fail(string error) => new(false, null, null, error);
    }

    public async Task<AutoPilotResult> RunAsync(
        StoryboardSpec spec,
        string? characterRef, string? sceneRef, string? outfitRef,
        bool postToFacebook,
        IProgress<AutoPilotProgress>? progress,
        CancellationToken ct)
    {
        // Guard BEFORE the try — otherwise the finally below would clear the
        // running flag that belongs to the other, still-active run.
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return AutoPilotResult.Fail("Auto Pilot กำลังทำงานอยู่แล้ว — รอรอบนี้จบก่อน");

        try
        {
            // ── Preflight ────────────────────────────────────────────────────
            if (spec.Clips.Count == 0)
                return AutoPilotResult.Fail("สตอรี่บอร์ดยังไม่มีคลิป");
            if (_ctx.FFmpeg.TryResolve() is null)
                return AutoPilotResult.Fail("ไม่พบ ffmpeg — ติดตั้งหรือระบุ path ใน Settings ก่อน (ใช้รวมคลิปเป็นไฟล์เดียว)");
            if (postToFacebook)
            {
                if (string.IsNullOrWhiteSpace(_ctx.Settings.PostFacebookPageId))
                    return AutoPilotResult.Fail("ยังไม่ได้ตั้ง Facebook Page ID ใน Settings → Posting");
                if (string.IsNullOrWhiteSpace(_ctx.Settings["facebook"]))
                    return AutoPilotResult.Fail("ยังไม่ได้วาง Facebook Page Access Token ใน Settings → Posting");
            }

            // ── 1. Generate every clip ───────────────────────────────────────
            var mediaByIndex = await GenerateClipsAsync(spec, characterRef, sceneRef, outfitRef, progress, ct);

            // ── 2. Assemble into one film ────────────────────────────────────
            progress?.Report(new(AutoPilotStep.Assembling,
                $"กำลังรวม {mediaByIndex.Count} คลิปเป็นไฟล์เดียว…", mediaByIndex.Count, mediaByIndex.Count));
            var finalPath = await AssembleAsync(spec, mediaByIndex, ct);

            // ── 3. Library rows ──────────────────────────────────────────────
            progress?.Report(new(AutoPilotStep.Saving, "บันทึกเข้า Library…", mediaByIndex.Count, mediaByIndex.Count));
            // The planned durations say nothing about what the engines really
            // returned; the Library and the post should carry the film's own.
            double? filmSec = null;
            try { filmSec = await _ctx.FFmpeg.ProbeDurationSecAsync(finalPath, ct); } catch (OperationCanceledException) { throw; } catch { }
            var clip = WriteLibraryRows(spec, finalPath, filmSec);

            // ── 4. Facebook ──────────────────────────────────────────────────
            if (!postToFacebook)
            {
                progress?.Report(new(AutoPilotStep.Done, "เสร็จแล้ว — ไฟล์รวมอยู่ใน Library ✓", mediaByIndex.Count, mediaByIndex.Count));
                return new AutoPilotResult(true, finalPath, null, null);
            }

            progress?.Report(new(AutoPilotStep.Posting, "กำลังโพสต์ขึ้น Facebook Page…", mediaByIndex.Count, mediaByIndex.Count));
            var caption = StoryboardBuilder.ComposeFacebookCaption(spec.Facebook);
            var post = await _ctx.Posting.PostAsync("facebook", clip, caption, ct);
            if (!post.Ok)
            {
                // The film exists and is in the Library — only the publish leg
                // failed, so say exactly that (retry lives in the Library card).
                return new AutoPilotResult(false, finalPath, null,
                    $"สร้างไฟล์สำเร็จ (อยู่ใน Library แล้ว) แต่โพสต์ Facebook ไม่ผ่าน: {post.Error}");
            }

            progress?.Report(new(AutoPilotStep.Done,
                $"โพสต์ขึ้น Facebook แล้ว ✓ (post id {post.PostId})", mediaByIndex.Count, mediaByIndex.Count));
            ActivityLog.Info("autopilot", $"board '{spec.Title}' → {Path.GetFileName(finalPath)} → fb post {post.PostId}");
            return new AutoPilotResult(true, finalPath, post.PostId, null);
        }
        catch (OperationCanceledException)
        {
            return AutoPilotResult.Fail("ยกเลิกแล้ว");
        }
        catch (Exception ex)
        {
            ActivityLog.Error("autopilot", "run failed", ex);
            return AutoPilotResult.Fail(ex.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    // ── Step 1 — generate ─────────────────────────────────────────────────────

    /// <summary>Submit every not-yet-rendered clip and wait for the batch.
    /// Returns clip-index → finished media path, in board order. Clips that
    /// already finished (Done + file on disk) are reused, which makes a re-run
    /// after a partial failure resume instead of regenerate.</summary>
    private async Task<List<string>> GenerateClipsAsync(
        StoryboardSpec spec,
        string? characterRef, string? sceneRef, string? outfitRef,
        IProgress<AutoPilotProgress>? progress, CancellationToken ct)
    {
        var clips = spec.Clips.ToList();
        var total = clips.Count;
        var pending = new ConcurrentDictionary<string, TaskCompletionSource<(bool Ok, string? Path, string? Error)>>();

        void OnProgress(object? sender, GenerationProgressEventArgs e)
        {
            if (!pending.TryGetValue(e.ShotId, out var tcs)) return;
            if (e.Status == ShotStatus.Done)
                tcs.TrySetResult((true, e.MediaPath, null));
            else if (e.Status == ShotStatus.Error)
                tcs.TrySetResult((false, null, e.Error ?? "generation failed"));
        }

        // Shots this run submitted and has not seen finish. A cancel or a
        // timeout used to leave them rendering — and billing — after the run
        // had already reported failure.
        var inFlight = new ConcurrentDictionary<string, byte>();
        void CancelInFlight()
        {
            foreach (var id in inFlight.Keys)
                _ = _ctx.Generation.CancelByShotAsync(id);
        }
        using var onCancel = ct.Register(CancelInFlight);

        _ctx.Generation.ProgressChanged += OnProgress;
        try
        {
            var waits = new List<(StoryboardClip Clip, Task<(bool Ok, string? Path, string? Error)> Task)>();
            foreach (var clip in clips)
            {
                ct.ThrowIfCancellationRequested();

                // Already rendered in a previous run / manual Queue → reuse.
                if (clip.IsDone && !string.IsNullOrEmpty(clip.MediaPath) && File.Exists(clip.MediaPath))
                {
                    waits.Add((clip, Task.FromResult<(bool, string?, string?)>((true, clip.MediaPath, null))));
                    continue;
                }

                // In-flight from a manual Queue press → just wait on its shot.
                if (clip.IsBusy && !string.IsNullOrEmpty(clip.ShotId))
                {
                    var tcsBusy = new TaskCompletionSource<(bool, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[clip.ShotId] = tcsBusy;
                    // Close the register-vs-finish race: if the shot completed
                    // between the IsBusy check and the handler registration,
                    // no event will ever arrive — settle from the clip mirror.
                    if (clip.IsDone && !string.IsNullOrEmpty(clip.MediaPath))
                        tcsBusy.TrySetResult((true, clip.MediaPath, null));
                    else if (clip.Status == ShotStatus.Error)
                        tcsBusy.TrySetResult((false, null, "generation failed"));
                    waits.Add((clip, tcsBusy.Task));
                    continue;
                }

                var shot = StoryboardBuilder.BuildShot(spec, clip, characterRef, sceneRef, outfitRef);
                var route = string.IsNullOrWhiteSpace(clip.Route) ? "seedance" : clip.Route;

                // ComfyUI renders the SCENE still through a ready graph — same
                // behaviour as the card's Queue button.
                string? workflowOverride = null;
                if (route == "comfyui")
                {
                    try
                    {
                        var graph = StoryboardBuilder.BuildComfyGraph(spec, clip);
                        var path = NodeFlowConverter.SaveToUserWorkflows(graph, $"storyboard-clip{clip.Index}");
                        _ctx.Workflows.Refresh();
                        workflowOverride = Path.GetFileNameWithoutExtension(path);
                    }
                    catch { /* fall back to the active workflow */ }
                }

                var tcs = new TaskCompletionSource<(bool, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[shot.Id] = tcs;
                // Registered before the submit so a cancel during a rented
                // card's warm-up reaches it too.
                inFlight[shot.Id] = 0;
                _ = tcs.Task.ContinueWith(_ => inFlight.TryRemove(shot.Id, out byte _), TaskScheduler.Default);

                clip.ShotId = shot.Id;
                clip.Status = ShotStatus.Queue;
                clip.Progress = 0;
                try
                {
                    // The run's token goes with the job: a cancel that lands
                    // while this clip is still being submitted reaches it too.
                    // A run that simply ends only disposes the source, so the
                    // clips it leaves rendering keep going (and are reused).
                    await _ctx.Generation.SubmitAsync(shot, ct, route, workflowOverride);
                    if (clip.Status == ShotStatus.Queue) clip.Status = ShotStatus.Generating;
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    clip.Status = ShotStatus.Error;
                    throw new OperationCanceledException(ct);
                }
                catch (Exception ex)
                {
                    clip.Status = ShotStatus.Error;
                    inFlight.TryRemove(shot.Id, out _);
                    // The clips already submitted keep going: a re-run after
                    // fixing this one reuses them instead of paying twice.
                    throw new InvalidOperationException($"CLIP {clip.Index} ส่งเข้า Queue ไม่ผ่าน: {ex.Message}", ex);
                }
                waits.Add((clip, tcs.Task));
            }

            // Wait for the batch, reporting done-count as clips land. Cloud
            // engines cap themselves at 8–15 min, but a local card renders the
            // clips one after another and a rented one first spends 10–40 min
            // warming up, so their share of the budget grows with the board.
            var slow = clips.Count(c => c.Route is "comfyui" or "rentgpu");
            var budget = TimeSpan.FromMinutes(Math.Min(6 * 60,
                45 + 20 * slow + (clips.Any(c => c.Route == "rentgpu") ? 45 : 0)));
            var deadline = Task.Delay(budget, ct);
            var done = 0;
            var results = new string?[waits.Count];
            var remaining = waits.Select((w, i) => (w.Clip, w.Task, Index: i)).ToList();
            progress?.Report(new(AutoPilotStep.Generating, $"กำลังสร้างคลิป… 0/{total}", 0, total));

            while (remaining.Count > 0)
            {
                var finished = await Task.WhenAny(remaining.Select(r => r.Task).Append(deadline).ToArray());
                // A user cancel also completes the deadline task (it shares the
                // token) — distinguish it so cancel doesn't read as a timeout.
                ct.ThrowIfCancellationRequested();
                if (finished == deadline)
                {
                    CancelInFlight();
                    throw new TimeoutException(
                        $"รอเกิน {budget.TotalMinutes:0} นาที — ยกเลิกคลิปที่รอบนี้ส่งไปแล้ว (คลิปที่กด Queue เองยังทำงานต่อ) คลิปที่เสร็จแล้วจะถูกใช้ซ้ำเมื่อกด Auto Pilot อีกครั้ง");
                }

                var hit = remaining.First(r => r.Task == finished);
                remaining.Remove(hit);
                var (ok, path, error) = await hit.Task;
                if (!ok)
                    throw new InvalidOperationException($"CLIP {hit.Clip.Index} ผิดพลาด: {error}");
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    throw new InvalidOperationException($"CLIP {hit.Clip.Index} เสร็จแต่ไม่พบไฟล์ผลลัพธ์");
                results[hit.Index] = path;
                done++;
                progress?.Report(new(AutoPilotStep.Generating, $"กำลังสร้างคลิป… {done}/{total}", done, total));
            }

            return results.Select(p => p!).ToList();
        }
        finally
        {
            _ctx.Generation.ProgressChanged -= OnProgress;
        }
    }

    // ── Step 2 — assemble ─────────────────────────────────────────────────────

    /// <summary>Conform every input to the board aspect (scale + pad, 30 fps,
    /// yuv420p, 48 kHz stereo — silence synthesised where an engine returned a
    /// mute track or a still image) and concat into one MP4 in the media folder.</summary>
    private async Task<string> AssembleAsync(StoryboardSpec spec, List<string> inputs, CancellationToken ct)
    {
        var ffmpeg = _ctx.FFmpeg.TryResolve()
            ?? throw new InvalidOperationException("ไม่พบ ffmpeg");
        var (w, h) = VideoTargetSize(spec.AspectId);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var outPath = Path.Combine(AppPaths.MediaFolder, $"board-{stamp}.mp4");
        for (var n = 2; File.Exists(outPath); n++)
            outPath = Path.Combine(AppPaths.MediaFolder, $"board-{stamp}-{n}.mp4");

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        var filter = new System.Text.StringBuilder();
        var concatIns = new System.Text.StringBuilder();

        // First pass: register all inputs, remembering which ffmpeg input index
        // feeds video / audio for each clip (silent sources get a lavfi input).
        var plans = new List<(int VideoIn, int AudioIn, bool AudioIsLavfi)>();
        var nextInput = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            var path = inputs[i];
            var clipDur = spec.Clips.Count > i ? spec.Clips[i].DurationSec : 8;
            if (MediaKind.IsAnimatedWebp(path))
                throw new InvalidOperationException(
                    $"CLIP {i + 1} เป็นภาพเคลื่อนไหว WebP ซึ่ง ffmpeg อ่านไม่ได้ — ตั้งให้ workflow บันทึกเป็นวิดีโอ (SaveVideo) แล้วสร้างคลิปนี้ใหม่");
            // A GIF moves, so it is a video input here, not a still.
            var isImage = MediaKind.IsImage(path) && !MediaKind.RendersAsVideo(path);

            int videoIn;
            if (isImage)
            {
                args.AddRange(new[] { "-loop", "1", "-t", clipDur.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", path });
                videoIn = nextInput++;
            }
            else
            {
                args.AddRange(new[] { "-i", path });
                videoIn = nextInput++;
            }

            var hasAudio = !isImage && await _ctx.FFmpeg.ProbeHasAudioAsync(path, ct);
            int audioIn;
            var lavfi = false;
            if (hasAudio)
            {
                audioIn = videoIn;
            }
            else
            {
                var silentDur = isImage
                    ? clipDur
                    : await _ctx.FFmpeg.ProbeDurationSecAsync(path, ct) ?? clipDur;
                args.AddRange(new[]
                {
                    "-f", "lavfi",
                    "-t", silentDur.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
                });
                audioIn = nextInput++;
                lavfi = true;
            }
            plans.Add((videoIn, audioIn, lavfi));
        }

        // Second pass: per-clip conform chains + the concat.
        for (var i = 0; i < plans.Count; i++)
        {
            var (videoIn, audioIn, _) = plans[i];
            filter.Append($"[{videoIn}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,")
                  .Append($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30,format=yuv420p[v{i}];");
            filter.Append($"[{audioIn}:a]")
                  .Append($"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            concatIns.Append($"[v{i}][a{i}]");
        }
        filter.Append($"{concatIns}concat=n={plans.Count}:v=1:a=1[vout][aout]");

        args.AddRange(new[]
        {
            "-filter_complex", filter.ToString(),
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "19",
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            outPath,
        });

        var (_, stderr, exit) = await _ctx.FFmpeg.RunAsync(ffmpeg, args, capture: true, ct: ct);
        if (exit != 0 || !File.Exists(outPath))
        {
            var tail = stderr.Length > 600 ? stderr[^600..] : stderr;
            throw new InvalidOperationException("รวมคลิปไม่สำเร็จ (ffmpeg): " + tail.Trim());
        }
        return outPath;
    }

    /// <summary>Delivery resolution per aspect — full-HD class, unlike
    /// <see cref="StoryboardBuilder.AspectToSize"/> which returns SDXL latent
    /// sizes for still generation.</summary>
    private static (int W, int H) VideoTargetSize(string aspectId) => aspectId switch
    {
        "9:16" => (1080, 1920),
        "1:1" => (1080, 1080),
        "21:9" => (2560, 1080),
        _ => (1920, 1080),
    };

    // ── Step 3 — library rows ─────────────────────────────────────────────────

    private Clip WriteLibraryRows(StoryboardSpec spec, string finalPath, double? filmSec)
    {
        var shotId = "board-" + Guid.NewGuid().ToString("N")[..8];
        var clipId = Guid.NewGuid().ToString("N");
        var durationMs = (int)((filmSec ?? spec.Clips.Sum(c => c.DurationSec)) * 1000);
        try
        {
            using var c = _ctx.Db.Open();
            c.Execute("""
                INSERT OR IGNORE INTO shots (id, sequence_id, number, title, created_at, updated_at)
                VALUES ($id, 'default', '—', $title, $now, $now)
                """,
                new { id = shotId, title = $"Auto Pilot · {spec.Title}", now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            c.Execute("""
                INSERT INTO clips (id, shot_id, duration_ms, file_path, created_at)
                VALUES ($id, $shotId, $dur, $path, $now)
                """,
                new { id = clipId, shotId, dur = durationMs, path = finalPath, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        catch (Exception ex)
        {
            // Best-effort — the file is on disk either way; posting only needs
            // the id + path below.
            ActivityLog.Warn("autopilot", "library row insert failed: " + ex.Message);
        }
        return new Clip { Id = clipId, ShotId = shotId, FilePath = finalPath, DurationMs = durationMs, CreatedAt = DateTimeOffset.UtcNow };
    }
}
