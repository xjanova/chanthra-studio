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

    public Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(string.IsNullOrWhiteSpace(apiKey)
            ? new ProviderHealth(false, "no key", "Paste an API key in Settings.")
            : new ProviderHealth(true, "key present", "Live probe lands in phase 3."));

    public Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} chat completion arrives in phase 3.");
}

internal abstract class StubVideoProvider : IVideoProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ApiKeyHint { get; }
    public ProviderKind Kind => ProviderKind.Video;
    public virtual bool RequiresApiKey => true;

    public virtual Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(string.IsNullOrWhiteSpace(apiKey)
            ? new ProviderHealth(false, "no key", "Paste an API key in Settings.")
            : new ProviderHealth(true, "key present", "Live probe lands in phase 3."));

    public Task<VideoJob> SubmitAsync(VideoRequest req, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} submit arrives in phase 3.");

    public Task<VideoJob> PollAsync(string jobId, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} poll arrives in phase 3.");

    public Task CancelAsync(string jobId, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} cancel arrives in phase 3.");
}

internal abstract class StubPostingProvider : IPostingProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string ApiKeyHint { get; }
    public ProviderKind Kind => ProviderKind.Posting;
    public virtual bool RequiresApiKey => true;

    public virtual Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
        => Task.FromResult(string.IsNullOrWhiteSpace(apiKey)
            ? new ProviderHealth(false, "no key", "Paste credentials in Settings.")
            : new ProviderHealth(true, "key present", "Live probe lands in phase 7."));

    public Task<PostResult> PostAsync(PostRequest req, CancellationToken ct = default)
        => throw new NotImplementedException($"{DisplayName} post arrives in phase 7.");
}

// All four LLM providers now have real implementations under
// Services/Providers/Llm/*. The StubLlmProvider base + concrete shells in
// this file are retained only as scaffolding for future providers.

// ---------------- Video concrete shells ----------------

internal sealed class ComfyUiVideoProvider : StubVideoProvider
{
    public override string Id => "comfyui";
    public override string DisplayName => "ComfyUI · local GPU";
    public override string ApiKeyHint => "Server URL in Settings · no key needed";
    public override bool RequiresApiKey => false;
}

internal sealed class ReplicateVideoProvider : StubVideoProvider
{
    public override string Id => "replicate";
    public override string DisplayName => "Replicate (Kling · Veo · Sora)";
    public override string ApiKeyHint => "r8_… · replicate.com/account/api-tokens";
}

internal sealed class RunwayVideoProvider : StubVideoProvider
{
    public override string Id => "runway";
    public override string DisplayName => "Runway · Gen-3";
    public override string ApiKeyHint => "key_… · dev.runwayml.com";
}

internal sealed class PikaVideoProvider : StubVideoProvider
{
    public override string Id => "pika";
    public override string DisplayName => "Pika Labs";
    public override string ApiKeyHint => "pk_… · pika.art";
}

internal sealed class FalVideoProvider : StubVideoProvider
{
    public override string Id => "fal";
    public override string DisplayName => "fal.ai";
    public override string ApiKeyHint => "fal-… · fal.ai/dashboard/keys";
}

// Posting providers moved to Services/Providers/Posting/ — they have real
// HTTP implementations rather than stubs.
