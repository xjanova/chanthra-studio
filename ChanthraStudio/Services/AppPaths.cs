using System;
using System.IO;
using System.Reflection;

namespace ChanthraStudio.Services;

/// <summary>
/// Resolves writeable paths for the portable build. The working directory is
/// where the executable lives. If that's not writeable (e.g. installed under
/// Program Files), fall back to %APPDATA%/ChanthraStudio.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "ChanthraStudio";

    private static string? _root;

    public static string Root => _root ??= ResolveRoot();

    public static string DatabaseFile => Path.Combine(Root, "chanthra.db");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string MediaFolder
    {
        get
        {
            var p = Path.Combine(Root, "media");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string LogsFolder
    {
        get
        {
            var p = Path.Combine(Root, "logs");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private static string ResolveRoot()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                     ?? AppContext.BaseDirectory;
        var probe = Path.Combine(exeDir, ".write-test");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return exeDir;
        }
        catch
        {
            var appdata = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName);
            Directory.CreateDirectory(appdata);
            return appdata;
        }
    }
}
