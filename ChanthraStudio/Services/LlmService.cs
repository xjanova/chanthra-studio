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

        // Honour the user's picked model from the Settings catalog UI; fall
        // back to the provider's hard-coded default if they haven't picked.
        var activeModel = _ctx.Settings.GetSetting($"activeModel:{providerId}");
        var req = new LlmRequest
        {
            ApiKey = apiKey,
            Model = activeModel,
            System = system,
            Prompt = user,
            Temperature = temperature,
            MaxTokens = maxTokens,
        };
        var result = await provider.CompleteAsync(req, ct);

        // Record usage. Best-effort — never break the call on a tracker error.
        try
        {
            _ctx.Tracker.RecordTokens(
                providerId,
                string.IsNullOrEmpty(result.Model) ? activeModel : result.Model!,
                result.InputTokens, result.OutputTokens, "llm");
        }
        catch { }

        return result.Text;
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

    /// <summary>
    /// Expand a concept into a full multi-clip storyboard as compact JSON for
    /// <see cref="StoryboardBuilder.Parse"/>. The model only writes the creative
    /// content (titles, visuals, camera, SFX, Thai dialogue, tarot cards, the
    /// Facebook post); the app owns the character lock, prompt assembly, routing
    /// and generation. Returns the raw JSON spec.
    /// </summary>
    public Task<string> WriteStoryboardAsync(string concept, string character, string styleNote,
        int clipCount, double clipDurationSec, string voiceNote, CancellationToken ct = default)
    {
        const string system = """
            You are a short-form vertical-video storyboard writer for a Thai
            fortune-telling brand (a young female card reader, "lunar atelier"
            mystical-but-warm aesthetic). You turn a concept into a clip-by-clip
            storyboard for talking-head AI video models (Seedance / Veo / Kling).

            Output ONLY a JSON object — no prose, no markdown fences. Schema:
            {
              "title": "<short Thai title>",
              "clips": [
                {
                  "title": "<short Thai beat title>",
                  "durationSec": <number>,
                  "visual": "<Thai: what we see — the character's action, lighting, props>",
                  "camera": "<camera language, e.g. 'fast zoom-in on face', 'close-up'>",
                  "sfx": "<sound effects, e.g. 'bell chime กริ๊ง, golden light'>",
                  "dialogue": [ { "text": "<one short spoken Thai line>", "emotion": "<optional cue>" } ],
                  "cards": [ "<tarot card name in English, e.g. The Star>" ],
                  "audioBehavior": "<voice direction, e.g. 'cheerful, spoken dialogue only'>"
                }
              ],
              "facebook": {
                "caption": "<Thai caption with line breaks + emojis, ending in a clear CTA>",
                "hashtags": [ "#..." ],
                "commaTags": [ "<keyword>", "..." ]
              }
            }

            Rules:
            - Write the DIALOGUE in natural spoken Thai (สุภาพ, เป็นกันเอง), 1 short
              sentence per line, ~3–10 lines total across the board's arc.
            - Build a hook → reveal → obstacle → call-to-action arc across the clips.
            - End the final clip with the brand CTA: ask viewers to type a Thai
              keyword (เช่น "เลิกจน") and share the post.
            - 2 tarot cards per clip is ideal. Use real tarot names in English.
            - The Facebook caption mirrors the board's message and ends with the
              same type-a-keyword-and-share CTA; 12–18 hashtags; 15–20 commaTags.
            - Keep it positive and inspirational — no guarantees of wealth, no
              fear-mongering, no medical/financial promises.
            """;
        var user =
            $"Concept / hook:\n{concept}\n\n" +
            $"Character (keep consistent every clip): {character}\n" +
            $"Visual style: {styleNote}\n" +
            $"Number of clips: {Math.Clamp(clipCount, 1, 8)}, each about {clipDurationSec:0} seconds\n" +
            $"Voice direction: {voiceNote}\n\n" +
            "Write the storyboard JSON now.";
        return CompleteAsync(system, user, temperature: 0.85, maxTokens: 3200, ct);
    }

    /// <summary>
    /// Build a ComfyUI workflow GRAPH from a natural-language description. The
    /// LLM emits a compact node/wire spec using ONLY the node kinds the app
    /// knows; <see cref="AiWorkflowBuilder"/> then seeds the real sockets and
    /// validates every wire, so the result is always a valid graph. Returns the
    /// raw JSON spec (caller passes it to AiWorkflowBuilder.BuildGraph).
    /// </summary>
    public Task<string> BuildComfyWorkflowAsync(string description, CancellationToken ct = default)
    {
        const string system = """
            You are a ComfyUI workflow architect. Given a user's description, output a
            JSON graph using ONLY these node kinds and their exact socket ids.
            Output ONLY the JSON object — no prose, no markdown fences.

            Node kinds — "kind": outputs[…] · inputs[…] · params:
            - LoadCheckpoint:   out[model,clip,vae]                       · param ckpt_name
            - LoraLoader:       in[model,clip] out[model,clip]            · params lora_name,strength_model,strength_clip
            - CLIPTextEncode:   in[clip] out[cond]                        · param text
            - EmptyLatentImage: out[latent]                              · params width,height,batch_size
            - KSampler:         in[model,positive,negative,latent_image] out[latent] · params seed,steps,cfg,sampler_name,scheduler,denoise
            - VAEDecode:        in[samples,vae] out[image]
            - SaveImage:        in[images]                                · param filename_prefix
            - LoadImage:        out[image,mask]                           · param image
            - ControlNetApply:  in[conditioning,control_net,image] out[conditioning] · param strength
            - AnimateDiff:      in[model] out[model]                      · params motion_model,beta_schedule

            Output shape:
            {"nodes":[{"id":"<unique>","kind":"<Kind>","params":{"<k>":"<v>"}}],
             "wires":[{"from":"<nodeId>:<outSocket>","to":"<nodeId>:<inSocket>"}]}

            A standard text-to-image graph:
            LoadCheckpoint(ckpt) → CLIPTextEncode(pos)+CLIPTextEncode(neg) + EmptyLatentImage(latent)
            → KSampler(sampler) → VAEDecode(vae) → SaveImage(save).
            Wire: ckpt:clip→pos:clip, ckpt:clip→neg:clip, ckpt:model→sampler:model,
            pos:cond→sampler:positive, neg:cond→sampler:negative, latent:latent→sampler:latent_image,
            sampler:latent→vae:samples, ckpt:vae→vae:vae, vae:image→save:images.

            Rules:
            - Put the user's subject in the POSITIVE CLIPTextEncode text; the negative gets
              common quality negatives ("blurry, lowres, deformed, bad anatomy").
            - Leave ckpt_name "" — the app auto-picks an installed checkpoint.
            - Add LoraLoader ONLY if the user asks for a LoRA, wired ckpt:model→lora:model,
              ckpt:clip→lora:clip, then lora:model→sampler:model and lora:clip→both CLIPTextEncode.
            - Set width/height/steps/cfg sensibly for the request. Keep it minimal and valid.
            """;
        var user = string.IsNullOrWhiteSpace(description)
            ? "A cinematic SDXL portrait, soft light."
            : description;
        return CompleteAsync(system, user, temperature: 0.3, maxTokens: 1800, ct);
    }
}
