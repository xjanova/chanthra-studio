using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services.Providers.ComfyUI;

namespace ChanthraStudio.Services.Providers.Music;

/// <summary>
/// Music generation on a ComfyUI server — the local one, or a rented card via
/// the same auth proxy the video route uses.
///
/// <b>Why this exists.</b> Every other way to make music in this app bills per
/// generation through Replicate. ACE-Step is Apache-2.0, runs in ComfyUI
/// natively, and its weights are a single 7 GB checkpoint that an 8 GB card can
/// hold — so a soundtrack costs either nothing (local GPU) or the same rented
/// minutes as a render.
///
/// The rented case is not a second implementation: <see cref="MusicRequest.ServerUrl"/>
/// and <see cref="MusicRequest.AuthToken"/> point at whichever ComfyUI is
/// meant to do the work, and <see cref="VoiceService"/> decides which.
/// </summary>
public class ComfyUiMusicProvider : IMusicProvider
{
    public virtual string Id => "comfyui-music";
    public virtual string DisplayName => "ComfyUI · ACE-Step (local GPU)";
    public string ApiKeyHint => "Server URL in Settings · no key needed";
    public ProviderKind Kind => ProviderKind.Music;
    public bool RequiresApiKey => false;

    /// <summary>Bundled graph used when the request names none.</summary>
    public const string DefaultWorkflow = "ace_step_text2music";

    /// <summary>
    /// How long to wait for one song. ACE-Step at 50 steps is a couple of
    /// minutes on a modern card and considerably longer on a small one; the
    /// cap exists so a wedged server eventually surfaces as an error rather
    /// than an await that never returns.
    /// </summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(20);

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        // apiKey is unused — this provider is addressed by URL. The probe is
        // deliberately not "always ok": a route that cannot reach its server
        // should say so in Settings rather than at submit time.
        return await Task.FromResult(new ProviderHealth(true, "local",
            "Set the ComfyUI URL in Settings. The server is probed live before each submit, "
            + "and needs ace_step_v1_3.5b.safetensors in models/checkpoints."));
    }

    public async Task<string> GenerateAsync(MusicRequest req, string destPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ServerUrl))
            throw new InvalidOperationException(
                "No ComfyUI server for music generation — set the ComfyUI URL in Settings.");

        using var client = new ComfyUiClient(req.ServerUrl, null, req.AuthToken);

        var probe = await client.ProbeAsync(ct);
        if (!probe.Ok)
            throw new ComfyUiException($"ComfyUI not reachable at {req.ServerUrl} — {probe.Status}");

        var workflow = LoadWorkflow(req.WorkflowName);

        workflow
            .SetPositivePrompt(req.Prompt)
            .SetLyrics(req.Lyrics)
            .SetAudioSeconds(req.DurationSec)
            .SetSeed(req.Seed ?? Random.Shared.Next(1, int.MaxValue))
            .SetFilenamePrefix("chanthra/music");

        await ResolveCheckpointAsync(client, workflow, ct);

        var promptId = await client.SubmitPromptAsync(workflow.Nodes, ct);

        var entry = await WaitForHistoryAsync(client, promptId, ct);

        var outputs = Workflow.ExtractOutputs(entry).ToList();
        var audio = outputs.FirstOrDefault(o => o.Kind == "audio") ?? outputs.FirstOrDefault();
        if (audio is null)
            throw new ComfyUiException(
                "The workflow finished but saved no audio. Check that it ends in a SaveAudio node.");

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Keep the server's extension when it disagrees with ours: asking for
        // .mp3 and silently writing a .flac under that name produces a file
        // every player refuses, and the cause is invisible.
        var serverExt = Path.GetExtension(audio.Filename);
        if (!string.IsNullOrEmpty(serverExt)
            && !serverExt.Equals(Path.GetExtension(destPath), StringComparison.OrdinalIgnoreCase))
        {
            destPath = Path.ChangeExtension(destPath, serverExt.TrimStart('.'));
        }

        await client.DownloadFileAsync(audio.Filename, audio.Subfolder, audio.Type, destPath, ct);
        return destPath;
    }

    // ------------------------------------------------------------- internals

    private static Workflow LoadWorkflow(string? name)
    {
        var stem = string.IsNullOrWhiteSpace(name) ? DefaultWorkflow : name!;
        if (!stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) stem += ".json";

        // User folder first so an edited copy wins over the bundled one, which
        // is the same precedence the video workflows use.
        var userPath = Path.Combine(AppPaths.Root, "workflows", stem);
        if (File.Exists(userPath)) return Workflow.LoadFromPath(userPath);

        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Workflows", stem);
        if (File.Exists(bundled)) return Workflow.LoadFromPath(bundled);

        throw new FileNotFoundException(
            $"Music workflow \"{stem}\" not found in the workflows folder or next to the .exe.", stem);
    }

    /// <summary>
    /// Point the checkpoint loader at a file the server actually has.
    ///
    /// A rented worker downloads exactly the catalog filename so this is a
    /// no-op there. On someone's local ComfyUI the same model is routinely on
    /// disk under a different name, and the failure without this is a raw
    /// validation error naming a node number.
    /// </summary>
    private static async Task ResolveCheckpointAsync(ComfyUiClient client, Workflow workflow, CancellationToken ct)
    {
        var wanted = workflow.GetCheckpoint();
        if (string.IsNullOrWhiteSpace(wanted)) return;

        List<string> available;
        try { available = await client.GetAvailableCheckpointsAsync(ct); }
        catch { return; }                       // server won't say — let it validate

        if (available.Count == 0) return;
        if (available.Contains(wanted!, StringComparer.OrdinalIgnoreCase)) return;

        // Same shape of match the video pipeline uses: compare on the stem with
        // precision suffixes stripped, so ace_step_v1_3.5b-fp16 satisfies
        // ace_step_v1_3.5b.
        var target = Normalise(wanted!);
        var hit = available.FirstOrDefault(a => Normalise(a) == target)
               ?? available.FirstOrDefault(a => Normalise(a).Contains(target, StringComparison.Ordinal));

        if (hit is not null)
        {
            workflow.SetCheckpoint(hit);
            return;
        }

        throw new ComfyUiException(
            $"This server has no checkpoint matching \"{wanted}\". "
            + $"Installed: {string.Join(", ", available.Take(8))}"
            + (available.Count > 8 ? $" (+{available.Count - 8} more)" : "")
            + ". Download ace_step_v1_3.5b.safetensors into models/checkpoints, "
            + "or render music on a rented GPU where the studio installs it for you.");
    }

    private static string Normalise(string fileName)
        => Path.GetFileNameWithoutExtension(fileName)
               .Replace("-fp8", "", StringComparison.OrdinalIgnoreCase)
               .Replace("-fp16", "", StringComparison.OrdinalIgnoreCase)
               .Replace("_scaled", "", StringComparison.OrdinalIgnoreCase)
               .Replace("_", "", StringComparison.Ordinal)
               .Replace("-", "", StringComparison.Ordinal)
               .ToLowerInvariant();

    private static async Task<System.Text.Json.Nodes.JsonObject> WaitForHistoryAsync(
        ComfyUiClient client, string promptId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + RenderTimeout;
        var failuresInARow = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            System.Text.Json.Nodes.JsonObject? history;
            try
            {
                history = await client.GetHistoryAsync(promptId, ct);
                failuresInARow = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A blip is retried; two minutes of nothing but errors is a
                // server that has gone away.
                if (++failuresInARow >= 60)
                    throw new ComfyUiException("ComfyUI stopped answering while the music rendered: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (history is not null)
            {
                // ComfyUI records a failed run in history too, with the error
                // under "status". Treating any history entry as success would
                // report a broken render as a finished one.
                var status = history["status"] as System.Text.Json.Nodes.JsonObject;
                var statusStr = status?["status_str"]?.GetValue<string>();
                if (string.Equals(statusStr, "error", StringComparison.OrdinalIgnoreCase))
                    throw new ComfyUiException(DescribeFailure(status!));

                if (history["outputs"] is System.Text.Json.Nodes.JsonObject outs && outs.Count > 0)
                    return history;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new ComfyUiException(
            $"The music render did not finish within {RenderTimeout.TotalMinutes:0} minutes. "
            + "It may still be running on the server — check ComfyUI's queue.");
    }

    private static string DescribeFailure(System.Text.Json.Nodes.JsonObject status)
    {
        // messages is an array of [type, {payload}] pairs; the useful text is
        // in the execution_error payload.
        if (status["messages"] is System.Text.Json.Nodes.JsonArray messages)
        {
            foreach (var m in messages)
            {
                if (m is not System.Text.Json.Nodes.JsonArray pair || pair.Count < 2) continue;
                if (pair[0]?.GetValue<string>() != "execution_error") continue;
                if (pair[1] is not System.Text.Json.Nodes.JsonObject payload) continue;

                var node = payload["node_type"]?.GetValue<string>() ?? "a node";
                var msg = payload["exception_message"]?.GetValue<string>() ?? "no detail";
                return $"ComfyUI failed in {node}: {msg}";
            }
        }
        return "ComfyUI reported the run as failed but gave no detail — see its console.";
    }
}

/// <summary>
/// The same generator, run on a card rented for the occasion. Separate id so
/// it gets its own row in the picker and its own key in settings; the work is
/// entirely inherited.
/// </summary>
public sealed class RentedGpuMusicProvider : ComfyUiMusicProvider
{
    public override string Id => "rentgpu-music";
    public override string DisplayName => "Rented GPU · ACE-Step (เช่าการ์ดมาทำเพลง)";
}
