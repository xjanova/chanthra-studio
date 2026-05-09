using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// OpenAI Chat Completions (<c>POST /v1/chat/completions</c>). Default model
/// is <c>gpt-4o-mini</c> — cheap, fast, good for short fortune-script work.
/// Caller can override via <see cref="LlmRequest.Model"/>.
/// </summary>
internal sealed class OpenAiLlmProvider : ILlmProvider
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";
    private static readonly HttpClient Http = new();

    public string Id => "openai";
    public string DisplayName => "OpenAI";
    public string ApiKeyHint => "sk-… · platform.openai.com/api-keys";
    public ProviderKind Kind => ProviderKind.Llm;
    public bool RequiresApiKey => true;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an OpenAI API key in Settings.");
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
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
            throw new InvalidOperationException("OpenAI API key missing — set it in Settings.");

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(req.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = req.System });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = req.Prompt });

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(req.Model) ? "gpt-4o-mini" : req.Model,
            ["messages"] = messages,
            ["temperature"] = req.Temperature,
            ["max_tokens"] = req.MaxTokens,
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await Http.SendAsync(msg, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenAI completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var root = JsonNode.Parse(body);
        var content = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        // OpenAI returns usage at root.usage.{prompt_tokens,completion_tokens,total_tokens}
        var inT = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        var model = root?["model"]?.GetValue<string>() ?? req.Model;
        return new LlmResult(content ?? "", inT, outT, model);
    }

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
