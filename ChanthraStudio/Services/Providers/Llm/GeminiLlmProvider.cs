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
/// Default model is <c>gemini-2.5-flash</c>. Gemini puts the api key in
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
            using var resp = await Http.GetAsync($"models?key={Uri.EscapeDataString(apiKey)}", ct);
            return new ProviderHealth(resp.IsSuccessStatusCode,
                resp.IsSuccessStatusCode ? "ok" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return new ProviderHealth(false, "probe failed", ex.Message); }
    }

    public async Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Gemini API key missing — set it in Settings.");

        var model = string.IsNullOrEmpty(req.Model) ? "gemini-2.5-flash" : req.Model;

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
                ["maxOutputTokens"] = req.MaxTokens,
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

        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(req.ApiKey)}")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        using var resp = await Http.SendAsync(msg, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Gemini completion failed ({(int)resp.StatusCode}): {ExtractError(body) ?? body}");

        var root = JsonNode.Parse(body);
        var text = root?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
        return text ?? "";
    }

    private static string? ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }
}
