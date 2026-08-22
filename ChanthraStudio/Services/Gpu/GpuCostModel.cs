using System;
using System.Collections.Generic;
using System.Linq;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// What one job on a given machine is estimated to cost end to end.
///
/// The point of this type is that <b>the cheapest machine is frequently the
/// most expensive one</b>. Warm-up on a rented box is almost entirely weight
/// downloading, and it is billed at the same hourly rate as rendering. A
/// 33 GB profile on a $0.25/hr box at 100 Mbps spends 51 paid minutes before
/// it renders a single frame ($0.21); the same profile on a $0.55/hr box at
/// 1000 Mbps warms up in ten ($0.10). Ranking by <c>$/hr</c> picks the first
/// one, charges twice as much, and makes the user wait forty minutes longer.
/// </summary>
/// <param name="TotalUsd">Warm-up plus expected working time, at this
/// machine's full hourly rate (GPU + disk).</param>
/// <param name="WarmupMinutes">Downloading the profile's weights plus the
/// fixed install overhead.</param>
/// <param name="WorkMinutes">How long we expect to hold the card after it is
/// ready — the user's typical session length, not a measurement.</param>
/// <param name="AssumedMbps">The link speed the estimate used.</param>
/// <param name="SpeedWasAssumed">True when the vendor did not report a speed
/// and <see cref="AssumedMbps"/> is our pessimistic stand-in. Surfaced so a
/// ranking built on a guess is never presented as a measurement.</param>
public sealed record GpuJobEstimate(
    decimal TotalUsd,
    double WarmupMinutes,
    double WorkMinutes,
    int AssumedMbps,
    bool SpeedWasAssumed)
{
    public decimal WarmupUsd => TotalUsd == 0m ? 0m
        : TotalUsd * (decimal)(WarmupMinutes / Math.Max(0.01, WarmupMinutes + WorkMinutes));

    public string Label =>
        $"~${TotalUsd:0.00} for this job · warm-up {WarmupMinutes:0} min @ {AssumedMbps} Mbps"
        + (SpeedWasAssumed ? " (speed not reported — assumed)" : "");
}

/// <summary>
/// Ranks marketplace offers by what the whole job will cost rather than by
/// sticker price.
/// </summary>
public static class GpuCostModel
{
    /// <summary>
    /// Link speed assumed for a machine whose vendor record has no speedtest
    /// figure. Deliberately pessimistic-but-plausible: assuming it is fast
    /// would let unmeasured machines win the ranking on a fiction, and
    /// discarding them outright would be the same "filter ate the market"
    /// failure the speed floor already risks.
    /// </summary>
    public const int UnknownSpeedMbps = 150;

    /// <summary>
    /// Minutes of warm-up that are not downloading: apt, the ComfyUI clone,
    /// pip, model load into VRAM. Matches
    /// <see cref="GpuModelProfile.EstimatedWarmupMinutes"/> so the two never
    /// disagree in the UI.
    /// </summary>
    public const double FixedWarmupMinutes = 6.0;

    /// <summary>Estimated cost of running one session on this offer.</summary>
    public static GpuJobEstimate Estimate(GpuOffer offer, GpuFilter filter)
    {
        var assumed = offer.DownloadMbps > 0;
        var mbps = assumed ? offer.DownloadMbps : UnknownSpeedMbps;

        // GB → megabits → minutes. 8192 rather than 8000: these are binary
        // gigabytes read from content-length, and the difference across a
        // 33 GB pull is a couple of paid minutes.
        var downloadMin = filter.WeightsGb * 8192.0 / Math.Max(1, mbps) / 60.0;
        var warmup = downloadMin + filter.FixedWarmupMinutes;
        var work = Math.Max(0, filter.ExpectedWorkMinutes);

        var hours = (decimal)((warmup + work) / 60.0);
        return new GpuJobEstimate(
            Math.Round(offer.TotalPricePerHourUsd * hours, 4),
            warmup, work, mbps, !assumed);
    }

    /// <summary>
    /// Cheapest-job-first. Sticker price breaks ties so that two machines
    /// which would cost the same for this job still prefer the one that
    /// costs less if the session runs long.
    /// </summary>
    public static List<GpuOffer> Rank(IEnumerable<GpuOffer> offers, GpuFilter filter)
        => offers
            .OrderBy(o => Estimate(o, filter).TotalUsd)
            .ThenBy(o => o.TotalPricePerHourUsd)
            .ThenByDescending(o => o.DownloadMbps)
            .ToList();

    /// <summary>
    /// One line explaining why <paramref name="pick"/> beat the machine with
    /// the lowest hourly rate. Empty when they are the same machine — there
    /// is nothing to justify, and a "picked the cheapest" line every time
    /// would train the user to ignore this field.
    /// </summary>
    public static string ExplainPick(GpuOffer pick, IReadOnlyList<GpuOffer> all, GpuFilter filter)
    {
        if (all.Count < 2) return "";
        var cheapest = all.OrderBy(o => o.TotalPricePerHourUsd).First();
        if (ReferenceEquals(cheapest, pick) || cheapest.Id == pick.Id) return "";

        var a = Estimate(pick, filter);
        var b = Estimate(cheapest, filter);
        if (b.TotalUsd <= a.TotalUsd) return "";

        return $"picked {pick.GpuModel} at ${pick.TotalPricePerHourUsd:0.00}/hr over " +
               $"{cheapest.GpuModel} at ${cheapest.TotalPricePerHourUsd:0.00}/hr — " +
               $"${a.TotalUsd:0.00} vs ${b.TotalUsd:0.00} for this job " +
               $"({a.WarmupMinutes:0} vs {b.WarmupMinutes:0} min of paid warm-up)";
    }
}
