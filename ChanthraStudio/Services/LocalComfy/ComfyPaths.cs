using System;
using System.IO;
using System.Linq;

namespace ChanthraStudio.Services.LocalComfy;

/// <summary>
/// Where the studio's own ComfyUI lives.
///
/// <b>Why this is not simply a folder under %APPDATA%.</b> The embedded Python
/// inside the portable build, and much of the tooling underneath it, still has
/// rough edges with non-ASCII characters in paths — and a large share of this
/// app's users have Thai names, which means their profile folder is
/// <c>C:\Users\สมชาย\…</c>. An engine installed there can fail in ways that
/// look like a broken GPU rather than a broken path. So the default root is
/// checked, and quietly moved to an ASCII path when it isn't safe.
/// </summary>
public static class ComfyPaths
{
    /// <summary>Settings key holding the chosen engine root, so a user who
    /// wants it on another drive is not stuck with our guess.</summary>
    public const string RootSettingKey = "comfy:engineRoot";

    /// <summary>Settings key for where model weights live.</summary>
    public const string ModelsSettingKey = "comfy:modelsRoot";

    /// <summary>
    /// Overrides <see cref="ModelsDir"/> when the user puts weights on another
    /// drive.
    ///
    /// <b>Why this is separate from the engine root.</b> The engine is about
    /// 4.5 GB and can be reinstalled in minutes. The weights are the expensive
    /// part — a single video profile is 33 GB — and the drive with room for
    /// them is frequently not the drive the app is installed on. Forcing them
    /// alongside the app fills whatever disk it happens to live on, and the
    /// first symptom is a render failing halfway through a download.
    /// </summary>
    private static string? _modelsRootOverride;

    /// <summary>Read the configured roots. Call once at startup.</summary>
    public static void Configure(AppSettings settings)
    {
        var models = settings.GetSetting(ModelsSettingKey);
        _modelsRootOverride = string.IsNullOrWhiteSpace(models) ? null : models;
    }

    /// <summary>Point the model folder somewhere else, and persist it.</summary>
    public static void SetModelsRoot(AppSettings settings, string? path)
    {
        _modelsRootOverride = string.IsNullOrWhiteSpace(path) ? null : path;
        settings.SetSetting(ModelsSettingKey, path ?? "");
        settings.Save();
    }

    /// <summary>
    /// The engine root: the app's own data folder when that path is plain
    /// ASCII, and a short drive-root folder when it is not.
    /// </summary>
    public static string DefaultRoot()
    {
        var preferred = Path.Combine(AppPaths.Root, "engine");
        if (IsAsciiSafe(preferred)) return preferred;

        // Deliberately short and ASCII. Same drive as the profile so we don't
        // silently fill a drive the user didn't choose.
        var drive = Path.GetPathRoot(AppPaths.Root);
        if (string.IsNullOrEmpty(drive)) drive = @"C:\";
        return Path.Combine(drive!, "ChanthraStudio", "engine");
    }

    /// <summary>
    /// True when every character is plain ASCII and the path has no character
    /// that has historically confused the embedded interpreter.
    /// </summary>
    public static bool IsAsciiSafe(string path)
        => !string.IsNullOrEmpty(path) && path.All(c => c < 128);

    public static string Root(AppSettings? settings)
    {
        var configured = settings?.GetSetting(RootSettingKey);
        return string.IsNullOrWhiteSpace(configured) ? DefaultRoot() : configured!;
    }

    /// <summary>The extracted portable — contains python_embeded/ and ComfyUI/.</summary>
    public static string PortableDir(string root) => Path.Combine(root, "portable");

    public static string PythonExe(string root)
        => Path.Combine(PortableDir(root), "python_embeded", "python.exe");

    public static string ComfyDir(string root)
        => Path.Combine(PortableDir(root), "ComfyUI");

    public static string MainPy(string root) => Path.Combine(ComfyDir(root), "main.py");

    /// <summary>Marker written only after a full, verified install. Its absence
    /// is what makes a half-extracted engine reinstall instead of half-run.</summary>
    public static string StampFile(string root) => Path.Combine(root, "installed.json");

    public static string LogFile(string root) => Path.Combine(root, "comfy.log");

    /// <summary>Downloads land here so a cancelled install leaves one obvious
    /// file to delete rather than debris inside the engine.</summary>
    public static string CacheDir(string root) => Path.Combine(root, "cache");

    /// <summary>
    /// Weights live under the app's media folder, NOT inside the engine.
    ///
    /// Models are the expensive part — tens of gigabytes that took an hour to
    /// fetch. Keeping them outside means the engine can be deleted, upgraded or
    /// reinstalled without touching them.
    /// </summary>
    public static string ModelsDir()
    {
        var p = _modelsRootOverride ?? Path.Combine(AppPaths.MediaFolder, "models");
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Model sub-folder, created on demand.</summary>
    public static string ModelFolder(string kind)
    {
        var p = Path.Combine(ModelsDir(), kind);
        Directory.CreateDirectory(p);
        return p;
    }
}
