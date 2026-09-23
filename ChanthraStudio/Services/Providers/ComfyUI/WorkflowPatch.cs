using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services.Providers.ComfyUI;

/// <summary>
/// One line of "here is what the studio changed in your graph". Shown before
/// submit so the settings panel can never claim to have applied something the
/// workflow had no node for.
/// </summary>
/// <param name="Field">Human label — "Steps", "LoRA 1", "Batch size".</param>
/// <param name="Value">What was written, or what would have been.</param>
/// <param name="Applied">False when no node in the graph accepts it.</param>
/// <param name="Note">Why it did or didn't land, in the user's terms.</param>
public sealed record ComfyPatchLine(string Field, string Value, bool Applied, string Note)
{
    public string Icon => Applied ? "✓" : "—";
}

/// <summary>
/// Applies <see cref="ComfyRenderSettings"/> to an arbitrary user-supplied
/// workflow.
///
/// <b>The one invariant everything here rests on:</b> a key is only ever
/// overwritten on a node that <i>already has that key</i>. Workflows come from
/// the user, from vendors, and from custom node packs; inventing an input that
/// a node does not declare produces a 400 from ComfyUI's validator whose text
/// points at a node the user never touched. Writing only over existing keys
/// also makes the node-type differences disappear for free — KSamplerAdvanced
/// has no <c>denoise</c>, so denoise simply does not land there, and the report
/// says so instead of the value vanishing silently.
/// </summary>
public static class WorkflowPatch
{
    // Node families. Deliberately generous: a graph that uses SamplerCustom or
    // a video latent still gets its settings, and the has-this-key rule stops
    // the extra names from doing damage.
    private static readonly string[] SamplerNodes =
        { "KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced" };

    private static readonly string[] LatentNodes =
    {
        "EmptyLatentImage", "EmptySD3LatentImage", "EmptyLatentVideo",
        "EmptyHunyuanLatentVideo", "EmptyMochiLatentVideo", "EmptyCosmosLatentVideo",
        "EmptyLTXVLatentVideo",
        // Image-to-video conditioners carry the output dimensions themselves —
        // they have no empty-latent node at all. Leaving them out made the size
        // controls inert on exactly the workflows people use for video.
        "WanImageToVideo", "SVD_img2vid_Conditioning",
    };

    private static readonly string[] VideoLengthNodes =
    {
        "EmptyHunyuanLatentVideo", "EmptyLatentVideo", "EmptyMochiLatentVideo",
        "EmptyCosmosLatentVideo", "WanImageToVideo", "SVD_img2vid_Conditioning",
    };

    private static readonly string[] FpsNodes =
        { "CreateVideo", "SaveAnimatedWEBP", "VHS_VideoCombine", "SaveAnimatedPNG", "SaveWEBM" };

    private static readonly string[] LoraNodes = { "LoraLoader", "LoraLoaderModelOnly" };

    /// <summary>Every node, in a deterministic order (numeric id when the ids
    /// are numeric, which they are in API-format graphs).</summary>
    public static IEnumerable<(string Id, string ClassType, JsonObject Inputs)> AllNodes(this Workflow wf)
        => wf.Nodes
            .Where(kv => kv.Value is JsonObject)
            .Select(kv => (kv.Key, Obj: (JsonObject)kv.Value!))
            .Where(t => t.Obj["class_type"] is not null && t.Obj["inputs"] is JsonObject)
            .OrderBy(t => int.TryParse(t.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => (t.Key, t.Obj["class_type"]!.GetValue<string>(), (JsonObject)t.Obj["inputs"]!));

    /// <summary>
    /// Write <paramref name="value"/> to <paramref name="key"/> on every node
    /// of one of <paramref name="classTypes"/> that already declares that key.
    /// Returns how many nodes were changed.
    /// </summary>
    /// <remarks>
    /// A key wired to another node's output is a link (a JSON array like
    /// <c>["4", 0]</c>), not a literal. Overwriting one with a scalar would
    /// silently sever the graph — the classic symptom being a workflow that
    /// renders pure noise because its latent input became the number 1024.
    /// Links are therefore skipped, and counted as not-applied.
    /// </remarks>
    public static int SetOnAll(this Workflow wf, IEnumerable<string> classTypes, string key, JsonNode? value)
    {
        var set = new HashSet<string>(classTypes, StringComparer.Ordinal);
        var count = 0;
        foreach (var (_, ct, inputs) in wf.AllNodes())
        {
            if (!set.Contains(ct)) continue;
            if (!inputs.ContainsKey(key)) continue;
            if (inputs[key] is JsonArray) continue;          // wired input — leave the link alone
            inputs[key] = value?.DeepClone();
            count++;
        }
        return count;
    }

    /// <summary>The width × height the graph's own latent node asks for, or
    /// null when it has none (image-to-image graphs size from the input).</summary>
    public static (int Width, int Height)? LatentSize(this Workflow wf)
    {
        var set = new HashSet<string>(LatentNodes, StringComparer.Ordinal);
        foreach (var (_, ct, inputs) in wf.AllNodes())
        {
            if (!set.Contains(ct)) continue;
            if (inputs["width"] is JsonValue w && w.TryGetValue<int>(out var width)
                && inputs["height"] is JsonValue h && h.TryGetValue<int>(out var height)
                && width > 0 && height > 0)
                return (width, height);
        }
        return null;
    }

    /// <summary>Nodes of the given types that declare the given input key.</summary>
    public static int CountWith(this Workflow wf, IEnumerable<string> classTypes, string key)
    {
        var set = new HashSet<string>(classTypes, StringComparer.Ordinal);
        return wf.AllNodes().Count(n => set.Contains(n.ClassType)
                                        && n.Inputs.ContainsKey(key)
                                        && n.Inputs[key] is not JsonArray);
    }

    /// <summary>The LoRA loader nodes, in graph order.</summary>
    public static List<(string Id, JsonObject Inputs)> LoraLoaders(this Workflow wf)
        => wf.AllNodes()
             .Where(n => LoraNodes.Contains(n.ClassType, StringComparer.Ordinal))
             .Select(n => (n.Id, n.Inputs))
             .ToList();

    /// <summary>
    /// Apply the user's render settings, returning a line per setting the user
    /// switched on.
    /// </summary>
    /// <param name="seed">The seed to write. Resolved by the caller so the
    /// randomiser advances once per submit rather than once per patch.</param>
    /// <param name="fallbackWidth">Latent width to use when the size group is
    /// off — the existing aspect/HD derivation.</param>
    public static List<ComfyPatchLine> Apply(
        this Workflow wf, ComfyRenderSettings s, long seed,
        int fallbackWidth, int fallbackHeight)
    {
        var report = new List<ComfyPatchLine>();

        // --- seed. Always applied: reproducibility is not optional, and every
        // sampler node declares it. -------------------------------------------
        var seedNodes = wf.SetOnAll(SamplerNodes, "seed", seed)
                      + wf.SetOnAll(SamplerNodes, "noise_seed", seed);
        report.Add(new ComfyPatchLine("Seed", seed.ToString(CultureInfo.InvariantCulture),
            seedNodes > 0,
            seedNodes > 0 ? $"{seedNodes} sampler node(s)" : "no sampler node in this workflow"));

        // --- sampler group ---------------------------------------------------
        if (s.OverrideSampler)
        {
            Line(report, "Steps", s.Steps, wf.SetOnAll(SamplerNodes, "steps", s.Steps));
            Line(report, "CFG", s.Cfg, wf.SetOnAll(SamplerNodes, "cfg", s.Cfg));
            Line(report, "Sampler", s.SamplerName, wf.SetOnAll(SamplerNodes, "sampler_name", s.SamplerName));
            Line(report, "Scheduler", s.Scheduler, wf.SetOnAll(SamplerNodes, "scheduler", s.Scheduler));
            Line(report, "Denoise", s.Denoise, wf.SetOnAll(SamplerNodes, "denoise", s.Denoise),
                "this sampler node has no denoise input");
        }

        // --- size group ------------------------------------------------------
        var w = s.OverrideSize ? s.Width : fallbackWidth;
        var h = s.OverrideSize ? s.Height : fallbackHeight;
        var sized = wf.SetOnAll(LatentNodes, "width", w) + wf.SetOnAll(LatentNodes, "height", h);
        report.Add(new ComfyPatchLine("Size", $"{w}×{h}", sized > 0,
            sized > 0
                ? (s.OverrideSize ? "custom" : "from aspect + HD toggle")
                : "no empty-latent node — size comes from the input image"));

        if (s.OverrideSize && s.BatchSize > 1)
            Line(report, "Batch size", s.BatchSize, wf.SetOnAll(LatentNodes, "batch_size", s.BatchSize));

        // --- checkpoint ------------------------------------------------------
        if (s.OverrideCheckpoint && !string.IsNullOrWhiteSpace(s.CheckpointName))
            Line(report, "Checkpoint", s.CheckpointName,
                wf.SetOnAll(new[] { "CheckpointLoaderSimple" }, "ckpt_name", s.CheckpointName),
                "this workflow loads a UNET/diffusion model, not a checkpoint");

        // --- clip skip -------------------------------------------------------
        if (s.OverrideClipSkip)
            Line(report, "Clip skip", s.ClipSkip,
                wf.SetOnAll(new[] { "CLIPSetLastLayer" }, "stop_at_clip_layer", s.ClipSkip),
                "add a CLIPSetLastLayer node to the workflow to use this");

        // --- lora stack ------------------------------------------------------
        if (s.OverrideLoras)
        {
            var wanted = s.Loras.Where(l => l.Enabled && !string.IsNullOrWhiteSpace(l.Name)).ToList();
            var loaders = wf.LoraLoaders();

            for (var i = 0; i < wanted.Count; i++)
            {
                var entry = wanted[i];
                if (i >= loaders.Count)
                {
                    // We do not synthesise LoraLoader nodes. Injecting one means
                    // rewiring MODEL and CLIP through it, and getting that wrong
                    // on someone else's graph is worse than declining: the render
                    // would succeed and look subtly wrong.
                    report.Add(new ComfyPatchLine($"LoRA {i + 1}", entry.Name, false,
                        $"workflow has {loaders.Count} LoRA slot(s) — this one was not applied"));
                    continue;
                }

                var inputs = loaders[i].Inputs;
                var wrote = false;
                if (inputs.ContainsKey("lora_name") && inputs["lora_name"] is not JsonArray)
                {
                    inputs["lora_name"] = entry.Name;
                    wrote = true;
                }
                if (inputs.ContainsKey("strength_model") && inputs["strength_model"] is not JsonArray)
                    inputs["strength_model"] = entry.ModelStrength;
                if (inputs.ContainsKey("strength_clip") && inputs["strength_clip"] is not JsonArray)
                    inputs["strength_clip"] = entry.ClipStrength;

                report.Add(new ComfyPatchLine($"LoRA {i + 1}",
                    $"{entry.Name} · {entry.ModelStrength:0.##}/{entry.ClipStrength:0.##}",
                    wrote, wrote ? $"node {loaders[i].Id}" : "loader node has a wired lora_name"));
            }

            // Slots the user left empty must be neutralised, not left at
            // whatever the workflow shipped with — otherwise "I removed that
            // LoRA" removes it from the list but not from the render.
            for (var i = wanted.Count; i < loaders.Count; i++)
            {
                var inputs = loaders[i].Inputs;
                if (inputs.ContainsKey("strength_model") && inputs["strength_model"] is not JsonArray)
                    inputs["strength_model"] = 0.0;
                if (inputs.ContainsKey("strength_clip") && inputs["strength_clip"] is not JsonArray)
                    inputs["strength_clip"] = 0.0;
                report.Add(new ComfyPatchLine($"LoRA {i + 1}", "off", true,
                    "unused slot zeroed so the stack matches what you see"));
            }
        }

        // --- video -----------------------------------------------------------
        if (s.OverrideVideo)
        {
            Line(report, "Frames", s.VideoFrames,
                wf.SetOnAll(VideoLengthNodes, "length", s.VideoFrames)
                + wf.SetOnAll(VideoLengthNodes, "num_frames", s.VideoFrames),
                "this workflow has no video-length node");
            Line(report, "FPS", s.VideoFps,
                wf.SetOnAll(FpsNodes, "fps", s.VideoFps)
                + wf.SetOnAll(FpsNodes, "frame_rate", s.VideoFps),
                "this workflow has no animated saver");
        }

        return report;
    }

    /// <summary>
    /// What the settings panel can offer for a given workflow: a capability
    /// probe so switches for things the graph cannot do are visibly inert
    /// instead of quietly ineffective.
    /// </summary>
    public static ComfyWorkflowCapabilities Probe(this Workflow wf) => new(
        Samplers: wf.CountWith(SamplerNodes, "steps"),
        HasDenoise: wf.CountWith(SamplerNodes, "denoise") > 0,
        LatentNodes: wf.CountWith(LatentNodes, "width"),
        SupportsBatch: wf.CountWith(LatentNodes, "batch_size") > 0,
        Checkpoints: wf.CountWith(new[] { "CheckpointLoaderSimple" }, "ckpt_name"),
        ClipSkipNodes: wf.CountWith(new[] { "CLIPSetLastLayer" }, "stop_at_clip_layer"),
        LoraSlots: wf.LoraLoaders().Count,
        VideoLengthNodes: wf.CountWith(VideoLengthNodes, "length") + wf.CountWith(VideoLengthNodes, "num_frames"),
        FpsNodes: wf.CountWith(FpsNodes, "fps") + wf.CountWith(FpsNodes, "frame_rate"));

    private static void Line(List<ComfyPatchLine> report, string field, object value, int count,
                             string missingNote = "no node in this workflow accepts it")
    {
        var text = value is double d
            ? d.ToString("0.###", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        report.Add(new ComfyPatchLine(field, text, count > 0,
            count > 0 ? $"{count} node(s)" : missingNote));
    }
}

/// <summary>What a given workflow is actually able to accept.</summary>
public sealed record ComfyWorkflowCapabilities(
    int Samplers,
    bool HasDenoise,
    int LatentNodes,
    bool SupportsBatch,
    int Checkpoints,
    int ClipSkipNodes,
    int LoraSlots,
    int VideoLengthNodes,
    int FpsNodes)
{
    public bool CanSample => Samplers > 0;
    public bool CanSize => LatentNodes > 0;
    public bool CanCheckpoint => Checkpoints > 0;
    public bool CanClipSkip => ClipSkipNodes > 0;
    public bool CanLora => LoraSlots > 0;
    public bool CanVideo => VideoLengthNodes > 0 || FpsNodes > 0;
}
