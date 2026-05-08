using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChanthraStudio.Helpers;
using Dapper;

namespace ChanthraStudio.Services;

/// <summary>
/// All persisted user settings. Stored in the SQLite <c>settings</c> table
/// since schema v2 — each property maps to one row keyed by its camelCase
/// name (or <c>apikey:&lt;providerId&gt;</c> for encrypted API keys). The
/// previous JSON file (<c>settings.json</c>) is auto-migrated on first run
/// then renamed with a <c>.migrated</c> suffix.
///
/// Encryption: API key values land in the table already DPAPI-encrypted —
/// the indexer + <see cref="SetApiKey"/> handle the protect/unprotect, so
/// the row's <c>value</c> column is the ciphertext blob and
/// <c>is_secret = 1</c> for inspection / backup tooling.
/// </summary>
public sealed class AppSettings
{
    private readonly Database _db;

    public string ComfyUiUrl { get; set; } = "http://127.0.0.1:8188";

    public Dictionary<string, string> EncryptedKeys { get; set; } = new();

    public string ActiveLlm { get; set; } = "gemini";
    public string ActiveVideo { get; set; } = "comfyui";
    public string PostFacebookPageId { get; set; } = "";
    public string PostWebhookUrl { get; set; } = "";
    public int AutosaveSeconds { get; set; } = 30;
    public string Theme { get; set; } = "lunar";

    public DateTime? LastSavedAt { get; private set; }

    public AppSettings(Database db) { _db = db; }

    /// <summary>Reads a decrypted API key. Empty string if unset.</summary>
    public string this[string providerId]
        => EncryptedKeys.TryGetValue(providerId, out var ct) ? DpapiCrypto.Unprotect(ct) : "";

    /// <summary>Stores an API key. Pass empty/null to remove it.</summary>
    public void SetApiKey(string providerId, string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            EncryptedKeys.Remove(providerId);
        else
            EncryptedKeys[providerId] = DpapiCrypto.Protect(plaintext);
    }

    public bool HasApiKey(string providerId)
        => EncryptedKeys.TryGetValue(providerId, out var ct) && !string.IsNullOrEmpty(ct);

    // -------- persistence -------------------------------------------------

    public static AppSettings Load(Database db)
    {
        var s = new AppSettings(db);
        using var c = db.Open();
        var rows = c.Query<SettingRow>("SELECT key, value, is_secret FROM settings").ToList();

        if (rows.Count == 0)
        {
            // First run on schema v2 — try migrating the legacy JSON file
            // if one is sitting next to the .exe. We only do this once.
            TryImportLegacyJson(s);
            // If we imported anything, persist it to the DB right away.
            if (s.EncryptedKeys.Count > 0
                || s.ComfyUiUrl != "http://127.0.0.1:8188"
                || !string.IsNullOrEmpty(s.PostFacebookPageId)
                || !string.IsNullOrEmpty(s.PostWebhookUrl))
            {
                s.Save();
            }
            return s;
        }

        foreach (var r in rows) ApplyRow(s, r);
        return s;
    }

    public void Save()
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Upsert(c, tx, "comfyUiUrl", ComfyUiUrl, false, now);
        Upsert(c, tx, "activeLlm", ActiveLlm, false, now);
        Upsert(c, tx, "activeVideo", ActiveVideo, false, now);
        Upsert(c, tx, "postFacebookPageId", PostFacebookPageId, false, now);
        Upsert(c, tx, "postWebhookUrl", PostWebhookUrl, false, now);
        Upsert(c, tx, "autosaveSeconds", AutosaveSeconds.ToString(), false, now);
        Upsert(c, tx, "theme", Theme, false, now);

        // Encrypted API keys — value column carries the ciphertext as-is.
        foreach (var (id, ciphertext) in EncryptedKeys)
        {
            Upsert(c, tx, "apikey:" + id, ciphertext, true, now);
        }

        // Drop any apikey:* rows the user removed since last save.
        if (EncryptedKeys.Count == 0)
        {
            c.Execute("DELETE FROM settings WHERE key LIKE 'apikey:%'", transaction: tx);
        }
        else
        {
            var keep = string.Join(",", EncryptedKeys.Keys.Select(k => $"'apikey:{k.Replace("'", "''")}'"));
            c.Execute($"DELETE FROM settings WHERE key LIKE 'apikey:%' AND key NOT IN ({keep})", transaction: tx);
        }

        tx.Commit();
        LastSavedAt = DateTime.UtcNow;
    }

    // -------- internals ---------------------------------------------------

    private sealed class SettingRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public int Is_secret { get; set; }
    }

    private static void ApplyRow(AppSettings s, SettingRow r)
    {
        if (r.Key.StartsWith("apikey:", StringComparison.Ordinal))
        {
            var id = r.Key["apikey:".Length..];
            // Stored as ciphertext — keep as-is in EncryptedKeys.
            s.EncryptedKeys[id] = r.Value;
            return;
        }
        switch (r.Key)
        {
            case "comfyUiUrl":         s.ComfyUiUrl = r.Value; break;
            case "activeLlm":          s.ActiveLlm = r.Value; break;
            case "activeVideo":        s.ActiveVideo = r.Value; break;
            case "postFacebookPageId": s.PostFacebookPageId = r.Value; break;
            case "postWebhookUrl":     s.PostWebhookUrl = r.Value; break;
            case "autosaveSeconds":    if (int.TryParse(r.Value, out var n)) s.AutosaveSeconds = n; break;
            case "theme":              s.Theme = r.Value; break;
            // unknown keys are silently dropped — likely a downgrade or a
            // future-version setting we don't recognise.
        }
    }

    private static void Upsert(System.Data.IDbConnection c, System.Data.IDbTransaction tx,
                               string key, string value, bool isSecret, long now)
    {
        c.Execute("""
            INSERT INTO settings (key, value, is_secret, updated_at)
            VALUES ($key, $value, $secret, $now)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                is_secret = excluded.is_secret,
                updated_at = excluded.updated_at
            """,
            new { key, value, secret = isSecret ? 1 : 0, now },
            transaction: tx);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void TryImportLegacyJson(AppSettings target)
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var legacy = JsonSerializer.Deserialize<LegacySettings>(json, JsonOpts);
            if (legacy is null) return;
            target.ComfyUiUrl = legacy.ComfyUiUrl ?? target.ComfyUiUrl;
            target.ActiveLlm = legacy.ActiveLlm ?? target.ActiveLlm;
            target.ActiveVideo = legacy.ActiveVideo ?? target.ActiveVideo;
            target.PostFacebookPageId = legacy.PostFacebookPageId ?? target.PostFacebookPageId;
            target.PostWebhookUrl = legacy.PostWebhookUrl ?? target.PostWebhookUrl;
            target.AutosaveSeconds = legacy.AutosaveSeconds ?? target.AutosaveSeconds;
            target.Theme = legacy.Theme ?? target.Theme;
            if (legacy.EncryptedKeys is not null)
            {
                foreach (var (id, ct) in legacy.EncryptedKeys)
                    target.EncryptedKeys[id] = ct;
            }
            // Rename the file so we don't import it again on next launch.
            var migratedPath = path + ".migrated";
            try
            {
                if (File.Exists(migratedPath)) File.Delete(migratedPath);
                File.Move(path, migratedPath);
            }
            catch
            {
                // If the rename fails, at least the imported values are in the DB.
                // Worst case: we'll re-import idempotently next launch.
            }
        }
        catch
        {
            // Corrupt legacy file — start fresh.
        }
    }

    private sealed class LegacySettings
    {
        [JsonPropertyName("comfyUiUrl")]
        public string? ComfyUiUrl { get; set; }
        [JsonPropertyName("encryptedKeys")]
        public Dictionary<string, string>? EncryptedKeys { get; set; }
        [JsonPropertyName("activeLlm")]
        public string? ActiveLlm { get; set; }
        [JsonPropertyName("activeVideo")]
        public string? ActiveVideo { get; set; }
        [JsonPropertyName("postFacebookPageId")]
        public string? PostFacebookPageId { get; set; }
        [JsonPropertyName("postWebhookUrl")]
        public string? PostWebhookUrl { get; set; }
        [JsonPropertyName("autosaveSeconds")]
        public int? AutosaveSeconds { get; set; }
        [JsonPropertyName("theme")]
        public string? Theme { get; set; }
    }
}
