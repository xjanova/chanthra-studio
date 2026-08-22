using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChanthraStudio.Services;

namespace ChanthraStudio.Models;

/// <summary>How the seed is chosen for the next submit.</summary>
public enum ComfySeedMode
{
    /// <summary>Same seed every time — the only mode that reproduces a result.</summary>
    Fixed,
    /// <summary>New random seed per submit.</summary>
    Randomize,
    /// <summary>Previous seed + 1. Walks a neighbourhood instead of jumping.</summary>
    Increment,
}

/// <summary>One LoRA in the stack.</summary>
public sealed class ComfyLoraEntry
{
    public string Name { get; set; } = "";

    /// <summary>Weight applied to the UNet. 0 disables the LoRA's effect on
    /// structure; above ~1.2 most LoRAs start to burn.</summary>
    public double ModelStrength { get; set; } = 1.0;

    /// <summary>Weight applied to the text encoder.</summary>
    public double ClipStrength { get; set; } = 1.0;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Everything the studio is allowed to change inside a user-supplied ComfyUI
/// workflow before submitting it.
///
/// <b>Why every group has its own override switch.</b> A workflow is somebody's
/// tuned artefact. Before this type existed the app unconditionally rewrote the
/// prompt, the seed and the latent size on every submit, and hard-coded 36
/// steps / CFG 7.5 whenever the HD toggle was on — so a graph carefully set to
/// 12 steps for a Lightning checkpoint silently rendered at 36 and took three
/// times as long. Overriding is now something the user asks for per group, and
/// a group that is off leaves the workflow's own values completely alone.
///
/// <b>Why nothing here is silently best-effort.</b> Applying a setting to a
/// workflow that has no node for it (a LoRA stack on a graph with no
/// LoraLoader, a CFG on a graph with no KSampler) used to be a no-op the user
/// could not see. <see cref="Apply"/> returns a report of what actually
/// landed so the UI can say so out loud.
/// </summary>
public sealed class ComfyRenderSettings
{
    // ----------------------------------------------------------- sampler

    /// <summary>When off, the workflow's own sampler settings are untouched.</summary>
    public bool OverrideSampler { get; set; }

    /// <summary>Must be a value the server reports for KSampler.sampler_name —
    /// the picker is populated from /object_info rather than a hard-coded list,
    /// because custom nodes add samplers and a stale list would offer the user
    /// a value that fails validation at submit.</summary>
    public string SamplerName { get; set; } = "euler";

    public string Scheduler { get; set; } = "normal";

    public int Steps { get; set; } = 20;

    public double Cfg { get; set; } = 7.0;

    /// <summary>1.0 = ignore the input latent (text2img). Lower values keep
    /// more of it, which is the whole control surface of image2image.</summary>
    public double Denoise { get; set; } = 1.0;

    // -------------------------------------------------------------- size

    /// <summary>When off, size comes from the composer's aspect + HD toggle
    /// exactly as before.</summary>
    public bool OverrideSize { get; set; }

    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;

    /// <summary>Images per submit. Above 1 the extra outputs land in the
    /// library as separate rows.</summary>
    public int BatchSize { get; set; } = 1;

    // -------------------------------------------------------------- seed

    public ComfySeedMode SeedMode { get; set; } = ComfySeedMode.Fixed;

    /// <summary>The seed actually used by the most recent submit. Written back
    /// after every generation so a good result stays reproducible even in
    /// randomize mode — a randomiser that forgets what it rolled is a
    /// generator you cannot use twice.</summary>
    public long LastSeed { get; set; } = 2814;

    // -------------------------------------------------------- checkpoint

    /// <summary>When off, the workflow's checkpoint is kept (subject to the
    /// existing fuzzy resolution against what the server actually has).</summary>
    public bool OverrideCheckpoint { get; set; }

    public string CheckpointName { get; set; } = "";

    // --------------------------------------------------------- clip skip

    public bool OverrideClipSkip { get; set; }

    /// <summary>ComfyUI's CLIPSetLastLayer takes a negative index: -1 is the
    /// last layer (no skip), -2 skips one. Stored the way the node wants it
    /// so nothing has to remember to negate it.</summary>
    public int ClipSkip { get; set; } = -1;

    // ------------------------------------------------------------- loras

    public bool OverrideLoras { get; set; }

    public List<ComfyLoraEntry> Loras { get; set; } = new();

    // ------------------------------------------------------------- video

    /// <summary>When off, frame count and fps come from the workflow.</summary>
    public bool OverrideVideo { get; set; }

    public int VideoFrames { get; set; } = 49;
    public int VideoFps { get; set; } = 16;

    // ------------------------------------------------------- persistence

    private const string P = "comfy:";

    public static ComfyRenderSettings Load(AppSettings s)
    {
        var c = new ComfyRenderSettings();
        c.OverrideSampler = Flag(s, "overrideSampler", c.OverrideSampler);
        c.SamplerName = Text(s, "samplerName", c.SamplerName);
        c.Scheduler = Text(s, "scheduler", c.Scheduler);
        c.Steps = Num(s, "steps", c.Steps, 1, 200);
        c.Cfg = Real(s, "cfg", c.Cfg, 0, 30);
        c.Denoise = Real(s, "denoise", c.Denoise, 0, 1);

        c.OverrideSize = Flag(s, "overrideSize", c.OverrideSize);
        c.Width = Num(s, "width", c.Width, 64, 4096);
        c.Height = Num(s, "height", c.Height, 64, 4096);
        c.BatchSize = Num(s, "batchSize", c.BatchSize, 1, 16);

        c.SeedMode = Text(s, "seedMode", "Fixed") switch
        {
            "Randomize" => ComfySeedMode.Randomize,
            "Increment" => ComfySeedMode.Increment,
            _ => ComfySeedMode.Fixed,
        };
        c.LastSeed = LongNum(s, "lastSeed", c.LastSeed);

        c.OverrideCheckpoint = Flag(s, "overrideCheckpoint", c.OverrideCheckpoint);
        c.CheckpointName = Text(s, "checkpointName", c.CheckpointName);

        c.OverrideClipSkip = Flag(s, "overrideClipSkip", c.OverrideClipSkip);
        c.ClipSkip = Num(s, "clipSkip", c.ClipSkip, -24, -1);

        c.OverrideLoras = Flag(s, "overrideLoras", c.OverrideLoras);
        c.Loras = ParseLoras(s.GetSetting(P + "loras"));

        c.OverrideVideo = Flag(s, "overrideVideo", c.OverrideVideo);
        c.VideoFrames = Num(s, "videoFrames", c.VideoFrames, 1, 1000);
        c.VideoFps = Num(s, "videoFps", c.VideoFps, 1, 120);
        return c;
    }

    public void SaveTo(AppSettings s)
    {
        // Booleans are written as explicit "0"/"1" rather than ""/"1":
        // SetSetting drops empty strings, so an off flag stored as "" reads
        // back as the constructor default and quietly re-arms itself.
        s.SetSetting(P + "overrideSampler", OverrideSampler ? "1" : "0");
        s.SetSetting(P + "samplerName", SamplerName);
        s.SetSetting(P + "scheduler", Scheduler);
        s.SetSetting(P + "steps", Str(Steps));
        s.SetSetting(P + "cfg", Str(Cfg));
        s.SetSetting(P + "denoise", Str(Denoise));

        s.SetSetting(P + "overrideSize", OverrideSize ? "1" : "0");
        s.SetSetting(P + "width", Str(Width));
        s.SetSetting(P + "height", Str(Height));
        s.SetSetting(P + "batchSize", Str(BatchSize));

        s.SetSetting(P + "seedMode", SeedMode.ToString());
        s.SetSetting(P + "lastSeed", LastSeed.ToString(CultureInfo.InvariantCulture));

        s.SetSetting(P + "overrideCheckpoint", OverrideCheckpoint ? "1" : "0");
        s.SetSetting(P + "checkpointName", CheckpointName);

        s.SetSetting(P + "overrideClipSkip", OverrideClipSkip ? "1" : "0");
        s.SetSetting(P + "clipSkip", Str(ClipSkip));

        s.SetSetting(P + "overrideLoras", OverrideLoras ? "1" : "0");
        s.SetSetting(P + "loras", SerialiseLoras());

        s.SetSetting(P + "overrideVideo", OverrideVideo ? "1" : "0");
        s.SetSetting(P + "videoFrames", Str(VideoFrames));
        s.SetSetting(P + "videoFps", Str(VideoFps));
        s.Save();
    }

    /// <summary>
    /// Write back only the seed. Called after every submit, which is why it is
    /// not <see cref="SaveTo"/> — that writes twenty-odd rows, and doing so on
    /// each generation would turn a render into a burst of database writes.
    /// </summary>
    public void PersistLastSeed(AppSettings s)
    {
        s.SetSetting(P + "lastSeed", LastSeed.ToString(CultureInfo.InvariantCulture));
        s.Save();
    }

    /// <summary>
    /// The seed to submit next, given the mode. Pure — the caller decides when
    /// to commit it via <see cref="LastSeed"/>, so previewing the next seed in
    /// the UI does not advance it.
    /// </summary>
    public long NextSeed(long workflowSeed) => SeedMode switch
    {
        ComfySeedMode.Randomize => Random.Shared.NextInt64(1, 0xFFFF_FFFFL),
        ComfySeedMode.Increment => unchecked(LastSeed + 1),
        _ => workflowSeed,
    };

    // --------------------------------------------------------- serialisation

    private static readonly JsonSerializerOptions LoraJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private string SerialiseLoras()
    {
        // "[]" rather than "": SetSetting drops empty strings, so clearing the
        // stack would otherwise leave the previous stack on disk.
        try { return Loras.Count == 0 ? "[]" : JsonSerializer.Serialize(Loras, LoraJson); }
        catch { return "[]"; }
    }

    private static List<ComfyLoraEntry> ParseLoras(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try
        {
            var list = JsonSerializer.Deserialize<List<ComfyLoraEntry>>(raw!, LoraJson);
            // A stack entry with no filename would patch a LoraLoader with an
            // empty name and fail server-side validation, which surfaces as an
            // opaque 400 far from here. Drop them at the boundary instead.
            return list?.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList() ?? new();
        }
        catch
        {
            // Hand-edited or half-written JSON must not take the app down on
            // launch; an empty stack is visibly wrong and recoverable.
            return new();
        }
    }

    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Str(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Text(AppSettings s, string key, string fallback)
    {
        var v = s.GetSetting(P + key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static bool Flag(AppSettings s, string key, bool fallback)
    {
        var v = s.GetSetting(P + key);
        return string.IsNullOrEmpty(v) ? fallback : v != "0";
    }

    private static int Num(AppSettings s, string key, int fallback, int min, int max)
        => int.TryParse(s.GetSetting(P + key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
           ? Math.Clamp(v, min, max) : fallback;

    private static long LongNum(AppSettings s, string key, long fallback)
        => long.TryParse(s.GetSetting(P + key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
           ? v : fallback;

    private static double Real(AppSettings s, string key, double fallback, double min, double max)
        => double.TryParse(s.GetSetting(P + key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
           ? Math.Clamp(v, min, max) : fallback;
}
