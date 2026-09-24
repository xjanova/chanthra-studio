using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Llm;

/// <summary>
/// Google Gemini via the v1beta REST API
/// (<c>POST /v1beta/models/{model}:generateContent?key={apiKey}</c>).
/// Default model is <see cref="DefaultModel"/>. Gemini puts the api key in
/// the URL query string rather than a header — keep that in mind when
/// inspecting logs.
/// </summary>
internal sealed class GeminiLlmProvider : ILlmProvider
{
    private const string Base = "https://generativelanguage.googleapis.com/v1beta/";
    private static readonly HttpClient Http = new() { BaseAddress = new Uri(Base) };

    public string Id => "gemini";
    public string DisplayName => "Google Gemini";
    public string ApiKeyHint => "AIzaSy… · aistudio.google.com";
    public ProviderKind Kind => ProviderKind.Llm;
    public bool RequiresApiKey => true;

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Paste a Gemini API key in Settings.");
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, "models");
            probe.Headers.Add("x-goog-api-key", apiKey);
            using var resp = await Http.SendAsync(probe, ct);
            return new ProviderHealth(resp.IsSuccessStatusCode,
                resp.IsSuccessStatusCode ? "ok" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Gemini API key missing — set it in Settings.");

        var model = string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model;

        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = req.Prompt },
                    },
                },
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = req.Temperature,
                // Thinking tokens count against maxOutputTokens on current
                // Gemini models, so the callers' 600-3200 came back cut short
                // or empty. A ceiling costs nothing unless it is used.
                ["maxOutputTokens"] = Math.Clamp(req.MaxTokens * 4, 16384, 65536),
            },
        };
        if (!string.IsNullOrEmpty(req.System))
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = req.System },
                },
            };
        }

        // Absolute: the shared LlmHttp client has no BaseAddress, and a
        // relative "models/…" threw before any request left the machine. The
        // key rides in a header rather than the URL, out of proxy logs.
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{Base}models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-goog-api-key", req.ApiKey);

        var (resp, body) = await LlmHttp.SendAsync(msg, "Gemini", ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Gemini completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");
        }

        var root = JsonNode.Parse(body);
        var blocked = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(blocked))
            throw new InvalidOperationException($"Gemini ปฏิเสธคำขอนี้ ({blocked})");

        var candidate = root?["candidates"]?[0];
        var finish = candidate?["finishReason"]?.GetValue<string>() ?? "";
        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION")
            throw new InvalidOperationException($"Gemini หยุดตอบเพราะนโยบายเนื้อหา ({finish})");

        // Every non-thought part, in order. Reading parts[0] alone returned
        // the model's thinking summary, or nothing, instead of the answer.
        var text = new StringBuilder();
        if (candidate?["content"]?["parts"] is JsonArray parts)
            foreach (var part in parts)
                if (part?["thought"]?.GetValue<bool>() != true)
                    text.Append(part?["text"]?.GetValue<string>() ?? "");

        // Gemini returns usageMetadata.{promptTokenCount, candidatesTokenCount, totalTokenCount}
        var inT = root?["usageMetadata"]?["promptTokenCount"]?.GetValue<int>() ?? 0;
        var outT = (root?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() ?? 0)
                 + (root?["usageMetadata"]?["thoughtsTokenCount"]?.GetValue<int>() ?? 0);
        return new LlmResult(text.ToString(), inT, outT, model, Truncated: finish == "MAX_TOKENS");
    }

    /// <summary>
    /// A current stable model new projects can use. The 2.5 family is limited
    /// to accounts that already used it (Google's model page, checked
    /// 2026-09-23), so a new key on the old default got nothing back.
    /// </summary>
    public const string DefaultModel = "gemini-3.8-flash";
    public string? DefaultModelId => DefaultModel;

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
