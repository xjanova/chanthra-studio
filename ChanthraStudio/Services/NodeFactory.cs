using System;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services.Providers.ComfyUI;

namespace ChanthraStudio.Services;

/// <summary>
/// Builds a canvas node from a server node definition.
///
/// This is what lets the editor place any of the engine's node types rather
/// than the ten the palette used to hard-code — the sockets, the parameter
/// list, the option lists and the defaults all come from the schema the server
/// published, so a node placed here is a node the server will accept.
/// </summary>
public static class NodeFactory
{
    public static FlowNode Create(ComfyNodeDef def, double x, double y, string id)
    {
        var node = new FlowNode
        {
            Id = id,
            ClassType = def.Name,
            Title = def.DisplayName,
            Kind = NodeFlowConverter.KindFor(def.Name),
            AccentKey = AccentFor(def),
            X = x,
            Y = y,
            // Wide enough for a long parameter name beside its value; the
            // curated palette's 220 clipped things like "sampler_name".
            Width = 250,
        };

        var linkRow = 0;
        foreach (var input in def.Inputs.Where(i => i.IsLink))
        {
            node.Inputs.Add(new NodeSocket
            {
                Id = input.Name,
                Label = input.Optional ? input.Name + " ?" : input.Name,
                Type = MapSocket(input.TypeName),
                TypeName = input.TypeName,
                IsInput = true,
                Row = linkRow++,
            });
        }

        var outRow = 0;
        foreach (var output in def.Outputs)
        {
            node.Outputs.Add(new NodeSocket
            {
                // The id only has to be unique within the node — the wire's
                // output index is derived from position when the graph is
                // converted, so a duplicate output name must not collapse two
                // sockets into one.
                Id = $"out{outRow}",
                Label = output.Name,
                Type = MapSocket(output.TypeName),
                TypeName = output.TypeName,
                IsInput = false,
                Row = outRow++,
            });
        }

        foreach (var widget in def.Inputs.Where(i => !i.IsLink))
        {
            var param = new NodeParam
            {
                Label = widget.Name,
                Value = widget.DefaultValue,
                Editor = widget.Multiline ? "textarea" : "text",
            };
            if (widget.Choices.Count > 0) param.SetChoices(widget.Choices);
            node.Params.Add(param);
        }

        return node;
    }

    /// <summary>
    /// Coarse colour bucket for the wire and socket styling. The precise type
    /// lives on <see cref="NodeSocket.TypeName"/>; this is only what the eye
    /// uses to follow a wire across the canvas.
    /// </summary>
    public static SocketType MapSocket(string typeName) => typeName switch
    {
        "MODEL" => SocketType.Model,
        "CLIP" or "CLIP_VISION" => SocketType.Clip,
        "VAE" => SocketType.Vae,
        "LATENT" => SocketType.Latent,
        "IMAGE" or "MASK" => SocketType.Image,
        "CONDITIONING" => SocketType.Conditioning,
        "INT" or "FLOAT" => SocketType.Number,
        "STRING" => SocketType.String,
        _ => SocketType.Number,
    };

    /// <summary>
    /// Header colour by what the node is for, so a graph reads at a glance:
    /// loaders gold, savers crimson, samplers amber, the rest plum.
    /// </summary>
    private static string AccentFor(ComfyNodeDef def)
    {
        if (def.IsOutputNode || def.Name.StartsWith("Save", StringComparison.Ordinal))
            return "BrushClipCrimson";
        if (def.Category.Contains("loader", StringComparison.OrdinalIgnoreCase)
            || def.Name.Contains("Loader", StringComparison.Ordinal))
            return "BrushClipGold";
        if (def.Category.Contains("sampl", StringComparison.OrdinalIgnoreCase)
            || def.Name.Contains("Sampler", StringComparison.Ordinal))
            return "BrushClipAmber";
        return "BrushClipPlum";
    }

    /// <summary>
    /// Whether a wire between two sockets is legal.
    ///
    /// ComfyUI rejects a mismatched connection at submit time with a message
    /// naming a node id, which is not something the user can act on from a
    /// canvas. Checking here means the wire simply does not attach — and the
    /// comparison uses the real type names, not the coarse colour bucket,
    /// because MASK and IMAGE share a colour and are not interchangeable.
    /// </summary>
    public static bool TypesCompatible(NodeSocket from, NodeSocket to)
    {
        // A socket with no recorded type came from an older graph or a
        // placeholder; allowing it keeps existing workflows editable rather
        // than declaring their wires invalid.
        if (string.IsNullOrEmpty(from.TypeName) || string.IsNullOrEmpty(to.TypeName)) return true;
        if (from.TypeName == "*" || to.TypeName == "*") return true;
        return string.Equals(from.TypeName, to.TypeName, StringComparison.Ordinal);
    }
}
