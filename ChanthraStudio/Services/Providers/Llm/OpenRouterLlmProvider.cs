using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// OpenRouter — OpenAI-wire-format proxy that fans out to dozens of upstream
/// models from one key. We use it via the same chat-completions shape as
/// <see cref="OpenAiLlmProvider"/>, just pointed at openrouter.ai. Default
/// model is <c>anthropic/claude-sonnet-4-5</c> — pick anything from
/// openrouter.ai/models via <see cref="LlmRequest.Model"/>.
///
/// Two soft headers ("HTTP-Referer" + "X-Title") are recommended by OpenRouter
/// so traffic gets attributed to the calling app — we send them.
/// </summary>
internal sealed class OpenRouterLlmProvider : ILlmProvider
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private static readonly HttpClient Http = new();

    public string Id => "openrouter";
    public string DisplayName => "OpenRouter";
    public string ApiKeyHint => "sk-or-… · openrouter.ai/keys";
    public ProviderKind Kind => ProviderKind.Llm;
    public bool RequiresApiKey => true;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an OpenRouter API key in Settings.");
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(msg, ct);
            return new ProviderHealth(resp.IsSuccessStatusCode,
                resp.IsSuccessStatusCode ? "ok" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("OpenRouter API key missing — set it in Settings.");

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(req.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = req.System });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = req.Prompt });

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(req.Model) ? "anthropic/claude-sonnet-4-5" : req.Model,
            ["messages"] = messages,
            ["temperature"] = req.Temperature,
            ["max_tokens"] = req.MaxTokens,
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
        // Optional but nice — OpenRouter dashboards show traffic by app.
        msg.Headers.Add("HTTP-Referer", "https://github.com/xjanova/chanthra-studio");
        msg.Headers.Add("X-Title", "Chanthra Studio");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await Http.SendAsync(msg, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenRouter completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var root = JsonNode.Parse(body);
        var content = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        // OpenRouter exposes the upstream provider's usage in OpenAI shape.
        var inT = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        var model = root?["model"]?.GetValue<string>() ?? req.Model;
        return new LlmResult(content, inT, outT, model);
    }

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
