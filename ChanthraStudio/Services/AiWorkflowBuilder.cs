using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Turns the LLM's compact graph spec into a validated <see cref="FlowGraph"/>.
///
/// The LLM only chooses node kinds, params, and high-level connections (by the
/// socket ids the app defines) — it never has to get ComfyUI's exact socket
/// wiring right. We seed every node's REAL sockets via <see cref="NodeKinds"/>,
/// then only accept wires whose endpoints actually resolve. Garbage in → a
/// smaller-but-valid graph out, never a crash.
///
/// Expected JSON (markdown fences tolerated):
///   { "nodes": [ { "id":"ckpt", "kind":"LoadCheckpoint", "params": { "ckpt_name":"" } } ],
///     "wires": [ { "from":"ckpt:model", "to":"sampler:model" } ] }
/// </summary>
public static class AiWorkflowBuilder
{
    public static FlowGraph BuildGraph(string llmJson)
    {
        var json = StripFences(llmJson);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("AI did not return a JSON object.");

        var graph = new FlowGraph();
        var byId = new Dictionary<string, FlowNode>(StringComparer.Ordinal);

        if (root["nodes"] is not JsonArray nodes || nodes.Count == 0)
            throw new InvalidOperationException("AI returned no nodes.");

        foreach (var item in nodes)
        {
            if (item is not JsonObject obj) continue;
            var id = obj["id"]?.GetValue<string>();
            var kindStr = obj["kind"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(kindStr)) continue;
            if (!Enum.TryParse<NodeKind>(kindStr, ignoreCase: true, out var kind)) continue;
            if (byId.ContainsKey(id)) continue;   // dedupe ids

            var node = new FlowNode
            {
                Id = id,
                Kind = kind,
                Title = NodeKinds.Humanise(kind),
                AccentKey = NodeKinds.Accent(kind),
                Width = 230,
            };
            NodeKinds.SeedSockets(node, kind);

            // Override default params with whatever the AI supplied.
            if (obj["params"] is JsonObject pars)
            {
                foreach (var (key, val) in pars)
                {
                    var value = JsonScalarToString(val);
                    var existing = node.Params.FirstOrDefault(p => p.Label == key);
                    if (existing is not null) existing.Value = value;
                    else node.Params.Add(new NodeParam { Label = key, Value = value });
                }
            }

            graph.Nodes.Add(node);
            byId[id] = node;
        }

        if (root["wires"] is JsonArray wires)
        {
            foreach (var item in wires)
            {
                if (item is not JsonObject obj) continue;
                if (!TrySplit(obj["from"]?.GetValue<string>(), out var fromId, out var fromSock)) continue;
                if (!TrySplit(obj["to"]?.GetValue<string>(), out var toId, out var toSock)) continue;
                if (!byId.TryGetValue(fromId, out var fromNode) || !byId.TryGetValue(toId, out var toNode)) continue;

                var outSocket = fromNode.Outputs.FirstOrDefault(s => s.Id == fromSock);
                var inSocket  = toNode.Inputs.FirstOrDefault(s => s.Id == toSock);
                if (outSocket is null || inSocket is null) continue;          // endpoint doesn't exist → skip
                if (graph.Wires.Any(w => w.ToNodeId == toId && w.ToSocketId == toSock)) continue;  // input already taken

                graph.Wires.Add(new FlowWire
                {
                    Id = $"{fromId}.{fromSock}->{toId}.{toSock}",
                    FromNodeId = fromId, FromSocketId = fromSock,
                    ToNodeId = toId, ToSocketId = toSock,
                    Type = outSocket.Type,
                });
            }
        }

        LayoutByDepth(graph);
        return graph;
    }

    /// <summary>Left→right layout by topological depth so the result reads
    /// nicely the moment it lands in the editor.</summary>
    private static void LayoutByDepth(FlowGraph graph)
    {
        var depth = graph.Nodes.ToDictionary(n => n.Id, _ => 0);
        for (int iter = 0; iter < 40; iter++)
        {
            var changed = false;
            foreach (var w in graph.Wires)
                if (depth.TryGetValue(w.FromNodeId, out var fd) && depth.TryGetValue(w.ToNodeId, out var td) && td < fd + 1)
                { depth[w.ToNodeId] = fd + 1; changed = true; }
            if (!changed) break;
        }
        const double colW = 300, rowH = 230;
        foreach (var col in graph.Nodes.GroupBy(n => depth[n.Id]).OrderBy(g => g.Key))
        {
            int row = 0;
            foreach (var n in col) { n.X = 60 + col.Key * colW; n.Y = 60 + row * rowH; row++; }
        }
    }

    private static bool TrySplit(string? endpoint, out string nodeId, out string socketId)
    {
        nodeId = ""; socketId = "";
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var idx = endpoint.IndexOf(':');
        if (idx <= 0 || idx >= endpoint.Length - 1) return false;
        nodeId = endpoint[..idx].Trim();
        socketId = endpoint[(idx + 1)..].Trim();
        return nodeId.Length > 0 && socketId.Length > 0;
    }

    private static string JsonScalarToString(JsonNode? v)
    {
        if (v is null) return "";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        return v.ToJsonString();
    }

    /// <summary>Strip ```json … ``` fences and any prose around the JSON body.</summary>
    private static string StripFences(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var s = raw.Trim();
        // Take the substring between the first '{' and the last '}' — robust to
        // fences / leading "Here is your workflow:" preambles.
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start >= 0 && end > start) return s[start..(end + 1)];
        return s;
    }
}
