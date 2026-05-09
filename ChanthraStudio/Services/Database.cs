using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;

namespace ChanthraStudio.Services;

/// <summary>
/// SQLite bootstrap + connection factory. Schema is applied idempotently on
/// startup using a numeric `schema_version` row in the `meta` table — every
/// migration is a numbered case in <see cref="Migrate"/> that runs only when
/// the stored version is below it.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database()
    {
        _connectionString = $"Data Source={AppPaths.DatabaseFile};Cache=Shared;Foreign Keys=True";
    }

    public Database(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IDbConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public void Bootstrap()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Migrate(c, tx);
        tx.Commit();
    }

    private static void Migrate(IDbConnection c, IDbTransaction tx)
    {
        Exec(c, tx, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);

        var current = GetSchemaVersion(c, tx);

        if (current < 2)
        {
            // settings — primary store for AppSettings since v2.
            // Values are plaintext for non-secrets; DPAPI ciphertext for
            // apikey:* rows (caller protects/unprotects before/after we touch
            // the column). The is_secret flag is descriptive metadata —
            // useful for backups + inspection, not enforced.
            Exec(c, tx, """
                CREATE TABLE IF NOT EXISTS settings (
                    key        TEXT PRIMARY KEY,
                    value      TEXT NOT NULL,
                    is_secret  INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL
                )
                """);
        }

        if (current < 1)
        {
            Exec(c, tx, """
                CREATE TABLE projects (
                    id            TEXT PRIMARY KEY,
                    title         TEXT NOT NULL,
                    created_at    INTEGER NOT NULL,
                    updated_at    INTEGER NOT NULL,
                    cover_path    TEXT
                );
                CREATE TABLE sequences (
                    id            TEXT PRIMARY KEY,
                    project_id    TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    title         TEXT NOT NULL,
                    sort_order    INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE shots (
                    id            TEXT PRIMARY KEY,
                    sequence_id   TEXT NOT NULL REFERENCES sequences(id) ON DELETE CASCADE,
                    number        TEXT NOT NULL,
                    title         TEXT NOT NULL,
                    description   TEXT,
                    prompt        TEXT NOT NULL DEFAULT '',
                    style_id      TEXT,
                    model_id      TEXT,
                    aspect        TEXT NOT NULL DEFAULT 'wide',
                    duration_sec  REAL NOT NULL DEFAULT 8.0,
                    motion        REAL NOT NULL DEFAULT 0.7,
                    seed_a        INTEGER,
                    seed_b        INTEGER,
                    hd4k          INTEGER NOT NULL DEFAULT 1,
                    audio         INTEGER NOT NULL DEFAULT 1,
                    cam_mode      TEXT NOT NULL DEFAULT 'locked',
                    status        TEXT NOT NULL DEFAULT 'queue',
                    progress      REAL NOT NULL DEFAULT 0,
                    thumb_path    TEXT,
                    video_path    TEXT,
                    created_at    INTEGER NOT NULL,
                    updated_at    INTEGER NOT NULL
                );
                CREATE TABLE clips (
                    id            TEXT PRIMARY KEY,
                    shot_id       TEXT NOT NULL REFERENCES shots(id) ON DELETE CASCADE,
                    duration_ms   INTEGER NOT NULL,
                    file_path     TEXT NOT NULL,
                    poster_path   TEXT,
                    created_at    INTEGER NOT NULL
                );
                CREATE TABLE generation_jobs (
                    id            TEXT PRIMARY KEY,
                    shot_id       TEXT NOT NULL REFERENCES shots(id) ON DELETE CASCADE,
                    provider      TEXT NOT NULL,
                    status        TEXT NOT NULL,
                    submitted_at  INTEGER NOT NULL,
                    completed_at  INTEGER,
                    error_message TEXT,
                    request_blob  TEXT,
                    response_blob TEXT
                );
                CREATE TABLE post_history (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    clip_id       TEXT NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    target        TEXT NOT NULL,
                    target_id     TEXT,
                    posted_at     INTEGER NOT NULL,
                    success       INTEGER NOT NULL,
                    response_blob TEXT
                );
                CREATE INDEX idx_shots_sequence ON shots(sequence_id);
                CREATE INDEX idx_shots_status ON shots(status);
                CREATE INDEX idx_clips_shot ON clips(shot_id);
                CREATE INDEX idx_jobs_shot ON generation_jobs(shot_id);
                CREATE INDEX idx_post_clip ON post_history(clip_id);
                """);
            SetSchemaVersion(c, tx, 1);
        }

        if (current < 2)
        {
            SetSchemaVersion(c, tx, 2);
        }

        if (current < 3)
        {
            // Auto-schedule tables (phase 6.6).
            //
            // schedules — one row per recurring auto-generation job. The
            // ScheduleService scans this table once a minute, fires any
            // entries whose next_fire_at <= now, then composes a Shot from
            // the template and dispatches through the existing Generation
            // pipeline.
            //
            // schedule_runs — append-only history of every fire so the user
            // can see a "last 30 days" timeline per schedule (planned, but
            // even just for debugging "did it run?" it's invaluable).
            Exec(c, tx, """
                CREATE TABLE schedules (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    name            TEXT    NOT NULL,
                    prompt_template TEXT    NOT NULL,
                    negative_prompt TEXT,
                    workflow        TEXT,
                    route           TEXT    NOT NULL DEFAULT 'comfyui',
                    style_id        TEXT,
                    aspect          TEXT    NOT NULL DEFAULT 'Wide',
                    camera          TEXT    NOT NULL DEFAULT 'Push',
                    duration_sec    REAL    NOT NULL DEFAULT 8,
                    motion          REAL    NOT NULL DEFAULT 0.7,
                    -- 'daily_slots' (spec="08:00,12:00,18:00") or
                    -- 'interval'    (spec="60" minutes)
                    kind            TEXT    NOT NULL DEFAULT 'daily_slots',
                    spec            TEXT    NOT NULL,
                    auto_post       INTEGER NOT NULL DEFAULT 0,
                    post_target     TEXT,
                    last_fire_at    INTEGER,
                    next_fire_at    INTEGER,
                    is_enabled      INTEGER NOT NULL DEFAULT 1,
                    created_at      INTEGER NOT NULL,
                    updated_at      INTEGER NOT NULL
                );
                CREATE TABLE schedule_runs (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    schedule_id   INTEGER NOT NULL REFERENCES schedules(id) ON DELETE CASCADE,
                    fired_at      INTEGER NOT NULL,
                    job_id        TEXT,
                    status        TEXT    NOT NULL,
                    error_message TEXT
                );
                CREATE INDEX idx_schedules_enabled ON schedules(is_enabled, next_fire_at);
                CREATE INDEX idx_schedule_runs_sched ON schedule_runs(schedule_id, fired_at DESC);
                """);
            SetSchemaVersion(c, tx, 3);
        }
    }

    private static int GetSchemaVersion(IDbConnection c, IDbTransaction tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        var v = cmd.ExecuteScalar();
        return v is null ? 0 : int.Parse((string)v);
    }

    private static void SetSchemaVersion(IDbConnection c, IDbTransaction tx, int version)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ('schema_version', $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = "$v";
        p.Value = version.ToString();
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(IDbConnection c, IDbTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
