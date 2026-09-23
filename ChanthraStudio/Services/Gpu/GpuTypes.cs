using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// A GPU rental marketplace (SimplePod, RunPod, Vast.ai …). Deliberately
/// NOT an inference API — these vendors hand us a bare machine with a
/// Docker image on it and charge by the second. Everything above this
/// interface (what we install, how we talk to it, when we kill it) is
/// provider-agnostic and lives in <see cref="GpuWorkerService"/>.
///
/// Adding a second marketplace = implement this + register in
/// <see cref="GpuRentalRegistry"/>. Nothing else changes.
/// </summary>
public interface IGpuRentalProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Where the user goes to create an API key. Shown in Settings.</summary>
    string ConsoleUrl { get; }

    /// <summary>Account credit remaining, in USD. Doubles as the key probe —
    /// if this succeeds the key is valid.</summary>
    Task<decimal> GetBalanceAsync(string apiKey, CancellationToken ct = default);

    /// <summary>Search the marketplace for machines matching a spec.
    /// Results are already sorted cheapest-first and client-side filtered
    /// (vendors' server-side filters are usually coarse).</summary>
    Task<IReadOnlyList<GpuOffer>> SearchMarketAsync(string apiKey, GpuFilter filter, CancellationToken ct = default);

    /// <summary>Rent a machine. Returns the vendor's instance id.</summary>
    Task<string> RentAsync(string apiKey, GpuRentSpec spec, CancellationToken ct = default);

    /// <summary>Fetch one instance. Null when the vendor no longer knows it
    /// (already destroyed).</summary>
    Task<GpuInstance?> GetInstanceAsync(string apiKey, string instanceId, CancellationToken ct = default);

    /// <summary>Every instance on the account — ours and the user's own.
    /// Used by the orphan sweep, which is why it must NOT filter.</summary>
    Task<IReadOnlyList<GpuInstance>> ListInstancesAsync(string apiKey, CancellationToken ct = default);

    /// <summary>Destroy the instance and stop the meter. Must be idempotent:
    /// terminating an already-gone instance is a success, not an error.</summary>
    Task TerminateAsync(string apiKey, string instanceId, CancellationToken ct = default);

    /// <summary>
    /// After a rent the vendor may have accepted without our seeing the new
    /// machine, find it and give it our name so the orphan sweep can kill it.
    /// Returns how many instances were tagged (0 or 1).
    /// </summary>
    Task<int> AdoptUnconfirmedAsync(string apiKey, PendingRental pending, CancellationToken ct = default);
}

/// <summary>What we knew just before a rent whose result we never saw: the
/// instances that already existed, when we ordered, and the name to stamp.</summary>
public sealed record PendingRental(IReadOnlyCollection<string> Before, DateTime OrderedAtUtc, string NameTag);

/// <summary>What we're shopping for.</summary>
public sealed class GpuFilter
{
    /// <summary>Minimum VRAM in GB — driven by the model profile.</summary>
    public int MinVramGb { get; set; } = 24;

    /// <summary>Minimum free disk in GB — weights have to land somewhere.</summary>
    public int MinDiskGb { get; set; } = 80;

    /// <summary>Hard ceiling on hourly price, USD. The guardrail the user sets.</summary>
    public decimal MaxPricePerHourUsd { get; set; } = 0.60m;

    /// <summary>Minimum download speed in Mbps. Matters more than it sounds:
    /// a 40 GB weight pull at 100 Mbps is an hour of paid warm-up.</summary>
    public int MinDownloadMbps { get; set; } = 500;

    /// <summary>How many gigabytes of weights this profile has to pull before
    /// it can render. Together with each offer's link speed this is what turns
    /// a sticker price into a job cost — see <see cref="GpuCostModel"/>.</summary>
    public double WeightsGb { get; set; }

    /// <summary>Warm-up minutes that are not downloading (apt, clone, pip,
    /// first load into VRAM).</summary>
    public double FixedWarmupMinutes { get; set; } = GpuCostModel.FixedWarmupMinutes;

    /// <summary>How long we expect to hold the card once it is ready. This is
    /// the knob that decides cheap-and-slow versus fast-and-pricier: a long
    /// session amortises warm-up and favours the low hourly rate, a short one
    /// makes download speed dominate.</summary>
    public double ExpectedWorkMinutes { get; set; } = 30;

    public int MaxResults { get; set; } = 40;
}

/// <summary>One rentable machine on the marketplace.</summary>
public sealed class GpuOffer
{
    public string Id { get; set; } = "";

    /// <summary>What the vendor wants back when renting this offer — for
    /// SimplePod the IRI <c>/instances/market/N</c>, not the bare id.</summary>
    public string MarketRef { get; set; } = "";
    public string GpuModel { get; set; } = "";
    public int GpuCount { get; set; } = 1;
    public int VramGb { get; set; }
    public int DiskGb { get; set; }
    /// <summary>The GPU line item, USD/hour. NOT the whole bill — see
    /// <see cref="TotalPricePerHourUsd"/>.</summary>
    public decimal PricePerHourUsd { get; set; }

    /// <summary>
    /// The vendor's separate charge for the disk we ask for, USD/hour.
    ///
    /// SimplePod quotes disk as USD per GB per month (0.15 on $0.48–1/hr
    /// cards, per the working aixman integration); the provider converts that
    /// to an hourly figure for the disk this rental will request. Reading the
    /// raw figure as dollars per hour — as this used to — pushed a $0.48 card
    /// to $0.63 and over the default price ceiling.
    /// </summary>
    public decimal DiskPricePerHourUsd { get; set; }

    /// <summary>Everything the meter charges per hour. All budget arithmetic
    /// and every price ceiling must use this, not the GPU line alone.</summary>
    public decimal TotalPricePerHourUsd => PricePerHourUsd + DiskPricePerHourUsd;

    public int DownloadMbps { get; set; }
    public string Region { get; set; } = "";
    public double Reliability { get; set; }

    /// <summary>Speed reads "?" rather than "0 Mbps" when the vendor did not
    /// report one — zero would read as a measurement of a very slow link.</summary>
    public string Summary => $"{GpuModel} · {VramGb} GB · ${TotalPricePerHourUsd:0.00}/hr · "
                           + (DownloadMbps > 0 ? $"{DownloadMbps} Mbps" : "? Mbps");
}

/// <summary>The order we place with the marketplace.</summary>
public sealed class GpuRentSpec
{
    /// <summary>The <see cref="GpuOffer.Id"/> we picked.</summary>
    public string OfferId { get; set; } = "";

    /// <summary>The picked offer's <see cref="GpuOffer.MarketRef"/>.</summary>
    public string OfferMarketRef { get; set; } = "";

    /// <summary>Disk to ask for, GB — weights plus room for ComfyUI and output.</summary>
    public int DiskGb { get; set; } = 80;

    /// <summary>Name to stamp on the instance. MUST start with
    /// <see cref="GpuWorkerService.NamePrefix"/> — that prefix is the only
    /// thing standing between the orphan sweep and the user's own machines.</summary>
    public string Name { get; set; } = "";

    public string DockerImage { get; set; } = "";

    /// <summary>Shell script the vendor runs on boot. This is where ComfyUI
    /// gets installed, the weights get pulled, and the auth proxy comes up.</summary>
    public string StartScript { get; set; } = "";

    /// <summary>Best-effort env vars. Every vendor documents these
    /// differently (or not at all), so <see cref="GpuProvisioning"/> also
    /// folds them into the start script as plain exports.</summary>
    public Dictionary<string, string> Env { get; } = new();

    public int GpuCount { get; set; } = 1;

    /// <summary>The container port ComfyUI's auth proxy listens on.</summary>
    public int ExposedPort { get; set; } = GpuProvisioning.ProxyPort;
}

/// <summary>Live state of a rented machine, as the vendor reports it.</summary>
public sealed class GpuInstance
{
    public string Id { get; set; } = "";

    /// <summary>Vendor-side name. Empty when the vendor won't tell us —
    /// which the orphan sweep treats as "do not touch".</summary>
    public string Name { get; set; } = "";

    public GpuInstanceState State { get; set; } = GpuInstanceState.Unknown;

    /// <summary>Raw vendor status string, kept for display + debugging.</summary>
    public string RawStatus { get; set; } = "";

    public string GpuModel { get; set; } = "";
    public decimal PricePerHourUsd { get; set; }
    public DateTime? StartedAt { get; set; }

    /// <summary>Public HTTPS endpoint for our exposed port, if the vendor
    /// fronts it with a tunnel. Preferred over <see cref="DirectUrl"/>
    /// because it terminates TLS.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>host:port fallback when there's no tunnel.</summary>
    public string? DirectUrl { get; set; }

    /// <summary>The URL our ComfyUI client should hit. Tunnel wins.</summary>
    public string? EndpointUrl => !string.IsNullOrWhiteSpace(ProxyUrl) ? ProxyUrl : DirectUrl;

    /// <summary>Errors and warnings the vendor attached to the instance.</summary>
    public string? StatusMessage { get; set; }
}

public enum GpuInstanceState
{
    Unknown,
    /// <summary>Vendor is allocating/booting the container.</summary>
    Provisioning,
    /// <summary>Container is up. Says nothing about whether ComfyUI is ready —
    /// that's what the health probe is for.</summary>
    Running,
    Stopped,
    Failed,
}

/// <summary>Thrown for vendor-API failures we want surfaced verbatim in the UI.</summary>
public class GpuRentalException : Exception
{
    public GpuRentalException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
    public GpuRentalException(string message, Exception inner) : base(message, inner) { }

    /// <summary>The vendor's HTTP status, when the failure was an HTTP answer.
    /// Checked instead of searching the message for "404", which also matched
    /// an instance id such as 154041.</summary>
    public int? StatusCode { get; }
}

/// <summary>The vendor may have rented a machine, but we never saw it appear.
/// Carries what the next ticks need to find and tag it.</summary>
public sealed class GpuRentUnconfirmedException : GpuRentalException
{
    public GpuRentUnconfirmedException(string message, PendingRental pending) : base(message) => Pending = pending;
    public PendingRental Pending { get; }
}
