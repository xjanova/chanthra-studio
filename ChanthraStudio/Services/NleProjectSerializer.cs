using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChanthraStudio.Services;

/// <summary>
/// Serialise the NLE editor's whole state to a portable JSON file
/// (<c>.chstudio</c>). The persisted form is content-addressed by clip
/// shot_id — re-opening looks the clips back up from the local clips
/// table, so an exported project file isn't self-contained (it assumes
/// the source clips still exist on disk). For sharing across machines
/// a future "bundle" command would zip the project file with its media.
///
/// Versioned via a top-level <c>schema</c> field so future format changes
/// can migrate gracefully instead of erroring on load.
/// </summary>
public static class NleProjectSerializer
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class ProjectFile
    {
        public int Schema { get; set; } = CurrentSchema;
        public string Name { get; set; } = "Untitled";
        public DateTimeOffset SavedAt { get; set; }

        public string OutputName { get; set; } = "";
        public int Fps { get; set; } = 30;
        public double CrossfadeSec { get; set; }

        /// <summary>"16:9" / "9:16" / "1:1" / "21:9". Default mirrors the
        /// EditorViewModel default so old projects without this field load
        /// cleanly.</summary>
        public string RenderAspect { get; set; } = "16:9";

        /// <summary>"draft" / "full" / "hi" — quality preset.</summary>
        public string RenderQuality { get; set; } = "full";

        public string? AudioPath { get; set; }
        public double AudioVolume { get; set; } = 1.0;
        public List<AudioEntry> AudioTracks { get; set; } = new();

        public List<SlotEntry> Timeline { get; set; } = new();
        public List<OverlayEntry> Overlay { get; set; } = new();
        public List<TitleEntry> Titles { get; set; } = new();
    }

    public sealed class AudioEntry
    {
        public string FilePath { get; set; } = "";
        public double StartSec { get; set; }
        public double Volume { get; set; } = 1.0;
        public double FadeInSec { get; set; }
        public double FadeOutSec { get; set; }
        public string Label { get; set; } = "";
    }

    public sealed class TitleEntry
    {
        public string Text { get; set; } = "";
        public double StartSec { get; set; }
        public double DurationSec { get; set; } = 3.0;
        public int FontSize { get; set; } = 64;
        public string Color { get; set; } = "0xD4A76A";
        public string Position { get; set; } = "C";
    }

    public sealed class SlotEntry
    {
        public string ShotId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public double DurationSec { get; set; }
    }

    public sealed class OverlayEntry
    {
        public string ShotId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public double StartSec { get; set; }
        public double DurationSec { get; set; }
        public double Scale { get; set; } = 0.3;
        public string Position { get; set; } = "TR";
    }

    public static void Save(string path, ProjectFile project)
    {
        project.SavedAt = DateTimeOffset.UtcNow;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOpts));
        ActivityLog.Info("nle", $"saved project · {project.Timeline.Count} slots · {project.Overlay.Count} overlays · {path}");
    }

    public static ProjectFile Load(string path)
    {
        var raw = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ProjectFile>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Project file deserialised to null.");
        if (project.Schema > CurrentSchema)
            throw new InvalidOperationException(
                $"Project file schema v{project.Schema} is newer than this build's v{CurrentSchema}. " +
                "Update Chanthra Studio to open it.");
        ActivityLog.Info("nle", $"loaded project · {project.Timeline.Count} slots · {path}");
        return project;
    }
}
