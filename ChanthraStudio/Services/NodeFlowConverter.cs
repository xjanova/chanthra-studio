using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Converts the visual <see cref="FlowGraph"/> from the Node Flow editor
/// into a ComfyUI API-format dict (string-keyed node table) — and back. The
/// node-id-only API format is what /prompt expects, and what
/// <c>Workflow.LoadFromPath</c> reads when the Composer picks a workflow.
///
/// Mapping rules:
///
///   * Each <see cref="FlowNode.Kind"/> maps to a fixed ComfyUI class_type.
///   * <see cref="NodeParam.Label"/> is the input key; the Value is parsed
///     as int → double → bool → string (try each in order).
///   * Each wire becomes <c>inputs[toSocketId] = [fromNodeId, outputIndex]</c>
///     where <c>outputIndex</c> is the position of the source socket in the
///     source node's <see cref="FlowNode.Outputs"/> collection.
///
/// Round-trip caveats: ComfyUI API format doesn't carry x/y positions for
/// the visual editor, so we sidecar them under a non-standard <c>_pos</c>
/// key the live ComfyUI server happily ignores but our loader can read
/// back. Workflows imported from elsewhere lose their visual layout — the
/// auto-arrange button rebuilds it.
/// </summary>
public static class NodeFlowConverter
{
    /// <summary>Mapping from the editor's <see cref="NodeKind"/> enum to the
    /// ComfyUI server-side <c>class_type</c> string.</summary>
    public static string ClassTypeFor(NodeKind kind) => kind switch
    {
        NodeKind.LoadCheckpoint    => "CheckpointLoaderSimple",
        NodeKind.CLIPTextEncode    => "CLIPTextEncode",
        NodeKind.KSampler          => "KSampler",
        NodeKind.VAEDecode         => "VAEDecode",
        NodeKind.SaveImage         => "SaveImage",
        NodeKind.EmptyLatentImage  => "EmptyLatentImage",
        NodeKind.LoadImage         => "LoadImage",
        NodeKind.LoraLoader        => "LoraLoader",
        NodeKind.ControlNetApply   => "ControlNetApply",
        NodeKind.AnimateDiff       => "AnimateDiffSimpleEvolved",
        _ => kind.ToString(),
    };

    /// <summary>Reverse lookup for loading existing workflow files back into
    /// the editor. Anything we can't match falls back to LoraLoader so the
    /// node still renders.</summary>
    public static NodeKind KindFor(string classType) => classType switch
    {
        "CheckpointLoaderSimple"     => NodeKind.LoadCheckpoint,
        "CLIPTextEncode"             => NodeKind.CLIPTextEncode,
        "KSampler"                   => NodeKind.KSampler,
        "VAEDecode"                  => NodeKind.VAEDecode,
        "SaveImage"                  => NodeKind.SaveImage,
        "EmptyLatentImage"           => NodeKind.EmptyLatentImage,
        "LoadImage"                  => NodeKind.LoadImage,
        "LoraLoader"                 => NodeKind.LoraLoader,
        "ControlNetApply"            => NodeKind.ControlNetApply,
        "AnimateDiffSimpleEvolved"   => NodeKind.AnimateDiff,
        _ => NodeKind.LoraLoader,
    };

    /// <summary>
    /// Build the ComfyUI API dict from the current graph. Returns a top-level
    /// <see cref="JsonObject"/> ready to either persist to disk or POST to
    /// <c>/prompt</c>.
    /// </summary>
    public static JsonObject ToComfyApi(FlowGraph graph)
    {
        var root = new JsonObject();

        // Build a fast lookup so wire-resolution doesn't walk the node list.
        var nodeById = graph.Nodes.ToDictionary(n => n.Id);

        foreach (var node in graph.Nodes)
        {
            var inputs = new JsonObject();

            // Literal params (text fields in the inspector) — try parsing as
            // a number / bool first so KSampler.steps becomes int 28, not "28".
            foreach (var p in node.Params)
                inputs[p.Label] = ParseValue(p.Value);

            // Socket-driven inputs (wires). The wire's target socket id IS
            // the ComfyUI input key — keep them named consistently when
            // seeding the sample graph (already true today).
            foreach (var wire in graph.Wires.Where(w => w.ToNodeId == node.Id))
            {
                if (!nodeById.TryGetValue(wire.FromNodeId, out var src)) continue;
                var outputIndex = -1;
                for (int i = 0; i < src.Outputs.Count; i++)
                    if (src.Outputs[i].Id == wire.FromSocketId) { outputIndex = i; break; }
                if (outputIndex < 0) continue;
                inputs[wire.ToSocketId] = new JsonArray { wire.FromNodeId, outputIndex };
            }

            var entry = new JsonObject
            {
                ["class_type"] = ClassTypeFor(node.Kind),
                ["inputs"]     = inputs,
                // Non-standard sidecar — ComfyUI ignores it, we use it to
                // restore positions when loading the file back into the editor.
                ["_pos"]       = new JsonArray { node.X, node.Y },
                ["_title"]     = node.Title,
            };
            root[node.Id] = entry;
        }
        return root;
    }

    /// <summary>
    /// Reverse direction: turn an existing ComfyUI workflow file back into a
    /// <see cref="FlowGraph"/>. Positions are restored from the sidecar
    /// <c>_pos</c> if present; otherwise nodes stack at (60, 60+i*220) and
    /// the user can press Auto-arrange to clean up.
    /// </summary>
    public static FlowGraph FromComfyApi(JsonObject root)
    {
        var graph = new FlowGraph();
        int fallbackRow = 0;
        foreach (var (id, value) in root)
        {
            if (value is not JsonObject obj) continue;
            var classType = obj["class_type"]?.GetValue<string>() ?? "";
            var kind = KindFor(classType);
            var node = new FlowNode
            {
                Id = id,
                Title = obj["_title"]?.GetValue<string>() ?? kind.ToString(),
                Kind = kind,
                AccentKey = AccentForKind(kind),
                Width = 230,
            };
            if (obj["_pos"] is JsonArray pos && pos.Count >= 2)
            {
                node.X = pos[0]?.GetValue<double>() ?? 60;
                node.Y = pos[1]?.GetValue<double>() ?? 60;
            }
            else
            {
                node.X = 60 + (fallbackRow % 6) * 260;
                node.Y = 60 + (fallbackRow / 6) * 200;
                fallbackRow++;
            }
            graph.Nodes.Add(node);
        }

        // Walk inputs a second pass — now that all nodes exist, we can
        // resolve wires by id. Anything that's a [nodeId, outputIndex] tuple
        // becomes a wire; literal values become params on the node.
        foreach (var node in graph.Nodes)
        {
            if (root[node.Id] is not JsonObject obj) continue;
            if (obj["inputs"] is not JsonObject inputs) continue;
            foreach (var (key, v) in inputs)
            {
                if (v is JsonArray a && a.Count == 2 && a[0] is JsonValue idV && a[0]!.GetValueKind() == JsonValueKind.String)
                {
                    var fromId = idV.GetValue<string>();
                    var fromOutIdx = a[1]?.GetValue<int>() ?? 0;
                    var src = graph.Nodes.FirstOrDefault(n => n.Id == fromId);
                    if (src is null) continue;
                    // We don't know the socket id without a class-type schema,
                    // so make a placeholder output socket if missing.
                    while (src.Outputs.Count <= fromOutIdx)
                    {
                        var idx = src.Outputs.Count;
                        src.Outputs.Add(new NodeSocket
                        {
                            Id = $"out{idx}", Label = $"OUT{idx}", Type = SocketType.Latent, IsInput = false, Row = idx,
                        });
                    }
                    // Add the target socket too if missing.
                    var existing = false;
                    foreach (var inp in node.Inputs)
                        if (inp.Id == key) { existing = true; break; }
                    if (!existing)
                    {
                        node.Inputs.Add(new NodeSocket
                        {
                            Id = key, Label = key, Type = SocketType.Latent, IsInput = true, Row = node.Inputs.Count,
                        });
                    }

                    graph.Wires.Add(new FlowWire
                    {
                        Id = $"{fromId}.{src.Outputs[fromOutIdx].Id}->{node.Id}.{key}",
                        FromNodeId = fromId,
                        FromSocketId = src.Outputs[fromOutIdx].Id,
                        ToNodeId = node.Id,
                        ToSocketId = key,
                        Type = SocketType.Latent,
                    });
                }
                else
                {
                    // Literal value → param.
                    node.Params.Add(new NodeParam { Label = key, Value = JsonValueToString(v) });
                }
            }
        }

        return graph;
    }

    /// <summary>
    /// Persist the graph to a JSON file in the user workflows folder so the
    /// Composer's workflow picker can choose it on the next refresh.
    /// </summary>
    public static string SaveToUserWorkflows(FlowGraph graph, string name)
    {
        var dir = Path.Combine(AppPaths.Root, "workflows");
        Directory.CreateDirectory(dir);
        var safe = SafeFileName(name);
        if (string.IsNullOrEmpty(safe)) safe = $"node_flow_{DateTime.Now:yyyyMMdd_HHmmss}";
        var path = Path.Combine(dir, safe + ".json");
        var json = ToComfyApi(graph).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path,
            "// \"" + name + "\" · NODE-FLOW — generated by the in-app graph editor\n" + json);
        return path;
    }

    /// <summary>
    /// Load a workflow JSON file back into a <see cref="FlowGraph"/>. Tolerates
    /// the same leading <c>//</c> header comment our Workflow loader accepts.
    /// </summary>
    public static FlowGraph LoadFromFile(string path)
    {
        var raw = File.ReadAllText(path);
        // Strip leading // comment lines.
        var idx = 0;
        while (idx < raw.Length)
        {
            while (idx < raw.Length && (raw[idx] == ' ' || raw[idx] == '\t')) idx++;
            if (idx + 1 >= raw.Length || raw[idx] != '/' || raw[idx + 1] != '/') break;
            while (idx < raw.Length && raw[idx] != '\n') idx++;
            if (idx < raw.Length) idx++;
        }
        var json = idx == 0 ? raw : raw[idx..];
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("workflow is not a JSON object");
        return FromComfyApi(node);
    }

    private static JsonNode ParseValue(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return JsonValue.Create(i);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        if (bool.TryParse(raw, out var b)) return JsonValue.Create(b);
        return JsonValue.Create(raw ?? "");
    }

    private static string JsonValueToString(JsonNode? v)
    {
        if (v is null) return "";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        return v.ToJsonString();
    }

    private static string SafeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(System.Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim();
    }

    private static string AccentForKind(NodeKind k) => k switch
    {
        NodeKind.LoadCheckpoint or NodeKind.KSampler => "BrushClipGold",
        NodeKind.CLIPTextEncode                      => "BrushClipAmber",
        NodeKind.EmptyLatentImage or NodeKind.SaveImage => "BrushClipPlum",
        NodeKind.VAEDecode                           => "BrushClipAmber",
        _ => "BrushClipPlum",
    };
}
