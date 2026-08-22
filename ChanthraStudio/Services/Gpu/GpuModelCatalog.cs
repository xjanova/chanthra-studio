using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChanthraStudio.Services.Gpu;

/// <summary>One file to pull onto the rented box before ComfyUI starts.</summary>
public sealed class GpuWeightFile
{
    /// <summary>Direct download URL. Verified against Hugging Face at the time
    /// this catalog was written — see <see cref="GpuModelCatalog"/> remarks.</summary>
    public string Url { get; set; } = "";

    /// <summary>Folder under <c>ComfyUI/models/</c> — "checkpoints",
    /// "diffusion_models", "vae", "text_encoders", "clip_vision", "loras".</summary>
    public string Folder { get; set; } = "";

    /// <summary>Filename on disk. Must match what the workflow's loader node
    /// asks for, or at least fuzzy-match it (the generation pipeline strips
    /// -fp8/-fp16-style suffixes when resolving).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Measured size in GB. Drives the disk requirement and the
    /// warm-up estimate — both of which are money, so these are real
    /// content-length readings rather than round numbers.</summary>
    public double SizeGb { get; set; }
}

/// <summary>
/// A rentable configuration: which card to look for, and what to install on it.
/// </summary>
public sealed class GpuModelProfile
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Minimum VRAM in GB the marketplace filter asks for.</summary>
    public int MinVramGb { get; set; } = 24;

    /// <summary>Docker image to boot. A stock PyTorch runtime — we install
    /// ComfyUI ourselves in the start script rather than depending on a
    /// third-party "ComfyUI image" whose contents can change under us.</summary>
    public string DockerImage { get; set; } = DefaultImage;

    /// <summary>True when the weights sit behind a gated Hugging Face repo,
    /// so renting without an HF token would burn money on a box that can't
    /// download anything.</summary>
    public bool RequiresHfToken { get; set; }

    /// <summary>Names of the bundled workflows this profile can actually run.
    /// Surfaced in the UI so the user doesn't rent a Flux box to run an SDXL
    /// workflow.</summary>
    public List<string> Workflows { get; set; } = new();

    public List<GpuWeightFile> Files { get; set; } = new();

    /// <summary>
    /// Torch 2.9.1 on CUDA 13.0.
    ///
    /// <b>The previous value — <c>pytorch/pytorch:2.6.0-cuda12.8-cudnn9-runtime</c>
    /// — does not exist.</b> Docker Hub 404s it: <c>pytorch/pytorch</c> has no
    /// CUDA 12.8 build before torch 2.7.0. Every rental would have paid for a
    /// machine that could never pull its image. It was never caught because the
    /// vendor API had not been called with a real key.
    ///
    /// The replacement is chosen against two published floors rather than
    /// taste. ComfyUI's README: <i>"torch 2.7 is minimally supported… Using a
    /// cu130 or above version of pytorch is required on Nvidia 20 series and
    /// above."</i> Every card worth renting is 20-series or above, so cu130 is
    /// the floor, not the luxury option. 2.9.1 rather than the newest tag
    /// because the old comment's instinct was right — a very new runtime
    /// strands you on hosts with older drivers, and the marketplace's cheap end
    /// is not where fresh drivers live.
    ///
    /// Overridable per profile, and via the <c>gpu:dockerImage</c> setting, so
    /// the next time this floor moves it is an edit rather than a build.
    /// </summary>
    public const string DefaultImage = "pytorch/pytorch:2.9.1-cuda13.0-cudnn9-runtime";

    [JsonIgnore]
    public double TotalWeightsGb => Files.Sum(f => f.SizeGb);

    /// <summary>Weights + ComfyUI + torch wheels + room for outputs.</summary>
    [JsonIgnore]
    public int RequiredDiskGb => (int)Math.Ceiling(TotalWeightsGb) + 30;

    /// <summary>
    /// Rough warm-up estimate in minutes at a given link speed. Download
    /// dominates; ~6 minutes covers apt + pip + ComfyUI clone + model load.
    /// Used to set an honest expectation in the UI, not to time anything out.
    /// </summary>
    public int EstimatedWarmupMinutes(int mbps)
    {
        var effective = Math.Max(50, mbps);
        var downloadMin = TotalWeightsGb * 8192.0 / effective / 60.0;
        return (int)Math.Ceiling(downloadMin) + 6;
    }
}

/// <summary>
/// The rentable profiles, matched one-to-one against the workflows this app
/// ships in <c>Assets/Workflows/</c> — renting a box that can't run any of
/// our workflows would be a bill with nothing to show for it.
///
/// Every URL and every size in the built-in list was verified against
/// Hugging Face (HTTP 200 + content-length) when the catalog was written.
/// URLs still rot, so <see cref="LoadOverrides"/> lets the user repair one
/// in a JSON file next to the database without waiting for a new build.
/// </summary>
public static class GpuModelCatalog
{
    private const string HfSdxl = "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main";
    private const string HfLightning = "https://huggingface.co/ByteDance/SDXL-Lightning/resolve/main";
    private const string HfSd15 = "https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive/resolve/main";
    private const string HfFlux = "https://huggingface.co/Comfy-Org/flux1-dev/resolve/main";
    private const string HfFluxVae = "https://huggingface.co/Comfy-Org/Lumina_Image_2.0_Repackaged/resolve/main/split_files/vae";
    private const string HfFluxText = "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main";
    private const string HfHunyuan = "https://huggingface.co/Comfy-Org/HunyuanVideo_repackaged/resolve/main/split_files";
    private const string HfWan = "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files";
    private const string HfAceStep = "https://huggingface.co/Comfy-Org/ACE-Step_ComfyUI_repackaged/resolve/main";

    private static readonly List<GpuModelProfile> BuiltIn = new()
    {
        new GpuModelProfile
        {
            Key = "sd15",
            DisplayName = "SD 1.5 · stills",
            Description = "Cheapest box that renders. Good for storyboard frames and look tests.",
            MinVramGb = 8,
            Workflows = { "default_text2img", "sd15_image2image" },
            Files =
            {
                new() { Url = $"{HfSd15}/v1-5-pruned-emaonly-fp16.safetensors", Folder = "checkpoints", FileName = "v1-5-pruned-emaonly-fp16.safetensors", SizeGb = 1.99 },
            },
        },
        new GpuModelProfile
        {
            Key = "sdxl",
            DisplayName = "SDXL · stills",
            Description = "SDXL base plus the 4-step Lightning checkpoint for fast drafts.",
            MinVramGb = 12,
            Workflows = { "sdxl_text2img", "sdxl_image2image", "sdxl_lightning_4step", "sdxl_lora_stack" },
            Files =
            {
                new() { Url = $"{HfSdxl}/sd_xl_base_1.0.safetensors", Folder = "checkpoints", FileName = "sd_xl_base_1.0.safetensors", SizeGb = 6.46 },
                new() { Url = $"{HfLightning}/sdxl_lightning_4step.safetensors", Folder = "checkpoints", FileName = "sdxl_lightning_4step.safetensors", SizeGb = 6.46 },
            },
        },
        new GpuModelProfile
        {
            Key = "flux",
            DisplayName = "FLUX.1 dev · stills",
            Description = "Highest-fidelity stills. FP8 build — no Hugging Face token needed.",
            MinVramGb = 24,
            Workflows = { "flux_dev_text2img" },
            Files =
            {
                // The fp8 repack rather than black-forest-labs/FLUX.1-dev, which
                // is gated (401 without a token). The generation pipeline's
                // fuzzy matcher resolves the workflow's "flux1-dev.safetensors"
                // to this file because it strips the -fp8 suffix when comparing.
                new() { Url = $"{HfFlux}/flux1-dev-fp8.safetensors", Folder = "diffusion_models", FileName = "flux1-dev-fp8.safetensors", SizeGb = 16.06 },
                new() { Url = $"{HfFluxVae}/ae.safetensors", Folder = "vae", FileName = "ae.safetensors", SizeGb = 0.31 },
                new() { Url = $"{HfFluxText}/t5xxl_fp8_e4m3fn.safetensors", Folder = "text_encoders", FileName = "t5xxl_fp8_e4m3fn.safetensors", SizeGb = 4.56 },
                new() { Url = $"{HfFluxText}/clip_l.safetensors", Folder = "text_encoders", FileName = "clip_l.safetensors", SizeGb = 0.23 },
            },
        },
        new GpuModelProfile
        {
            Key = "wan21",
            DisplayName = "WAN 2.1 · image → video",
            Description = "Animates a reference still. Needs a reference image on the shot.",
            MinVramGb = 24,
            Workflows = { "wan_image2video" },
            Files =
            {
                new() { Url = $"{HfWan}/diffusion_models/wan2.1_i2v_480p_14B_fp8_e4m3fn.safetensors", Folder = "diffusion_models", FileName = "wan2.1_i2v_480p_14B_fp8_e4m3fn.safetensors", SizeGb = 15.27 },
                new() { Url = $"{HfWan}/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", Folder = "text_encoders", FileName = "umt5_xxl_fp8_e4m3fn_scaled.safetensors", SizeGb = 6.27 },
                new() { Url = $"{HfWan}/vae/wan_2.1_vae.safetensors", Folder = "vae", FileName = "wan_2.1_vae.safetensors", SizeGb = 0.24 },
                new() { Url = $"{HfWan}/clip_vision/clip_vision_h.safetensors", Folder = "clip_vision", FileName = "clip_vision_h.safetensors", SizeGb = 1.18 },
            },
        },
        new GpuModelProfile
        {
            Key = "hunyuan",
            DisplayName = "Hunyuan · text → video",
            Description = "Text-to-video at 720p. The heaviest profile — longest warm-up, highest bill.",
            MinVramGb = 24,
            Workflows = { "hunyuan_text2video" },
            Files =
            {
                new() { Url = $"{HfHunyuan}/diffusion_models/hunyuan_video_t2v_720p_bf16.safetensors", Folder = "diffusion_models", FileName = "hunyuan_video_t2v_720p_bf16.safetensors", SizeGb = 23.88 },
                new() { Url = $"{HfHunyuan}/vae/hunyuan_video_vae_bf16.safetensors", Folder = "vae", FileName = "hunyuan_video_vae_bf16.safetensors", SizeGb = 0.46 },
                new() { Url = $"{HfHunyuan}/text_encoders/llava_llama3_fp8_scaled.safetensors", Folder = "text_encoders", FileName = "llava_llama3_fp8_scaled.safetensors", SizeGb = 8.47 },
                new() { Url = $"{HfHunyuan}/text_encoders/clip_l.safetensors", Folder = "text_encoders", FileName = "clip_l.safetensors", SizeGb = 0.23 },
            },
        },
        new GpuModelProfile
        {
            Key = "music",
            DisplayName = "ACE-Step · text → music",
            Description = "Full songs with vocals from a tag list, lyrics optional. One 7 GB file and an 8 GB card — the cheapest useful box in the catalog after sd15.",
            MinVramGb = 8,
            Workflows = { "ace_step_text2music" },
            Files =
            {
                // One all-in-one checkpoint: reading the safetensors header shows
                // vae.*, model.* and text_encoders.* all present, so
                // CheckpointLoaderSimple alone yields MODEL/CLIP/VAE and there is
                // no separate encoder or VAE to pull. Apache-2.0, ungated,
                // verified 200 with content-length 7,699,743,341.
                new() { Url = $"{HfAceStep}/all_in_one/ace_step_v1_3.5b.safetensors", Folder = "checkpoints", FileName = "ace_step_v1_3.5b.safetensors", SizeGb = 7.17 },
            },
        },
    };

    private static List<GpuModelProfile>? _cache;

    /// <summary>Built-in profiles with any on-disk overrides folded in.</summary>
    public static IReadOnlyList<GpuModelProfile> All => _cache ??= BuildEffective();

    public static GpuModelProfile? Find(string? key)
        => string.IsNullOrWhiteSpace(key) ? null
         : All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cheapest profile able to run the given bundled workflow.</summary>
    public static GpuModelProfile? ForWorkflow(string? workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName)) return null;
        var stem = Path.GetFileNameWithoutExtension(workflowName);
        return All
            .Where(p => p.Workflows.Any(w => string.Equals(w, stem, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.MinVramGb)
            .FirstOrDefault();
    }

    /// <summary>Drop the memoised list so an edited overrides file takes
    /// effect without restarting the app.</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>Where the user edits profiles. Sits next to the database so
    /// it survives an app update.</summary>
    public static string OverridesPath => Path.Combine(AppPaths.Root, "gpu-profiles.json");

    private static List<GpuModelProfile> BuildEffective()
    {
        var effective = BuiltIn.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var o in LoadOverrides())
        {
            if (string.IsNullOrWhiteSpace(o.Key)) continue;
            effective[o.Key] = o;    // whole-profile replace: partial merges are worse to reason about
        }
        return effective.Values.OrderBy(p => p.MinVramGb).ThenBy(p => p.DisplayName).ToList();
    }

    private static List<GpuModelProfile> LoadOverrides()
    {
        try
        {
            if (!File.Exists(OverridesPath)) return new List<GpuModelProfile>();
            var json = File.ReadAllText(OverridesPath);
            return JsonSerializer.Deserialize<List<GpuModelProfile>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new List<GpuModelProfile>();
        }
        catch
        {
            // A malformed overrides file must not take the whole feature down —
            // fall back to the built-ins the app shipped with.
            return new List<GpuModelProfile>();
        }
    }

    /// <summary>Writes the built-in list out as a starting point for editing.</summary>
    public static void ExportBuiltInsTo(string path)
    {
        var json = JsonSerializer.Serialize(BuiltIn, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, json);
    }
}
