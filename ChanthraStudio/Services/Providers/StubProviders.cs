using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers;

// =====================================================================
// Phase-2 SCAFFOLDING: each provider has its real id/name/key-hint and a
// stubbed ProbeAsync that just confirms a key is present. Real HTTP/WS
// implementations land in phase 3, when the Generate flow actually calls
// SubmitAsync. The Settings UI wires up to these classes today so users
// can paste their keys and have them encrypted + persisted.
// =====================================================================

internal abstract class StubLlmProvider : ILlmProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ApiKeyHint { get; }
    public ProviderKind Kind => ProviderKind.Llm;
    public bool RequiresApiKey => true;
    public bool IsImplemented => false;

    public Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderHealth(false, "not implemented",
            $"{DisplayName} has no real HTTP client yet — use Gemini, OpenAI, or Anthropic."));

    public Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} chat completion not yet implemented.");
}

internal abstract class StubVideoProvider : IVideoProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ApiKeyHint { get; }
    public ProviderKind Kind => ProviderKind.Video;
    public virtual bool RequiresApiKey => true;
    public virtual bool IsImplemented => false;

    public virtual Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderHealth(false, "not implemented",
            $"{DisplayName} has no real HTTP client yet — use Replicate or ComfyUI."));

    public Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} submit not yet implemented.");

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} poll not yet implemented.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} cancel not yet implemented.");
}

internal abstract class StubPostingProvider : IPostingProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ApiKeyHint { get; }
    public ProviderKind Kind => ProviderKind.Posting;
    public virtual bool RequiresApiKey => true;
    public virtual bool IsImplemented => false;

    public virtual Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderHealth(false, "not implemented",
            $"{DisplayName} has no real HTTP client yet."));

    public Task<PostResult> PostAsync(PostRequest req, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} post not yet implemented.");
}

// All four LLM providers now have real implementations under
// Services/Providers/Llm/*. The StubLlmProvider base + concrete shells in
// this file are retained only as scaffolding for future providers.

// ---------------- Video concrete shells ----------------

internal sealed class ComfyUiVideoProvider : StubVideoProvider
{
    public override string Id => "comfyui";
    public override string DisplayName => "ComfyUI · การ์ดจอเครื่องนี้";
    public override string ApiKeyHint => "ไม่ต้องใช้คีย์ — ติดตั้งเอนจินในหน้า ComfyUI";
    public override bool RequiresApiKey => false;

    /// <summary>
    /// ComfyUI route IS wired — it just bypasses the IVideoProvider interface
    /// and goes through ComfyUiClient directly from GenerationService. We
    /// keep this shell so Settings renders a row for the local route.
    /// </summary>
    public override bool IsImplemented => true;

    public override Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderHealth(true, "local",
            "ใช้เอนจิน ComfyUI ที่สตูดิโอติดตั้งเอง (หน้า ComfyUI) หรือชี้ไปเซิร์ฟเวอร์ที่มีอยู่แล้วผ่าน Settings → ComfyUI URL"));
}

internal sealed class RentedGpuVideoProvider : StubVideoProvider
{
    public override string Id => "rentgpu";
    public override string DisplayName => "Rented GPU · ComfyUI on a hired card";
    public override string ApiKeyHint => "SimplePod API key · set in the GPU panel";
    public override bool RequiresApiKey => false;   // the GPU panel owns the key, not Settings

    /// <summary>
    /// Like the ComfyUI row, this is a shell: the route is wired, but it goes
    /// through GenerationService → GpuWorkerService → ComfyUiClient rather
    /// than the IVideoProvider submit/poll interface. The shell exists so the
    /// Composer's engine picker lists the route.
    /// </summary>
    public override bool IsImplemented => true;

    public override Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderHealth(true, "on demand",
            "Rents a GPU, installs ComfyUI on it, renders, then releases it. " +
            "Budget caps and the live meter live in the GPU panel."));
}

// Replicate is now a real implementation in Services/Providers/Video/ —
// the registry instantiates it directly.

// Runway / Pika / fal.ai all moved to Services/Providers/Video/*.cs with real
// HTTP clients (Runway in 7.4, Pika + fal.ai in 7.6). The shells that used to
// live here were only useful as scaffolding — deleted to avoid dead-class
// confusion. The StubVideoProvider base remains for hypothetical future
// providers that haven't been wired yet.

// Posting providers moved to Services/Providers/Posting/ — they have real
// HTTP implementations rather than stubs.
