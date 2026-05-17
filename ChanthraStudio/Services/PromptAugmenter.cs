using System.Text;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Folds the Composer's non-text controls (camera language, motion intensity,
/// style preset, HD upscale, native audio, duration) into the prompt string
/// that gets POST'd to ComfyUI / Replicate.
///
/// Before this helper, those controls only set fields on <see cref="Shot"/>
/// that nothing read — UI lies. Now the prompt the model sees actually
/// reflects what the user clicked.
///
/// Strategy: prepend a short "camera + style" clause, append a "motion +
/// fidelity" clause. Keeps the user's own prompt as the lead so manual
/// edits still dominate the result; the augmentation only shapes texture.
/// </summary>
public static class PromptAugmenter
{
    /// <summary>
    /// Build the final positive prompt to send to the model. The user's raw
    /// <see cref="Shot.Prompt"/> is preserved verbatim in the middle; we add
    /// shaping clauses on either side based on the other Shot fields.
    /// </summary>
    public static string Augment(Shot shot)
    {
        var head = new StringBuilder();
        var tail = new StringBuilder();

        var stylePrefix = StyleClause(shot.StyleId);
        if (!string.IsNullOrEmpty(stylePrefix))
            head.Append(stylePrefix).Append(", ");

        var camPrefix = CameraClause(shot.Cam);
        if (!string.IsNullOrEmpty(camPrefix))
            head.Append(camPrefix).Append(", ");

        var motionTail = MotionClause(shot.Motion);
        if (!string.IsNullOrEmpty(motionTail))
            tail.Append(", ").Append(motionTail);

        if (shot.Hd4k)
            tail.Append(", 4k uhd, sharp focus, highly detailed, crisp textures");

        // Audio is a video-only concern — we can't inject it into text-to-image
        // prompts meaningfully, so we don't. Video providers receive it via
        // VideoRequest.Audio and decide whether the underlying model supports
        // it (most kling/minimax/hunyuan checkpoints do, flux/sdxl don't).

        var combined = head.ToString() + (shot.Prompt ?? "") + tail.ToString();
        return combined.Trim().TrimStart(',').Trim();
    }

    /// <summary>Camera-language → cinematographer-vocabulary clause.</summary>
    private static string CameraClause(CamMode cam) => cam switch
    {
        CamMode.Locked => "static locked-off shot, no camera movement",
        CamMode.Pan    => "smooth horizontal camera pan",
        CamMode.Tilt   => "graceful camera tilt",
        CamMode.Push   => "slow cinematic camera push-in",
        CamMode.Orbit  => "orbital camera arc around subject",
        CamMode.Dolly  => "dolly tracking shot, parallax foreground",
        _ => "",
    };

    /// <summary>
    /// Motion slider (0–1) is bucketed to three descriptors. Linear
    /// interpolation in the prompt wouldn't add nuance — the model reads
    /// "slight" vs "dramatic" categorically, not as a scalar.
    /// </summary>
    private static string MotionClause(double motion)
    {
        if (motion < 0.25) return "subtle micro-movement, near-still";
        if (motion < 0.60) return "moderate cinematic motion";
        if (motion < 0.85) return "energetic motion, flowing fabrics and particles";
        return "dramatic dynamic motion, sweeping action";
    }

    /// <summary>
    /// Style preset → brand-locked prompt prefix. The Empress is our default
    /// (juntra-payakorn fortune-teller brand); other presets are riffs on it.
    /// </summary>
    private static string StyleClause(string styleId) => styleId switch
    {
        "empress"     => "an empress in crimson silk with a gold halo, lunar-atelier aesthetic",
        "lunar-veil"  => "ethereal subject draped in moonlight veil, mauve and silver palette",
        "temple-silk" => "ornate temple silk drapery, gold leaf detail, sacred geometry",
        "oracle"      => "oracle priestess, gold and obsidian, mystical atmosphere",
        _ => "",
    };

    /// <summary>
    /// Some Replicate video models accept a <c>duration</c> input expressed
    /// in seconds; others use frames; some don't accept it at all. This
    /// helper returns the right input key (or null = don't pass it) for a
    /// given model slug. Centralised here so the provider stays generic.
    /// </summary>
    public static (string? Key, object? Value) ResolveDurationParam(string modelSlug, double durationSec)
    {
        if (string.IsNullOrEmpty(modelSlug)) return (null, null);
        var s = modelSlug.ToLowerInvariant();

        // Frames-based models — hunyuan ~24fps, ltx ~25fps.
        if (s.Contains("hunyuan") || s.Contains("ltx"))
            return ("video_length", (int)System.Math.Round(durationSec * 24));

        // Seconds-based models — kling, minimax, runway, mochi, wan, veo.
        if (s.Contains("kling") || s.Contains("minimax") || s.Contains("runway")
            || s.Contains("mochi") || s.Contains("wan") || s.Contains("veo")
            || s.Contains("video"))
        {
            // Kling allows only 5 or 10; round to nearest supported value.
            if (s.Contains("kling"))
                return ("duration", durationSec < 7.5 ? 5 : 10);
            return ("duration", (int)System.Math.Round(durationSec));
        }

        // Image / unknown → no duration input.
        return (null, null);
    }
}
