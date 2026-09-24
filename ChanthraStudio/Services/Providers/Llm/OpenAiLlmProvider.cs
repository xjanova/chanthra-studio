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

    /// <summary>Used when no model chip is picked: cheap, fast, and takes a temperature.</summary>
    public const string DefaultModel = "gpt-4o-mini";
    public string? DefaultModelId => DefaultModel;
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

        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;
        var reasoning = IsReasoningModel(model);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            // GPT-5.x / o-series REQUIRE max_completion_tokens and reject the
            // legacy max_tokens with a 400. max_completion_tokens is also
            // accepted by gpt-4o / 4o-mini, so it's the universal, future-proof
            // choice. On reasoning models it also counts the hidden reasoning,
            // so they get room for both. (verified 2026-06 against OpenAI's docs)
            ["max_completion_tokens"] = reasoning ? Math.Max(req.MaxTokens, 16000) : req.MaxTokens,
        };
        if (reasoning)
        {
            // Reasoning models reject a non-default temperature; effort is
            // their knob, kept low for writing work.
            payload["reasoning_effort"] = "low";
        }
        else
        {
            payload["temperature"] = req.Temperature;
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);

        var (resp, body) = await LlmHttp.SendAsync(msg, "OpenAI", ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"OpenAI completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");
        }

        var root = JsonNode.Parse(body);
        var choice = root?["choices"]?[0];
        var content = choice?["message"]?["content"]?.GetValue<string>();
        var refusal = choice?["message"]?["refusal"]?.GetValue<string>();
        if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(refusal))
            throw new InvalidOperationException("OpenAI ปฏิเสธคำขอนี้: " + refusal);
        var finish = choice?["finish_reason"]?.GetValue<string>();
        // OpenAI returns usage at root.usage.{prompt_tokens,completion_tokens,total_tokens}
        var inT = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        var used = root?["model"]?.GetValue<string>() ?? model;
        return new LlmResult(content ?? "", inT, outT, used, Truncated: finish == "length");
    }

    /// <summary>GPT-5.x and the o-series: no temperature, reasoning inside the token budget.</summary>
    internal static bool IsReasoningModel(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains('/')) m = m[(m.LastIndexOf('/') + 1)..];   // OpenRouter-style "openai/gpt-5.5"
        return m.StartsWith("gpt-5") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4");
    }

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
