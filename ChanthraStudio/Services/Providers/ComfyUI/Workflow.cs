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

    /// <summary>Load the bundled default text2img workflow from embedded resources.</summary>
    public static Workflow LoadDefault()
    {
        var asm = Assembly.GetExecutingAssembly();
        // Resource is included as application Resource (pack URI) — in the
        // self-contained .exe path it's bundled; in dev it's also on disk.
        // We try the disk path first (faster + supports user edits), then
        // fall back to the embedded copy.
        var diskPath = Path.Combine(
            Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory,
            "Assets", "Workflows", "default_text2img.json");
        string json;
        if (File.Exists(diskPath))
        {
            json = File.ReadAllText(diskPath);
        }
        else
        {
            var uri = new Uri("pack://application:,,,/Assets/Workflows/default_text2img.json");
            using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
            using var sr = new StreamReader(stream);
            json = sr.ReadToEnd();
        }
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("default workflow is not a JSON object");
        return new Workflow(node);
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
