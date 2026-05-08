using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services.Providers.ComfyUI;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// Orchestrates a single generation job end-to-end.
///
/// Architecture (revised after a real-server bug where the file landed in
/// ComfyUI's output/ but our app never picked it up):
///
///   * One <see cref="ComfyUiClient"/> instance shared between submit and
///     listener — same client_id throughout, so the WebSocket sees events
///     for our own prompts.
///   * Two parallel tasks once the prompt is in flight:
///       1. WebSocket stream  — best-effort live progress for the UI bar.
///       2. /history poller   — authoritative completion detector. Runs
///                              every 1.5s and stops the moment the
///                              prompt's outputs appear (or status=error).
///     The poller wins races against the WS, which can drop messages or
///     connect after "executed" has already fired on a fast workflow.
/// </summary>
public sealed class GenerationService
{
    private readonly StudioContext _ctx;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public event EventHandler<GenerationProgressEventArgs>? ProgressChanged;

    public GenerationService(StudioContext ctx)
    {
        _ctx = ctx;
        _ui = System.Windows.Application.Current?.Dispatcher
              ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
    }

    /// <summary>Submit a shot for generation. Returns the prompt id.</summary>
    public async Task<string> SubmitAsync(Shot shot, CancellationToken ct = default)
    {
        var url = _ctx.Settings.ComfyUiUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("ComfyUI server URL is empty — set it in Settings.");

        // NOTE: client lifecycle is owned by the listener task — do NOT use a
        // `using` here. Disposing it kills the listener's WebSocket and HTTP
        // pipeline mid-job.
        var client = new ComfyUiClient(url);

        try
        {
            var probe = await client.ProbeAsync(ct);
            if (!probe.Ok)
            {
                client.Dispose();
                throw new ComfyUiException($"ComfyUI not reachable at {url} — {probe.Status}");
            }

            // Load the user's active workflow from the repository — falls
            // back to the bundled default if the saved name has been removed
            // from disk since last save.
            var descriptor = _ctx.Workflows.FindByName(_ctx.Settings.ActiveWorkflow)
                          ?? _ctx.Workflows.Default();
            var workflow = descriptor is not null
                ? Workflow.LoadFromPath(descriptor.Path)
                : Workflow.LoadDefault();

            workflow
                .SetPositivePrompt(shot.Prompt)
                .SetNegativePrompt(shot.NegativePrompt)
                .SetSeed(shot.Seed.A)
                .SetSize(shot.Aspect == AspectRatio.Wide ? 1024 : 768,
                         shot.Aspect == AspectRatio.Wide ? 576 : 768)
                .SetFilenamePrefix($"chanthra/shot{shot.Number}");

            // If the workflow uses LoadImage AND the shot has a reference image
            // attached, upload it to ComfyUI's input/ folder and patch the
            // LoadImage node to reference the server-side filename. Without
            // this step the workflow would error with "reference.png not found".
            if (workflow.HasLoadImage() && !string.IsNullOrEmpty(shot.ReferenceImagePath))
            {
                if (!File.Exists(shot.ReferenceImagePath))
                {
                    client.Dispose();
                    throw new ComfyUiException(
                        $"Reference image not found: {shot.ReferenceImagePath}");
                }
                var uploaded = await client.UploadImageAsync(shot.ReferenceImagePath, ct);
                workflow.SetReferenceImage(uploaded);
            }
            else if (workflow.HasLoadImage())
            {
                client.Dispose();
                throw new ComfyUiException(
                    "This workflow requires a reference image (LoadImage node). " +
                    "Drag-drop or browse an image in the Generate panel first.");
            }

            // Auto-substitute model names in EVERY loader node (checkpoint,
            // unet, vae, clip, clip-vision, lora) so a workflow that bakes
            // in "flux1-dev.safetensors" still runs on a server that only
            // has "flux1-dev-fp8.safetensors". We use FuzzyResolve below.
            await AutoFixModelReferencesAsync(client, workflow, shot, ct);

            var promptId = await client.SubmitPromptAsync(workflow.Nodes, ct);
            WriteJobRow(shot.Id, promptId, "queued");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running[promptId] = cts;

            // Listener takes ownership of `client` from here.
            _ = Task.Run(async () =>
            {
                try { await RunListenerAsync(shot, promptId, client, cts.Token); }
                finally { client.Dispose(); }
            });

            return promptId;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task CancelAsync(string promptId)
    {
        if (_running.TryRemove(promptId, out var cts))
            cts.Cancel();
        var url = _ctx.Settings.ComfyUiUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        using var c = new ComfyUiClient(url);
        await c.InterruptAsync();
    }

    private async Task RunListenerAsync(Shot shot, string promptId, ComfyUiClient client, CancellationToken ct)
    {
        try
        {
            // Background WS task — feeds progress to the UI but is NOT trusted
            // to detect completion (race-prone, see class docs).
            using var wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wsTask = Task.Run(() => StreamWsProgressAsync(client, shot, promptId, wsCts.Token));

            // Polling completion detector — authoritative.
            var completed = await PollHistoryUntilDoneAsync(client, promptId, shot, ct);
            wsCts.Cancel();
            try { await wsTask; } catch { /* expected on cancel */ }

            if (completed is null)
            {
                WriteJobUpdate(promptId, "error", "history empty after timeout");
                Raise(promptId, shot.Id, ShotStatus.Error, 0, "ComfyUI returned no history (timed out)");
                return;
            }

            // Server-side error?
            var statusObj = completed["status"] as JsonObject;
            var statusStr = statusObj?["status_str"]?.GetValue<string>();
            if (statusStr == "error")
            {
                var msg = ExtractStatusError(statusObj) ?? "execution error (no message)";
                WriteJobUpdate(promptId, "error", msg);
                Raise(promptId, shot.Id, ShotStatus.Error, 0, msg);
                return;
            }

            // Download every output file the workflow produced.
            string? primaryPath = null;
            int downloaded = 0;
            foreach (var output in Workflow.ExtractOutputs(completed))
            {
                var safeName = SafeFilename(shot.Id + "_" + Path.GetFileName(output.Filename));
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                try
                {
                    await client.DownloadFileAsync(output.Filename, output.Subfolder, output.Type, dest, ct);
                    primaryPath ??= dest;
                    WriteClipRow(shot.Id, dest, output.Kind);
                    downloaded++;
                }
                catch (Exception ex)
                {
                    Raise(promptId, shot.Id, ShotStatus.Error, 0, $"download failed for {output.Filename}: {ex.Message}");
                }
            }

            if (downloaded == 0)
            {
                WriteJobUpdate(promptId, "error", "no output files in history");
                Raise(promptId, shot.Id, ShotStatus.Error, 0,
                    "ComfyUI finished but reported no SaveImage / video outputs. Workflow may be missing a save node.");
                return;
            }

            WriteJobUpdate(promptId, "done", null);
            Raise(promptId, shot.Id, ShotStatus.Done, 100, null, primaryPath);
        }
        catch (OperationCanceledException)
        {
            WriteJobUpdate(promptId, "cancelled", null);
            Raise(promptId, shot.Id, ShotStatus.Error, 0, "cancelled");
        }
        catch (Exception ex)
        {
            WriteJobUpdate(promptId, "error", ex.Message);
            Raise(promptId, shot.Id, ShotStatus.Error, 0, ex.Message);
        }
        finally
        {
            _running.TryRemove(promptId, out _);
        }
    }

    /// <summary>
    /// Polls /history every 1.5s until the prompt's record carries outputs
    /// or status=error. Returns the inner record, or null on cancel/timeout.
    /// </summary>
    private static async Task<JsonObject?> PollHistoryUntilDoneAsync(
        ComfyUiClient client, string promptId, Shot shot, CancellationToken ct)
    {
        // Hard ceiling so we don't dangle forever on a hung server (15 minutes).
        var deadline = DateTime.UtcNow.AddMinutes(15);
        var firstSeen = false;

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException) { return null; }

            JsonObject? record;
            try
            {
                record = await client.GetHistoryAsync(promptId, ct);
            }
            catch
            {
                continue;  // transient network glitch, try again
            }
            if (record is null)
            {
                // History entry not present yet — server still queueing it.
                continue;
            }
            firstSeen = true;

            var statusStr = record["status"]?["status_str"]?.GetValue<string>();
            if (statusStr == "error") return record;

            // Outputs filled in once SaveImage/SaveAnimatedWEBP/etc. has run.
            if (record["outputs"] is JsonObject outputs && outputs.Count > 0)
                return record;

            // Otherwise the prompt is still queued or running. Keep polling.
            _ = firstSeen;  // (kept for future "first heartbeat" telemetry)
        }
        return null;
    }

    /// <summary>WebSocket reader that only updates progress for our prompt id.</summary>
    private async Task StreamWsProgressAsync(ComfyUiClient client, Shot shot, string promptId, CancellationToken ct)
    {
        try
        {
            await foreach (var e in client.StreamProgressAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                if (e.Type == "progress" && e.ProgressFraction is double frac)
                {
                    Raise(promptId, shot.Id, ShotStatus.Generating, frac * 100, null);
                }
                else if (e.Type == "executing" && e.PromptId == promptId)
                {
                    Raise(promptId, shot.Id, ShotStatus.Generating, shot.Progress, null);
                }
                else if (e.Type == "execution_error" && e.PromptId == promptId)
                {
                    var msg = e.Data?["exception_message"]?.GetValue<string>() ?? "execution error";
                    Raise(promptId, shot.Id, ShotStatus.Error, 0, msg);
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch
        {
            // WS disconnects are non-fatal — the poller still drives completion.
        }
    }

    private static string? ExtractStatusError(JsonObject? statusObj)
    {
        if (statusObj?["messages"] is not JsonArray messages) return null;
        foreach (var m in messages)
        {
            if (m is not JsonArray pair || pair.Count < 2) continue;
            var kind = pair[0]?.GetValue<string>();
            if (kind != "execution_error") continue;
            var details = pair[1] as JsonObject;
            var ex = details?["exception_message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(ex)) return ex;
        }
        return null;
    }

    private void Raise(string promptId, string shotId, ShotStatus status, double progress, string? error, string? mediaPath = null)
    {
        var args = new GenerationProgressEventArgs(promptId, shotId, status, progress, error, mediaPath);
        if (_ui.CheckAccess())
            ProgressChanged?.Invoke(this, args);
        else
            _ui.BeginInvoke(new Action(() => ProgressChanged?.Invoke(this, args)));
    }

    private void WriteJobRow(string shotId, string promptId, string status)
    {
        try
        {
            using var c = _ctx.Db.Open();
            c.Execute("""
                INSERT INTO generation_jobs (id, shot_id, provider, status, submitted_at)
                VALUES ($id, $shotId, 'comfyui', $status, $now)
                """,
                new { id = promptId, shotId, status, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        catch
        {
            // DB write failures are non-fatal for the in-memory job.
        }
    }

    private void WriteJobUpdate(string promptId, string status, string? error)
    {
        try
        {
            using var c = _ctx.Db.Open();
            c.Execute("""
                UPDATE generation_jobs
                SET status = $status,
                    completed_at = $now,
                    error_message = $err
                WHERE id = $id
                """,
                new { id = promptId, status, err = error, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        catch { }
    }

    private void WriteClipRow(string shotId, string filePath, string kind)
    {
        try
        {
            using var c = _ctx.Db.Open();
            c.Execute("""
                INSERT INTO clips (id, shot_id, duration_ms, file_path, created_at)
                VALUES ($id, $shotId, 0, $path, $now)
                """,
                new
                {
                    id = Guid.NewGuid().ToString("N"),
                    shotId,
                    path = filePath,
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
        }
        catch { }
    }

    private static string SafeFilename(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    private static bool IsLikelySdxl(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("xl") || n.Contains("sdxl") || n.Contains("juggernaut")
            || n.Contains("dreamshaper") && n.Contains("xl") || n.Contains("realvis");
    }

    private static bool IsLikelySd15(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("v1-5") || n.Contains("v1.5") || n.Contains("sd15")
            || n.Contains("epicrealism") || n.Contains("dreamshaper8");
    }

    /// <summary>
    /// For each model-loader node in the workflow, check whether the
    /// referenced file exists on the ComfyUI server. If it doesn't,
    /// substring-match against the available files (e.g. "flux1-dev.safetensors"
    /// → "flux1-dev-fp8.safetensors") and substitute. If no match exists,
    /// throw a friendly error listing what IS available.
    /// </summary>
    private static async Task AutoFixModelReferencesAsync(
        ComfyUiClient client, ChanthraStudio.Services.Providers.ComfyUI.Workflow workflow, Shot shot, CancellationToken ct)
    {
        // Pre-fetch all relevant lists once — these calls hit /object_info
        // which is cheap on the server but we still avoid hitting it 6x.
        var checkpoints = await client.GetAvailableCheckpointsAsync(ct);
        var unets = await client.GetAvailableUnetsAsync(ct);
        var vaes = await client.GetAvailableVaesAsync(ct);
        var clips = await client.GetAvailableClipsAsync(ct);
        var clipVision = await client.GetAvailableClipVisionAsync(ct);
        var loras = await client.GetAvailableLorasAsync(ct);

        // Special case: SD-style workflow with no checkpoints installed at all
        // — surface the early-return error users hit in v0.2.x.
        if (!workflow.UsesUnetLoader() && checkpoints.Count == 0)
            throw new ComfyUiException(
                "No checkpoints found in ComfyUI. Drop a .safetensors model into " +
                "ComfyUI/models/checkpoints/ and restart the server.");

        // For SD checkpoints, prefer SDXL if the workflow seems SDXL-shaped
        // and SD 1.5 otherwise — keeps the legacy 0.1 auto-pick behaviour.
        workflow.PatchInputs(new[] { "CheckpointLoaderSimple" }, "ckpt_name", current =>
        {
            if (checkpoints.Contains(current)) return current;
            var fuzzy = FuzzyMatch(current, checkpoints);
            if (fuzzy is not null) return fuzzy;
            return checkpoints.FirstOrDefault(IsLikelySdxl)
                ?? checkpoints.FirstOrDefault(IsLikelySd15)
                ?? (checkpoints.Count > 0 ? checkpoints[0] : null);
        });

        workflow.PatchInputs(new[] { "UNETLoader" }, "unet_name", current =>
            unets.Contains(current) ? current : FuzzyMatch(current, unets));

        workflow.PatchInputs(new[] { "VAELoader" }, "vae_name", current =>
            vaes.Contains(current) ? current : FuzzyMatch(current, vaes));

        workflow.PatchInputs(new[] { "DualCLIPLoader" }, "clip_name1", current =>
            clips.Contains(current) ? current : FuzzyMatch(current, clips));
        workflow.PatchInputs(new[] { "DualCLIPLoader" }, "clip_name2", current =>
            clips.Contains(current) ? current : FuzzyMatch(current, clips));
        workflow.PatchInputs(new[] { "CLIPLoader" }, "clip_name", current =>
            clips.Contains(current) ? current : FuzzyMatch(current, clips));

        workflow.PatchInputs(new[] { "CLIPVisionLoader" }, "clip_name", current =>
            clipVision.Contains(current) ? current : FuzzyMatch(current, clipVision));

        workflow.PatchInputs(new[] { "LoraLoader" }, "lora_name", current =>
            loras.Contains(current) ? current : FuzzyMatch(current, loras));

        // After patching, walk the workflow once more and assemble a list
        // of any references that STILL don't resolve — those will fail at
        // submit time with cryptic node_errors. Throw early with a friendly
        // message instead.
        var missing = new List<string>();
        foreach (var (cls, key, file) in workflow.EnumerateModelReferences())
        {
            var pool = cls switch
            {
                "CheckpointLoaderSimple" => checkpoints,
                "UNETLoader" => unets,
                "VAELoader" => vaes,
                "DualCLIPLoader" or "CLIPLoader" => clips,
                "CLIPVisionLoader" => clipVision,
                "LoraLoader" => loras,
                _ => null,
            };
            if (pool is not null && !pool.Contains(file))
                missing.Add($"  · {cls}.{key} = \"{file}\"");
        }
        if (missing.Count > 0)
        {
            var hint = "Install the missing files into your ComfyUI/models/<type>/ folder, " +
                       "or pick a different workflow that uses what you have.";
            throw new ComfyUiException(
                "This workflow references models that aren't installed:\n" +
                string.Join("\n", missing) + "\n\n" + hint);
        }

        // Final safety: if the picked checkpoint is SD 1.5 and the user is
        // shooting wide, the legacy default workflow's 1024-base latent
        // crashes on low-VRAM cards. Drop to 768x432 — same heuristic as before.
        var ckpt = workflow.GetCheckpoint();
        if (!string.IsNullOrEmpty(ckpt) && IsLikelySd15(ckpt) && shot.Aspect == AspectRatio.Wide)
            workflow.SetSize(768, 432);
    }

    /// <summary>
    /// Best-effort filename matcher. Tries: exact (handled by caller),
    /// case-insensitive equality, then a "stem" comparison that strips
    /// common suffix tokens like -fp8, -fp16, _v2, _scaled, _emaonly,
    /// _pruned. Returns the chosen pool entry or null if nothing usable.
    /// </summary>
    private static string? FuzzyMatch(string requested, List<string> pool)
    {
        if (pool.Count == 0) return null;
        // Case-insensitive exact match
        var ci = pool.FirstOrDefault(p => string.Equals(p, requested, StringComparison.OrdinalIgnoreCase));
        if (ci is not null) return ci;
        // Stem match — strip extension + common suffix tokens
        var stem = Stem(requested);
        var match = pool.FirstOrDefault(p => Stem(p).Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        // Substring fallback — first pool entry that contains the stem (useful when
        // request is "flux1-dev" and pool has "flux1-dev-Q4_K_S.gguf")
        match = pool.FirstOrDefault(p => p.Contains(stem, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    private static string Stem(string fileName)
    {
        var noExt = Path.GetFileNameWithoutExtension(fileName);
        // Strip suffix tokens that vary across quantizations / repacks.
        string[] strip = { "-fp8", "-fp16", "-bf16", "_fp8", "_fp16", "_bf16",
                           "_e4m3fn", "_scaled", "_emaonly", "_pruned",
                           "-Q4_K_S", "-Q5_K_S", "-Q8_0", "-fp8_e4m3fn" };
        foreach (var s in strip)
            noExt = noExt.Replace(s, "", StringComparison.OrdinalIgnoreCase);
        return noExt;
    }
}

public sealed class GenerationProgressEventArgs : EventArgs
{
    public string PromptId { get; }
    public string ShotId { get; }
    public ShotStatus Status { get; }
    public double Progress { get; }
    public string? Error { get; }
    public string? MediaPath { get; }

    public GenerationProgressEventArgs(string promptId, string shotId, ShotStatus status, double progress, string? error, string? mediaPath = null)
    {
        PromptId = promptId;
        ShotId = shotId;
        Status = status;
        Progress = progress;
        Error = error;
        MediaPath = mediaPath;
    }
}
