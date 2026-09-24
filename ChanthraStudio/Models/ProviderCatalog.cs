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

    /// <summary>
    /// Prices for ids that left the chip lists but can still be a user's saved
    /// model — or still answer for accounts that used them (Gemini 2.5), or
    /// redirect to a successor (grok-3 → grok-4.3, billed at 4.3's rate).
    /// Pricing only: they are not offered as choices. Without these, every
    /// call on such a model was recorded at $0.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ModelOption>> RetiredTokenModels { get; } =
        new Dictionary<string, IReadOnlyList<ModelOption>>
        {
            ["anthropic"] = new[]
            {
                new ModelOption("claude-opus-4-6", "Claude Opus 4.6", "legacy", InputUsdPer1M: 5, OutputUsdPer1M: 25),
                new ModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "legacy", InputUsdPer1M: 3, OutputUsdPer1M: 15),
                new ModelOption("claude-opus-4-0", "Claude Opus 4.0", "legacy", InputUsdPer1M: 15, OutputUsdPer1M: 75),
                new ModelOption("claude-sonnet-4-0", "Claude Sonnet 4.0", "legacy", InputUsdPer1M: 3, OutputUsdPer1M: 15),
            },
            ["gemini"] = new[]
            {
                new ModelOption("gemini-2.5-pro", "Gemini 2.5 Pro", "legacy", InputUsdPer1M: 1.25, OutputUsdPer1M: 10),
                new ModelOption("gemini-2.5-flash", "Gemini 2.5 Flash", "legacy", InputUsdPer1M: 0.30, OutputUsdPer1M: 2.50),
                new ModelOption("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", "legacy", InputUsdPer1M: 0.10, OutputUsdPer1M: 0.40),
                new ModelOption("gemini-2.0-flash-001", "Gemini 2.0 Flash", "legacy", InputUsdPer1M: 0.10, OutputUsdPer1M: 0.40),
            },
            ["grok"] = new[]
            {
                new ModelOption("grok-3", "Grok 3 → 4.3", "legacy", InputUsdPer1M: 1.25, OutputUsdPer1M: 2.50),
                new ModelOption("grok-3-mini", "Grok 3 Mini → 4.3", "legacy", InputUsdPer1M: 1.25, OutputUsdPer1M: 2.50),
                new ModelOption("grok-4", "Grok 4 → 4.3", "legacy", InputUsdPer1M: 1.25, OutputUsdPer1M: 2.50),
                new ModelOption("grok-4-fast", "Grok 4 Fast → 4.3", "legacy", InputUsdPer1M: 1.25, OutputUsdPer1M: 2.50),
            },
        };

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
                new ModelOption("gpt-5.5-pro", "GPT-5.5 Pro", "best", "$30 / $180",
                    "ชั้นพรีเมียม ตอบแม่นกว่า · แพงมาก",
                    InputUsdPer1M: 30, OutputUsdPer1M: 180),
                new ModelOption("gpt-5.4", "GPT-5.4", null, "$2.50 / $15",
                    "Frontier · งานทั่วไประดับมือโปร",
                    InputUsdPer1M: 2.50, OutputUsdPer1M: 15),
                new ModelOption("gpt-5.4-mini", "GPT-5.4 mini", "fast", "$0.75 / $4.50",
                    "เร็วกว่า ถูกกว่า สำหรับ chat ทั่วไป",
                    InputUsdPer1M: 0.75, OutputUsdPer1M: 4.50),
                new ModelOption("gpt-5.4-nano", "GPT-5.4 nano", "cheap", "$0.20 / $1.25",
                    "ราคาประหยัด · งาน batch · classify",
                    InputUsdPer1M: 0.20, OutputUsdPer1M: 1.25),
                new ModelOption("gpt-4o-mini", "GPT-4o mini", "cheap", "$0.15 / $0.60",
                    "ค่าเริ่มต้น · ถูกที่สุด · ตอบเร็ว",
                    InputUsdPer1M: 0.15, OutputUsdPer1M: 0.60),
                new ModelOption("gpt-5", "GPT-5", "legacy", "$1.25 / $10", "รุ่นก่อน · ยังใช้ได้",
                    InputUsdPer1M: 1.25, OutputUsdPer1M: 10),
                new ModelOption("gpt-5-mini", "GPT-5 mini", "legacy", "$0.25 / $2", null,
                    InputUsdPer1M: 0.25, OutputUsdPer1M: 2),
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
                new ModelOption("claude-opus-5", "Claude Opus 5", "best", "$5 / $25 per 1M tok",
                    "ค่าเริ่มต้น · เก่งที่สุดในราคา Opus · คิดก่อนตอบอัตโนมัติ",
                    InputUsdPer1M: 5, OutputUsdPer1M: 25),
                new ModelOption("claude-sonnet-5", "Claude Sonnet 5", "fast", "$2 / $10",
                    "เร็วและคุ้ม สำหรับงานเขียนทั่วไป",
                    InputUsdPer1M: 2, OutputUsdPer1M: 10),
                new ModelOption("claude-fable-5-1", "Claude Fable 5.1", "best", "$10 / $50",
                    "รุ่นที่เก่งที่สุด · งานยาวและซับซ้อน",
                    InputUsdPer1M: 10, OutputUsdPer1M: 50),
                new ModelOption("claude-opus-4-8", "Claude Opus 4.8", null, "$5 / $25", null,
                    InputUsdPer1M: 5, OutputUsdPer1M: 25),
                new ModelOption("claude-opus-4-7", "Claude Opus 4.7", null, "$5 / $25", null,
                    InputUsdPer1M: 5, OutputUsdPer1M: 25),
                new ModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6", null, "$3 / $15", null,
                    InputUsdPer1M: 3, OutputUsdPer1M: 15),
                new ModelOption("claude-haiku-4-5", "Claude Haiku 4.5", "cheap", "$1 / $5",
                    "เร็วและถูก สำหรับงานสั้น",
                    InputUsdPer1M: 1, OutputUsdPer1M: 5),
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
                new ModelOption("gemini-3.8-flash", "Gemini 3.8 Flash", "best", "$0.75 / $3.75 per 1M tok",
                    "ค่าเริ่มต้น · รุ่นเสถียรล่าสุด · 1M context",
                    InputUsdPer1M: 0.75, OutputUsdPer1M: 3.75),
                new ModelOption("gemini-3.5-flash", "Gemini 3.5 Flash", null, "$1.50 / $9", null,
                    InputUsdPer1M: 1.5, OutputUsdPer1M: 9),
                new ModelOption("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", "cheap", "$0.30 / $2.50",
                    "งานปริมาณมาก ราคาประหยัด",
                    InputUsdPer1M: 0.30, OutputUsdPer1M: 2.5),
                new ModelOption("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", "cheap", "$0.25 / $1.50",
                    "ถูกที่สุด",
                    InputUsdPer1M: 0.25, OutputUsdPer1M: 1.5),
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
                new ModelOption("qwen/qwen3.8-27b:free", "Qwen 3.8 27B (free)", "free", null,
                    "262K context"),
                new ModelOption("nvidia/nemotron-3-super-120b-a12b:free", "Nemotron 3 Super (free)", "free", null,
                    "262K context"),
                new ModelOption("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash", "cheap", "$0.09 / $0.18",
                    null, InputUsdPer1M: 0.089, OutputUsdPer1M: 0.177),
                new ModelOption("stepfun/step-3.5-flash", "Step 3.5 Flash", "cheap", "$0.10 / $0.30",
                    null, InputUsdPer1M: 0.1, OutputUsdPer1M: 0.3),
                new ModelOption("anthropic/claude-opus-5", "Claude Opus 5 (passthrough)", "best", "$5 / $25",
                    null, InputUsdPer1M: 5, OutputUsdPer1M: 25),
                new ModelOption("anthropic/claude-sonnet-5", "Claude Sonnet 5 (passthrough)", null, "$2 / $10",
                    null, InputUsdPer1M: 2, OutputUsdPer1M: 10),
                new ModelOption("openai/gpt-5.5", "GPT-5.5 (passthrough)", "best", "$5 / $30",
                    null, InputUsdPer1M: 5, OutputUsdPer1M: 30),
                new ModelOption("google/gemini-3.8-flash", "Gemini 3.8 Flash (passthrough)", "fast", "$0.75 / $3.75",
                    null, InputUsdPer1M: 0.75, OutputUsdPer1M: 3.75),
            },
            FreeTierNote: "Free models · 20 req/min, 50-200 req/day"),

        new ProviderInfo(
            Id: "grok",
            DisplayName: "xAI Grok",
            KeyPageUrl: "https://console.x.ai",
            DashboardUrl: "https://console.x.ai",
            SetupSteps: new[]
            {
                "เข้า console.x.ai → Sign in ด้วยบัญชี X (Twitter) หรืออีเมล",
                "สร้าง team/workspace ถ้าระบบถาม แล้วไปที่เมนู API Keys",
                "กด \"Create API Key\" → ตั้งชื่อ → ก๊อปปี้ (ขึ้นต้น xai-)",
                "วาง + Save ที่นี่ · ต้องเติมเครดิตก่อนใช้งานจริง",
            },
            Models: new[]
            {
                new ModelOption("grok-4.3", "Grok 4.3", "cheap", "$1.25 / $2.50 per 1M tok",
                    "ค่าเริ่มต้น · รุ่นที่ xAI แนะนำสำหรับงานทั่วไป · 1M context",
                    InputUsdPer1M: 1.25, OutputUsdPer1M: 2.50),
                new ModelOption("grok-4.7", "Grok 4.7", "best", "$2 / $6",
                    "รุ่นใหม่ล่าสุด · เก่งที่สุด",
                    InputUsdPer1M: 2, OutputUsdPer1M: 6),
                new ModelOption("grok-4.6", "Grok 4.6", null, "$2 / $6", null,
                    InputUsdPer1M: 2, OutputUsdPer1M: 6),
                new ModelOption("grok-4.20-0309-non-reasoning", "Grok 4.20 (non-reasoning)", "fast", "$1.25 / $2.50",
                    "ตอบทันทีไม่ต้องคิดก่อน · 2M context",
                    InputUsdPer1M: 1.25, OutputUsdPer1M: 2.50),
            },
            FreeTierNote: "ไม่มี free tier ถาวร · ต้องเติมเครดิต"),

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
                    "ค่าเริ่มต้น · รุ่นเดียวที่รองรับภาษาไทย · 70+ ภาษา · สูงสุด 5,000 ตัวอักษร/ครั้ง",
                    UsdPerChar: 0.30 / 1_000.0),  // ~$0.30 per 1K chars on Creator+
                new ModelOption("eleven_multilingual_v2", "Multilingual v2", null, null,
                    "29 ภาษา · ไม่รองรับภาษาไทย",
                    UsdPerChar: 0.30 / 1_000.0),
                new ModelOption("eleven_flash_v2_5", "Flash v2.5", "fast", null,
                    "75ms latency · 32 ภาษา · ไม่รองรับภาษาไทย",
                    UsdPerChar: 0.15 / 1_000.0),
                new ModelOption("eleven_turbo_v2_5", "Turbo v2.5", "legacy", null,
                    "เลิกพัฒนาแล้ว ใช้ Flash v2.5 แทน · ไม่รองรับภาษาไทย",
                    UsdPerChar: 0.15 / 1_000.0),
            },
            FreeTierNote: "Free tier · 10K chars/เดือน · เสียงไทยต้องใช้ Eleven v3"),

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

        new ProviderInfo(
            Id: "kling",
            DisplayName: "Kling AI",
            KeyPageUrl: "https://app.klingai.com",
            DashboardUrl: "https://app.klingai.com",
            SetupSteps: new[]
            {
                "เข้า klingai.com → Sign up แล้วไปที่ API / Developer console",
                "สมัคร API plan แล้วสร้าง Access Key + Secret Key",
                "วางในรูปแบบ AccessKey:SecretKey (คั่นด้วย : ตัวเดียว) ในช่อง key",
                "Save + Probe · แล้วเลือก route \"Kling AI\" ใน Composer ก่อนกด Summon",
            },
            Models: new[]
            {
                new ModelOption("kling-v2-master", "Kling 2.0 Master", "best", "~$0.28 / s",
                    "คุณภาพสูงสุด · motion สมจริง · cinematic",
                    UsdPerSecond: 0.28),
                new ModelOption("kling-v1-6", "Kling 1.6", null, null,
                    "default · เสถียร · text+image → video",
                    UsdPerSecond: 0.14),
                new ModelOption("kling-v1-5", "Kling 1.5", null, null, "รุ่นก่อน",
                    UsdPerSecond: 0.14),
                new ModelOption("kling-v1", "Kling 1.0", "legacy", null, "รุ่นแรก",
                    UsdPerSecond: 0.10),
            },
            FreeTierNote: "API key แยกจากการใช้งานบนเว็บ · จ่ายตามวินาที · 5s หรือ 10s"),

        new ProviderInfo(
            Id: "seedance",
            DisplayName: "Seedance 2.0 (ByteDance)",
            KeyPageUrl: "https://console.byteplus.com/ark",
            DashboardUrl: "https://console.byteplus.com/ark",
            SetupSteps: new[]
            {
                "เข้า console.byteplus.com → เปิดบริการ ModelArk (BytePlus international)",
                "เปิดใช้โมเดล Seedance 2.0 ในหน้า Model · แล้วไปที่ API Keys",
                "สร้าง API Key → ก๊อปปี้ → วาง + Save ที่นี่ (กด Probe เพื่อทดสอบ)",
                "เลือก route \"Seedance 2.0\" ใน Composer ก่อนกด Summon",
            },
            Models: new[]
            {
                new ModelOption("dreamina-seedance-2-0-260128", "Seedance 2.0", "best", "~$0.05-0.10 / s",
                    "ByteDance flagship · text+image+audio · 4-15s · สูงสุด 2K",
                    UsdPerSecond: 0.08),
                new ModelOption("dreamina-seedance-2-0-fast-260128", "Seedance 2.0 Fast", "fast", null,
                    "เร็วกว่า ถูกกว่า · เหมาะกับ iterate",
                    UsdPerSecond: 0.03),
            },
            FreeTierNote: "BytePlus ModelArk · จ่ายตามวินาที · ขึ้นกับ resolution"),

        new ProviderInfo(
            Id: "veo",
            DisplayName: "Google Veo 3.1 (omni)",
            KeyPageUrl: "https://aistudio.google.com/apikey",
            DashboardUrl: "https://aistudio.google.com",
            SetupSteps: new[]
            {
                "ใช้ Gemini API key ตัวเดียวกับ Google Gemini ด้านบน (AIzaSy…) — ไม่ต้องสมัครใหม่",
                "เปิดใช้ Veo ในบัญชี (ต้องเป็น paid tier · เช็คที่ aistudio.google.com)",
                "วาง key ที่นี่ หรือเว้นว่างไว้ — ระบบจะใช้ key ของ Gemini ให้อัตโนมัติ",
                "เลือก route \"Veo 3.1\" ใน Composer/Storyboard ก่อนกด Summon · รองรับเสียงพูด+lip-sync",
            },
            Models: new[]
            {
                new ModelOption("veo-3.1-fast-generate-preview", "Veo 3.1 Fast", "fast", "~$0.15 / s",
                    "ใหม่ล่าสุด · เร็ว+ถูก · native audio+dialogue · เหมาะกับ iterate",
                    UsdPerSecond: 0.15),
                new ModelOption("veo-3.1-generate-preview", "Veo 3.1", "best", "~$0.40 / s",
                    "คุณภาพสูงสุด · native audio · lip-sync แม่น · 4/6/8s · สูงสุด 1080p",
                    UsdPerSecond: 0.40),
                new ModelOption("veo-3.0-generate-001", "Veo 3.0 (stable)", null, null,
                    "GA · เสถียร · มีเสียง · ใช้ได้ทุกบัญชีที่เปิด Veo",
                    UsdPerSecond: 0.40),
                new ModelOption("veo-3.0-fast-generate-001", "Veo 3.0 Fast (stable)", "fast", null,
                    "GA fast variant · ถูกกว่า",
                    UsdPerSecond: 0.15),
                new ModelOption("veo-2.0-generate-001", "Veo 2.0", "legacy", null,
                    "รุ่นก่อน · ไม่มีเสียง (silent)",
                    UsdPerSecond: 0.35),
            },
            FreeTierNote: "ใช้ Gemini key · ต้อง paid tier · จ่ายตามวินาที · 9:16 หรือ 16:9 · 4/6/8s"),

        new ProviderInfo(
            Id: "minimax",
            DisplayName: "MiniMax · Hailuo",
            KeyPageUrl: "https://platform.minimax.io",
            DashboardUrl: "https://platform.minimax.io/user-center/basic-information",
            SetupSteps: new[]
            {
                "เข้า platform.minimax.io → Sign up (เวอร์ชัน international .io)",
                "ไปที่ API Keys → Create new key → ก๊อปปี้",
                "วาง + Save ที่นี่ · เช็ค balance ก่อนใช้ · กด Probe",
                "เลือก route \"MiniMax\" ใน Composer ก่อนกด Summon",
            },
            Models: new[]
            {
                new ModelOption("MiniMax-Hailuo-2.3", "Hailuo 2.3", "best", "~$0.04-0.08 / s",
                    "ใหม่ล่าสุด · physics engine · 1080P · 6/10s",
                    UsdPerSecond: 0.06),
                new ModelOption("MiniMax-Hailuo-02", "Hailuo 02", null, null,
                    "เสถียร · text+image → video",
                    UsdPerSecond: 0.045),
                new ModelOption("S2V-01", "S2V-01", null, null,
                    "subject reference → video (เก็บหน้าตัวละคร)",
                    UsdPerSecond: 0.045),
            },
            FreeTierNote: "จ่ายตามวินาที · คลิป 6 หรือ 10 วินาที"),
    };

    /// <summary>Convenience: filter the catalog to providers whose Id is in the given set.</summary>
    public static IEnumerable<ProviderInfo> ByIds(params string[] ids)
        => All.Where(p => ids.Contains(p.Id));
}
