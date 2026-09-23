using System;
using System.Globalization;
using ChanthraStudio.Helpers;

namespace ChanthraStudio.Services.Gpu;

/// <summary>Lifecycle of a rented machine, from our side of the wire.</summary>
public enum GpuWorkerStatus
{
    /// <summary>Row written, rent call in flight. The dangerous window: the
    /// vendor may already be billing while we don't yet know the id.</summary>
    Renting,
    /// <summary>Machine exists, boot script is installing/downloading.</summary>
    Warming,
    /// <summary>ComfyUI answers. Available for jobs.</summary>
    Ready,
    /// <summary>A render is on the card right now. Never reaped by the soft
    /// stops — only the absolute lifetime cap can take it.</summary>
    Busy,
    // No "Draining" state: a worker that is over budget mid-render simply
    // finishes the render it has already been paid for, drops back to Ready
    // when the listener releases it, and is then terminated by the ordinary
    // budget check on the next tick. A separate drain state would have to be
    // distinguishable from Busy to avoid killing that render, which is
    // exactly what Busy already does.
    /// <summary>Gone. The meter has stopped.</summary>
    Terminated,
    /// <summary>Boot failed or the vendor lost it. Terminated too, but the
    /// distinction matters for the "is this profile broken?" question.</summary>
    Failed,
}

/// <summary>
/// One rented machine. Persisted in <c>gpu_workers</c>; rows outlive the
/// machine because the cost history is the only audit trail the user has.
/// </summary>
public sealed class GpuWorker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ProviderId { get; set; } = "simplepod";

    /// <summary>Vendor-side id. Null between writing the row and the rent
    /// call returning — that gap is exactly why the row is written first.</summary>
    public string? InstanceId { get; set; }

    public string Name { get; set; } = "";
    public string ProfileKey { get; set; } = "";
    public GpuWorkerStatus Status { get; set; } = GpuWorkerStatus.Renting;

    /// <summary>Boot stage reported by the worker's proxy — "deps",
    /// "comfyui", "weights", "starting", "ready", "failed".</summary>
    public string? Stage { get; set; }

    /// <summary>Sub-stage detail, e.g. the weight file currently downloading.</summary>
    public string? StageDetail { get; set; }

    public string? GpuModel { get; set; }
    public decimal PricePerHourUsd { get; set; }
    public string? EndpointUrl { get; set; }

    /// <summary>DPAPI ciphertext. Use <see cref="Token"/> to read it.</summary>
    public string AuthTokenCipher { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>For display: the history list printed the UTC clock, seven
    /// hours off for a Bangkok user.</summary>
    public DateTime CreatedAtLocal => DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc).ToLocalTime();
    public DateTime? ReadyAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastJobAt { get; set; }
    public DateTime? TerminatedAt { get; set; }
    public string? TerminateReason { get; set; }

    /// <summary>Seconds a job actually held the card.</summary>
    public double RenderSeconds { get; set; }
    public int JobsDone { get; set; }
    public string? ErrorMessage { get; set; }

    // ---------------------------------------------------------------- derived

    /// <summary>Plaintext bearer token. Empty if this row came from another
    /// Windows account (DPAPI is per-user) — which makes the worker
    /// unusable but still terminable, the property that actually matters.</summary>
    public string Token
    {
        get => string.IsNullOrEmpty(AuthTokenCipher) ? "" : DpapiCrypto.Unprotect(AuthTokenCipher);
        set => AuthTokenCipher = string.IsNullOrWhiteSpace(value) ? "" : DpapiCrypto.Protect(value);
    }

    public bool IsAlive => Status is GpuWorkerStatus.Renting or GpuWorkerStatus.Warming
                                  or GpuWorkerStatus.Ready or GpuWorkerStatus.Busy;

    /// <summary>Wall-clock seconds we have been on the hook for. This — not
    /// render time — is what the vendor charges for.</summary>
    public double UptimeSeconds
        => Math.Max(0, ((TerminatedAt ?? DateTime.UtcNow) - CreatedAt).TotalSeconds);

    /// <summary>What this machine has actually cost, including warm-up and
    /// every idle second.</summary>
    public decimal TotalCostUsd => (decimal)(UptimeSeconds / 3600.0) * PricePerHourUsd;

    /// <summary>The slice of the bill that produced frames.</summary>
    public decimal RenderCostUsd => (decimal)(RenderSeconds / 3600.0) * PricePerHourUsd;

    /// <summary>Fraction of the bill spent warming up or sitting idle.
    /// The number that tells the user whether renting is worth it.</summary>
    public double OverheadFraction
    {
        get
        {
            var up = UptimeSeconds;
            if (up <= 0) return 0;
            return Math.Clamp(1.0 - (RenderSeconds / up), 0, 1);
        }
    }

    public double UtilisationFraction => 1.0 - OverheadFraction;

    /// <summary>Short human label for the status pill.</summary>
    public string StatusLabel => Status switch
    {
        GpuWorkerStatus.Renting => "renting",
        GpuWorkerStatus.Warming => string.IsNullOrEmpty(Stage) ? "warming up" : StageLabel,
        GpuWorkerStatus.Ready => "ready",
        GpuWorkerStatus.Busy => "rendering",
        GpuWorkerStatus.Terminated => "terminated",
        GpuWorkerStatus.Failed => "failed",
        _ => "unknown",
    };

    public string StageLabel => Stage switch
    {
        "booting" => "booting",
        "deps" => "installing packages",
        "comfyui" => "installing ComfyUI",
        "weights" => string.IsNullOrWhiteSpace(StageDetail)
            ? "downloading weights"
            : $"downloading {StageDetail}",
        "starting" => "starting ComfyUI",
        "ready" => "ready",
        "failed" => "failed",
        _ => "warming up",
    };

    public string UptimeLabel
    {
        get
        {
            var t = TimeSpan.FromSeconds(UptimeSeconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
                : $"{t.Minutes}m {t.Seconds:00}s";
        }
    }

    public string CostLabel => "$" + TotalCostUsd.ToString("0.00", CultureInfo.InvariantCulture);
}
