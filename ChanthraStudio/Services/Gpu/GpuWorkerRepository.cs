using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// Persistence for <see cref="GpuWorker"/>. Schema lives in
/// <see cref="Database"/> v6.
///
/// Rows are never deleted. A terminated worker's row is the receipt for what
/// it cost, and the daily budget check sums history rather than live state.
/// </summary>
public sealed class GpuWorkerRepository
{
    private readonly Database _db;

    public GpuWorkerRepository(Database db) { _db = db; }

    private const string Columns = """
        id, provider_id, instance_id, name, profile_key, status, stage, stage_detail,
        gpu_model, price_hour_usd, endpoint_url, auth_token, created_at, ready_at,
        last_seen_at, last_job_at, terminated_at, terminate_reason,
        render_seconds, jobs_done, error_message
        """;

    public void Insert(GpuWorker w)
    {
        using var c = _db.Open();
        c.Execute($"""
            INSERT INTO gpu_workers ({Columns}) VALUES (
                $id, $providerId, $instanceId, $name, $profileKey, $status, $stage, $stageDetail,
                $gpuModel, $price, $endpoint, $token, $createdAt, $readyAt,
                $lastSeenAt, $lastJobAt, $terminatedAt, $terminateReason,
                $renderSeconds, $jobsDone, $error
            )
            """, ToParams(w));
    }

    public void Update(GpuWorker w)
    {
        using var c = _db.Open();
        c.Execute("""
            UPDATE gpu_workers SET
                provider_id = $providerId, instance_id = $instanceId, name = $name,
                profile_key = $profileKey, status = $status, stage = $stage,
                stage_detail = $stageDetail, gpu_model = $gpuModel, price_hour_usd = $price,
                endpoint_url = $endpoint, auth_token = $token, created_at = $createdAt,
                ready_at = $readyAt, last_seen_at = $lastSeenAt, last_job_at = $lastJobAt,
                terminated_at = $terminatedAt, terminate_reason = $terminateReason,
                render_seconds = $renderSeconds, jobs_done = $jobsDone, error_message = $error
            WHERE id = $id
            """, ToParams(w));
    }

    public GpuWorker? Find(string id)
    {
        using var c = _db.Open();
        var row = c.QuerySingleOrDefault<Row>(
            $"SELECT {Columns} FROM gpu_workers WHERE id = $id", new { id });
        return row is null ? null : Hydrate(row);
    }

    /// <summary>Workers we still believe are costing money.</summary>
    public IReadOnlyList<GpuWorker> Live()
    {
        using var c = _db.Open();
        var rows = c.Query<Row>($"""
            SELECT {Columns} FROM gpu_workers
            WHERE status IN ('Renting','Warming','Ready','Busy')
            ORDER BY created_at ASC
            """).ToList();
        return rows.Select(Hydrate).ToList();
    }

    /// <summary>Most recent rows, live and dead, for the history panel.</summary>
    public IReadOnlyList<GpuWorker> Recent(int limit = 50)
    {
        using var c = _db.Open();
        var rows = c.Query<Row>($"""
            SELECT {Columns} FROM gpu_workers
            ORDER BY created_at DESC LIMIT $limit
            """, new { limit }).ToList();
        return rows.Select(Hydrate).ToList();
    }

    /// <summary>
    /// Total USD committed since <paramref name="sinceUtc"/>. Live workers
    /// count at their cost *so far*, which is what makes this usable as a
    /// budget gate: a machine that has been up two hours has already spent
    /// that money whether or not it has been terminated yet.
    /// </summary>
    public decimal SpendSince(DateTime sinceUtc)
    {
        using var c = _db.Open();
        var rows = c.Query<Row>($"""
            SELECT {Columns} FROM gpu_workers WHERE created_at >= $since
            """, new { since = ToUnix(sinceUtc) }).ToList();
        return rows.Select(Hydrate).Sum(w => w.TotalCostUsd);
    }

    /// <summary>Spend so far today, in the user's local day.</summary>
    public decimal SpendToday() => SpendSince(DateTime.Now.Date.ToUniversalTime());

    // ------------------------------------------------------------- mapping

    private static object ToParams(GpuWorker w) => new
    {
        id = w.Id,
        providerId = w.ProviderId,
        instanceId = w.InstanceId,
        name = w.Name,
        profileKey = w.ProfileKey,
        status = w.Status.ToString(),
        stage = w.Stage,
        stageDetail = w.StageDetail,
        gpuModel = w.GpuModel,
        price = (double)w.PricePerHourUsd,
        endpoint = w.EndpointUrl,
        token = w.AuthTokenCipher,
        createdAt = ToUnix(w.CreatedAt),
        readyAt = ToUnixOrNull(w.ReadyAt),
        lastSeenAt = ToUnixOrNull(w.LastSeenAt),
        lastJobAt = ToUnixOrNull(w.LastJobAt),
        terminatedAt = ToUnixOrNull(w.TerminatedAt),
        terminateReason = w.TerminateReason,
        renderSeconds = w.RenderSeconds,
        jobsDone = w.JobsDone,
        error = w.ErrorMessage,
    };

    private static GpuWorker Hydrate(Row r) => new()
    {
        Id = r.Id,
        ProviderId = r.Provider_id,
        InstanceId = r.Instance_id,
        Name = r.Name,
        ProfileKey = r.Profile_key,
        Status = Enum.TryParse<GpuWorkerStatus>(r.Status, out var s) ? s : GpuWorkerStatus.Failed,
        Stage = r.Stage,
        StageDetail = r.Stage_detail,
        GpuModel = r.Gpu_model,
        PricePerHourUsd = (decimal)r.Price_hour_usd,
        EndpointUrl = r.Endpoint_url,
        AuthTokenCipher = r.Auth_token ?? "",
        CreatedAt = FromUnix(r.Created_at),
        ReadyAt = FromUnixOrNull(r.Ready_at),
        LastSeenAt = FromUnixOrNull(r.Last_seen_at),
        LastJobAt = FromUnixOrNull(r.Last_job_at),
        TerminatedAt = FromUnixOrNull(r.Terminated_at),
        TerminateReason = r.Terminate_reason,
        RenderSeconds = r.Render_seconds,
        JobsDone = r.Jobs_done,
        ErrorMessage = r.Error_message,
    };

    private static long ToUnix(DateTime t) => new DateTimeOffset(
        DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static long? ToUnixOrNull(DateTime? t) => t is null ? null : ToUnix(t.Value);

    private static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    private static DateTime? FromUnixOrNull(long? s) => s is null ? null : FromUnix(s.Value);

    /// <summary>Dapper row shape — column names as SQLite reports them.</summary>
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string Provider_id { get; set; } = "";
        public string? Instance_id { get; set; }
        public string Name { get; set; } = "";
        public string Profile_key { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Stage { get; set; }
        public string? Stage_detail { get; set; }
        public string? Gpu_model { get; set; }
        public double Price_hour_usd { get; set; }
        public string? Endpoint_url { get; set; }
        public string? Auth_token { get; set; }
        public long Created_at { get; set; }
        public long? Ready_at { get; set; }
        public long? Last_seen_at { get; set; }
        public long? Last_job_at { get; set; }
        public long? Terminated_at { get; set; }
        public string? Terminate_reason { get; set; }
        public double Render_seconds { get; set; }
        public int Jobs_done { get; set; }
        public string? Error_message { get; set; }
    }
}
