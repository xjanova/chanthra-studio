using System;
using System.Collections.Generic;
using System.Linq;
using ChanthraStudio.Models;
using Dapper;

namespace ChanthraStudio.Services;

public sealed class UsageRepository
{
    private readonly Database _db;

    public UsageRepository(Database db) { _db = db; }

    public long Insert(UsageEvent e)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO usage_events
                (occurred_at, provider_id, model_slug, unit_kind,
                 input_units, output_units, cost_usd, fx_rate, cost_thb, note)
            VALUES ($at, $pid, $slug, $kind, $inu, $outu, $usd, $fx, $thb, $note);
            SELECT last_insert_rowid();
            """, new
            {
                at = e.OccurredAt.ToUnixTimeSeconds(),
                pid = e.ProviderId,
                slug = e.ModelSlug,
                kind = e.UnitKind,
                inu = e.InputUnits,
                outu = e.OutputUnits,
                usd = e.CostUsd,
                fx = e.FxRate,
                thb = e.CostThb,
                note = e.Note,
            });
    }

    public IReadOnlyList<UsageEvent> Recent(int limit = 200)
    {
        using var c = _db.Open();
        var rows = c.Query<Row>("""
            SELECT * FROM usage_events ORDER BY occurred_at DESC LIMIT $limit
            """, new { limit }).ToList();
        return rows.Select(Hydrate).ToList();
    }

    /// <summary>(totalThb, totalCalls) for events whose occurred_at >= sinceUtc.</summary>
    public (double Thb, long Calls) Total(DateTimeOffset sinceUtc)
    {
        using var c = _db.Open();
        var s = sinceUtc.ToUnixTimeSeconds();
        var thb = c.ExecuteScalar<double?>(
            "SELECT COALESCE(SUM(cost_thb), 0) FROM usage_events WHERE occurred_at >= $s",
            new { s }) ?? 0;
        var calls = c.ExecuteScalar<long?>(
            "SELECT COUNT(*) FROM usage_events WHERE occurred_at >= $s",
            new { s }) ?? 0;
        return (thb, calls);
    }

    /// <summary>Cost breakdown grouped by provider_id since the cutoff.</summary>
    public IReadOnlyList<(string Provider, double Thb, long Calls)> TotalByProvider(DateTimeOffset sinceUtc)
    {
        using var c = _db.Open();
        return c.Query<(string, double, long)>("""
            SELECT provider_id, COALESCE(SUM(cost_thb), 0), COUNT(*)
            FROM usage_events
            WHERE occurred_at >= $s
            GROUP BY provider_id
            ORDER BY 2 DESC
            """, new { s = sinceUtc.ToUnixTimeSeconds() }).ToList();
    }

    public IReadOnlyList<(string Model, double Thb, long Calls)> TotalByModel(DateTimeOffset sinceUtc, int limit = 12)
    {
        using var c = _db.Open();
        return c.Query<(string, double, long)>("""
            SELECT model_slug, COALESCE(SUM(cost_thb), 0), COUNT(*)
            FROM usage_events
            WHERE occurred_at >= $s
            GROUP BY model_slug
            ORDER BY 2 DESC
            LIMIT $limit
            """, new { s = sinceUtc.ToUnixTimeSeconds(), limit }).ToList();
    }

    private static UsageEvent Hydrate(Row r) => new()
    {
        Id = r.Id,
        OccurredAt = DateTimeOffset.FromUnixTimeSeconds(r.Occurred_at),
        ProviderId = r.Provider_id,
        ModelSlug = r.Model_slug,
        UnitKind = r.Unit_kind,
        InputUnits = r.Input_units,
        OutputUnits = r.Output_units,
        CostUsd = r.Cost_usd,
        FxRate = r.Fx_rate,
        CostThb = r.Cost_thb,
        Note = r.Note,
    };

    private sealed class Row
    {
        public long Id { get; set; }
        public long Occurred_at { get; set; }
        public string Provider_id { get; set; } = "";
        public string Model_slug { get; set; } = "";
        public string Unit_kind { get; set; } = "";
        public long Input_units { get; set; }
        public long Output_units { get; set; }
        public double Cost_usd { get; set; }
        public double Fx_rate { get; set; }
        public double Cost_thb { get; set; }
        public string? Note { get; set; }
    }
}
