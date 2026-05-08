using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services.Providers;

namespace ChanthraStudio.Services;

/// <summary>
/// Front-of-house for LLM completions. Resolves the active provider, pulls
/// its API key from settings, and runs domain-specific prompt templates
/// (fortune-telling script, prompt enhancement, caption gen). Templates
/// keep the system prompts in one place so we can iterate them centrally.
/// </summary>
public sealed class LlmService
{
    private readonly StudioContext _ctx;

    public LlmService(StudioContext ctx) { _ctx = ctx; }

    /// <summary>
    /// Generic completion against whichever provider is set as ActiveLlm.
    /// Throws if the active provider has no key configured.
    /// </summary>
    public Task<string> CompleteAsync(string system, string user,
        double temperature = 0.8, int maxTokens = 2048, CancellationToken ct = default)
        => CompleteWithAsync(_ctx.Settings.ActiveLlm, system, user, temperature, maxTokens, ct);

    public async Task<string> CompleteWithAsync(string providerId, string system, string user,
        double temperature = 0.8, int maxTokens = 2048, CancellationToken ct = default)
    {
        var provider = _ctx.Providers.Llm.FirstOrDefault(p => p.Id == providerId)
            ?? throw new InvalidOperationException($"Unknown LLM provider: {providerId}");
        var apiKey = _ctx.Settings[providerId];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"No API key for {provider.DisplayName} — set it in Settings.");

        var req = new LlmRequest
        {
            ApiKey = apiKey,
            Model = "",                // provider picks its sensible default
            System = system,
            Prompt = user,
            Temperature = temperature,
            MaxTokens = maxTokens,
        };
        return await provider.CompleteAsync(req, ct);
    }

    // -------- Domain templates -------------------------------------------

    /// <summary>
    /// Writes a short cinematic fortune-telling script in Thai (mostly) for
    /// แม่หมอจันทรา / Juntra Payakorn brand voice. The brief is whatever the
    /// user typed — could be a topic ("ดวงประจำวันที่ 14 ตุลาคม"), or a half-
    /// drafted line they want polished.
    /// </summary>
    public Task<string> WriteFortuneScriptAsync(string brief, CancellationToken ct = default)
    {
        const string system = """
            You are the writer's voice for แม่หมอจันทรา (Mae Mor Chanthra) — a
            Thai fortune-telling brand with a "lunar atelier" aesthetic.
            Voice: cinematic, intimate, mystical but grounded. Mix of Thai
            (preferred for the body) and a couple of evocative English phrases
            for atmosphere if it fits.

            When given a brief:
            - Open with one strong line that hooks (a moon image, a question,
              a sensory detail).
            - 60–120 seconds of voice-over (250–500 Thai characters).
            - Avoid generic horoscope clichés ("today is your lucky day") —
              use specific imagery (silk, gold thread, lotus, shadow, moon).
            - End with one quiet line that closes the spell.

            Return only the script text, ready to be read aloud. No headers,
            no stage directions, no quotation marks. Keep it under 600 chars.
            """;
        var prompt = string.IsNullOrWhiteSpace(brief)
            ? "Write a fortune-telling voice-over for the audience visiting today."
            : $"Brief: {brief}\n\nWrite the voice-over now.";
        return CompleteAsync(system, prompt, temperature: 0.85, maxTokens: 1024, ct);
    }

    /// <summary>
    /// Polishes / expands a ComfyUI image prompt for cinematic detail.
    /// Used by the wand button in the Composer.
    /// </summary>
    public Task<string> EnhanceImagePromptAsync(string draft, CancellationToken ct = default)
    {
        const string system = """
            You expand short image prompts into rich, cinematic prompts for
            Stable Diffusion / SDXL / FLUX-style image generators. Style
            anchor: lunar atelier — gold accents, crimson silk, mauve
            shadows, deep void backgrounds, Thai mystical imagery.

            Rules:
            - Keep the user's subject + intent intact; expand surroundings,
              lighting, lens, mood.
            - Comma-separated tags or short phrases work better than prose.
            - 60–140 words total.
            - End with concrete technical anchors: "anamorphic 50mm,
              cinematic lighting, 35mm film grain, octane render" — vary as
              appropriate.

            Return only the expanded prompt, one paragraph, no labels.
            """;
        return CompleteAsync(system, draft, temperature: 0.8, maxTokens: 600, ct);
    }
}
