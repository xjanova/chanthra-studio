using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// Anthropic Messages (<c>POST /v1/messages</c>). Default model is
/// <c>claude-sonnet-4-5</c> — the best blend of speed + quality for
/// scripted Thai fortune-telling content. Auth is <c>x-api-key</c> +
/// the static <c>anthropic-version</c> header per their stability policy.
/// </summary>
internal sealed class AnthropicLlmProvider : ILlmProvider
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private static readonly HttpClient Http = new();

    public string Id => "anthropic";
    public string DisplayName => "Anthropic Claude";
    public string ApiKeyHint => "sk-ant-… · console.anthropic.com";
    public ProviderKind Kind => ProviderKind.Llm;
    public bool RequiresApiKey => true;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste an Anthropic API key in Settings.");

        // Cheap probe: a 1-token completion. The key still gets billed for it
        // but at fractions of a cent.
        try
        {
            var payload = new JsonObject
            {
                ["model"] = "claude-haiku-4-5",
                ["max_tokens"] = 1,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "." },
                },
            };
            using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            msg.Headers.Add("x-api-key", apiKey);
            msg.Headers.Add("anthropic-version", ApiVersion);
            using var resp = await Http.SendAsync(msg, ct);
            return new ProviderHealth(resp.IsSuccessStatusCode,
                resp.IsSuccessStatusCode ? "ok" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Anthropic API key missing — set it in Settings.");

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrEmpty(req.Model) ? "claude-sonnet-4-5" : req.Model,
            ["max_tokens"] = req.MaxTokens,
            ["temperature"] = req.Temperature,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = req.Prompt },
            },
        };
        if (!string.IsNullOrEmpty(req.System))
            payload["system"] = req.System;

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", req.ApiKey);
        msg.Headers.Add("anthropic-version", ApiVersion);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await Http.SendAsync(msg, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        // content is an array of blocks; we want the first text block.
        var root = JsonNode.Parse(body);
        var text = "";
        if (root?["content"] is JsonArray blocks)
        {
            foreach (var b in blocks)
            {
                if (b?["type"]?.GetValue<string>() == "text")
                {
                    text = b["text"]?.GetValue<string>() ?? "";
                    break;
                }
            }
        }
        // Anthropic returns usage at root.usage.{input_tokens,output_tokens}
        var inT = root?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        var model = root?["model"]?.GetValue<string>() ?? req.Model;
        return new LlmResult(text, inT, outT, model);
    }

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
