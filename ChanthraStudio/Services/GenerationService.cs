using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using ChanthraStudio.Services.Providers.ComfyUI;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// Orchestrates a single generation job end-to-end:
///   1. build the workflow from the shot's params
///   2. POST to ComfyUI, get prompt_id
///   3. attach to /ws and stream progress → fire <see cref="ProgressChanged"/>
///   4. on "executed", pull the output filenames from /history
///   5. download via /view, persist to media folder, insert clip row in DB
///
/// All public members are thread-safe — Background tasks dispatch the
/// ProgressChanged event back to the UI thread via the Dispatcher captured
/// at construction.
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

        using var client = new ComfyUiClient(url);

        var probe = await client.ProbeAsync(ct);
        if (!probe.Ok)
            throw new ComfyUiException($"ComfyUI not reachable at {url} — {probe.Status}");

        var workflow = Workflow.LoadDefault()
            .SetPositivePrompt(shot.Prompt)
            .SetSeed(shot.Seed.A)
            .SetSize(shot.Aspect == AspectRatio.Wide ? 1024 : 768,
                     shot.Aspect == AspectRatio.Wide ? 576 : 768)
            .SetFilenamePrefix($"chanthra/shot{shot.Number}");

        var promptId = await client.SubmitPromptAsync(workflow.Nodes, ct);

        // Persist a generation_jobs row immediately so the queue UI can find it.
        WriteJobRow(shot.Id, promptId, "queued");

        // Spin up the listener task — its lifetime tied to a CTS we own.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[promptId] = cts;
        _ = Task.Run(() => RunListenerAsync(shot, promptId, cts.Token));

        return promptId;
    }

    public async Task CancelAsync(string promptId)
    {
        if (_running.TryRemove(promptId, out var cts))
            cts.Cancel();
        var url = _ctx.Settings.ComfyUiUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        using var client = new ComfyUiClient(url);
        await client.InterruptAsync();
    }

    private async Task RunListenerAsync(Shot shot, string promptId, CancellationToken ct)
    {
        var url = _ctx.Settings.ComfyUiUrl;
        try
        {
            using var client = new ComfyUiClient(url);

            // Stream progress until we see "executed" for our prompt id.
            await foreach (var e in client.StreamProgressAsync(ct))
            {
                if (e.Type == "progress" && e.ProgressFraction is double frac)
                {
                    Raise(promptId, shot.Id, ShotStatus.Generating, frac * 100, null);
                }
                else if (e.Type == "executing")
                {
                    Raise(promptId, shot.Id, ShotStatus.Generating, shot.Progress, null);
                }
                else if (e.Type == "executed" && e.PromptId == promptId)
                {
                    break;
                }
                else if (e.Type == "execution_error")
                {
                    var msg = e.Data?["exception_message"]?.GetValue<string>() ?? "execution error";
                    WriteJobUpdate(promptId, "error", msg);
                    Raise(promptId, shot.Id, ShotStatus.Error, 0, msg);
                    return;
                }
            }

            // Fetch history to learn the output file names.
            var history = await client.GetHistoryAsync(promptId, ct);
            if (history is null)
            {
                WriteJobUpdate(promptId, "error", "history empty");
                Raise(promptId, shot.Id, ShotStatus.Error, 0, "ComfyUI returned no history");
                return;
            }

            // Save each output file to the media folder + insert clip rows.
            string? primaryPath = null;
            foreach (var output in Workflow.ExtractOutputs(history))
            {
                var safeName = SafeFilename(shot.Id + "_" + Path.GetFileName(output.Filename));
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                await client.DownloadFileAsync(output.Filename, output.Subfolder, output.Type, dest, ct);
                primaryPath ??= dest;
                WriteClipRow(shot.Id, dest, output.Kind);
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
            // DB write failures are non-fatal for the in-memory job — log later when we add ILogger.
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
