using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// xAI Grok via the OpenAI-compatible Chat Completions API
/// (<c>POST https://api.x.ai/v1/chat/completions</c>, <c>Authorization: Bearer xai-…</c>).
/// Default model <c>grok-3</c> — broadly available on any paid xAI account; the
/// user can switch to grok-4 / grok-4-fast / grok-3-mini from the Settings
/// model chips (persisted as <c>activeModel:grok</c>).
/// </summary>
internal sealed class GrokLlmProvider : ILlmProvider
{
    private const string Endpoint = "https://api.x.ai/v1/chat/completions";
    private static readonly HttpClient Http = new();

    public string Id => "grok";
    public string DisplayName => "xAI Grok";
    public string ApiKeyHint => "xai-… · console.x.ai";
    public ProviderKind Kind => ProviderKind.Llm;
    public bool RequiresApiKey => true;
    public bool IsImplemented => true;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an xai-… key in Settings.");
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/models");
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
            throw new InvalidOperationException("Grok API key missing — set it in Settings.");

        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(req.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = req.System });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = req.Prompt });

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(req.Model) ? "grok-3" : req.Model,
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
                $"Grok completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var root = JsonNode.Parse(body);
        var content = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        // xAI mirrors OpenAI's usage block: usage.{prompt_tokens, completion_tokens}.
        var inT = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        var model = root?["model"]?.GetValue<string>() ?? req.Model;
        return new LlmResult(content ?? "", inT, outT, model);
    }

    private static string? ExtractError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            // xAI errors come as {"error":{"message":…}} or {"error":"…"}.
            return n?["error"]?["message"]?.GetValue<string>()
                ?? n?["error"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
