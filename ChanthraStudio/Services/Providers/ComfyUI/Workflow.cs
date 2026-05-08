using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ChanthraStudio.Services.Providers.ComfyUI;

/// <summary>
/// Loads ComfyUI API-format workflows from disk, applies parameter overrides
/// using small selectors (node id + input key path), and exposes the result
/// as a JsonObject ready to POST.
///
/// We don't model the full ComfyUI graph types — workflows are user-supplied
/// JSON, often hundreds of nodes deep. We just patch the few nodes we care
/// about (positive prompt, negative prompt, seed, dimensions, etc.) and let
/// the rest pass through verbatim.
/// </summary>
public sealed class Workflow
{
    public JsonObject Nodes { get; }

    public Workflow(JsonObject nodes) => Nodes = nodes;

    /// <summary>Load the bundled default text2img workflow from disk.</summary>
    public static Workflow LoadDefault()
    {
        var asm = Assembly.GetExecutingAssembly();
        var diskPath = Path.Combine(
            Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory,
            "Assets", "Workflows", "default_text2img.json");
        if (!File.Exists(diskPath))
            throw new FileNotFoundException(
                "default_text2img.json not found next to the .exe — broken install?",
                diskPath);
        return LoadFromPath(diskPath);
    }

    /// <summary>Load any workflow file from disk. Tolerates leading <c>//</c> header comments.</summary>
    public static Workflow LoadFromPath(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"workflow file not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    private static Workflow Parse(string raw)
    {
        var json = StripLeadingComments(raw);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("workflow is not a JSON object");
        return new Workflow(node);
    }

    /// <summary>
    /// Strips any contiguous run of <c>//</c> lines from the head of the file,
    /// so users can document a workflow inline without breaking JSON parsing.
    /// </summary>
    private static string StripLeadingComments(string raw)
    {
        var idx = 0;
        while (idx < raw.Length)
        {
            // Skip whitespace at line start
            while (idx < raw.Length && (raw[idx] == ' ' || raw[idx] == '\t')) idx++;
            if (idx + 1 >= raw.Length || raw[idx] != '/' || raw[idx + 1] != '/') break;
            // Advance to end of line
            while (idx < raw.Length && raw[idx] != '\n') idx++;
            if (idx < raw.Length) idx++; // consume \n
        }
        return idx == 0 ? raw : raw[idx..];
    }

    /// <summary>Find the first node with a given class_type. Returns its inputs object.</summary>
    public JsonObject? InputsOf(string classType)
    {
        foreach (var (_, node) in Nodes)
        {
            if (node is JsonObject obj && obj["class_type"]?.GetValue<string>() == classType)
                return obj["inputs"] as JsonObject;
        }
        return null;
    }

    public Workflow SetPositivePrompt(string text)
    {
        // First CLIPTextEncode (node 6 in the default workflow) is the positive prompt.
        var inputs = InputsOf("CLIPTextEncode");
        if (inputs is not null) inputs["text"] = text;
        return this;
    }

    /// <summary>
    /// Replace the SECOND CLIPTextEncode node's text — convention is positive
    /// first, negative second. No-op if the workflow has only one prompt node
    /// (e.g. Flux dev where negative is just an empty string passthrough).
    /// </summary>
    public Workflow SetNegativePrompt(string text)
    {
        if (string.IsNullOrEmpty(text)) return this;
        bool first = true;
        foreach (var (_, node) in Nodes)
        {
            if (node is not JsonObject obj) continue;
            if (obj["class_type"]?.GetValue<string>() != "CLIPTextEncode") continue;
            if (first) { first = false; continue; }
            if (obj["inputs"] is JsonObject inputs) inputs["text"] = text;
            return this;
        }
        return this;
    }

    public Workflow SetSeed(long seed)
    {
        var inputs = InputsOf("KSampler");
        if (inputs is not null) inputs["seed"] = seed;
        return this;
    }

    public Workflow SetSteps(int steps)
    {
        var inputs = InputsOf("KSampler");
        if (inputs is not null) inputs["steps"] = steps;
        return this;
    }

    public Workflow SetCfg(double cfg)
    {
        var inputs = InputsOf("KSampler");
        if (inputs is not null) inputs["cfg"] = cfg;
        return this;
    }

    public Workflow SetSize(int width, int height)
    {
        var inputs = InputsOf("EmptyLatentImage");
        if (inputs is null) return this;
        inputs["width"] = width;
        inputs["height"] = height;
        return this;
    }

    public Workflow SetFilenamePrefix(string prefix)
    {
        var inputs = InputsOf("SaveImage");
        if (inputs is not null) inputs["filename_prefix"] = prefix;
        return this;
    }

    public Workflow SetCheckpoint(string ckptName)
    {
        var inputs = InputsOf("CheckpointLoaderSimple");
        if (inputs is not null) inputs["ckpt_name"] = ckptName;
        return this;
    }

    public string? GetCheckpoint()
        => InputsOf("CheckpointLoaderSimple")?["ckpt_name"]?.GetValue<string>();

    /// <summary>True if the workflow contains a LoadImage node — i.e. it
    /// expects a reference image to be uploaded before submission.</summary>
    public bool HasLoadImage() => InputsOf("LoadImage") is not null;

    /// <summary>
    /// Set the LoadImage node's <c>image</c> input to the given filename.
    /// The filename must match what ComfyUI's <c>/upload/image</c> returned
    /// in its <c>name</c> field — already URL-safe.
    /// </summary>
    public Workflow SetReferenceImage(string filename)
    {
        var inputs = InputsOf("LoadImage");
        if (inputs is not null) inputs["image"] = filename;
        return this;
    }

    /// <summary>True if any node is a UNETLoader — flux/hunyuan/wan-style
    /// workflows that don't use the SD CheckpointLoaderSimple path. We skip
    /// the auto-checkpoint-pick logic for these.</summary>
    public bool UsesUnetLoader() => InputsOf("UNETLoader") is not null;

    /// <summary>True if the workflow saves animated/video output (so the
    /// caller knows to expect a .webp/.mp4/.gif rather than a still .png).</summary>
    public bool ProducesVideo()
    {
        foreach (var (_, node) in Nodes)
        {
            if (node is not JsonObject obj) continue;
            var ct = obj["class_type"]?.GetValue<string>();
            if (ct == "SaveAnimatedWEBP" || ct == "VHS_VideoCombine" || ct == "SaveAnimatedPNG")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Walks the SaveImage / SaveAnimatedWEBP / VHS_VideoCombine outputs in a
    /// completed history record and returns each output's (filename, subfolder, type).
    /// </summary>
    public static IEnumerable<OutputFile> ExtractOutputs(JsonObject historyEntry)
    {
        var outputs = historyEntry["outputs"] as JsonObject;
        if (outputs is null) yield break;
        foreach (var (nodeId, node) in outputs)
        {
            if (node is not JsonObject obj) continue;
            // ComfyUI puts file lists under "images", "gifs", "videos" depending on the saver.
            foreach (var key in new[] { "images", "gifs", "videos" })
            {
                if (obj[key] is not JsonArray arr) continue;
                foreach (var item in arr)
                {
                    if (item is not JsonObject f) continue;
                    var filename = f["filename"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(filename)) continue;
                    var subfolder = f["subfolder"]?.GetValue<string>() ?? "";
                    var type = f["type"]?.GetValue<string>() ?? "output";
                    yield return new OutputFile(nodeId, filename, subfolder, type, key);
                }
            }
        }
    }
}

public sealed record OutputFile(string NodeId, string Filename, string Subfolder, string Type, string Kind);
