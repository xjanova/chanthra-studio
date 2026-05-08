using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

/// <summary>
/// Result of a "check for updates" call. Built from a GitHub Releases API
/// response (releases/latest) augmented with the local installed version
/// for comparison.
/// </summary>
public sealed class UpdateInfo : ObservableObject
{
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public bool HasUpdate { get; init; }

    /// <summary>Human-readable name of the release ("v0.2.0 — Sound Atelier polish").</summary>
    public string ReleaseName { get; init; } = "";

    /// <summary>Release body — Markdown from the GitHub release notes.</summary>
    public string Notes { get; init; } = "";

    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>Direct asset URL (e.g. "ChanthraStudio-v0.2.0-portable.zip").</summary>
    public string DownloadUrl { get; init; } = "";

    public string AssetName { get; init; } = "";

    public long AssetSizeBytes { get; init; }

    public string AssetSizeFormatted =>
        AssetSizeBytes <= 0 ? ""
        : AssetSizeBytes >= 1_048_576 ? $"{AssetSizeBytes / 1_048_576.0:F1} MB"
        : $"{AssetSizeBytes / 1024.0:F0} KB";
}
