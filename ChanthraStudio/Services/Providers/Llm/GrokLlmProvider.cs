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
/// Default model <see cref="DefaultModel"/>; the user can switch from the
/// Settings model chips (persisted as <c>activeModel:grok</c>).
/// </summary>
internal sealed class GrokLlmProvider : ILlmProvider
{
    private const string Endpoint = "https://api.x.ai/v1/chat/completions";
    private static readonly HttpClient Http = new();

    public string Id => "grok";

    /// <summary>
    /// Used when no model chip is picked. grok-3 and grok-4 were retired on
    /// 2026-05-15 and now only redirect here (docs.x.ai, May 15 retirement).
    /// </summary>
    public const string DefaultModel = "grok-4.3";
    public string? DefaultModelId => DefaultModel;
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
            ["model"] = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model,
            ["messages"] = messages,
            ["temperature"] = req.Temperature,
            // Room for the reasoning current Grok models do first; a ceiling only.
            ["max_tokens"] = Math.Max(req.MaxTokens, 8000),
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        var (resp, body) = await LlmHttp.SendAsync(msg, "Grok", ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Grok completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");
        }

        var root = JsonNode.Parse(body);
        var content = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        var finish = root?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
        // xAI mirrors OpenAI's usage block: usage.{prompt_tokens, completion_tokens}.
        var inT = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        var model = root?["model"]?.GetValue<string>() ?? req.Model;
        return new LlmResult(content ?? "", inT, outT, model, Truncated: finish == "length");
    }

    private static string? ExtractError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            // xAI errors come as {"error":{"message":…}} or {"error":"…"};
            // indexing a string node throws, which lost the second shape.
            var err = n?["error"];
            return err is JsonObject o ? o["message"]?.GetValue<string>() : err?.GetValue<string>();
        }
        catch { return null; }
    }
}
