using System;
using System.Globalization;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// The spending limits, read fresh from settings on every tick so a change
/// takes effect immediately rather than at next launch.
///
/// Every default here is deliberately timid. A rented GPU bills by the
/// second whether or not anyone is watching, and the failure mode of a
/// generous default is a bill, not a warning. The user can raise any of
/// these in the GPU panel once they know what their runs actually cost.
/// </summary>
public sealed class GpuGuardrails
{
    /// <summary>Master switch. Off by default — this feature spends money,
    /// so it does not arm itself.</summary>
    public bool Enabled { get; set; }

    public string ProviderId { get; set; } = "simplepod";

    /// <summary>Overridable vendor API host, so a moved endpoint is a
    /// settings edit rather than a new build.</summary>
    public string ApiBase { get; set; } = SimplePodProvider.DefaultApiBase;

    /// <summary>Which profile to rent when nothing more specific is chosen.</summary>
    public string ProfileKey { get; set; } = "sdxl";

    /// <summary>Hard ceiling on hourly rate. Machines above it are never rented.</summary>
    public decimal MaxPricePerHourUsd { get; set; } = 0.60m;

    /// <summary>Spend ceiling per local day, counting live workers at their
    /// cost so far. Hitting it drains idle workers and blocks new rentals.</summary>
    public decimal DailyBudgetUsd { get; set; } = 10m;

    /// <summary>How many machines may be up at once.</summary>
    public int MaxConcurrentWorkers { get; set; } = 1;

    /// <summary>Idle minutes before a ready worker is released.</summary>
    public int IdleTimeoutMinutes { get; set; } = 10;

    /// <summary>Absolute age cap. The last line of defence: whatever else
    /// goes wrong, no machine outlives this.</summary>
    public int MaxLifetimeMinutes { get; set; } = 240;

    /// <summary>How long a worker may stay in warm-up before we give up on
    /// it. Needs to comfortably exceed the download time of the heaviest
    /// profile or we would kill healthy machines mid-download.</summary>
    public int WarmupTimeoutMinutes { get; set; } = 75;

    /// <summary>Link-speed floor when shopping. Low bandwidth is billed
    /// warm-up time, so this is a cost setting more than a quality one.</summary>
    public int MinDownloadMbps { get; set; } = 500;

    /// <summary>Terminate everything when the app closes. Strongly
    /// recommended: unlike a server, a desktop app that is not running
    /// cannot reap anything, and the meter does not care.</summary>
    public bool TerminateOnExit { get; set; } = true;

    // ------------------------------------------------------------ persistence

    private const string P = "gpu:";

    public static GpuGuardrails Load(AppSettings s)
    {
        var g = new GpuGuardrails();
        g.Enabled = Flag(s, "enabled", g.Enabled);
        g.ProviderId = Text(s, "provider", g.ProviderId);
        g.ApiBase = Text(s, "apiBase", g.ApiBase);
        g.ProfileKey = Text(s, "profile", g.ProfileKey);
        g.MaxPricePerHourUsd = Money(s, "maxPricePerHourUsd", g.MaxPricePerHourUsd);
        g.DailyBudgetUsd = Money(s, "dailyBudgetUsd", g.DailyBudgetUsd);
        g.MaxConcurrentWorkers = Num(s, "maxConcurrentWorkers", g.MaxConcurrentWorkers, 1, 8);
        g.IdleTimeoutMinutes = Num(s, "idleTimeoutMinutes", g.IdleTimeoutMinutes, 1, 240);
        g.MaxLifetimeMinutes = Num(s, "maxLifetimeMinutes", g.MaxLifetimeMinutes, 5, 1440);
        g.WarmupTimeoutMinutes = Num(s, "warmupTimeoutMinutes", g.WarmupTimeoutMinutes, 5, 240);
        g.MinDownloadMbps = Num(s, "minDownloadMbps", g.MinDownloadMbps, 0, 10_000);
        g.TerminateOnExit = Flag(s, "terminateOnExit", g.TerminateOnExit);
        return g;
    }

    public void SaveTo(AppSettings s)
    {
        s.SetSetting(P + "enabled", Enabled ? "1" : "0");
        s.SetSetting(P + "provider", ProviderId);
        s.SetSetting(P + "apiBase", ApiBase);
        s.SetSetting(P + "profile", ProfileKey);
        s.SetSetting(P + "maxPricePerHourUsd", Str(MaxPricePerHourUsd));
        s.SetSetting(P + "dailyBudgetUsd", Str(DailyBudgetUsd));
        s.SetSetting(P + "maxConcurrentWorkers", Str(MaxConcurrentWorkers));
        s.SetSetting(P + "idleTimeoutMinutes", Str(IdleTimeoutMinutes));
        s.SetSetting(P + "maxLifetimeMinutes", Str(MaxLifetimeMinutes));
        s.SetSetting(P + "warmupTimeoutMinutes", Str(WarmupTimeoutMinutes));
        s.SetSetting(P + "minDownloadMbps", Str(MinDownloadMbps));
        // "0" is meaningful here — SetSetting drops empty strings, and a
        // bool written as "" would silently read back as the default (true),
        // re-arming terminate-on-exit against the user's wishes.
        s.SetSetting(P + "terminateOnExit", TerminateOnExit ? "1" : "0");
        s.Save();
    }

    /// <summary>Marketplace filter for a given profile under these limits.</summary>
    public GpuFilter ToFilter(GpuModelProfile profile) => new()
    {
        MinVramGb = profile.MinVramGb,
        MinDiskGb = profile.RequiredDiskGb,
        MaxPricePerHourUsd = MaxPricePerHourUsd,
        MinDownloadMbps = MinDownloadMbps,
    };

    private static string Str(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Text(AppSettings s, string key, string fallback)
    {
        var v = s.GetSetting(P + key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static bool Flag(AppSettings s, string key, bool fallback)
    {
        var v = s.GetSetting(P + key);
        return string.IsNullOrEmpty(v) ? fallback : v != "0";
    }

    private static decimal Money(AppSettings s, string key, decimal fallback)
        => decimal.TryParse(s.GetSetting(P + key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
           && v >= 0 ? v : fallback;

    private static int Num(AppSettings s, string key, int fallback, int min, int max)
        => int.TryParse(s.GetSetting(P + key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
           ? Math.Clamp(v, min, max) : fallback;
}
