using System.Collections.Generic;
using System.Linq;
using ChanthraStudio.Services.Providers.Llm;
using ChanthraStudio.Services.Providers.Posting;
using ChanthraStudio.Services.Providers.Video;
using ChanthraStudio.Services.Providers.Voice;

namespace ChanthraStudio.Services.Providers;

/// <summary>
/// Static-by-design registry of providers. New providers added here are
/// instantly available in the Settings page and the Composer's engine
/// picker. No reflection scanning — explicit list keeps startup fast and
/// build-time-validated.
/// </summary>
public sealed class ProviderRegistry
{
    public IReadOnlyList<ILlmProvider> Llm { get; }
    public IReadOnlyList<IVideoProvider> Video { get; }
    public IReadOnlyList<IPostingProvider> Posting { get; }
    public IReadOnlyList<IVoiceProvider> Voice { get; }

    public IEnumerable<IProvider> All
        => Llm.Cast<IProvider>().Concat(Video).Concat(Posting).Concat(Voice);

    public ProviderRegistry()
    {
        Llm = new ILlmProvider[]
        {
            new GeminiLlmProvider(),
            new OpenAiLlmProvider(),
            new AnthropicLlmProvider(),
            new OpenRouterLlmProvider(),  // still stub
        };

        Video = new IVideoProvider[]
        {
            new ComfyUiVideoProvider(),
            new ReplicateVideoProvider(),
            new RunwayVideoProvider(),
            new PikaVideoProvider(),
            new FalVideoProvider(),
        };

        Posting = new IPostingProvider[]
        {
            new FacebookPostingProvider(),
            new WebhookPostingProvider(),
        };

        Voice = new IVoiceProvider[]
        {
            new OpenAiTtsProvider(),
            new ElevenLabsTtsProvider(),
        };
    }

    public IProvider? FindById(string id)
        => All.FirstOrDefault(p => p.Id == id);
}
