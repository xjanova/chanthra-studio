using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Gpu;

/// <summary>Stage updates while a caller waits for a machine.</summary>
public sealed record GpuWarmupProgress(string Stage, string Message, double Fraction);

/// <summary>
/// Owns the life of every rented machine: shopping, renting, warming,
/// handing out, reaping, and — above all — making sure nothing is left
/// running that nobody is using.
///
/// <b>The desktop problem.</b> The design this is modelled on ran on a
/// server that was always up, so a one-minute cron could be trusted to
/// eventually kill anything stray. This is a desktop app: close it and
/// nothing of ours runs at all, while the vendor keeps billing by the
/// second. Three things follow, and they are the most important code here:
///
///   1. The DB row is written <i>before</i> the rent call, so a crash in
///      the middle still leaves a breadcrumb.
///   2. On startup we reconcile every row we think is alive against the
///      vendor, and adopt or bury each one.
///   3. On shutdown we terminate, by default, everything we own.
///
/// The reaper also runs while the feature is switched <i>off</i>, because
/// turning a feature off must never strand a machine that is already running.
/// </summary>
public sealed class GpuWorkerService : IDisposable
{
    /// <summary>
    /// Every instance we create is named with this prefix, and the orphan
    /// sweep will not touch anything that lacks it.
    ///
    /// This is not cosmetic. The sweep's job is to kill machines on the
    /// user's account that our database has lost track of — and the same
    /// account very likely holds machines they rented themselves for
    /// something else. Deleting one of those would be far worse than
    /// leaking a few cents on an orphan, so the rule is: kill only what we
    /// can positively identify as ours, and if the name can't be read at
    /// all, leave it alone.
    /// </summary>
    public const string NamePrefix = "chanthra-";

    private readonly StudioContext _ctx;
    private readonly GpuWorkerRepository _repo;
    private readonly SemaphoreSlim _rentLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastOrphanSweep = DateTime.MinValue;
    private bool _disposed;

    /// <summary>Raised after each tick so the UI can refresh without polling.</summary>
    public event Action? WorkersChanged;

    /// <summary>Human-readable note about the last thing the reaper did.</summary>
    public string? LastActivity { get; private set; }

    public GpuWorkerService(StudioContext ctx)
    {
        _ctx = ctx;
        _repo = new GpuWorkerRepository(ctx.Db);
    }

    public GpuWorkerRepository Repository => _repo;

    public GpuGuardrails Guardrails => GpuGuardrails.Load(_ctx.Settings);

    public string ApiKey => _ctx.Settings["simplepod"];

    /// <summary>Optional Hugging Face token, for profiles behind gated repos.</summary>
    public string HfToken => _ctx.Settings["huggingface"];

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    private IGpuRentalProvider Provider(GpuGuardrails g)
        => GpuRentalRegistry.Resolve(g.ProviderId, g.ApiBase);

    // =====================================================================
    // Background tick
    // =====================================================================

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task LoopAsync(CancellationToken ct)
    {
        // Reconcile before the first tick: rows left over from a previous
        // run are the single most likely source of a machine nobody is
        // watching.
        try { await ReconcileOnStartupAsync(ct); }
        catch (Exception ex) { LastActivity = "startup reconcile failed: " + ex.Message; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastActivity = "tick error: " + ex.Message;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass: refresh what we own, then apply the guardrails.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var live = _repo.Live();
        var g = Guardrails;

        // Early-out only when there is genuinely nothing to look after. Note
        // the deliberate order: live workers are checked BEFORE the enabled
        // flag, so switching the feature off never abandons a running box.
        if (live.Count == 0)
        {
            if (!g.Enabled || !HasApiKey) return;
            await MaybeSweepOrphansAsync(g, ct);
            return;
        }

        if (!HasApiKey)
        {
            // We can't reach the vendor to terminate anything. Say so loudly
            // rather than silently leaving machines running.
            LastActivity = $"{live.Count} worker(s) may still be billing, but the API key is gone — re-paste it to terminate them.";
            WorkersChanged?.Invoke();
            return;
        }

        foreach (var w in live)
        {
            ct.ThrowIfCancellationRequested();
            try { await ReconcileOneAsync(w, g, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { LastActivity = $"{w.Name}: {ex.Message}"; }
        }

        await MaybeSweepOrphansAsync(g, ct);
        WorkersChanged?.Invoke();
    }

    /// <summary>
    /// Health + guardrails for one worker.
    ///
    /// Ordering matters: the hard stops are evaluated first and depend on
    /// nothing but the row itself. A missing or unparseable profile must
    /// never be able to block a termination — that failure mode turns a
    /// config typo into an open-ended bill.
    /// </summary>
    private async Task ReconcileOneAsync(GpuWorker w, GpuGuardrails g, CancellationToken ct)
    {
        var provider = Provider(g);
        var ageMinutes = (DateTime.UtcNow - w.CreatedAt).TotalMinutes;

        // ---- hard stops (no profile lookup, no network dependency) --------
        if (ageMinutes > g.MaxLifetimeMinutes)
        {
            await TerminateAsync(w, $"hit the {g.MaxLifetimeMinutes} min lifetime cap", ct);
            return;
        }

        if (w.Status == GpuWorkerStatus.Renting)
        {
            // Written the row, never got an id back. Either the rent call is
            // still in flight or it died. Give it a few minutes, then treat
            // it as an orphan candidate rather than a live worker.
            if (w.InstanceId is null && ageMinutes > 5)
            {
                w.Status = GpuWorkerStatus.Failed;
                w.ErrorMessage = "rent call never returned an instance id";
                w.TerminatedAt = DateTime.UtcNow;
                w.TerminateReason = "no instance id";
                _repo.Update(w);
                LastActivity = $"{w.Name}: rent did not complete — the orphan sweep will look for it";
                _lastOrphanSweep = DateTime.MinValue;   // force a sweep next tick
            }
            return;
        }

        if (w.InstanceId is null) return;

        // ---- vendor-side truth --------------------------------------------
        var inst = await provider.GetInstanceAsync(ApiKey, w.InstanceId, ct);
        if (inst is null)
        {
            w.Status = GpuWorkerStatus.Terminated;
            w.TerminatedAt = DateTime.UtcNow;
            w.TerminateReason = "vendor no longer lists this instance";
            _repo.Update(w);
            LastActivity = $"{w.Name} is gone from the vendor";
            return;
        }

        if (inst.State == GpuInstanceState.Failed || inst.State == GpuInstanceState.Stopped)
        {
            await TerminateAsync(w, $"vendor reported '{inst.RawStatus}'", ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(inst.EndpointUrl) && inst.EndpointUrl != w.EndpointUrl)
        {
            w.EndpointUrl = inst.EndpointUrl;
            WarnIfInsecure(w);
        }
        if (!string.IsNullOrWhiteSpace(inst.GpuModel)) w.GpuModel = inst.GpuModel;
        if (inst.PricePerHourUsd > 0) w.PricePerHourUsd = inst.PricePerHourUsd;

        // ---- boot progress -------------------------------------------------
        var report = await GpuWorkerClient.ProbeAsync(w.EndpointUrl ?? "", w.Token, ct);
        if (report.Reachable)
        {
            w.LastSeenAt = DateTime.UtcNow;
            w.Stage = report.Stage;
            w.StageDetail = report.Detail;

            if (report.IsFailed)
            {
                w.ErrorMessage = report.Error ?? "boot script reported failure";
                _repo.Update(w);
                await TerminateAsync(w, "boot failed: " + w.ErrorMessage, ct);
                return;
            }

            if (report.IsReady && w.Status == GpuWorkerStatus.Warming)
            {
                w.Status = GpuWorkerStatus.Ready;
                w.ReadyAt ??= DateTime.UtcNow;
                LastActivity = $"{w.Name} is ready ({w.UptimeLabel} to warm up)";
            }
        }
        else if (w.Status == GpuWorkerStatus.Warming && ageMinutes > g.WarmupTimeoutMinutes)
        {
            await TerminateAsync(w,
                $"never came up within {g.WarmupTimeoutMinutes} min", ct);
            return;
        }

        // ---- soft stops ----------------------------------------------------
        // A busy worker is never reaped here: killing it throws away a render
        // the user has already paid for and leaves them with nothing. One
        // that is over budget mid-render finishes, drops to Ready when the
        // listener releases it, and gets terminated on the next tick.
        if (w.Status == GpuWorkerStatus.Ready)
        {
            var idleSince = w.LastJobAt ?? w.ReadyAt ?? w.CreatedAt;
            var idleMinutes = (DateTime.UtcNow - idleSince).TotalMinutes;

            if (idleMinutes > g.IdleTimeoutMinutes)
            {
                await TerminateAsync(w, $"idle for {(int)idleMinutes} min", ct);
                return;
            }

            var spentToday = _repo.SpendToday();
            if (g.DailyBudgetUsd > 0 && spentToday >= g.DailyBudgetUsd)
            {
                await TerminateAsync(w,
                    $"daily budget reached (${spentToday:0.00} of ${g.DailyBudgetUsd:0.00})", ct);
                return;
            }
        }

        _repo.Update(w);
    }

    /// <summary>
    /// On launch, decide what to do with every worker the last session left
    /// behind. A machine that is still healthy is adopted — closing and
    /// reopening the app shouldn't throw away a box that already spent 20
    /// paid minutes downloading weights.
    /// </summary>
    public async Task ReconcileOnStartupAsync(CancellationToken ct = default)
    {
        var live = _repo.Live();
        if (live.Count == 0) return;

        var g = Guardrails;
        if (!HasApiKey)
        {
            LastActivity = $"{live.Count} worker(s) from the last session may still be billing — paste the API key to check.";
            WorkersChanged?.Invoke();
            return;
        }

        foreach (var w in live)
        {
            // A worker mid-render when the app died is not mid-render now:
            // the listener that was watching it is gone. Demote so the idle
            // timer can see it; otherwise Busy would exempt it from reaping
            // forever.
            if (w.Status == GpuWorkerStatus.Busy)
            {
                w.Status = GpuWorkerStatus.Ready;
                w.ErrorMessage = "app closed while a render was in flight";
                _repo.Update(w);
            }

            try { await ReconcileOneAsync(w, g, ct); }
            catch (Exception ex) { LastActivity = $"{w.Name}: {ex.Message}"; }
        }

        _lastOrphanSweep = DateTime.MinValue;   // always sweep once at startup
        try { await MaybeSweepOrphansAsync(g, ct); }
        catch (Exception ex) { LastActivity = "orphan sweep failed: " + ex.Message; }

        WorkersChanged?.Invoke();
    }

    // =====================================================================
    // Renting
    // =====================================================================

    /// <summary>
    /// Return a machine that can run <paramref name="profileKey"/>, renting
    /// one and waiting for it to warm up if necessary.
    /// </summary>
    public async Task<GpuWorker> EnsureWorkerAsync(
        string profileKey,
        IProgress<GpuWarmupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var g = Guardrails;
        if (!g.Enabled)
            throw new GpuRentalException(
                "The rented-GPU route is switched off. Turn it on in the GPU panel.");
        if (!HasApiKey)
            throw new GpuRentalException(
                "No SimplePod API key — paste one in the GPU panel first.");

        var profile = GpuModelCatalog.Find(profileKey)
            ?? throw new GpuRentalException($"Unknown GPU profile \"{profileKey}\".");

        if (profile.RequiresHfToken && string.IsNullOrWhiteSpace(HfToken))
            throw new GpuRentalException(
                $"{profile.DisplayName} downloads from a gated Hugging Face repo. " +
                "Add a Hugging Face token in the GPU panel, or pick another profile — " +
                "renting without it would pay for a machine that can't fetch its weights.");

        // One rent at a time. Two concurrent submits would otherwise each see
        // "no worker" and rent their own, doubling the bill.
        await _rentLock.WaitAsync(ct);
        try
        {
            var existing = _repo.Live().FirstOrDefault(w =>
                w.ProfileKey == profileKey &&
                w.Status is GpuWorkerStatus.Ready or GpuWorkerStatus.Warming or GpuWorkerStatus.Busy);

            if (existing is not null)
            {
                progress?.Report(new GpuWarmupProgress("reuse",
                    $"Reusing {existing.Name} ({existing.StatusLabel})", 0.5));
                return await WaitUntilReadyAsync(existing, g, progress, ct);
            }

            var fresh = await RentNewAsync(profile, g, progress, ct);
            return await WaitUntilReadyAsync(fresh, g, progress, ct);
        }
        finally
        {
            _rentLock.Release();
        }
    }

    private async Task<GpuWorker> RentNewAsync(
        GpuModelProfile profile, GpuGuardrails g,
        IProgress<GpuWarmupProgress>? progress, CancellationToken ct)
    {
        // ---- budget gates, before a single cent is committed --------------
        var liveCount = _repo.Live().Count;
        if (liveCount >= g.MaxConcurrentWorkers)
            throw new GpuRentalException(
                $"Already running {liveCount} worker(s), and the limit is {g.MaxConcurrentWorkers}. " +
                "Raise the limit in the GPU panel or wait for one to free up.");

        var spentToday = _repo.SpendToday();
        if (g.DailyBudgetUsd > 0 && spentToday >= g.DailyBudgetUsd)
            throw new GpuRentalException(
                $"Today's GPU budget is spent (${spentToday:0.00} of ${g.DailyBudgetUsd:0.00}). " +
                "Raise it in the GPU panel if you want to keep going.");

        var provider = Provider(g);

        progress?.Report(new GpuWarmupProgress("shopping", "Looking for a machine…", 0.02));
        var filter = g.ToFilter(profile);
        var offers = await provider.SearchMarketAsync(ApiKey, filter, ct);
        if (offers.Count == 0)
            throw new GpuRentalException(
                $"No machine on the market matches {profile.DisplayName}: " +
                $"≥{profile.MinVramGb} GB VRAM, ≥{profile.RequiredDiskGb} GB disk, " +
                $"≥{g.MinDownloadMbps} Mbps, at or under ${g.MaxPricePerHourUsd:0.00}/hr. " +
                "Raising the price ceiling or lowering the speed floor in the GPU panel usually finds one. " +
                $"{profile.DisplayName} is one of the heavier profiles — " +
                "the cheap end of these marketplaces is 8–16 GB cards, so a 24 GB+ profile " +
                "typically needs a ceiling around $1.00/hr before anything matches.");

        // Ranked cheapest-JOB-first by the provider (GpuCostModel), because
        // warm-up download time is billed at the same rate as rendering.
        var pick = offers[0];
        var estimate = GpuCostModel.Estimate(pick, filter);
        var why = GpuCostModel.ExplainPick(pick, offers, filter);
        if (!string.IsNullOrEmpty(why)) ActivityLog.Info("gpu", why);

        // The cheapest machine can still bust the budget if it runs long
        // enough. Refuse anything whose lifetime cap alone would exceed
        // what's left for today.
        var worstCase = pick.TotalPricePerHourUsd * (decimal)(g.MaxLifetimeMinutes / 60.0);
        var remaining = g.DailyBudgetUsd - spentToday;
        if (g.DailyBudgetUsd > 0 && worstCase > remaining)
            throw new GpuRentalException(
                $"{pick.Summary} could cost up to ${worstCase:0.00} before the lifetime cap stops it, " +
                $"but only ${remaining:0.00} of today's budget is left. " +
                "Lower the lifetime cap or raise the daily budget.");

        var worker = new GpuWorker
        {
            ProviderId = provider.Id,
            ProfileKey = profile.Key,
            Status = GpuWorkerStatus.Renting,
            GpuModel = pick.GpuModel,
            // Total, not the GPU line alone — this figure is what the daily
            // budget sums and what the panel reports.
            PricePerHourUsd = pick.TotalPricePerHourUsd,
            CreatedAt = DateTime.UtcNow,
        };
        worker.Name = NamePrefix + worker.Id;
        worker.Token = GpuProvisioning.NewWorkerToken();

        // Write BEFORE renting. If the process dies between here and the
        // vendor answering, this row is the only thing that will let the
        // next launch find and kill the machine.
        _repo.Insert(worker);
        WorkersChanged?.Invoke();

        progress?.Report(new GpuWarmupProgress("renting",
            $"Renting {pick.Summary}…", 0.06));

        var spec = new GpuRentSpec
        {
            OfferId = pick.Id,
            Name = worker.Name,
            DockerImage = string.IsNullOrWhiteSpace(g.DockerImage) ? profile.DockerImage : g.DockerImage,
            GpuCount = 1,
            ExposedPort = GpuProvisioning.ProxyPort,
            StartScript = GpuProvisioning.BuildStartScript(profile, worker.Token, HfToken),
        };
        spec.Env["CHANTHRA_TOKEN"] = worker.Token;
        spec.Env["CHANTHRA_PROXY_PORT"] = GpuProvisioning.ProxyPort.ToString();
        spec.Env["CHANTHRA_COMFY_PORT"] = GpuProvisioning.ComfyPort.ToString();
        if (!string.IsNullOrWhiteSpace(HfToken)) spec.Env["HF_TOKEN"] = HfToken;

        try
        {
            worker.InstanceId = await provider.RentAsync(ApiKey, spec, ct);
        }
        catch (Exception ex)
        {
            worker.Status = GpuWorkerStatus.Failed;
            worker.ErrorMessage = ex.Message;
            worker.TerminatedAt = DateTime.UtcNow;
            worker.TerminateReason = "rent failed";
            _repo.Update(worker);
            // Whatever went wrong, something may have started. Sweep soon.
            _lastOrphanSweep = DateTime.MinValue;
            WorkersChanged?.Invoke();
            throw;
        }

        worker.Status = GpuWorkerStatus.Warming;
        worker.Stage = "booting";
        _repo.Update(worker);
        WorkersChanged?.Invoke();

        // One estimate, computed once above, used for both the ETA and the
        // cost line — two independently-derived numbers would eventually
        // disagree on screen and neither would be trustworthy.
        LastActivity = $"rented {pick.Summary} as {worker.Name} — {estimate.Label}";
        return worker;
    }

    /// <summary>
    /// Block until the worker answers "ready", surfacing each boot stage as
    /// it goes.
    ///
    /// The stage reporting is the whole point. Warm-up runs 10–40 minutes
    /// depending on profile and link speed; without visible progress a user
    /// cannot tell a healthy download from a hung box, and the natural
    /// response — cancel and retry — pays for the warm-up twice.
    /// </summary>
    private async Task<GpuWorker> WaitUntilReadyAsync(
        GpuWorker worker, GpuGuardrails g,
        IProgress<GpuWarmupProgress>? progress, CancellationToken ct)
    {
        if (worker.Status == GpuWorkerStatus.Ready) return worker;

        var provider = Provider(g);
        var deadline = worker.CreatedAt.AddMinutes(g.WarmupTimeoutMinutes);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var fresh = _repo.Find(worker.Id);
            if (fresh is null)
                throw new GpuRentalException("Worker row vanished mid-warm-up.");
            worker = fresh;

            if (worker.Status == GpuWorkerStatus.Ready || worker.Status == GpuWorkerStatus.Busy)
            {
                progress?.Report(new GpuWarmupProgress("ready", $"{worker.Name} is ready", 1.0));
                return worker;
            }

            if (!worker.IsAlive)
                throw new GpuRentalException(
                    worker.ErrorMessage ?? worker.TerminateReason ?? "The worker stopped before it was ready.");

            // Pick up the endpoint as soon as the vendor publishes it.
            if (string.IsNullOrWhiteSpace(worker.EndpointUrl) && worker.InstanceId is not null)
            {
                var inst = await provider.GetInstanceAsync(ApiKey, worker.InstanceId, ct);
                if (!string.IsNullOrWhiteSpace(inst?.EndpointUrl))
                {
                    worker.EndpointUrl = inst!.EndpointUrl;
                    WarnIfInsecure(worker);
                    _repo.Update(worker);
                }
            }

            if (!string.IsNullOrWhiteSpace(worker.EndpointUrl))
            {
                var report = await GpuWorkerClient.ProbeAsync(worker.EndpointUrl!, worker.Token, ct);
                if (report.Reachable)
                {
                    worker.Stage = report.Stage;
                    worker.StageDetail = report.Detail;
                    worker.LastSeenAt = DateTime.UtcNow;

                    if (report.IsFailed)
                    {
                        var msg = report.Error ?? "boot failed";
                        worker.ErrorMessage = msg;
                        _repo.Update(worker);
                        await TerminateAsync(worker, "boot failed: " + msg, CancellationToken.None);
                        throw new GpuRentalException($"The rented machine failed to set itself up: {msg}");
                    }

                    if (report.IsReady)
                    {
                        worker.Status = GpuWorkerStatus.Ready;
                        worker.ReadyAt ??= DateTime.UtcNow;
                        _repo.Update(worker);
                        WorkersChanged?.Invoke();
                        progress?.Report(new GpuWarmupProgress("ready", $"{worker.Name} is ready", 1.0));
                        return worker;
                    }

                    _repo.Update(worker);
                }
            }

            progress?.Report(new GpuWarmupProgress(
                worker.Stage ?? "warming",
                $"{worker.StageLabel} · {worker.UptimeLabel} elapsed · {worker.CostLabel} so far",
                StageFraction(worker.Stage)));

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }

        await TerminateAsync(worker, $"warm-up exceeded {g.WarmupTimeoutMinutes} min", CancellationToken.None);
        throw new GpuRentalException(
            $"The machine never finished setting up within {g.WarmupTimeoutMinutes} minutes, so it was released. " +
            "A faster host (raise the Mbps floor) usually fixes this.");
    }

    /// <summary>
    /// The vendor normally fronts the exposed port with an HTTPS tunnel. When
    /// it doesn't, the only route to the worker is a bare host:port, and the
    /// bearer token would travel in clear text where an on-path observer
    /// could lift it and queue work on a card we are paying for.
    ///
    /// We still use the connection — refusing would break the feature outright
    /// on hosts without a tunnel — but this is not something to swallow
    /// quietly, so it is logged and surfaced in the panel.
    /// </summary>
    private void WarnIfInsecure(GpuWorker w)
    {
        if (w.EndpointUrl is null) return;
        if (w.EndpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

        var msg = $"{w.Name} has no HTTPS tunnel — talking to it over plain HTTP, " +
                  "so its access token is exposed on the network path.";
        LastActivity = msg;
        try { ActivityLog.Warn("gpu", msg); } catch { /* logging must not break the tick */ }
    }

    /// <summary>Rough completion for the progress bar. Weighted towards the
    /// weights download because that is where nearly all the time goes.</summary>
    private static double StageFraction(string? stage) => stage switch
    {
        "booting" => 0.08,
        "deps" => 0.15,
        "comfyui" => 0.25,
        "weights" => 0.60,
        "starting" => 0.92,
        "ready" => 1.0,
        _ => 0.10,
    };

    // =====================================================================
    // Job accounting
    // =====================================================================

    /// <summary>Mark a worker as occupied. Busy workers are never reaped.</summary>
    public void MarkJobStarted(string workerId)
    {
        var w = _repo.Find(workerId);
        if (w is null || !w.IsAlive) return;
        w.Status = GpuWorkerStatus.Busy;
        w.LastJobAt = DateTime.UtcNow;
        _repo.Update(w);
        WorkersChanged?.Invoke();
    }

    /// <summary>
    /// Release a worker and bank the seconds it actually spent rendering.
    /// Comparing this against uptime is what surfaces "you paid for 40
    /// minutes of warm-up to get 90 seconds of video".
    /// </summary>
    public void MarkJobFinished(string workerId, double renderSeconds)
    {
        var w = _repo.Find(workerId);
        if (w is null) return;
        w.RenderSeconds += Math.Max(0, renderSeconds);
        w.JobsDone += 1;
        w.LastJobAt = DateTime.UtcNow;
        // Draining means someone asked for it to go; don't resurrect it.
        if (w.Status == GpuWorkerStatus.Busy) w.Status = GpuWorkerStatus.Ready;
        _repo.Update(w);
        WorkersChanged?.Invoke();
    }

    // =====================================================================
    // Termination
    // =====================================================================

    public async Task TerminateAsync(GpuWorker w, string reason, CancellationToken ct = default)
    {
        var g = Guardrails;
        if (w.InstanceId is not null && HasApiKey)
        {
            try
            {
                await Provider(g).TerminateAsync(ApiKey, w.InstanceId, ct);
            }
            catch (Exception ex)
            {
                // The row must still be closed out, but the user has to know
                // the meter might not have stopped — that's real money.
                w.ErrorMessage = $"terminate call failed: {ex.Message}";
                LastActivity = $"could not terminate {w.Name} — check the vendor dashboard: {ex.Message}";
            }
        }

        w.Status = w.Status == GpuWorkerStatus.Failed ? GpuWorkerStatus.Failed : GpuWorkerStatus.Terminated;
        w.TerminatedAt ??= DateTime.UtcNow;
        w.TerminateReason = reason;
        _repo.Update(w);
        if (LastActivity is null || !LastActivity.StartsWith("could not terminate"))
            LastActivity = $"released {w.Name} — {reason} ({w.CostLabel}, {w.UptimeLabel})";
        WorkersChanged?.Invoke();
    }

    public async Task TerminateAsync(string workerId, string reason, CancellationToken ct = default)
    {
        var w = _repo.Find(workerId);
        if (w is not null) await TerminateAsync(w, reason, ct);
    }

    /// <summary>
    /// Kill everything we own. Used by the panel's "release all" button and
    /// by app shutdown.
    /// </summary>
    public async Task<int> TerminateAllAsync(string reason, CancellationToken ct = default)
    {
        var live = _repo.Live();
        var n = 0;
        foreach (var w in live)
        {
            try { await TerminateAsync(w, reason, ct); n++; }
            catch (Exception ex) { LastActivity = $"{w.Name}: {ex.Message}"; }
        }
        return n;
    }

    /// <summary>
    /// Called from app shutdown. Deliberately synchronous-with-timeout: the
    /// process is about to exit, and a fire-and-forget task would simply not
    /// run. Better to hold the close for a few seconds than to leak a machine.
    /// </summary>
    public void TerminateAllOnExit()
    {
        var g = Guardrails;
        if (!g.TerminateOnExit) return;
        if (!HasApiKey) return;
        if (_repo.Live().Count == 0) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            TerminateAllAsync("app closed", cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown path — nothing useful left to do with an exception, and
            // the rows stay marked live so the next launch will find them.
        }
    }

    // =====================================================================
    // Orphan sweep
    // =====================================================================

    private async Task MaybeSweepOrphansAsync(GpuGuardrails g, CancellationToken ct)
    {
        if (!HasApiKey) return;
        if ((DateTime.UtcNow - _lastOrphanSweep) < TimeSpan.FromMinutes(15)) return;
        _lastOrphanSweep = DateTime.UtcNow;
        await SweepOrphansAsync(g, ct);
    }

    /// <summary>
    /// Find machines on the account that carry our name prefix but that our
    /// database doesn't account for, and kill them.
    ///
    /// The guard rules, in order of how much they matter:
    ///   * A name that doesn't start with <see cref="NamePrefix"/> is not
    ///     ours. Skip it.
    ///   * A name we can't read at all is skipped too. Leaking an orphan
    ///     costs cents; destroying a machine the user rented for their own
    ///     work is unrecoverable.
    ///   * A machine younger than 5 minutes is skipped, because a rent that
    ///     is still in flight has an id we haven't stored yet.
    /// </summary>
    public async Task<int> SweepOrphansAsync(GpuGuardrails? guardrails = null, CancellationToken ct = default)
    {
        var g = guardrails ?? Guardrails;
        if (!HasApiKey) return 0;

        var provider = Provider(g);
        var vendorInstances = await provider.ListInstancesAsync(ApiKey, ct);

        // Every id we have ever recorded, not just the live ones — a row we
        // already terminated should not be re-killed and re-logged.
        var known = _repo.Recent(500)
            .Select(w => w.InstanceId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal)!;

        var killed = 0;
        foreach (var inst in vendorInstances)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(inst.Name)) continue;                     // unreadable → never touch
            if (!inst.Name.StartsWith(NamePrefix, StringComparison.Ordinal)) continue; // not ours
            if (known.Contains(inst.Id)) continue;                                   // accounted for
            if (inst.State == GpuInstanceState.Stopped) continue;                    // already dead

            var age = inst.StartedAt is null
                ? TimeSpan.MaxValue
                : DateTime.UtcNow - inst.StartedAt.Value;
            if (age < TimeSpan.FromMinutes(5)) continue;   // a rent may still be in flight

            try
            {
                await provider.TerminateAsync(ApiKey, inst.Id, ct);
                killed++;
                LastActivity = $"swept orphan {inst.Name} ({inst.GpuModel})";
            }
            catch (Exception ex)
            {
                LastActivity = $"could not sweep orphan {inst.Name}: {ex.Message}";
            }
        }

        if (killed > 0) WorkersChanged?.Invoke();
        return killed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { /* best effort */ }
        try { _cts?.Dispose(); } catch { /* best effort */ }
        _rentLock.Dispose();
    }
}
