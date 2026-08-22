using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.ComfyUI;

/// <summary>One input on a node, as the server describes it.</summary>
/// <param name="IsLink">True when this input is fed by a wire from another
/// node; false when it is a value the user types or picks.</param>
public sealed record ComfyInputDef(
    string Name,
    string TypeName,
    bool IsLink,
    bool Optional,
    IReadOnlyList<string> Choices,
    string DefaultValue,
    bool Multiline,
    string Tooltip);

public sealed record ComfyOutputDef(string Name, string TypeName);

/// <summary>One node type the server can run.</summary>
public sealed record ComfyNodeDef(
    string Name,
    string DisplayName,
    string Category,
    string Description,
    bool IsOutputNode,
    IReadOnlyList<ComfyInputDef> Inputs,
    IReadOnlyList<ComfyOutputDef> Outputs)
{
    /// <summary>Lower-cased haystack for the palette's search box.</summary>
    public string SearchKey { get; } =
        $"{Name} {DisplayName} {Category}".ToLowerInvariant();
}

/// <summary>
/// Every node the connected ComfyUI can run, read from its own
/// <c>/object_info</c>.
///
/// <b>Why the palette is not a hand-written list.</b> It used to be ten node
/// types with hard-coded sockets, which meant the editor could express about
/// one percent of what the engine could do — no LoRA stack beyond a single
/// loader, no ControlNet, no upscalers, no video or audio nodes, and nothing
/// at all from an installed custom node pack. A stock engine reports 849 node
/// types across 196 categories; a curated list can never keep up with that,
/// and every gap is a workflow the user has to go and build somewhere else.
///
/// The catalog is cached to disk so the palette still works while the engine
/// is stopped — opening the editor should not require a cold start.
/// </summary>
public sealed class ComfyNodeCatalog
{
    private ComfyNodeCatalog(IReadOnlyList<ComfyNodeDef> nodes, DateTime fetchedUtc)
    {
        Nodes = nodes;
        FetchedUtc = fetchedUtc;
        Categories = nodes.Select(n => n.Category)
                          .Where(c => !string.IsNullOrEmpty(c))
                          .Distinct()
                          .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        _byName = nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, ComfyNodeDef> _byName;

    public IReadOnlyList<ComfyNodeDef> Nodes { get; }
    public IReadOnlyList<string> Categories { get; }
    public DateTime FetchedUtc { get; }

    public ComfyNodeDef? Find(string className)
        => _byName.TryGetValue(className, out var d) ? d : null;

    /// <summary>Name, display name and category all match; empty query returns
    /// everything so the palette can show the full list.</summary>
    public IEnumerable<ComfyNodeDef> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Nodes;
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Nodes.Where(n => terms.All(t => n.SearchKey.Contains(t, StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------- parsing

    /// <summary>
    /// Types that are values rather than wires. Everything else — MODEL, CLIP,
    /// LATENT, IMAGE, CONDITIONING, and whatever a custom node invents — is a
    /// socket. Classifying by exclusion rather than by a list of known socket
    /// types is what makes an unknown custom type behave correctly.
    /// </summary>
    private static readonly HashSet<string> WidgetTypes = new(StringComparer.Ordinal)
        { "INT", "FLOAT", "STRING", "BOOLEAN" };

    public static ComfyNodeCatalog Parse(JsonObject objectInfo)
    {
        var nodes = new List<ComfyNodeDef>(objectInfo.Count);

        foreach (var (className, value) in objectInfo)
        {
            if (value is not JsonObject node) continue;

            var inputs = new List<ComfyInputDef>();
            if (node["input"] is JsonObject input)
            {
                ReadInputs(input["required"] as JsonObject, optional: false, inputs);
                ReadInputs(input["optional"] as JsonObject, optional: true, inputs);
            }

            var outputs = new List<ComfyOutputDef>();
            if (node["output"] is JsonArray outTypes)
            {
                var names = node["output_name"] as JsonArray;
                for (var i = 0; i < outTypes.Count; i++)
                {
                    var type = TypeNameOf(outTypes[i]);
                    var name = names is not null && i < names.Count
                        ? names[i]?.GetValue<string>() ?? type
                        : type;
                    outputs.Add(new ComfyOutputDef(string.IsNullOrEmpty(name) ? $"OUT{i}" : name, type));
                }
            }

            nodes.Add(new ComfyNodeDef(
                className,
                node["display_name"]?.GetValue<string>() ?? className,
                node["category"]?.GetValue<string>() ?? "",
                node["description"]?.GetValue<string>() ?? "",
                node["output_node"]?.GetValue<bool>() ?? false,
                inputs,
                outputs));
        }

        return new ComfyNodeCatalog(
            nodes.OrderBy(n => n.Category, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                 .ToList(),
            DateTime.UtcNow);
    }

    private static void ReadInputs(JsonObject? group, bool optional, List<ComfyInputDef> into)
    {
        if (group is null) return;

        foreach (var (name, spec) in group)
        {
            // Every input is [type, options?]. The type is either a string —
            // "MODEL", "INT" — or an array of the values a combo accepts.
            if (spec is not JsonArray arr || arr.Count == 0) continue;

            var choices = new List<string>();
            string typeName;
            var isLink = false;

            if (arr[0] is JsonArray choiceList)
            {
                typeName = "COMBO";
                foreach (var c in choiceList)
                {
                    var s = c?.ToString();
                    if (!string.IsNullOrEmpty(s)) choices.Add(s!);
                }
            }
            else
            {
                typeName = TypeNameOf(arr[0]);
                isLink = !WidgetTypes.Contains(typeName);
            }

            var options = arr.Count > 1 ? arr[1] as JsonObject : null;
            var multiline = options?["multiline"]?.GetValue<bool>() ?? false;
            var tooltip = options?["tooltip"]?.GetValue<string>() ?? "";

            into.Add(new ComfyInputDef(
                name, typeName, isLink, optional, choices,
                DefaultOf(options, typeName, choices), multiline, tooltip));
        }
    }

    /// <summary>
    /// A starting value the server will accept.
    ///
    /// Taken from the schema's own default where there is one, and otherwise
    /// from the first legal choice — never a guess. A guessed default is how
    /// the previous editor shipped nodes pre-filled with a checkpoint filename
    /// nobody had.
    /// </summary>
    private static string DefaultOf(JsonObject? options, string typeName, List<string> choices)
    {
        var def = options?["default"];
        if (def is not null)
        {
            return def.GetValueKind() switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => def.ToString(),
            };
        }
        if (choices.Count > 0) return choices[0];
        return typeName switch
        {
            "INT" => "0",
            "FLOAT" => "0",
            "BOOLEAN" => "false",
            _ => "",
        };
    }

    private static string TypeNameOf(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString(); }
        catch { return node.ToString(); }
    }

    // --------------------------------------------------------------- cache

    private static string CacheFile => Path.Combine(AppPaths.Root, "cache", "comfy-nodes.json");

    /// <summary>Fetch from a live server and cache the result.</summary>
    public static async Task<ComfyNodeCatalog?> FetchAsync(string baseUrl, string? token = null, CancellationToken ct = default)
    {
        try
        {
            using var client = new ComfyUiClient(baseUrl, null, token);
            var info = await client.GetObjectInfoAsync(ct);
            if (info is null) return null;

            var catalog = Parse(info);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                // Cache the raw payload rather than our parsed shape: if the
                // parser changes, a stale cache should be re-read correctly
                // rather than replayed through an old interpretation.
                await File.WriteAllTextAsync(CacheFile, info.ToJsonString(), ct);
            }
            catch (Exception ex)
            {
                ActivityLog.Warn("comfy", "could not cache the node list: " + ex.Message);
            }
            return catalog;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Last known node list, so the palette works with the engine off.</summary>
    public static ComfyNodeCatalog? LoadCached()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            if (JsonNode.Parse(File.ReadAllText(CacheFile)) is not JsonObject info) return null;
            return Parse(info);
        }
        catch
        {
            return null;
        }
    }
}
