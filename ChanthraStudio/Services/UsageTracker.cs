using System;
using System.Linq;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Thin facade over <see cref="UsageRepository"/> that knows how to look
/// up pricing in <see cref="ProviderCatalog"/> and convert USD to THB
/// using the current FX rate. Every provider call site (LLM, TTS,
/// music, image, video) goes through one of the Record* methods so we
/// have a single source of truth for cost calculation.
/// </summary>
public sealed class UsageTracker
{
    private readonly StudioContext _ctx;

    /// <summary>Default fallback rate when the user hasn't set one.</summary>
    public const double DefaultFxRate = 36.0;

    public event Action<UsageEvent>? Recorded;

    public UsageTracker(StudioContext ctx) { _ctx = ctx; }

    public double CurrentFxRate
    {
        get
        {
            var raw = _ctx.Settings.GetSetting("fx_usd_thb");
            if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
            return DefaultFxRate;
        }
        set
        {
            _ctx.Settings.SetSetting("fx_usd_thb",
                value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            try { _ctx.Settings.Save(); } catch { /* best-effort */ }
        }
    }

    /// <summary>Record a chat-completion call with token counts from the API response.</summary>
    public UsageEvent RecordTokens(string providerId, string modelSlug, long inputTokens, long outputTokens, string? note = null)
    {
        var (priceIn, priceOut) = LookupTokenPricing(providerId, modelSlug);
        // Per-1M pricing — divide by 1M to get USD per token.
        var usd = (inputTokens / 1_000_000.0) * priceIn
                + (outputTokens / 1_000_000.0) * priceOut;
        return Persist(providerId, modelSlug, "tokens", inputTokens, outputTokens, usd, note);
    }

    /// <summary>Record a TTS call. <paramref name="chars"/> is the raw input character count.</summary>
    public UsageEvent RecordChars(string providerId, string modelSlug, long chars, string? note = null)
    {
        var pricePerChar = LookupCharPricing(providerId, modelSlug);
        var usd = chars * pricePerChar;
        return Persist(providerId, modelSlug, "chars", chars, 0, usd, note);
    }

    /// <summary>Record a text-to-image call. <paramref name="images"/> is the count of images returned.</summary>
    public UsageEvent RecordImages(string providerId, string modelSlug, int images, string? note = null)
    {
        var perImage = LookupImagePricing(providerId, modelSlug);
        var usd = images * perImage;
        return Persist(providerId, modelSlug, "images", images, 0, usd, note);
    }

    /// <summary>Record a video / music call. <paramref name="durationSec"/> is the OUTPUT duration.</summary>
    public UsageEvent RecordSeconds(string providerId, string modelSlug, double durationSec, string? note = null)
    {
        var perSec = LookupSecondPricing(providerId, modelSlug);
        var usd = durationSec * perSec;
        return Persist(providerId, modelSlug, "seconds", (long)Math.Ceiling(durationSec), 0, usd, note);
    }

    private UsageEvent Persist(string providerId, string modelSlug, string unitKind,
        long inputUnits, long outputUnits, double costUsd, string? note)
    {
        var fx = CurrentFxRate;
        var ev = new UsageEvent
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ProviderId = providerId,
            ModelSlug = modelSlug,
            UnitKind = unitKind,
            InputUnits = inputUnits,
            OutputUnits = outputUnits,
            CostUsd = costUsd,
            FxRate = fx,
            CostThb = costUsd * fx,
            Note = note,
        };
        try
        {
            ev.Id = _ctx.Usage.Insert(ev);
        }
        catch
        {
            // DB write is best-effort — losing telemetry must never break
            // the actual generation call that triggered it.
        }
        Recorded?.Invoke(ev);
        return ev;
    }

    /// <summary>Look up per-1M token pricing. Falls back to (0, 0) for free models or unknown slugs.</summary>
    private static (double InUsdPer1M, double OutUsdPer1M) LookupTokenPricing(string providerId, string modelSlug)
    {
        var info = ProviderCatalog.FindById(providerId);
        var model = info?.Models.FirstOrDefault(m => m.Slug == modelSlug);
        return (model?.InputUsdPer1M ?? 0, model?.OutputUsdPer1M ?? 0);
    }

    private static double LookupCharPricing(string providerId, string modelSlug)
    {
        var info = ProviderCatalog.FindById(providerId);
        var model = info?.Models.FirstOrDefault(m => m.Slug == modelSlug);
        return model?.UsdPerChar ?? 0;
    }

    private static double LookupImagePricing(string providerId, string modelSlug)
    {
        var info = ProviderCatalog.FindById(providerId);
        // For Replicate the slug is "owner/name" and lives in the Replicate catalog.
        var model = info?.Models.FirstOrDefault(m => m.Slug == modelSlug);
        if (model is null && providerId == "replicate")
        {
            model = ProviderCatalog.FindById("replicate")?.Models
                .FirstOrDefault(m => m.Slug == modelSlug);
        }
        return model?.UsdPerImage ?? 0;
    }

    private static double LookupSecondPricing(string providerId, string modelSlug)
    {
        var info = ProviderCatalog.FindById(providerId);
        var model = info?.Models.FirstOrDefault(m => m.Slug == modelSlug);
        if (model is null && providerId == "replicate")
        {
            model = ProviderCatalog.FindById("replicate")?.Models
                .FirstOrDefault(m => m.Slug == modelSlug);
        }
        return model?.UsdPerSecond ?? 0;
    }
}
