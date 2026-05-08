using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.Music;

/// <summary>
/// Music generation through Replicate. Same auth + prediction API as the
/// video provider, just with audio-output models like
/// <c>meta/musicgen</c>, <c>lucataco/ace-step</c>, or
/// <c>riffusion/riffusion</c>. The output URL points at a .wav/.mp3 — we
/// download it to the requested local path.
/// </summary>
public sealed class ReplicateMusicProvider : IMusicProvider
{
    public string Id => "replicate-music";
    public string DisplayName => "Replicate · Music (MusicGen · ACE-Step · Riffusion)";
    public string ApiKeyHint => "r8_… · same key as Replicate video provider";
    public ProviderKind Kind => ProviderKind.Music;
    public bool RequiresApiKey => true;

    public const string DefaultModel = "meta/musicgen";

    private const string BaseUrl = "https://api.replicate.com/v1";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<ProviderHealth> ProbeAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderHealth(false, "no key", "Same r8_… token as Replicate video.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/account");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? new ProviderHealth(true, "ok", "music gen routes through the same Replicate account")
                : new ProviderHealth(false, $"HTTP {(int)resp.StatusCode}", await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return new ProviderHealth(false, "error", ex.Message);
        }
    }

    public async Task<string> GenerateAsync(MusicRequest req, string destPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
            throw new InvalidOperationException("Replicate API key missing — paste your r8_… token in Settings.");
        var (owner, name) = ParseSlug(string.IsNullOrEmpty(req.Model) ? DefaultModel : req.Model);

        var input = new JsonObject
        {
            ["prompt"] = req.Prompt,
            ["duration"] = (int)Math.Clamp(req.DurationSec, 4, 120),
        };
        if (req.Seed is int seed) input["seed"] = seed;

        var payload = new JsonObject { ["input"] = input };
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/models/{owner}/{name}/predictions")
        {
            Content = JsonContent.Create((JsonNode)payload),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
        msg.Headers.Add("Prefer", "wait=2");

        using var resp = await _http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Replicate music submit failed (HTTP {(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("Replicate returned non-JSON.");
        var id = node["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Replicate returned no prediction id.");

        // Poll until the prediction lands on a terminal status.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        string? outputUrl = null;
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/predictions/{id}");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
            using var pollResp = await _http.SendAsync(poll, ct);
            var pollBody = await pollResp.Content.ReadAsStringAsync(ct);
            var pollNode = JsonNode.Parse(pollBody) as JsonObject;
            var status = pollNode?["status"]?.GetValue<string>() ?? "starting";
            if (status == "succeeded")
            {
                outputUrl = pollNode!["output"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonArray a when a.Count > 0 => a[0]?.GetValue<string>(),
                    _ => null,
                };
                break;
            }
            if (status == "failed" || status == "canceled")
            {
                var error = pollNode?["error"]?.GetValue<string>() ?? status;
                throw new InvalidOperationException($"Replicate music prediction {status}: {error}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { throw; }
        }

        if (string.IsNullOrEmpty(outputUrl))
            throw new TimeoutException("Replicate music prediction did not finish within 10 minutes.");

        // Stream the audio bytes into destPath.
        using (var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            using var audioResp = await dl.GetAsync(outputUrl, ct);
            audioResp.EnsureSuccessStatusCode();
            Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
            await using var fs = File.Create(destPath);
            await audioResp.Content.CopyToAsync(fs, ct);
        }
        return destPath;
    }

    private static (string Owner, string Name) ParseSlug(string slug)
    {
        var trimmed = (slug ?? "").Trim();
        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash == trimmed.Length - 1)
            throw new ArgumentException(
                $"Music model must be owner/name — got \"{slug}\". " +
                "Examples: meta/musicgen, lucataco/ace-step, riffusion/riffusion");
        return (trimmed[..slash], trimmed[(slash + 1)..]);
    }
}
