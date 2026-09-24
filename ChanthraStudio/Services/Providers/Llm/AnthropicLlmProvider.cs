using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// Anthropic Messages (<c>POST /v1/messages</c>). Default model is
/// <see cref="DefaultModel"/>. Auth is <c>x-api-key</c> + the static
/// <c>anthropic-version</c> header per their stability policy.
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

    /// <summary>Used when no model chip is picked.</summary>
    public const string DefaultModel = "claude-opus-5";
    public string? DefaultModelId => DefaultModel;

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Anthropic API key missing — set it in Settings.");

        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;
        var current = IsCurrentGeneration(model);
        var payload = new JsonObject
        {
            ["model"] = model,
            // Newer models think by default and thinking counts against
            // max_tokens; the callers' 600–3200 left a storyboard cut off
            // mid-JSON. The ceiling is only a cap — billing is for tokens used.
            ["max_tokens"] = Math.Max(req.MaxTokens, current ? 16000 : 4096),
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = req.Prompt },
            },
        };
        if (current)
        {
            // Opus 4.7+, Sonnet 5 and Fable answer temperature/top_p/top_k with
            // a 400 — which broke every LLM feature on the model the app itself
            // recommended. Effort is their knob instead.
            payload["output_config"] = new JsonObject { ["effort"] = "medium" };
        }
        else
        {
            payload["temperature"] = req.Temperature;
        }
        if (!string.IsNullOrEmpty(req.System))
            payload["system"] = req.System;

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", req.ApiKey);
        msg.Headers.Add("anthropic-version", ApiVersion);

        var (resp, body) = await LlmHttp.SendAsync(msg, "Claude", ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Anthropic completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");
        }

        var root = JsonNode.Parse(body);
        var stop = root?["stop_reason"]?.GetValue<string>();
        if (stop == "refusal")
        {
            var why = root?["stop_details"]?["explanation"]?.GetValue<string>();
            throw new InvalidOperationException("Claude ปฏิเสธคำขอนี้" + (string.IsNullOrWhiteSpace(why) ? "" : ": " + why));
        }

        // Every text block, in order — thinking blocks come first on newer
        // models and are skipped.
        var text = new StringBuilder();
        if (root?["content"] is JsonArray blocks)
        {
            foreach (var b in blocks)
                if (b?["type"]?.GetValue<string>() == "text")
                    text.Append(b["text"]?.GetValue<string>() ?? "");
        }
        // Anthropic returns usage at root.usage.{input_tokens,output_tokens}
        var inT = root?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        var outT = root?["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        var used = root?["model"]?.GetValue<string>() ?? model;
        return new LlmResult(text.ToString(), inT, outT, used, Truncated: stop == "max_tokens");
    }

    /// <summary>
    /// Models that reject sampling parameters (and take <c>effort</c>): Opus
    /// 4.7 and later, every generation-5 model, Fable and Mythos. Older ids —
    /// including dated ones such as claude-sonnet-4-5-20250929 — still take a
    /// temperature.
    /// </summary>
    internal static bool IsCurrentGeneration(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("fable") || m.Contains("mythos")) return true;
        var match = System.Text.RegularExpressions.Regex.Match(m, @"claude-(opus|sonnet|haiku)-(\d+)(?:-(\d+))?");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = match.Groups[3].Success && match.Groups[3].Value.Length <= 2
            ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (major >= 5) return true;
        return match.Groups[1].Value == "opus" && major == 4 && minor >= 7;
    }

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
