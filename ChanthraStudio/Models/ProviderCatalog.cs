using System.Collections.Generic;
using System.Linq;

namespace ChanthraStudio.Models;

/// <summary>
/// Static catalog of every cloud provider the app integrates with — links
/// to the API-key signup page, a 3–4 step Thai-bilingual onboarding
/// recipe, and a curated list of recommended models verified against
/// the providers' own docs in May 2026.
///
/// Refresh cadence: when a provider releases a new flagship (e.g. Claude
/// Opus 4.8, Gemini 3.2), update the matching <c>Models</c> array. The
/// rest of the app reads from here, so updates ripple to the Settings
/// UI, the Voice view's music model chips, the GenerationService's
/// Replicate slug suggestions, etc.
/// </summary>
public static class ProviderCatalog
{
    public sealed record ProviderInfo(
        string Id,
        string DisplayName,
        string KeyPageUrl,
        string DashboardUrl,
        IReadOnlyList<string> SetupSteps,
        IReadOnlyList<ModelOption> Models,
        string? FreeTierNote = null);

    /// <summary>
    /// One picker entry. <see cref="Tag"/> is one of "best", "fast",
    /// "cheap", "free", "legacy" — drives the chip color in the Settings
    /// UI. <see cref="PricingHint"/> is shown as a small mono caption
    /// under the model name. The numeric price fields below are what
    /// <c>UsageTracker</c> uses to convert raw token/char/image counts
    /// into a USD figure (then THB via the FX rate).
    ///
    /// Choose the right pricing dimension for the model kind:
    ///   <see cref="InputUsdPer1M"/> + <see cref="OutputUsdPer1M"/> for LLM tokens
    ///   <see cref="UsdPerChar"/> for TTS character counts
    ///   <see cref="UsdPerImage"/> for Replicate text-to-image
    ///   <see cref="UsdPerSecond"/> for video / music duration billing
    /// All are nullable doubles — leave unset for free models so the
    /// tracker records the call but logs $0.
    /// </summary>
    public sealed record ModelOption(
        string Slug,
        string DisplayName,
        string? Tag = null,
        string? PricingHint = null,
        string? Description = null,
        double? InputUsdPer1M = null,
        double? OutputUsdPer1M = null,
        double? UsdPerChar = null,
        double? UsdPerImage = null,
        double? UsdPerSecond = null);

    public static ProviderInfo? FindById(string id) =>
        All.FirstOrDefault(p => p.Id == id);

    public static IReadOnlyList<ProviderInfo> All { get; } = new[]
    {
        // ───────────────────── LLM providers ─────────────────────

        new ProviderInfo(
            Id: "openai",
            DisplayName: "OpenAI",
            KeyPageUrl: "https://platform.openai.com/api-keys",
            DashboardUrl: "https://platform.openai.com/usage",
            SetupSteps: new[]
            {
                "เข้า platform.openai.com แล้ว Sign in หรือสร้างบัญชีใหม่ (ใช้ Google ก็ได้)",
                "ไปที่ Settings → API keys ที่เมนูซ้าย (หรือกด \"Get key\" ด้านบน)",
                "กด \"Create new secret key\" → ตั้งชื่อ → ก๊อปปี้ทันที · key โผล่แค่ครั้งเดียว",
                "วางในช่อง API key ที่นี่ แล้วกด Save · กด Probe เพื่อทดสอบ",
            },
            Models: new[]
            {
                new ModelOption("gpt-5.5", "GPT-5.5", "best", "$5 / $30 per 1M tok",
                    "Flagship · เก่งสุด สำหรับงาน reasoning + coding ซับซ้อน",
                    InputUsdPer1M: 5, OutputUsdPer1M: 30),
                new ModelOption("gpt-5.5-pro", "GPT-5.5 Pro", "best", null,
                    "ชั้นพรีเมียม ตอบแม่นกว่า · ราคาต่อรอง",
                    InputUsdPer1M: 15, OutputUsdPer1M: 75),
                new ModelOption("gpt-5.4", "GPT-5.4", null, "$2.50 / $15",
                    "Frontier · งานทั่วไประดับมือโปร",
                    InputUsdPer1M: 2.50, OutputUsdPer1M: 15),
                new ModelOption("gpt-5.4-mini", "GPT-5.4 mini", "fast", null,
                    "เร็วกว่า ถูกกว่า สำหรับ chat ทั่วไป",
                    InputUsdPer1M: 0.40, OutputUsdPer1M: 1.60),
                new ModelOption("gpt-5.4-nano", "GPT-5.4 nano", "cheap", "$0.20 / $1.25",
                    "ราคาประหยัดสุด · งาน batch · classify",
                    InputUsdPer1M: 0.20, OutputUsdPer1M: 1.25),
                new ModelOption("gpt-5", "GPT-5", "legacy", null, "รุ่นก่อน · ยังใช้ได้",
                    InputUsdPer1M: 5, OutputUsdPer1M: 15),
                new ModelOption("gpt-5-mini", "GPT-5 mini", "legacy", null, null,
                    InputUsdPer1M: 0.30, OutputUsdPer1M: 1.20),
            },
            FreeTierNote: "ไม่มี free tier · ต้องเติมเครดิตขั้นต่ำ $5"),

        new ProviderInfo(
            Id: "anthropic",
            DisplayName: "Anthropic Claude",
            KeyPageUrl: "https://console.anthropic.com/settings/keys",
            DashboardUrl: "https://console.anthropic.com",
            SetupSteps: new[]
            {
                "เข้า console.anthropic.com → Sign up (Google / email) → ยืนยันเบอร์มือถือ",
                "ไปที่ Settings → API Keys ทางซ้าย",
                "กด \"Create Key\" → ตั้งชื่อ workspace → ก๊อปปี้ key (ขึ้นต้น sk-ant-)",
                "วาง + Save ที่นี่ · บัญชีใหม่ได้เครดิตทดลอง $5 ฟรี",
            },
            Models: new[]
            {
                new ModelOption("claude-opus-4-7", "Claude Opus 4.7", "best", "$15 / $75 per 1M tok",
                    "Flagship · best at agentic coding + image up to 2576px",
                    InputUsdPer1M: 15, OutputUsdPer1M: 75),
                new ModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6", "best", "$3 / $15",
                    "94% computer-use accuracy · เกือบเทียบ Opus 4 รุ่นก่อน",
                    InputUsdPer1M: 3, OutputUsdPer1M: 15),
                new ModelOption("claude-opus-4-6", "Claude Opus 4.6", null, "$15 / $75", null,
                    InputUsdPer1M: 15, OutputUsdPer1M: 75),
                new ModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", null, "$3 / $15", null,
                    InputUsdPer1M: 3, OutputUsdPer1M: 15),
                new ModelOption("claude-haiku-4-5", "Claude Haiku 4.5", "cheap", "$1 / $5",
                    "เร็วและถูก สำหรับ batch/classify",
                    InputUsdPer1M: 1, OutputUsdPer1M: 5),
                new ModelOption("claude-opus-4-0", "Claude Opus 4.0", "legacy", null,
                    "deprecate มิ.ย. 2026 — เปลี่ยนไป 4.6/4.7",
                    InputUsdPer1M: 15, OutputUsdPer1M: 75),
                new ModelOption("claude-sonnet-4-0", "Claude Sonnet 4.0", "legacy", null,
                    "deprecate มิ.ย. 2026",
                    InputUsdPer1M: 3, OutputUsdPer1M: 15),
            },
            FreeTierNote: "$5 เครดิตทดลอง · พอ chat ราว 200-500 ข้อ"),

        new ProviderInfo(
            Id: "gemini",
            DisplayName: "Google Gemini",
            KeyPageUrl: "https://aistudio.google.com/apikey",
            DashboardUrl: "https://aistudio.google.com",
            SetupSteps: new[]
            {
                "เข้า aistudio.google.com ด้วยบัญชี Google",
                "กด \"Get API key\" มุมซ้ายบน",
                "เลือก \"Create API key in new project\" → ก๊อปปี้ key",
                "วาง + Save ที่นี่ · มี free tier 1500 คำต่อวัน",
            },
            Models: new[]
            {
                new ModelOption("gemini-3.1-pro", "Gemini 3.1 Pro", "best", "$1.25-15 / 1M tok",
                    "Reasoning-first · 1M context · adaptive thinking",
                    InputUsdPer1M: 1.25, OutputUsdPer1M: 15),
                new ModelOption("gemini-3-flash", "Gemini 3 Flash", "fast",
                    null, "Balanced speed + capability",
                    InputUsdPer1M: 0.30, OutputUsdPer1M: 2.50),
                new ModelOption("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "cheap",
                    "$0.10-3 / 1M", "ราคาประหยัดสุด",
                    InputUsdPer1M: 0.10, OutputUsdPer1M: 3),
                new ModelOption("gemini-2.5-pro", "Gemini 2.5 Pro", null, "$1.25-15",
                    "1M context · adaptive thinking",
                    InputUsdPer1M: 1.25, OutputUsdPer1M: 15),
                new ModelOption("gemini-2.5-flash", "Gemini 2.5 Flash", "fast", null, null,
                    InputUsdPer1M: 0.30, OutputUsdPer1M: 2.50),
                new ModelOption("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", "cheap", null, null,
                    InputUsdPer1M: 0.10, OutputUsdPer1M: 0.40),
                new ModelOption("gemini-2.0-flash-001", "Gemini 2.0 Flash", "legacy",
                    null, "shutdown 1 มิ.ย. 2026",
                    InputUsdPer1M: 0.10, OutputUsdPer1M: 0.40),
            },
            FreeTierNote: "Free tier · 15 RPM, 1M tokens/day, 1500 requests/day"),

        new ProviderInfo(
            Id: "openrouter",
            DisplayName: "OpenRouter",
            KeyPageUrl: "https://openrouter.ai/keys",
            DashboardUrl: "https://openrouter.ai/activity",
            SetupSteps: new[]
            {
                "เข้า openrouter.ai → Sign in ด้วย Google",
                "ไปที่ Keys (เมนูบน)",
                "กด \"Create Key\" → ตั้งชื่อ → ก๊อปปี้ (ขึ้นต้น sk-or-)",
                "วาง + Save · OpenRouter เป็น proxy เลือกได้ 300+ model จากที่เดียว",
            },
            Models: new[]
            {
                new ModelOption("openrouter/free", "Free auto-router", "free", null,
                    "เลือก free model ที่เหมาะสมที่สุดให้อัตโนมัติ"),
                new ModelOption("qwen/qwen-3.6-plus", "Qwen 3.6 Plus", "free", null,
                    "1M context · always-on CoT · #2 บน OpenRouter"),
                new ModelOption("qwen/qwen3-coder-480b", "Qwen3 Coder 480B", "free", null,
                    "free coder ที่แรงสุด · 262K context"),
                new ModelOption("step/step-3.5-flash", "Step 3.5 Flash", "free", null,
                    "262K context · #3 ranking"),
                new ModelOption("anthropic/claude-opus-4-7", "Claude Opus 4.7 (passthrough)", "best"),
                new ModelOption("openai/gpt-5.5", "GPT-5.5 (passthrough)", "best"),
                new ModelOption("google/gemini-3.1-pro", "Gemini 3.1 Pro (passthrough)", "best"),
                new ModelOption("deepseek/deepseek-v3", "DeepSeek V3", "free"),
                new ModelOption("meta-llama/llama-3.3-70b-instruct:free", "Llama 3.3 70B (free)", "free"),
            },
            FreeTierNote: "Free models · 20 req/min, 50-200 req/day"),

        // ───────────────────── Voice (TTS) providers ─────────────────────

        new ProviderInfo(
            Id: "openai-tts",
            DisplayName: "OpenAI TTS",
            KeyPageUrl: "https://platform.openai.com/api-keys",
            DashboardUrl: "https://platform.openai.com/usage",
            SetupSteps: new[]
            {
                "ใช้ key เดียวกับ OpenAI LLM ด้านบน",
                "ถ้ายังไม่มี key — ดูขั้นตอนใน OpenAI section",
                "เลือก voice (alloy/echo/fable/onyx/nova/shimmer) ใน Sound atelier → Generate",
            },
            Models: new[]
            {
                new ModelOption("tts-1", "TTS-1", "fast", "$15 / 1M chars",
                    "ความเร็วสูง สำหรับ realtime",
                    UsdPerChar: 15.0 / 1_000_000.0),
                new ModelOption("tts-1-hd", "TTS-1 HD", "best", "$30 / 1M chars",
                    "คุณภาพสูง · เหมาะกับงานเผยแพร่",
                    UsdPerChar: 30.0 / 1_000_000.0),
                new ModelOption("gpt-4o-mini-tts", "gpt-4o-mini-tts", null, null,
                    "ใหม่ · ปรับ tone ได้ผ่าน prompt",
                    UsdPerChar: 12.0 / 1_000_000.0),
            }),

        new ProviderInfo(
            Id: "elevenlabs",
            DisplayName: "ElevenLabs",
            KeyPageUrl: "https://elevenlabs.io/app/settings/api-keys",
            DashboardUrl: "https://elevenlabs.io/app",
            SetupSteps: new[]
            {
                "เข้า elevenlabs.io → Sign up · ใช้ Google ก็ได้",
                "ไปที่ Profile (มุมซ้ายล่าง) → API Keys",
                "กด \"Create API Key\" → ตั้งชื่อ + เลือก scope → ก๊อปปี้ (ขึ้นต้น xi_ หรือ sk_)",
                "วาง + Save · Free plan 10,000 chars/เดือน · v3 ใช้ได้",
            },
            Models: new[]
            {
                new ModelOption("eleven_v3", "Eleven v3", "best", null,
                    "ใหม่สุด · expressive · 70+ ภาษา · alpha",
                    UsdPerChar: 0.30 / 1_000.0),  // ~$0.30 per 1K chars on Creator+
                new ModelOption("eleven_multilingual_v2", "Multilingual v2", "best", null,
                    "29 ภาษา · เสียงนุ่ม · เหมาะกับงานคุณภาพสูง",
                    UsdPerChar: 0.30 / 1_000.0),
                new ModelOption("eleven_flash_v2_5", "Flash v2.5", "fast", null,
                    "75ms latency · 32 ภาษา · realtime",
                    UsdPerChar: 0.15 / 1_000.0),
                new ModelOption("eleven_turbo_v2_5", "Turbo v2.5", "fast", null,
                    "เร็ว · ราคาประหยัด",
                    UsdPerChar: 0.15 / 1_000.0),
            },
            FreeTierNote: "Free tier · 10K chars/เดือน · v3 + multilingual ใช้ได้"),

        // ───────────────────── Video / image generation ─────────────────────

        new ProviderInfo(
            Id: "replicate",
            DisplayName: "Replicate",
            KeyPageUrl: "https://replicate.com/account/api-tokens",
            DashboardUrl: "https://replicate.com/account",
            SetupSteps: new[]
            {
                "เข้า replicate.com → Sign in ด้วย GitHub",
                "ไปที่ Account → API tokens",
                "กด \"Create token\" → ตั้งชื่อ → ก๊อปปี้ (ขึ้นต้น r8_)",
                "วาง + Save ที่นี่ · เติมเครดิตขั้นต่ำ $1 เพื่อรันได้",
            },
            Models: new[]
            {
                // Image
                new ModelOption("black-forest-labs/flux-2-max", "Flux 2 Max", "best",
                    "$0.08-0.12 / image", "Highest fidelity · product photo · character consistency",
                    UsdPerImage: 0.10),
                new ModelOption("black-forest-labs/flux-2-pro", "Flux 2 Pro", "best",
                    "$0.04-0.06", "ใกล้เคียง Max ราคาถูกกว่า · structured JSON prompts",
                    UsdPerImage: 0.05),
                new ModelOption("black-forest-labs/flux-schnell", "Flux Schnell", "fast",
                    "$0.003-0.005", "เร็วสุด · iterate ราคาถูกที่สุดในตระกูล Flux",
                    UsdPerImage: 0.004),
                new ModelOption("black-forest-labs/flux-2-flex", "Flux 2 Flex", null, null,
                    "Typography specialist · render text สวย",
                    UsdPerImage: 0.05),
                new ModelOption("stability-ai/sdxl", "SDXL", null, "$0.01-0.02",
                    "คลาสสิก · open-source · controllable",
                    UsdPerImage: 0.015),
                new ModelOption("bytedance/seedream-4.5", "Seedream 4.5", "best",
                    null, "ByteDance · cinematic · spatial understanding",
                    UsdPerImage: 0.04),
                // Video — billed per second of output
                new ModelOption("tencent/hunyuan-video", "Hunyuan Video", "best",
                    "$0.30+ / clip", "text-to-video 720p · 73 frames",
                    UsdPerSecond: 0.10),
                new ModelOption("minimax/video-01", "MiniMax Video-01", "best",
                    null, "text-to-video · 6s clips",
                    UsdPerSecond: 0.085),
                new ModelOption("kwaivgi/kling-v1.6-pro", "Kling v1.6 Pro", "best",
                    null, "high-quality cinematic video",
                    UsdPerSecond: 0.28),
                new ModelOption("lightricks/ltx-video", "LTX Video", "fast",
                    null, "fast realtime video",
                    UsdPerSecond: 0.04),
                // Music — billed per second
                new ModelOption("meta/musicgen", "MusicGen", null, "$0.05+ / 30s",
                    "30s music generation",
                    UsdPerSecond: 0.0017),
                new ModelOption("riffusion/riffusion", "Riffusion", "fast", null,
                    "spectrogram-style music",
                    UsdPerSecond: 0.0013),
                new ModelOption("lucataco/ace-step", "ACE-Step", null, null,
                    "song generation with vocals",
                    UsdPerSecond: 0.005),
            },
            FreeTierNote: "ต้องเติมเครดิตขั้นต่ำ $1 · จากนั้นจ่ายตามใช้"),
    };

    /// <summary>Convenience: filter the catalog to providers whose Id is in the given set.</summary>
    public static IEnumerable<ProviderInfo> ByIds(params string[] ids)
        => All.Where(p => ids.Contains(p.Id));
}
