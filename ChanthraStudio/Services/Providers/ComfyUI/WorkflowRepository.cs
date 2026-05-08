using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using ChanthraStudio.Models;
using ChanthraStudio.Services;

namespace ChanthraStudio.Services.Providers.ComfyUI;

/// <summary>
/// Discovers workflow JSON files. Two roots:
///
///   * Builtin — <c>{exeDir}/Assets/Workflows/*.json</c>. Shipped with the
///     app, copied next to the .exe. We also surface them under "✦ Built-in"
///     in the picker.
///   * User    — <c>{appDataRoot}/workflows/*.json</c>. Created on first
///     scan if missing. Anything the user drops here appears in the picker
///     after a Refresh.
///
/// The first line of a JSON file is checked for an optional comment prefix
/// (<c>// label · spec</c>) which becomes the picker's display text. Pure
/// JSON without a comment still works — we fall back to the file name.
/// </summary>
public sealed class WorkflowRepository
{
    public IReadOnlyList<WorkflowDescriptor> All { get; private set; } = Array.Empty<WorkflowDescriptor>();

    public string BuiltinDir { get; }
    public string UserDir { get; }

    public WorkflowRepository()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                     ?? AppContext.BaseDirectory;
        BuiltinDir = Path.Combine(exeDir, "Assets", "Workflows");
        UserDir = Path.Combine(AppPaths.Root, "workflows");
        Directory.CreateDirectory(UserDir);
        Refresh();
    }

    public void Refresh()
    {
        var list = new List<WorkflowDescriptor>();
        if (Directory.Exists(BuiltinDir))
        {
            foreach (var path in Directory.EnumerateFiles(BuiltinDir, "*.json"))
                list.Add(Describe(path, builtin: true));
        }
        if (Directory.Exists(UserDir))
        {
            foreach (var path in Directory.EnumerateFiles(UserDir, "*.json"))
                list.Add(Describe(path, builtin: false));
        }
        All = list
            .OrderByDescending(d => d.IsBuiltin)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WorkflowDescriptor? FindByName(string name)
        => All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public WorkflowDescriptor? Default()
        => All.FirstOrDefault(d => d.Name == "default_text2img") ?? All.FirstOrDefault();

    private static WorkflowDescriptor Describe(string path, bool builtin)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var (display, spec, desc) = ReadHeader(path) ?? (Humanise(name), builtin ? "BUILT-IN" : "USER", "");
        return new WorkflowDescriptor
        {
            Name = name,
            DisplayName = display,
            Path = path,
            Description = desc,
            IsBuiltin = builtin,
            Spec = spec,
        };
    }

    /// <summary>
    /// Tries to read a leading comment line of the form
    /// <c>// "Display Name" · spec — description</c>. We ignore parse errors
    /// (header is optional). Reads only the first 2KB of the file.
    /// </summary>
    private static (string display, string spec, string desc)? ReadHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(2048, fs.Length)];
            var read = fs.Read(buf, 0, buf.Length);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var firstLine = text.Split('\n')[0].Trim();
            if (!firstLine.StartsWith("//", StringComparison.Ordinal)) return null;
            var rest = firstLine[2..].Trim();
            // Format: "Display Name" · SPEC — description
            var pipe = rest.IndexOf('·');
            if (pipe < 0) return (rest, "USER", "");
            var display = rest[..pipe].Trim().Trim('"');
            var afterPipe = rest[(pipe + 1)..].Trim();
            var dash = afterPipe.IndexOf('—');
            if (dash < 0) return (display, afterPipe.ToUpperInvariant(), "");
            return (display, afterPipe[..dash].Trim().ToUpperInvariant(), afterPipe[(dash + 1)..].Trim());
        }
        catch { return null; }
    }

    private static string Humanise(string fileName)
    {
        var s = fileName.Replace('_', ' ').Replace('-', ' ');
        return char.ToUpper(s[0]) + s[1..];
    }
}
