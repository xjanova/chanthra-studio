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
    /// <summary>promptId → shotId for in-flight jobs. Populated when each
    /// route mints a job id, so CancelByShotAsync can target a specific
    /// shot instead of killing the whole queue (which was the old bug —
    /// the schedule fan-out in 6.6/7.8 made it user-reachable).</summary>
    private readonly ConcurrentDictionary<string, string> _promptToShot = new();

    /// <summary>promptId → the ComfyUI server that prompt was sent to. Local
    /// jobs and rented-GPU jobs run through identical code, so cancelling has
    /// to remember which box to call /interrupt on — firing it at the local
    /// URL would stop the wrong queue and leave the rented card churning on
    /// work nobody wants any more.</summary>
    private readonly ConcurrentDictionary<string, ComfyEndpoint> _promptToEndpoint = new();

    /// <summary>Where a ComfyUI job runs. <see cref="WorkerId"/> is null for
    /// the local server and set for a rented machine, which is what lets the
    /// listener bank render seconds against the right worker.</summary>
    private sealed record ComfyEndpoint(string Url, string? Token, string? WorkerId);

    public event EventHandler<GenerationProgressEventArgs>? ProgressChanged;

    public GenerationService(StudioContext ctx)
    {
        _ctx = ctx;
        _ui = System.Windows.Application.Current?.Dispatcher
              ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
    }

    /// <summary>Submit a shot for generation. Returns a job id (the ComfyUI
    /// prompt_id when routed locally, or the Replicate prediction id when
    /// routed to the cloud). Routing is driven by <c>Settings.ActiveVideo</c>:
    /// "comfyui" → local GPU pipeline, "replicate" → SubmitToReplicate.
    ///
    /// <paramref name="routeOverride"/> and <paramref name="workflowOverride"/>
    /// let the auto-scheduler honour each schedule's own route + workflow
    /// without mutating the global Settings.ActiveVideo / ActiveWorkflow.
    /// </summary>
    public Task<string> SubmitAsync(Shot shot, CancellationToken ct = default,
        string? routeOverride = null, string? workflowOverride = null)
    {
        var route = (routeOverride ?? _ctx.Settings.ActiveVideo ?? "comfyui").ToLowerInvariant();
        return route switch
        {
            "replicate" => SubmitToReplicateAsync(shot, workflowOverride, ct),
            "runway"    => SubmitToRunwayAsync(shot, ct),
            "kling"     => SubmitToKlingAsync(shot, ct),
            "seedance"  => SubmitToCloudAsync(shot, "seedance", workflowOverride, ct),
            "minimax"   => SubmitToCloudAsync(shot, "minimax", workflowOverride, ct),
            "veo"       => SubmitToCloudAsync(shot, "veo", workflowOverride, ct),
            "pika"      => SubmitToCloudAsync(shot, "pika", workflowOverride, ct),
            "fal"       => SubmitToCloudAsync(shot, "fal", workflowOverride, ct),
            "rentgpu"   => SubmitToRentedGpuAsync(shot, workflowOverride, ct),
            _           => SubmitToComfyUiAsync(shot, workflowOverride, ct),
        };
    }

    /// <summary>
    /// Rented-GPU route: rent (or reuse) a machine, wait for it to finish
    /// installing itself, then hand the job to the ordinary ComfyUI path
    /// pointed at that machine.
    ///
    /// The reuse of <see cref="SubmitToComfyUiAsync"/> is the whole design.
    /// A rented worker runs stock ComfyUI, so workflow loading, reference-image
    /// upload, model-name fuzzy resolution, progress streaming and output
    /// download all work exactly as they do locally — there is no second
    /// pipeline to keep in step with the first.
    /// </summary>
    private async Task<string> SubmitToRentedGpuAsync(Shot shot, string? workflowOverride, CancellationToken ct)
    {
        var gpu = _ctx.GpuWorkers;
        var workflowName = !string.IsNullOrEmpty(workflowOverride) ? workflowOverride : _ctx.Settings.ActiveWorkflow;

        // Pick the profile that can actually run the chosen workflow. Renting
        // a Flux box for an SDXL workflow would bill for weights the workflow
        // never loads and then fail on a missing checkpoint.
        var guard = Gpu.GpuGuardrails.Load(_ctx.Settings);
        var profile = Gpu.GpuModelCatalog.ForWorkflow(workflowName)
                      ?? Gpu.GpuModelCatalog.Find(guard.ProfileKey)
                      ?? throw new Gpu.GpuRentalException(
                          $"No GPU profile covers the workflow \"{workflowName}\". " +
                          "Pick a bundled workflow, or set a default profile in the GPU panel.");

        // Warm-up runs for tens of minutes. Report it as job progress so the
        // composer shows real stages instead of an idle spinner — a user who
        // cannot tell "downloading 16 GB" from "hung" will cancel and retry,
        // and pay for the warm-up twice.
        // Warm-up occupies the first 15% of the shot's progress bar; the
        // render itself takes the rest. No prompt id exists yet, so the shot
        // id stands in — the UI keys off ShotId alone.
        var warmup = new Progress<Gpu.GpuWarmupProgress>(p =>
            Raise(shot.Id, shot.Id, ShotStatus.Generating, Math.Clamp(p.Fraction * 15.0, 1, 15), null));

        var worker = await gpu.EnsureWorkerAsync(profile.Key, warmup, ct);
        if (string.IsNullOrWhiteSpace(worker.EndpointUrl))
            throw new Gpu.GpuRentalException(
                $"{worker.Name} is ready but published no endpoint — release it from the GPU panel and try again.");

        var endpoint = new ComfyEndpoint(worker.EndpointUrl!, worker.Token, worker.Id);
        gpu.MarkJobStarted(worker.Id);
        try
        {
            return await SubmitToComfyUiAsync(shot, workflowOverride, ct, endpoint);
        }
        catch
        {
            // Submit never got off the ground, so no listener will ever
            // release this worker. Free it here or it stays Busy — and Busy
            // workers are deliberately exempt from the idle reaper, so it
            // would bill until the lifetime cap.
            gpu.MarkJobFinished(worker.Id, 0);
            throw;
        }
    }

    /// <summary>
    /// Generic cloud-route orchestrator for providers that follow the same
    /// SubmitAndWaitAsync(VideoRequest, IProgress, ct) pattern as Runway,
    /// Pika, fal.ai. The only per-provider knob is the provider id used to
    /// look up the key + the SubmitAndWait implementation; everything else
    /// — DB rows, billing, progress raising, downloading — is identical.
    /// </summary>
    private async Task<string> SubmitToCloudAsync(Shot shot, string providerId, string? workflowOverride, CancellationToken ct)
    {
        var apiKey = _ctx.Settings[providerId];
        // Veo rides on the Gemini API, so fall back to the Gemini key rather
        // than making the user paste the same AIzaSy… key under a second slot.
        if (string.IsNullOrWhiteSpace(apiKey) && providerId == "veo")
            apiKey = _ctx.Settings["gemini"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"{providerId} API key missing — paste it in Settings → Video Providers.");

        // SubmitAndWaitAsync lives on the concrete provider class, not the
        // IVideoProvider interface. Dispatch by id.
        Func<Providers.VideoRequest, IProgress<double>, CancellationToken, Task<string>> waiter = providerId switch
        {
            "pika" => (r, p, c) => new Providers.Video.PikaVideoProvider().SubmitAndWaitAsync(r, p, c),
            "fal"  => (r, p, c) => new Providers.Video.FalVideoProvider().SubmitAndWaitAsync(r, p, c),
            "seedance" => (r, p, c) => new Providers.Video.SeedanceVideoProvider().SubmitAndWaitAsync(r, p, c),
            "minimax"  => (r, p, c) => new Providers.Video.MinimaxVideoProvider().SubmitAndWaitAsync(r, p, c),
            "veo"      => (r, p, c) => new Providers.Video.GeminiVeoVideoProvider().SubmitAndWaitAsync(r, p, c),
            _      => throw new InvalidOperationException($"No cloud route handler for {providerId}"),
        };

        var aspect = shot.Aspect switch
        {
            AspectRatio.Vertical => "9:16",
            AspectRatio.Square => "1:1",
            AspectRatio.Cinema => "21:9",
            _ => "16:9",
        };
        var slugSource = !string.IsNullOrEmpty(workflowOverride) ? workflowOverride : _ctx.Settings.ActiveWorkflow;
        var modelSlug = providerId switch
        {
            "fal" => slugSource is { Length: > 0 } s && s.Contains('/') ? s : Providers.Video.FalVideoProvider.DefaultModel,
            "seedance" => ResolveActiveModel("seedance", Providers.Video.SeedanceVideoProvider.DefaultModel),
            "minimax" => ResolveActiveModel("minimax", Providers.Video.MinimaxVideoProvider.DefaultModel),
            "veo" => ResolveActiveModel("veo", Providers.Video.GeminiVeoVideoProvider.DefaultModel),
            _ => slugSource ?? "",  // Pika doesn't take a model slug — engine choice is account-tier
        };

        var req = new Providers.VideoRequest
        {
            ApiKey = apiKey,
            Model = modelSlug,
            Prompt = PromptAugmenter.Augment(shot),
            NegativePrompt = shot.NegativePrompt,
            ReferenceImagePath = shot.ReferenceImagePath,
            SceneReferenceImagePath = shot.SceneReferenceImagePath,
            OutfitReferenceImagePath = shot.OutfitReferenceImagePath,
            Aspect = aspect,
            Seed = shot.Seed.A,
            DurationSec = shot.DurationSec,
            Hd4k = shot.Hd4k,
            Audio = shot.Audio,
        };

        var jobId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _ctx.Shots.Insert(shot);
        WriteJobRow(shot.Id, jobId, "queued");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[jobId] = cts;
        _promptToShot[jobId] = shot.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                Raise(jobId, shot.Id, ShotStatus.Generating, 1, null);
                var progress = new Progress<double>(p => Raise(jobId, shot.Id, ShotStatus.Generating, p, null));
                var outputUrl = await waiter(req, progress, cts.Token);

                var ext = Path.GetExtension(new Uri(outputUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";
                var safeName = SafeFilename($"{shot.Id}_{providerId}{ext}");
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                using (var dl = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    using var resp = await dl.GetAsync(outputUrl, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
                    await using var fs = File.Create(dest);
                    await resp.Content.CopyToAsync(fs, cts.Token);
                }

                WriteClipRow(shot.Id, dest, ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "videos" : "images");
                WriteJobUpdate(jobId, "done", null);

                try
                {
                    if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                        _ctx.Tracker.RecordSeconds(providerId, modelSlug, shot.DurationSec, "video");
                    else
                        _ctx.Tracker.RecordImages(providerId, modelSlug, 1, "image");
                }
                catch { }

                Raise(jobId, shot.Id, ShotStatus.Done, 100, null, dest);
            }
            catch (OperationCanceledException)
            {
                WriteJobUpdate(jobId, "cancelled", null);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, "cancelled");
            }
            catch (Exception ex)
            {
                WriteJobUpdate(jobId, "error", ex.Message);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, ex.Message);
            }
            finally
            {
                _running.TryRemove(jobId, out _);
                _promptToShot.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    /// <summary>
    /// Cloud route through Runway Gen-3 (image-to-video only — the shot
    /// MUST carry a reference image). Same orchestration shape as
    /// <see cref="SubmitToReplicateAsync"/>: mint a local jobId, kick off
    /// a background task that submits + polls, download the result, raise
    /// the same Done event so storyboard cards work identically.
    /// </summary>
    private async Task<string> SubmitToRunwayAsync(Shot shot, CancellationToken ct)
    {
        var apiKey = _ctx.Settings["runway"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Runway API key missing — paste your key_… token in Settings → Video Providers.");
        if (string.IsNullOrEmpty(shot.ReferenceImagePath))
            throw new InvalidOperationException(
                "Runway Gen-3 needs a reference image. Drop one in the Composer first.");

        var provider = new Providers.Video.RunwayVideoProvider();
        var aspect = shot.Aspect switch
        {
            AspectRatio.Vertical => "9:16",
            AspectRatio.Square => "1:1",
            AspectRatio.Cinema => "21:9",
            _ => "16:9",
        };
        var req = new Providers.VideoRequest
        {
            ApiKey = apiKey,
            Model = Providers.Video.RunwayVideoProvider.DefaultModel,
            Prompt = PromptAugmenter.Augment(shot),
            ReferenceImagePath = shot.ReferenceImagePath,
            SceneReferenceImagePath = shot.SceneReferenceImagePath,
            OutfitReferenceImagePath = shot.OutfitReferenceImagePath,
            Aspect = aspect,
            Seed = shot.Seed.A,
            DurationSec = shot.DurationSec,
        };

        var jobId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _ctx.Shots.Insert(shot);
        WriteJobRow(shot.Id, jobId, "queued");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[jobId] = cts;
        _promptToShot[jobId] = shot.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                Raise(jobId, shot.Id, ShotStatus.Generating, 1, null);
                var progress = new Progress<double>(p => Raise(jobId, shot.Id, ShotStatus.Generating, p, null));
                var outputUrl = await provider.SubmitAndWaitAsync(req, progress, cts.Token);

                // Runway returns .mp4 URLs valid for ~24 hours — pull it
                // local so Library + Render film treat it like every other clip.
                var safeName = SafeFilename($"{shot.Id}_runway.mp4");
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                using (var dl = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    using var resp = await dl.GetAsync(outputUrl, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
                    await using var fs = File.Create(dest);
                    await resp.Content.CopyToAsync(fs, cts.Token);
                }

                WriteClipRow(shot.Id, dest, "videos");
                WriteJobUpdate(jobId, "done", null);

                // Bill the call. Runway is per-second; PromptAugmenter doesn't
                // map runway to a duration param itself, but UsageTracker
                // picks up the per-second rate from ProviderCatalog if the
                // user has runway pricing configured.
                try { _ctx.Tracker.RecordSeconds("runway", req.Model, req.DurationSec, "video"); }
                catch { }

                Raise(jobId, shot.Id, ShotStatus.Done, 100, null, dest);
            }
            catch (OperationCanceledException)
            {
                WriteJobUpdate(jobId, "cancelled", null);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, "cancelled");
            }
            catch (Exception ex)
            {
                WriteJobUpdate(jobId, "error", ex.Message);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, ex.Message);
            }
            finally
            {
                _running.TryRemove(jobId, out _);
                _promptToShot.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    /// <summary>
    /// Cloud route through Kling AI (Kuaishou). Unlike Runway this supports
    /// BOTH text-to-video and image-to-video — the KlingVideoProvider picks
    /// the endpoint based on whether the shot carries a reference image. Same
    /// orchestration shape as the other cloud routes: mint a jobId, submit +
    /// poll in the background, download the result, raise the shared Done event.
    /// </summary>
    private async Task<string> SubmitToKlingAsync(Shot shot, CancellationToken ct)
    {
        var apiKey = _ctx.Settings["kling"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Kling keys missing — paste your AccessKey:SecretKey in Settings → Video Providers.");

        var provider = new Providers.Video.KlingVideoProvider();
        var aspect = shot.Aspect switch
        {
            AspectRatio.Vertical => "9:16",
            AspectRatio.Square => "1:1",
            AspectRatio.Cinema => "21:9",
            _ => "16:9",
        };
        // The user's picked Kling model (Settings chips persist it under
        // activeModel:kling); empty → provider default (kling-v1-6).
        var model = _ctx.Settings.GetSetting("activeModel:kling");
        var req = new Providers.VideoRequest
        {
            ApiKey = apiKey,
            Model = model,
            Prompt = PromptAugmenter.Augment(shot),
            NegativePrompt = shot.NegativePrompt,
            ReferenceImagePath = shot.ReferenceImagePath,
            SceneReferenceImagePath = shot.SceneReferenceImagePath,
            OutfitReferenceImagePath = shot.OutfitReferenceImagePath,
            Aspect = aspect,
            Seed = shot.Seed.A,
            DurationSec = shot.DurationSec,
            Hd4k = shot.Hd4k,
            Audio = shot.Audio,
            CamMode = shot.Cam.ToString().ToLowerInvariant(),
        };

        var jobId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _ctx.Shots.Insert(shot);
        WriteJobRow(shot.Id, jobId, "queued");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[jobId] = cts;
        _promptToShot[jobId] = shot.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                Raise(jobId, shot.Id, ShotStatus.Generating, 1, null);
                var progress = new Progress<double>(p => Raise(jobId, shot.Id, ShotStatus.Generating, p, null));
                var outputUrl = await provider.SubmitAndWaitAsync(req, progress, cts.Token);

                var safeName = SafeFilename($"{shot.Id}_kling.mp4");
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                using (var dl = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    using var resp = await dl.GetAsync(outputUrl, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
                    await using var fs = File.Create(dest);
                    await resp.Content.CopyToAsync(fs, cts.Token);
                }

                WriteClipRow(shot.Id, dest, "videos");
                WriteJobUpdate(jobId, "done", null);
                try
                {
                    _ctx.Tracker.RecordSeconds("kling",
                        string.IsNullOrEmpty(model) ? Providers.Video.KlingVideoProvider.DefaultModel : model,
                        shot.DurationSec, "video");
                }
                catch { }

                Raise(jobId, shot.Id, ShotStatus.Done, 100, null, dest);
            }
            catch (OperationCanceledException)
            {
                WriteJobUpdate(jobId, "cancelled", null);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, "cancelled");
            }
            catch (Exception ex)
            {
                WriteJobUpdate(jobId, "error", ex.Message);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, ex.Message);
            }
            finally
            {
                _running.TryRemove(jobId, out _);
                _promptToShot.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    /// <param name="endpoint">
    /// Which ComfyUI to talk to. Null means the local server configured in
    /// Settings; a rented worker passes its own URL and bearer token here.
    /// </param>
    private async Task<string> SubmitToComfyUiAsync(
        Shot shot, string? workflowOverride, CancellationToken ct, ComfyEndpoint? endpoint = null)
    {
        // A rented worker brings its own URL. Otherwise this is the local
        // route, which means the studio's own engine — started here if it is
        // installed and merely stopped, so the first render of a session does
        // not fail on a server the user was never told to run.
        var url = endpoint?.Url ?? await _ctx.ComfyEngine.ResolveUrlForRenderAsync(ct);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "ยังไม่มีเอนจิน ComfyUI — ติดตั้งเอนจินของสตูดิโอในหน้า ComfyUI "
                + "หรือใส่ URL ของเซิร์ฟเวอร์ที่มีอยู่แล้วในหน้า Settings");

        // NOTE: client lifecycle is owned by the listener task — do NOT use a
        // `using` here. Disposing it kills the listener's WebSocket and HTTP
        // pipeline mid-job.
        var client = new ComfyUiClient(url, clientId: null, authToken: endpoint?.Token);

        try
        {
            var probe = await client.ProbeAsync(ct);
            if (!probe.Ok)
            {
                client.Dispose();
                throw new ComfyUiException(endpoint is null
                    ? $"ComfyUI not reachable at {url} — {probe.Status}"
                    : $"The rented worker stopped answering — {probe.Status}");
            }

            // Load the user's active workflow from the repository — falls
            // back to the bundled default if the saved name has been removed
            // from disk since last save.
            var workflowName = !string.IsNullOrEmpty(workflowOverride) ? workflowOverride : _ctx.Settings.ActiveWorkflow;
            var descriptor = _ctx.Workflows.FindByName(workflowName)
                          ?? _ctx.Workflows.Default();
            var workflow = descriptor is not null
                ? Workflow.LoadFromPath(descriptor.Path)
                : Workflow.LoadDefault();

            // Aspect-aware resolution. HD 4K bumps the latent base ~+50% so
            // a single-pass render benefits from the toggle without us having
            // to inject an Upscale node into arbitrary user workflows.
            var (baseW, baseH) = shot.Aspect switch
            {
                AspectRatio.Wide     => (1024, 576),
                AspectRatio.Vertical => (576, 1024),
                AspectRatio.Cinema   => (1280, 544),
                _                    => (768, 768),
            };
            if (shot.Hd4k)
            {
                baseW = (int)(baseW * 1.5);
                baseH = (int)(baseH * 1.5);
            }

            // Fold camera/motion/style/HD into the prompt so the controls
            // the user can see in the composer actually shape the output.
            // Without this, those sliders/toggles were UI lies.
            var augmentedPrompt = PromptAugmenter.Augment(shot);

            workflow
                .SetPositivePrompt(augmentedPrompt)
                .SetNegativePrompt(shot.NegativePrompt)
                .SetFilenamePrefix($"chanthra/shot{shot.Number}");

            var comfy = ComfyRenderSettings.Load(_ctx.Settings);

            // HD 4K bumps quality: more steps + slightly higher CFG so the extra
            // pixels carry detail instead of just upscaling noise. Skipped once
            // the user has taken the sampler over — silently overriding their 12
            // steps with 36 because a different toggle is on is exactly the
            // behaviour the override switches exist to end.
            if (shot.Hd4k && !comfy.OverrideSampler)
                workflow.SetSteps(36).SetCfg(7.5);

            // Seed, size, sampler, LoRAs, clip skip and video length all land
            // here, and the report says which of them the workflow could
            // actually accept.
            var seed = comfy.NextSeed(shot.Seed.A);
            var patched = workflow.Apply(comfy, seed, baseW, baseH);
            comfy.LastSeed = seed;
            comfy.PersistLastSeed(_ctx.Settings);

            foreach (var line in patched.Where(l => !l.Applied))
                ActivityLog.Warn("comfy", $"{line.Field} ({line.Value}) not applied — {line.Note}");

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
            // Persist the shot's full composer state before the job row so
            // the FK from generation_jobs.shot_id resolves cleanly and so a
            // relaunch can rebuild the storyboard with prompts intact.
            _ctx.Shots.Insert(shot);
            WriteJobRow(shot.Id, promptId, "queued");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running[promptId] = cts;
            _promptToShot[promptId] = shot.Id;
            _promptToEndpoint[promptId] = endpoint ?? new ComfyEndpoint(url, null, null);

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

    /// <summary>
    /// Submit a graph the user built by hand in the Node Flow editor.
    ///
    /// <b>Why this goes through here rather than posting straight to /prompt.</b>
    /// The editor used to do exactly that: submit, print a prompt id, and tell
    /// the user to go and look in ComfyUI's output folder. So a graph you had
    /// just built produced nothing you could see inside the studio — no
    /// progress, no thumbnail, no Library row, no way to cancel. Routing it
    /// through the same listener as every other render means the outputs come
    /// back to the same place as everything else, and the Queue can stop it.
    ///
    /// Nothing is patched into the graph — no prompt augmentation, no aspect
    /// sizing, no render-settings override. The point of the node editor is
    /// that the user controls every input, so this submits exactly what is on
    /// the canvas.
    /// </summary>
    /// <param name="nodes">API-format graph, already converted.</param>
    /// <param name="label">What to call it in the Library and Queue.</param>
    public async Task<string> SubmitGraphAsync(JsonObject nodes, string label, CancellationToken ct = default)
    {
        var url = await _ctx.ComfyEngine.ResolveUrlForRenderAsync(ct);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "ยังไม่มีเอนจิน ComfyUI — ติดตั้งเอนจินของสตูดิโอในหน้า ComfyUI ก่อน");

        // Owned by the listener from the moment it starts; see the note in
        // SubmitToComfyUiAsync about not disposing this early.
        var client = new ComfyUiClient(url);
        try
        {
            var probe = await client.ProbeAsync(ct);
            if (!probe.Ok)
            {
                client.Dispose();
                throw new ComfyUiException($"ComfyUI not reachable at {url} — {probe.Status}");
            }

            var shot = new Shot
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Number = DateTime.Now.ToString("HHmmss"),
                Title = string.IsNullOrWhiteSpace(label) ? "Node graph" : label,
                Prompt = $"node flow · {label}",
                Description = "Built in the Node Flow editor.",
                Status = ShotStatus.Generating,
            };

            var promptId = await client.SubmitPromptAsync(nodes, ct);
            _ctx.Shots.Insert(shot);
            WriteJobRow(shot.Id, promptId, "queued");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running[promptId] = cts;
            _promptToShot[promptId] = shot.Id;
            _promptToEndpoint[promptId] = new ComfyEndpoint(url, null, null);

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

    /// <summary>
    /// Cancel every running job whose <see cref="Shot.Id"/> matches. The
    /// composer's per-shot cancel button drives this — auto-schedules
    /// (6.6/7.8) can fan out multiple concurrent jobs, so killing only
    /// the targeted shot is critical.
    ///
    /// Walks <see cref="_promptToShot"/> to find the matching prompt id(s),
    /// cancels their <see cref="CancellationTokenSource"/>, and (for the
    /// ComfyUI route) fires /interrupt only when a ComfyUI job was
    /// actually involved — interrupting always-on would stop unrelated
    /// queue work the user didn't ask to cancel.
    /// </summary>
    /// <summary>
    /// Cancel EVERY running generation. Used by the Composer's
    /// "Cancel all" button when a fan-out batch is going sideways and
    /// the user wants to bail out fast rather than click each shot's ×
    /// button. (T56 · 7.21)
    /// </summary>
    public async Task CancelAllAsync()
    {
        var touched = new List<ComfyEndpoint>();
        foreach (var (promptId, _) in _promptToShot.ToArray())
        {
            if (_running.TryRemove(promptId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            _promptToShot.TryRemove(promptId, out _);
            if (_promptToEndpoint.TryGetValue(promptId, out var ep)) touched.Add(ep);
        }
        await InterruptEndpointsAsync(touched);
    }

    /// <summary>
    /// Fire /interrupt at each distinct server we actually sent work to.
    /// Blanket-interrupting the local server (the old behaviour) both missed
    /// rented workers and stopped unrelated local queue items.
    /// </summary>
    private async Task InterruptEndpointsAsync(IEnumerable<ComfyEndpoint> endpoints)
    {
        foreach (var ep in endpoints.DistinctBy(e => e.Url))
        {
            if (string.IsNullOrWhiteSpace(ep.Url)) continue;
            try
            {
                using var c = new ComfyUiClient(ep.Url, clientId: null, authToken: ep.Token);
                await c.InterruptAsync();
            }
            catch { /* best effort — the CTS has already stopped our side */ }
        }
    }

    /// <summary>Snapshot count of currently-running prompt ids. Drives the
    /// "Cancel all" button's enable state and a small "N running" badge.</summary>
    public int RunningCount => _running.Count;

    public async Task CancelByShotAsync(string shotId)
    {
        var touched = new List<ComfyEndpoint>();
        foreach (var (promptId, sid) in _promptToShot.ToArray())
        {
            if (sid != shotId) continue;
            if (_running.TryRemove(promptId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            _promptToShot.TryRemove(promptId, out _);
            // Only ComfyUI-routed prompts have an endpoint recorded, so this
            // replaces the old "does it parse as a Guid?" heuristic with the
            // actual fact of where the job went.
            if (_promptToEndpoint.TryGetValue(promptId, out var ep)) touched.Add(ep);
        }
        await InterruptEndpointsAsync(touched);
    }

    public async Task CancelAsync(string promptId)
    {
        if (_running.TryRemove(promptId, out var cts))
            cts.Cancel();
        // Replicate cancellation is handled by the linked CTS above — the
        // poll loop checks ct on every tick. ComfyUI also gets a hard
        // /interrupt so the in-flight prompt stops chewing GPU — on the
        // machine it was actually sent to, which for a rented worker is not
        // the local server.
        if (_promptToEndpoint.TryGetValue(promptId, out var ep))
            await InterruptEndpointsAsync(new[] { ep });
    }

    /// <summary>
    /// Cloud route: submit the shot to Replicate, poll the prediction id
    /// every ~3s, download the resulting video/image into media/, then
    /// raise the same Done event that ComfyUI submissions raise. This way
    /// the GenerateView storyboard cards work identically for both routes.
    /// </summary>
    private async Task<string> SubmitToReplicateAsync(Shot shot, string? workflowOverride, CancellationToken ct)
    {
        var apiKey = _ctx.Settings["replicate"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Replicate API key missing — paste your r8_… token in Settings → Video Providers.");

        var provider = new Providers.Video.ReplicateVideoProvider();
        var aspect = shot.Aspect switch
        {
            AspectRatio.Vertical => "9:16",
            AspectRatio.Square => "1:1",
            AspectRatio.Cinema => "21:9",
            _ => "16:9",
        };
        var slugSource = !string.IsNullOrEmpty(workflowOverride) ? workflowOverride : _ctx.Settings.ActiveWorkflow;
        var req = new Providers.VideoRequest
        {
            ApiKey = apiKey,
            // Settings.ActiveWorkflow (or the schedule's per-row override)
            // doubles as the Replicate model slug when routed cloud-side.
            // Falls back to the provider's default (flux-schnell) for users
            // who haven't picked one yet.
            Model = LooksLikeReplicateSlug(slugSource)
                    ? slugSource
                    : Providers.Video.ReplicateVideoProvider.DefaultModel,
            // Camera / motion / style / HD descriptors get folded into the
            // prompt here too, so Replicate models honour the composer just
            // like ComfyUI does.
            Prompt = PromptAugmenter.Augment(shot),
            NegativePrompt = shot.NegativePrompt,
            ReferenceImagePath = shot.ReferenceImagePath,
            SceneReferenceImagePath = shot.SceneReferenceImagePath,
            OutfitReferenceImagePath = shot.OutfitReferenceImagePath,
            Aspect = aspect,
            Seed = shot.Seed.A,
            DurationSec = shot.DurationSec,
            Motion = shot.Motion,
            Hd4k = shot.Hd4k,
            Audio = shot.Audio,
            CamMode = shot.Cam.ToString().ToLowerInvariant(),
        };

        // Replicate ids are unique enough to use as our internal jobId.
        // We mint a temporary one until the first poll returns the real one,
        // so the storyboard card has something to bind progress against.
        var jobId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _ctx.Shots.Insert(shot);
        WriteJobRow(shot.Id, jobId, "queued");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[jobId] = cts;
        _promptToShot[jobId] = shot.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                Raise(jobId, shot.Id, ShotStatus.Generating, 1, null);
                var progress = new Progress<double>(p => Raise(jobId, shot.Id, ShotStatus.Generating, p, null));

                var outputUrl = await provider.SubmitAndWaitAsync(req, progress, cts.Token);

                // Replicate returns a public CDN URL — pull it down into
                // media/ so the rest of the app (Library, Render film) treats
                // it the same as a ComfyUI output.
                var ext = Path.GetExtension(new Uri(outputUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";
                var safeName = SafeFilename($"{shot.Id}_replicate{ext}");
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                using (var dl = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    using var resp = await dl.GetAsync(outputUrl, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
                    await using var fs = File.Create(dest);
                    await resp.Content.CopyToAsync(fs, cts.Token);
                }

                WriteClipRow(shot.Id, dest, ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "videos" : "images");
                WriteJobUpdate(jobId, "done", null);

                // Bill the cloud call. Video models price per second of
                // duration; image models price per image. The catalog
                // entry's UsdPerSecond OR UsdPerImage drives the choice
                // — both fall back to 0 for free / unknown slugs, so
                // we can record either way without overcounting.
                try
                {
                    if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                        _ctx.Tracker.RecordSeconds("replicate", req.Model, shot.DurationSec, "video");
                    else
                        _ctx.Tracker.RecordImages("replicate", req.Model, 1, "image");
                }
                catch { }

                Raise(jobId, shot.Id, ShotStatus.Done, 100, null, dest);
            }
            catch (OperationCanceledException)
            {
                WriteJobUpdate(jobId, "cancelled", null);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, "cancelled");
            }
            catch (Exception ex)
            {
                WriteJobUpdate(jobId, "error", ex.Message);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, ex.Message);
            }
            finally
            {
                _running.TryRemove(jobId, out _);
                _promptToShot.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    /// <summary>Resolve the user's picked model for a cloud provider from the
    /// Settings model chips (<c>activeModel:&lt;id&gt;</c>), falling back to the
    /// provider's default so both the request and billing carry a real model.</summary>
    private string ResolveActiveModel(string providerId, string fallback)
    {
        var m = _ctx.Settings.GetSetting($"activeModel:{providerId}");
        return string.IsNullOrWhiteSpace(m) ? fallback : m;
    }

    /// <summary>Heuristic: a Replicate model slug looks like "owner/name".
    /// Anything else (bare workflow name, JSON file) fails this check and we
    /// fall through to the provider's default model.</summary>
    private static bool LooksLikeReplicateSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var slash = s.IndexOf('/');
        return slash > 0 && slash < s.Length - 1 && !s.Contains(' ');
    }

    private async Task RunListenerAsync(Shot shot, string promptId, ComfyUiClient client, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
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
            _promptToShot.TryRemove(promptId, out _);
            _promptToEndpoint.TryRemove(promptId, out var ep);

            // Release the rented worker on EVERY exit path — success, error,
            // and cancellation alike. A worker left marked Busy is exempt
            // from the idle reaper by design, so missing this would keep a
            // card billing until the lifetime cap hours later.
            if (ep?.WorkerId is { } workerId)
            {
                try
                {
                    _ctx.GpuWorkers.MarkJobFinished(
                        workerId, (DateTime.UtcNow - startedAt).TotalSeconds);
                }
                catch { /* accounting must never mask the job's own outcome */ }
            }
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

    /// <summary>WebSocket reader that only updates progress for our prompt id.
    /// All three branches gate on <c>e.PromptId == promptId</c> — the
    /// "progress" branch used to skip the filter, which meant a second
    /// concurrent job's progress would overwrite the first one's UI state
    /// (the schedule fan-out in 6.6/7.8 makes this reachable). For
    /// step-progress events that ComfyUI emits without a prompt_id (k-sampler
    /// step counter) we allow the update through when only one job is in
    /// flight, but skip it when multiple are running to avoid cross-talk.</summary>
    private async Task StreamWsProgressAsync(ComfyUiClient client, Shot shot, string promptId, CancellationToken ct)
    {
        try
        {
            await foreach (var e in client.StreamProgressAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                if (e.Type == "progress" && e.ProgressFraction is double frac)
                {
                    // Step counter — only update OUR shot if this event
                    // belongs to it OR no prompt id is attached AND we're
                    // the only job running (so the bar still moves on the
                    // common single-shot case).
                    var ours = e.PromptId == promptId || (e.PromptId is null && _running.Count == 1);
                    if (ours) Raise(promptId, shot.Id, ShotStatus.Generating, frac * 100, null);
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
        catch (Exception ex)
        {
            // WS disconnects are non-fatal — the poller still drives completion.
            ActivityLog.Warn("generation", $"WS stream ended for {promptId[..Math.Min(8, promptId.Length)]}: {ex.Message}");
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
        // Mirror status into the shots table so relaunch sees Done/Error
        // accurately instead of a stuck "Queue" badge from submit-time.
        _ctx.Shots.UpdateStatus(shotId, status, progress, mediaPath, mediaPath);

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
        catch (Exception ex)
        {
            // DB write failures are non-fatal for the in-memory job — but
            // log them so a user reporting "Library is empty" can be told
            // to check %APPDATA%/ChanthraStudio/logs.
            ActivityLog.Error("generation", $"WriteJobRow {promptId[..System.Math.Min(8, promptId.Length)]}", ex);
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
        catch (Exception ex)
        {
            ActivityLog.Error("generation", $"WriteJobUpdate {promptId[..System.Math.Min(8, promptId.Length)]} → {status}", ex);
        }
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
        catch (Exception ex)
        {
            ActivityLog.Error("generation", $"WriteClipRow {System.IO.Path.GetFileName(filePath)}", ex);
        }
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
