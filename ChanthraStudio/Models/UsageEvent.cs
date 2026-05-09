using System;

namespace ChanthraStudio.Models;

/// <summary>One billable provider call. Append-only audit row.</summary>
public sealed class UsageEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string ProviderId { get; set; } = "";
    public string ModelSlug { get; set; } = "";

    /// <summary>"tokens" | "chars" | "images" | "seconds"</summary>
    public string UnitKind { get; set; } = "tokens";

    /// <summary>Tokens-in / chars-in / images / seconds (depends on UnitKind).</summary>
    public long InputUnits { get; set; }

    /// <summary>Output tokens (only meaningful when UnitKind = "tokens").</summary>
    public long OutputUnits { get; set; }

    public double CostUsd { get; set; }

    /// <summary>USD→THB rate snapshot at the time of the call.</summary>
    public double FxRate { get; set; } = 36;

    public double CostThb { get; set; }

    public string? Note { get; set; }

    public string CostThbLabel => $"฿{CostThb:N2}";
    public string UnitLabel => UnitKind switch
    {
        "tokens" => $"{InputUnits + OutputUnits:N0} tok",
        "chars" => $"{InputUnits:N0} chars",
        "images" => $"{InputUnits:N0} img",
        "seconds" => $"{InputUnits:N1}s",
        _ => $"{InputUnits} {UnitKind}",
    };
}
