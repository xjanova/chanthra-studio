using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChanthraStudio.Services;

/// <summary>
/// Most-recently-used list of <c>.chstudio</c> project paths. Stored in
/// <c>{AppPaths.Root}/recent-projects.json</c> as a flat array of absolute
/// paths, newest first. Capped at <see cref="MaxEntries"/> = 5.
///
/// <para>
/// Paths that no longer exist on disk are pruned on every read — opens
/// don't surface phantom entries, and a "trash the file then come back"
/// flow self-heals.
/// </para>
/// </summary>
public sealed class RecentProjectsService
{
    public const int MaxEntries = 5;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private static string StorePath => Path.Combine(AppPaths.Root, "recent-projects.json");

    /// <summary>Return the current MRU list, newest first. Phantom paths
    /// (file gone from disk) get pruned before return.</summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return Array.Empty<string>();
            var raw = File.ReadAllText(StorePath);
            var list = JsonSerializer.Deserialize<List<string>>(raw, JsonOpts) ?? new();
            // Drop entries whose file is gone — keep the in-memory copy
            // accurate without rewriting the file (cheaper).
            return list.Where(File.Exists).ToList();
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("recent", "load failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>Promote a path to the top of the MRU. Idempotent — if the
    /// path was already in the list it gets moved to position 0 instead
    /// of duplicating.</summary>
    public void Promote(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var list = Load().ToList();
            // Move-to-front: remove existing occurrences (case-insensitive)
            // and prepend.
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            // Trim to cap.
            if (list.Count > MaxEntries) list = list.Take(MaxEntries).ToList();
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list, JsonOpts));
            ActivityLog.Info("recent", $"promoted {Path.GetFileName(path)} · {list.Count} entries");
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("recent", "promote failed: " + ex.Message);
        }
    }

    /// <summary>Drop a specific path from the MRU (e.g. user clicked a
    /// "remove from recent" affordance). Idempotent.</summary>
    public void Forget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var list = Load().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("recent", "forget failed: " + ex.Message);
        }
    }
}
