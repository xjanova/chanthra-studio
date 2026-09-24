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

    /// <summary>
    /// Something about a submitted shot the user should know although the
    /// render goes ahead — an attached input the route cannot use. Raised on
    /// the UI thread.
    /// </summary>
    public event EventHandler<string>? Notice;

    private void RaiseNotice(string message)
    {
        ActivityLog.Warn("generation", message);
        if (_ui.CheckAccess()) Notice?.Invoke(this, message);
        else _ui.BeginInvoke(new Action(() => Notice?.Invoke(this, message)));
    }

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

        // Cancelling during the warm-up has to reach it too: there is no
        // prompt id yet, so the shot id stands in until the render starts —
        // CancelByShotAsync finds it the same way.
        using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[shot.Id] = warmCts;
        _promptToShot[shot.Id] = shot.Id;
        Gpu.GpuWorker worker;
        try
        {
            worker = await gpu.EnsureWorkerAsync(profile.Key, warmup, warmCts.Token);
        }
        finally
        {
            _running.TryRemove(shot.Id, out _);
            _promptToShot.TryRemove(shot.Id, out _);
        }
        ct.ThrowIfCancellationRequested();
        warmCts.Token.ThrowIfCancellationRequested();
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

        return StartCloudJob(shot, providerId, modelSlug, ".mp4",
            (progress, token) => waiter(req, progress, token), ct);
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

        // Runway returns .mp4 URLs valid for ~24 hours — pulled local so
        // Library and Render film treat it like every other clip.
        return StartCloudJob(shot, "runway", req.Model, ".mp4",
            (progress, token) => provider.SubmitAndWaitAsync(req, progress, token), ct);
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

        var billedModel = string.IsNullOrEmpty(model) ? Providers.Video.KlingVideoProvider.DefaultModel : model;
        return StartCloudJob(shot, "kling", billedModel, ".mp4",
            (progress, token) => provider.SubmitAndWaitAsync(req, progress, token), ct);
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
            var descriptor = _ctx.Workflows.FindByName(workflowName);
            // A named workflow that has gone missing is an error, not a cue to
            // quietly render the stock text-to-image graph instead — a video
            // shot came back as a still with no explanation.
            if (descriptor is null && !string.IsNullOrWhiteSpace(workflowName))
            {
                client.Dispose();
                throw new ComfyUiException(
                    $"ไม่พบเวิร์กโฟลว์ \"{workflowName}\" — ไฟล์อาจถูกลบหรือเปลี่ยนชื่อ เลือกเวิร์กโฟลว์ใหม่ในหน้า Generate");
            }
            descriptor ??= _ctx.Workflows.Default();
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

            // Video models are trained at one resolution, and their cost is
            // every pixel of every frame. Sizing a WAN 480p graph from the
            // stills table (1024×576, or 1536×864 with HD on) asked an 8 GB card
            // for three to six times the memory the model was built for. Keep
            // the graph's own pixel budget and only turn it to the shot's aspect.
            var isVideo = workflow.ProducesVideo();
            if (isVideo && workflow.LatentSize() is { } native)
                (baseW, baseH) = FitAspect(native.Width * native.Height, shot.Aspect);

            // An animated .webp is a single still to every player in the
            // studio, so a finished video render used to arrive as one frame.
            // Save through the engine's own H.264 writer instead when it has one.
            if (workflow.HasAnimatedImageSaver()
                && await client.HasNodeAsync("SaveVideo", ct)
                && await client.HasNodeAsync("CreateVideo", ct))
                workflow.NormalizeVideoOutputs();

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
            // Only on classic CFG graphs: Flux, SDXL-Lightning and Hunyuan run at
            // CFG 1–2 by design, and 7.5 burns them; a video graph at 36 steps
            // is an hour of GPU time the user did not ask for.
            var sampler = workflow.InputsOf("KSampler");
            var classicCfg = sampler?["cfg"] is JsonValue cfgNode && cfgNode.TryGetValue<double>(out var cfgValue) && cfgValue >= 3;
            if (shot.Hd4k && !comfy.OverrideSampler && !isVideo && classicCfg)
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
                    "workflow นี้ต้องใช้ภาพอ้างอิง (มีโหนด LoadImage) — ลากภาพมาวาง หรือกดเลือกภาพในแผง Generate ก่อน");
            }
            else if (!string.IsNullOrEmpty(shot.ReferenceImagePath))
            {
                // Rendering on without it is right, doing it silently is not:
                // the user attached a face expecting it to be kept.
                RaiseNotice($"workflow \"{(string.IsNullOrEmpty(workflowName) ? "ที่ใช้อยู่" : workflowName)}\" ไม่มีช่องรับภาพ (LoadImage) — ภาพอ้างอิงที่แนบไว้จะไม่ถูกใช้ในช็อตนี้ เลือก workflow แบบ image-to-video ถ้าต้องการล็อกหน้าตัวละคร");
            }

            // Auto-substitute model names in EVERY loader node (checkpoint,
            // unet, vae, clip, clip-vision, lora) so a workflow that bakes
            // in "flux1-dev.safetensors" still runs on a server that only
            // has "flux1-dev-fp8.safetensors". We use FuzzyResolve below.
            await AutoFixModelReferencesAsync(client, workflow, shot, ct);

            // Persist the shot BEFORE submitting: if the write failed after
            // the prompt was queued, the render ran with nobody listening for
            // it — and on a rented card, billed for output nobody collected.
            _ctx.Shots.Insert(shot);
            var promptId = await client.SubmitPromptAsync(workflow.Nodes, ct);
            WriteJobRow(shot.Id, promptId, "queued", endpoint?.WorkerId is null ? "comfyui" : "rentgpu");

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

            _ctx.Shots.Insert(shot);
            var promptId = await client.SubmitPromptAsync(nodes, ct);
            WriteJobRow(shot.Id, promptId, "queued", "comfyui");

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
        var touched = new List<(ComfyEndpoint, string)>();
        foreach (var (promptId, _) in _promptToShot.ToArray())
        {
            // Read where it went BEFORE cancelling: the listener's cleanup
            // removes that entry, and losing the race skipped the server-side
            // cancel so the job kept rendering.
            if (_promptToEndpoint.TryGetValue(promptId, out var ep)) touched.Add((ep, promptId));
            if (_running.TryRemove(promptId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            _promptToShot.TryRemove(promptId, out _);
        }
        await CancelOnServersAsync(touched);
    }

    /// <summary>
    /// Cancel each prompt on the server it was actually sent to — the local
    /// engine or a rented worker — by prompt id, so a job waiting in the queue
    /// is removed rather than left to run later, and other people's work on
    /// the same server is not interrupted in its place.
    /// </summary>
    private static async Task CancelOnServersAsync(IEnumerable<(ComfyEndpoint Endpoint, string PromptId)> jobs)
    {
        // One call per server, all servers at once, with a short timeout: done
        // one prompt at a time on the default two-minute client, "cancel all"
        // against a rented worker that had died took two minutes per job.
        var perServer = jobs
            .Where(j => !string.IsNullOrWhiteSpace(j.Endpoint.Url))
            .GroupBy(j => (j.Endpoint.Url, j.Endpoint.Token))
            .Select(async g =>
            {
                try
                {
                    using var c = new ComfyUiClient(g.Key.Url, clientId: null, authToken: g.Key.Token,
                                                    timeout: TimeSpan.FromSeconds(8));
                    await c.CancelPromptsAsync(g.Select(j => j.PromptId).Distinct().ToList());
                }
                catch { /* best effort — the CTS has already stopped our side */ }
            });
        await Task.WhenAll(perServer);
    }

    /// <summary>Snapshot count of currently-running prompt ids. Drives the
    /// "Cancel all" button's enable state and a small "N running" badge.</summary>
    public int RunningCount => _running.Count;

    public async Task CancelByShotAsync(string shotId)
    {
        var touched = new List<(ComfyEndpoint, string)>();
        foreach (var (promptId, sid) in _promptToShot.ToArray())
        {
            if (sid != shotId) continue;
            // Only ComfyUI-routed prompts have an endpoint recorded, so this
            // replaces the old "does it parse as a Guid?" heuristic with the
            // actual fact of where the job went. Read before cancelling (see
            // CancelAllAsync).
            if (_promptToEndpoint.TryGetValue(promptId, out var ep)) touched.Add((ep, promptId));
            if (_running.TryRemove(promptId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
            _promptToShot.TryRemove(promptId, out _);
        }
        await CancelOnServersAsync(touched);
    }

    public async Task CancelAsync(string promptId)
    {
        _promptToEndpoint.TryGetValue(promptId, out var ep);
        if (_running.TryRemove(promptId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        // Replicate cancellation is handled by the linked CTS above — the
        // poll loop checks ct on every tick. ComfyUI also gets told directly
        // so the prompt stops chewing GPU — on the machine it was actually
        // sent to, which for a rented worker is not the local server.
        if (ep is not null)
            await CancelOnServersAsync(new[] { (ep, promptId) });
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

        // Replicate returns a public CDN URL — pulled down into media/ so the
        // rest of the app treats it the same as a ComfyUI output.
        return StartCloudJob(shot, "replicate", req.Model, ".mp4",
            (progress, token) => provider.SubmitAndWaitAsync(req, progress, token), ct);
    }


    /// <summary>
    /// What every cloud route does once its request is built: record the job,
    /// submit and wait in the background, bill, download, and raise the same
    /// Done event the ComfyUI route raises.
    /// </summary>
    /// <remarks>
    /// Four routes carried their own copy of this, and all four billed only
    /// after a successful download (the vendor charges either way), reported
    /// an HTTP timeout as "cancelled", held a whole video in memory with a
    /// five-minute cap, and named the file after the shot alone — so a second
    /// take of the same shot overwrote the first one's file.
    /// </remarks>
    private string StartCloudJob(Shot shot, string providerId, string model, string defaultExt,
        Func<IProgress<double>, CancellationToken, Task<string>> submitAndWait, CancellationToken ct)
    {
        var jobId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _ctx.Shots.Insert(shot);
        WriteJobRow(shot.Id, jobId, "queued", providerId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[jobId] = cts;
        _promptToShot[jobId] = shot.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                Raise(jobId, shot.Id, ShotStatus.Generating, 1, null);
                var progress = new Progress<double>(p => Raise(jobId, shot.Id, ShotStatus.Generating, Math.Min(99, p), null));
                var outputUrl = await submitAndWait(progress, cts.Token);

                var ext = Path.GetExtension(new Uri(outputUrl).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = defaultExt;
                var isVideo = MediaKind.RendersAsVideo("x" + ext);

                // Bill as soon as the vendor has rendered — it charges whether
                // or not our download then succeeds.
                try
                {
                    if (isVideo) _ctx.Tracker.RecordSeconds(providerId, model, shot.DurationSec, "video");
                    else _ctx.Tracker.RecordImages(providerId, model, 1, "image");
                }
                catch { }

                var dest = Path.Combine(AppPaths.MediaFolder, SafeFilename($"{shot.Id}_{providerId}_{jobId[..6]}{ext}"));
                await DownloadWithRetryAsync(outputUrl, dest, cts.Token);

                var clipId = WriteClipRow(shot.Id, dest);
                await RecordDurationAsync(clipId, dest);
                WriteJobUpdate(jobId, "done", null);
                Raise(jobId, shot.Id, ShotStatus.Done, 100, null, dest);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                WriteJobUpdate(jobId, "cancelled", null);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, "cancelled");
            }
            catch (OperationCanceledException)
            {
                // A network timeout, not the user: say so.
                var msg = $"{providerId}: หมดเวลารอการตอบกลับ — งานอาจยังเรนเดอร์อยู่ที่ผู้ให้บริการ ลองดูในหน้าเว็บของเขา";
                WriteJobUpdate(jobId, "error", msg);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, msg);
            }
            catch (Exception ex)
            {
                WriteJobUpdate(jobId, "error", ex.Message);
                Raise(jobId, shot.Id, ShotStatus.Error, 0, ex.Message);
            }
            finally
            {
                if (_running.TryRemove(jobId, out var own)) own.Dispose();
                _promptToShot.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    private static readonly System.Net.Http.HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Stream a finished render to disk: into a .part file that is
    /// renamed only when complete, with two retries for a dropped connection,
    /// so a failure never leaves a truncated file that looks like a clip.</summary>
    private static async Task DownloadWithRetryAsync(string url, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        var part = dest + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cap.CancelAfter(TimeSpan.FromMinutes(30));
                using var resp = await DownloadHttp.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cap.Token);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(part))
                await using (var net = await resp.Content.ReadAsStreamAsync(cap.Token))
                    await net.CopyToAsync(fs, cap.Token);
                File.Move(part, dest, overwrite: true);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
            }
        }
    }

    /// <summary>Store a video's real length on its clip row (it was always 0).</summary>
    private async Task RecordDurationAsync(string? clipId, string path)
    {
        if (clipId is null || !MediaKind.RendersAsVideo(path)) return;
        try
        {
            if (await new FFmpegService(_ctx).ProbeDurationSecAsync(path) is double sec)
                _ctx.Clips.SetDuration(clipId, (int)Math.Round(sec * 1000));
        }
        catch { /* the clip is usable without it */ }
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
            var (completed, lostReason) = await PollHistoryUntilDoneAsync(client, promptId, ct);
            wsCts.Cancel();
            try { await wsTask; } catch { /* expected on cancel */ }

            // A cancel ends the poll too; without this it was reported as
            // "timed out", which sent people looking for a server problem.
            ct.ThrowIfCancellationRequested();

            if (completed is null)
            {
                WriteJobUpdate(promptId, "error", lostReason);
                Raise(promptId, shot.Id, ShotStatus.Error, 0, lostReason);
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

            // Download every output file the workflow produced. Previews
            // (type "temp") are skipped when real outputs exist — a graph with
            // a PreviewImage ahead of its SaveVideo made the preview the
            // shot's media. The primary file is the most "finished" kind:
            // video, then image, then audio.
            var outputs = Workflow.ExtractOutputs(completed).ToList();
            if (outputs.Any(o => o.Type == "output")) outputs = outputs.Where(o => o.Type == "output").ToList();
            var ordered = outputs.OrderBy(o => MediaKind.RendersAsVideo(o.Filename) ? 0
                                             : MediaKind.IsImage(o.Filename) ? 1 : 2).ToList();

            string? primaryPath = null;
            int downloaded = 0;
            var failures = new List<string>();
            foreach (var output in ordered)
            {
                var safeName = SafeFilename(shot.Id + "_" + Path.GetFileName(output.Filename));
                var dest = Path.Combine(AppPaths.MediaFolder, safeName);
                try
                {
                    await DownloadComfyOutputAsync(client, output, dest, ct);
                    primaryPath ??= dest;
                    var clipId = WriteClipRow(shot.Id, dest);
                    await RecordDurationAsync(clipId, dest);
                    downloaded++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{output.Filename}: {ex.Message}");
                }
            }

            if (downloaded == 0)
            {
                // "No outputs" and "outputs we could not fetch" are different
                // problems with different fixes; the second used to be
                // reported as the first ("check your save node").
                var msg = ordered.Count == 0
                    ? "ComfyUI ทำงานเสร็จแต่ไม่มีไฟล์ผลลัพธ์ — เวิร์กโฟลว์อาจไม่มีโหนด Save"
                    : "เรนเดอร์เสร็จแต่ดาวน์โหลดผลลัพธ์ไม่สำเร็จ: " + string.Join(" · ", failures);
                WriteJobUpdate(promptId, "error", msg);
                Raise(promptId, shot.Id, ShotStatus.Error, 0, msg);
                return;
            }
            if (failures.Count > 0)
                ActivityLog.Warn("generation", $"{failures.Count} of {ordered.Count} outputs failed to download: {string.Join(" · ", failures)}");

            WriteJobUpdate(promptId, "done", null);
            Raise(promptId, shot.Id, ShotStatus.Done, 100, null, primaryPath);
        }
        catch (OperationCanceledException)
        {
            // Whoever cancelled — the Queue, Auto Pilot's own token — the engine
            // has to hear it as well. Only the Queue's buttons used to tell it,
            // so a cancelled Auto Pilot left its prompt rendering (and a rented
            // card billing) with nobody listening. Harmless when already told.
            try
            {
                using var drop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await client.CancelPromptsAsync(new[] { promptId }, drop.Token);
            }
            catch { /* the server may be gone; nothing more to do */ }
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
            if (_running.TryRemove(promptId, out var own)) own.Dispose();
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
    /// or status=error. Returns the inner record, or null with the reason the
    /// job was given up on (null record and null reason on cancel).
    /// </summary>
    /// <remarks>
    /// There is deliberately no fixed time limit. The old 15-minute ceiling
    /// counted from submit, so a video render on a consumer card — or any job
    /// that waited in the queue behind another — was declared dead while
    /// ComfyUI went on rendering it. The wait now ends when the prompt is in
    /// neither the history nor the queue (the engine restarted, or someone
    /// cleared it), or when the server stops answering for ten minutes.
    /// </remarks>
    private static async Task<(JsonObject? Record, string Reason)> PollHistoryUntilDoneAsync(
        ComfyUiClient client, string promptId, CancellationToken ct)
    {
        var lastAlive = DateTime.UtcNow;
        var lastReachable = DateTime.UtcNow;
        var lastQueueCheck = DateTime.MinValue;
        var hardCap = DateTime.UtcNow.AddHours(12);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < hardCap)
        {
            try
            {
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException) { return (null, ""); }

            JsonObject? record = null;
            try
            {
                record = await client.GetHistoryAsync(promptId, ct);
                lastReachable = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (null, "");
            }
            catch
            {
                // Transient network glitch — judged below by how long it lasts.
            }

            if (record is not null)
            {
                var status = record["status"] as JsonObject;
                if (status?["status_str"]?.GetValue<string>() == "error") return (record, "");

                // Outputs filled in once SaveImage/SaveVideo/etc. has run.
                if (record["outputs"] is JsonObject outputs && outputs.Count > 0) return (record, "");

                // Finished with nothing saved: hand it back so the caller can
                // say "no outputs" instead of waiting here forever.
                if (status?["completed"] is JsonValue done && done.TryGetValue<bool>(out var isDone) && isDone)
                    return (record, "");
                lastAlive = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - lastReachable) > TimeSpan.FromMinutes(10))
                return (null, "ComfyUI ไม่ตอบมา 10 นาทีแล้ว — เอนจินอาจปิดตัวไปหรือเครื่องหลุดจากเน็ต");

            if ((DateTime.UtcNow - lastQueueCheck).TotalSeconds < 10) continue;
            lastQueueCheck = DateTime.UtcNow;

            var queue = await client.GetQueuedPromptIdsAsync(ct);
            if (queue is not { } q) continue;
            lastReachable = DateTime.UtcNow;
            if (q.Running.Contains(promptId) || q.Pending.Contains(promptId))
            {
                lastAlive = DateTime.UtcNow;
                continue;
            }

            // In neither list and no history: ComfyUI writes history and drops
            // the queue entry in one step, so a short grace covers the rest.
            if ((DateTime.UtcNow - lastAlive) > TimeSpan.FromSeconds(60))
                return (null, "ComfyUI ไม่มีงานนี้ในคิวหรือประวัติแล้ว — เอนจินอาจถูกเปิดใหม่หรือคิวถูกล้าง");
        }
        return ct.IsCancellationRequested
            ? (null, "")
            : (null, "งานนี้รันเกิน 12 ชั่วโมง จึงเลิกรอผล");
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
                    // Held below 100 while working: after the sampler come VAE
                    // decode and the video encode, minutes on a video graph,
                    // and a bar parked at 100% for that long reads as hung.
                    if (ours) Raise(promptId, shot.Id, ShotStatus.Generating, Math.Min(99, frac * 100), null);
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
            if (kind == "execution_interrupted")
                return "งานถูกหยุดจากนอกสตูดิโอ (เช่นกดหยุดในหน้า ComfyUI เอง)";
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

    private void WriteJobRow(string shotId, string promptId, string status, string provider)
    {
        try
        {
            using var c = _ctx.Db.Open();
            // The provider column said 'comfyui' for every route.
            c.Execute("""
                INSERT INTO generation_jobs (id, shot_id, provider, status, submitted_at)
                VALUES ($id, $shotId, $provider, $status, $now)
                """,
                new { id = promptId, shotId, provider, status, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
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

    /// <summary>Insert a clips row and return its id (null when the write failed).</summary>
    private string? WriteClipRow(string shotId, string filePath)
    {
        try
        {
            var id = Guid.NewGuid().ToString("N");
            using var c = _ctx.Db.Open();
            c.Execute("""
                INSERT INTO clips (id, shot_id, duration_ms, file_path, created_at)
                VALUES ($id, $shotId, 0, $path, $now)
                """,
                new
                {
                    id,
                    shotId,
                    path = filePath,
                    now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
            return id;
        }
        catch (Exception ex)
        {
            ActivityLog.Error("generation", $"WriteClipRow {System.IO.Path.GetFileName(filePath)}", ex);
            return null;
        }
    }

    /// <summary>Fetch one ComfyUI output with two retries, via a .part file.</summary>
    private static async Task DownloadComfyOutputAsync(ComfyUiClient client, OutputFile output, string dest, CancellationToken ct)
    {
        var part = dest + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.DownloadFileAsync(output.Filename, output.Subfolder, output.Type, part, ct);
                File.Move(part, dest, overwrite: true);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
            }
        }
    }

    /// <summary>A width × height near <paramref name="pixels"/> in the shot's
    /// aspect, on the 16-pixel grid video latents need.</summary>
    private static (int W, int H) FitAspect(int pixels, AspectRatio aspect)
    {
        var ratio = aspect switch
        {
            AspectRatio.Wide => 16.0 / 9.0,
            AspectRatio.Vertical => 9.0 / 16.0,
            AspectRatio.Cinema => 1280.0 / 544.0,
            _ => 1.0,
        };
        var h = Math.Sqrt(pixels / ratio);
        var w = h * ratio;
        static int Snap(double v) => Math.Max(16, (int)Math.Round(v / 16.0) * 16);
        return (Snap(w), Snap(h));
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
