using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.ViewModels;

namespace ChanthraStudio.Services;

/// <summary>
/// Save / load the NLE editor's project state to / from <c>.chstudio</c>
/// files. Extracted from EditorViewModel in 7.14 so the VM stays focused
/// on UI state — this class owns the file-dialog plumbing, the clip
/// resolution from path/shotId, and the MRU promotion side effect.
///
/// <para>
/// API:
/// <list type="bullet">
///   <li><see cref="PromptSavePath"/> — show the Save As dialog, return the chosen path.</li>
///   <li><see cref="PromptOpenPath"/> — show the Open dialog, return the chosen path.</li>
///   <li><see cref="Save"/> — serialise the current state and promote in MRU.</li>
///   <li><see cref="Load"/> — read the file, rebuild the state, promote in MRU.</li>
/// </list>
/// </para>
///
/// <para>
/// All UI text (toast strings, dialog titles) returned via the <see cref="LoadResult"/>
/// record so the VM can drive its toast/snackbar without re-doing the math.
/// </para>
/// </summary>
public sealed class NleProjectFiles
{
    private readonly StudioContext _ctx;

    public NleProjectFiles(StudioContext ctx) { _ctx = ctx; }

    /// <summary>Default folder for .chstudio files. Created if missing.</summary>
    public string DefaultDirectory
    {
        get
        {
            var dir = Path.Combine(AppPaths.Root, "projects");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>Path to the autosave recovery file. Single slot —
    /// overwritten on every autosave tick, deleted on a successful
    /// explicit save or a clean exit so launching after a clean shutdown
    /// doesn't pop the recovery banner.</summary>
    public string AutosaveFilePath => Path.Combine(DefaultDirectory, ".autosave.chstudio");

    /// <summary>True when there's a recovery file from a previous session.
    /// Checked at app launch to decide whether to show the restore banner.</summary>
    public bool HasAutosaveRecovery
    {
        get
        {
            try
            {
                if (!File.Exists(AutosaveFilePath)) return false;
                // Only count it as a recovery candidate if it's at least 5
                // seconds old — anything fresher might be from a tick that
                // happened while the user was still actively saving.
                var age = DateTimeOffset.UtcNow - new FileInfo(AutosaveFilePath).LastWriteTimeUtc;
                return age > TimeSpan.FromSeconds(5);
            }
            catch { return false; }
        }
    }

    /// <summary>Write the current editor state to the autosave slot.
    /// Best-effort — failures (file locked, disk full) get logged but
    /// don't bubble up; the next tick will try again.</summary>
    public void Autosave(EditorViewModel vm)
    {
        try
        {
            // Skip empty projects — autosaving a blank slate just keeps
            // the recovery banner showing forever after a clean launch.
            if (vm.Timeline.Count == 0 && vm.OverlayTimeline.Count == 0
                && vm.TitleTimeline.Count == 0 && vm.AudioTracks.Count == 0)
            {
                ClearAutosave();
                return;
            }
            var file = BuildProjectFile(vm, name: "autosave");
            NleProjectSerializer.Save(AutosaveFilePath, file);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("nle", $"autosave failed: {ex.Message}");
        }
    }

    /// <summary>Delete the autosave recovery file. Called after a
    /// successful explicit Save and on a clean exit so the banner
    /// doesn't fire on the next launch.</summary>
    public void ClearAutosave()
    {
        try
        {
            if (File.Exists(AutosaveFilePath)) File.Delete(AutosaveFilePath);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("nle", $"clear autosave failed: {ex.Message}");
        }
    }

    /// <summary>Construct the ProjectFile DTO from a VM — shared between
    /// explicit Save and Autosave so both serialise the same shape.</summary>
    private static NleProjectSerializer.ProjectFile BuildProjectFile(EditorViewModel vm, string name) => new()
    {
        Name = name,
        OutputName = vm.OutputName,
        Fps = vm.Fps,
        CrossfadeSec = vm.CrossfadeSec,
        RenderAspect = vm.RenderAspect,
        RenderQuality = vm.RenderQuality,
        AudioPath = string.IsNullOrEmpty(vm.AudioPath) ? null : vm.AudioPath,
        AudioVolume = vm.AudioVolume,
        AudioTracks = vm.AudioTracks.Select(a => new NleProjectSerializer.AudioEntry
        {
            FilePath = a.FilePath, StartSec = a.StartSec, Volume = a.Volume,
            FadeInSec = a.FadeInSec, FadeOutSec = a.FadeOutSec, Label = a.Label,
        }).ToList(),
        Timeline = vm.Timeline.Select(s => new NleProjectSerializer.SlotEntry
        {
            ShotId = s.Clip.ShotId, FilePath = s.Clip.FilePath, DurationSec = s.DurationSec,
        }).ToList(),
        Overlay = vm.OverlayTimeline.Select(o => new NleProjectSerializer.OverlayEntry
        {
            ShotId = o.Clip.ShotId, FilePath = o.Clip.FilePath,
            StartSec = o.StartSec, DurationSec = o.DurationSec,
            Scale = o.Scale, Position = o.Position,
        }).ToList(),
        Titles = vm.TitleTimeline.Select(t => new NleProjectSerializer.TitleEntry
        {
            Text = t.Text, StartSec = t.StartSec, DurationSec = t.DurationSec,
            FontSize = t.FontSize, Color = t.Color, Position = t.Position,
        }).ToList(),
    };

    /// <summary>Show a Save dialog seeded with the suggested file name.
    /// Returns the chosen path or null if the user cancelled.</summary>
    public string? PromptSavePath(string suggestedName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save NLE project",
            Filter = "Chanthra Studio project (*.chstudio)|*.chstudio|All files|*.*",
            FileName = SafeFile(string.IsNullOrEmpty(suggestedName) ? "project" : suggestedName) + ".chstudio",
            InitialDirectory = DefaultDirectory,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Show an Open dialog. Returns the chosen path or null.</summary>
    public string? PromptOpenPath()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open NLE project",
            Filter = "Chanthra Studio project (*.chstudio)|*.chstudio|All files|*.*",
            InitialDirectory = Directory.Exists(DefaultDirectory) ? DefaultDirectory : AppPaths.Root,
            CheckFileExists = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Serialise the editor state to the chosen path. Promotes the path
    /// in the recent-projects MRU on success.
    /// </summary>
    public SaveResult Save(string path, EditorViewModel vm)
    {
        try
        {
            var file = BuildProjectFile(vm, Path.GetFileNameWithoutExtension(path));
            NleProjectSerializer.Save(path, file);
            _ctx.RecentProjects.Promote(path);
            // Explicit save replaces the autosave recovery slot — if the
            // user saved manually they don't need the banner next launch.
            ClearAutosave();
            return SaveResult.Ok(file.Name);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("nle", $"SaveProject {path}", ex);
            return SaveResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Read a project file and apply it to the editor VM. Returns the
    /// number of clip references that couldn't be resolved from the
    /// in-memory Library so the caller can surface a partial-load warning.
    /// </summary>
    public LoadResult Load(string path, EditorViewModel vm)
    {
        try
        {
            var file = NleProjectSerializer.Load(path);
            int missing = 0;

            vm.PushUndoForProjectLoad();
            vm.DetachAndClearTimeline();
            vm.OverlayTimeline.Clear();
            vm.TitleTimeline.Clear();
            vm.AudioTracks.Clear();

            foreach (var entry in file.Timeline)
            {
                var clip = ResolveClip(vm, entry.FilePath, entry.ShotId);
                if (clip is null) { missing++; continue; }
                vm.AppendRestoredSlot(clip, entry.DurationSec);
            }
            foreach (var entry in file.Overlay)
            {
                var clip = ResolveClip(vm, entry.FilePath, entry.ShotId);
                if (clip is null) { missing++; continue; }
                vm.OverlayTimeline.Add(new OverlaySlot
                {
                    Clip = clip,
                    StartSec = entry.StartSec,
                    DurationSec = entry.DurationSec,
                    Scale = entry.Scale,
                    Position = entry.Position,
                });
            }
            foreach (var entry in file.Titles ?? new List<NleProjectSerializer.TitleEntry>())
            {
                vm.TitleTimeline.Add(new TitleSlot
                {
                    Text = entry.Text,
                    StartSec = entry.StartSec,
                    DurationSec = entry.DurationSec,
                    FontSize = entry.FontSize,
                    Color = entry.Color,
                    Position = entry.Position,
                });
            }
            foreach (var entry in file.AudioTracks ?? new List<NleProjectSerializer.AudioEntry>())
            {
                vm.AudioTracks.Add(new AudioSlot
                {
                    FilePath = entry.FilePath,
                    StartSec = entry.StartSec,
                    Volume = entry.Volume,
                    FadeInSec = entry.FadeInSec,
                    FadeOutSec = entry.FadeOutSec,
                    Label = entry.Label,
                });
            }

            vm.OutputName = file.OutputName;
            vm.Fps = file.Fps;
            vm.CrossfadeSec = file.CrossfadeSec;
            vm.RenderAspect = string.IsNullOrEmpty(file.RenderAspect) ? "16:9" : file.RenderAspect;
            vm.RenderQuality = string.IsNullOrEmpty(file.RenderQuality) ? "full" : file.RenderQuality;
            vm.AudioPath = file.AudioPath ?? "";
            vm.AudioVolume = file.AudioVolume;
            vm.ProjectName = file.Name;
            vm.PromoteLoadedSelections();

            _ctx.RecentProjects.Promote(path);
            return LoadResult.Ok(vm.Timeline.Count, vm.OverlayTimeline.Count, missing);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("nle", $"LoadProject {path}", ex);
            return LoadResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Look the project's clip refs back up in the Library by file path
    /// (most stable identifier across sessions) with shot id as a
    /// fallback for files that were renamed on disk.
    /// </summary>
    private static Clip? ResolveClip(EditorViewModel vm, string filePath, string shotId)
    {
        var byPath = vm.LibraryClips.FirstOrDefault(c =>
            string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null) return byPath;
        return vm.LibraryClips.FirstOrDefault(c => c.ShotId == shotId);
    }

    private static string SafeFile(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim();
    }

    public sealed record SaveResult(bool IsOk, string? ProjectName, string? Error)
    {
        public static SaveResult Ok(string name) => new(true, name, null);
        public static SaveResult Failed(string err) => new(false, null, err);
    }

    public sealed record LoadResult(bool IsOk, int Slots, int Overlays, int MissingRefs, string? Error)
    {
        public static LoadResult Ok(int slots, int overlays, int missing) => new(true, slots, overlays, missing, null);
        public static LoadResult Failed(string err) => new(false, 0, 0, 0, err);
    }
}

