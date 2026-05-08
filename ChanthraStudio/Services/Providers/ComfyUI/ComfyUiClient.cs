using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Providers.ComfyUI;

/// <summary>
/// Minimal ComfyUI driver — HTTP for prompt submit / history fetch / file
/// retrieval, WebSocket for live progress events. Works with stock ComfyUI
/// (no plugins required) running locally or behind any reverse proxy that
/// preserves the /ws upgrade.
///
/// Flow:
///   1. <see cref="ProbeAsync"/>             — confirm the server is reachable
///   2. <see cref="SubmitPromptAsync"/>      — POST /prompt, get prompt_id
///   3. listen on <see cref="StreamProgressAsync"/> until "executed"
///   4. <see cref="GetHistoryAsync"/>        — read final outputs (filenames)
///   5. <see cref="DownloadFileAsync"/>      — fetch each output via /view
/// </summary>
public sealed class ComfyUiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _clientId;
    private bool _disposed;

    public ComfyUiClient(string baseUrl, string? clientId = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        _http = new HttpClient { BaseAddress = _baseUri, Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _clientId = clientId ?? Guid.NewGuid().ToString("N");
    }

    public string ClientId => _clientId;
    public Uri BaseUri => _baseUri;

    /// <summary>
    /// Fetches /object_info — the schema dictionary that lists every node
    /// type the server knows about, including the valid values for each
    /// input (e.g. CheckpointLoaderSimple.input.required.ckpt_name carries
    /// the list of installed .safetensors / .ckpt files).
    /// </summary>
    public async Task<JsonObject?> GetObjectInfoAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("object_info", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(json) as JsonObject;
    }

    /// <summary>List the checkpoints the server has installed. Empty if none.</summary>
    public async Task<List<string>> GetAvailableCheckpointsAsync(CancellationToken ct = default)
    {
        var info = await GetObjectInfoAsync(ct);
        return ExtractInputChoices(info, "CheckpointLoaderSimple", "ckpt_name");
    }

    /// <summary>
    /// Walks <c>info[nodeType].input.required[inputName]</c> — the first
    /// element is conventionally an array of valid values for combobox-style
    /// inputs. Returns empty if the node or input doesn't exist.
    /// </summary>
    public static List<string> ExtractInputChoices(JsonObject? info, string nodeType, string inputName)
    {
        var result = new List<string>();
        if (info?[nodeType] is not JsonObject node) return result;
        if (node["input"]?["required"]?[inputName] is not JsonArray spec) return result;
        if (spec.Count == 0 || spec[0] is not JsonArray choices) return result;
        foreach (var c in choices)
        {
            var s = c?.GetValue<string>();
            if (!string.IsNullOrEmpty(s)) result.Add(s!);
        }
        return result;
    }

    public async Task<ComfyUiHealth> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("system_stats", ct);
            if (!resp.IsSuccessStatusCode)
                return new ComfyUiHealth(false, $"HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            var stats = JsonNode.Parse(json);
            var device = stats?["devices"]?[0]?["name"]?.GetValue<string>() ?? "unknown";
            var vramTotal = stats?["devices"]?[0]?["vram_total"]?.GetValue<long>() ?? 0;
            var vramFree = stats?["devices"]?[0]?["vram_free"]?.GetValue<long>() ?? 0;
            return new ComfyUiHealth(true, "online", device, vramFree, vramTotal);
        }
        catch (Exception ex)
        {
            return new ComfyUiHealth(false, ex.Message);
        }
    }

    /// <summary>Submit a workflow (already-built prompt JSON object).</summary>
    public async Task<string> SubmitPromptAsync(JsonObject workflow, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["prompt"] = workflow,
            ["client_id"] = _clientId,
        };
        var json = payload.ToJsonString();
        using var req = new HttpRequestMessage(HttpMethod.Post, "prompt")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ComfyUiException(FormatValidationError(body, (int)resp.StatusCode));

        var node = JsonNode.Parse(body) as JsonObject;
        // ComfyUI sometimes returns 200 with an `error` object on the body
        // (older builds) — treat that as a validation failure too.
        if (node?["error"] is JsonNode err && node["prompt_id"] is null)
            throw new ComfyUiException(FormatValidationError(body, 200));

        var promptId = node?["prompt_id"]?.GetValue<string>()
            ?? throw new ComfyUiException($"ComfyUI returned no prompt_id: {body}");
        return promptId;
    }

    /// <summary>
    /// Turns a ComfyUI 400 validation response into a one-line human message.
    /// Format from server (newer builds):
    ///   { "error": { "type": "...", "message": "...", "details": "..." },
    ///     "node_errors": { "<nodeId>": { "errors": [{ "type": ..., "message": ..., "details": ..., "extra_info": {...} }] } } }
    /// </summary>
    public static string FormatValidationError(string body, int httpStatus)
    {
        try
        {
            var node = JsonNode.Parse(body) as JsonObject;
            if (node is null) return $"submit failed (HTTP {httpStatus}): {body}";

            var topMessage = node["error"]?["message"]?.GetValue<string>();
            var nodeErrors = node["node_errors"] as JsonObject;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(topMessage)) parts.Add(topMessage!);

            if (nodeErrors is not null)
            {
                foreach (var (nodeId, val) in nodeErrors)
                {
                    if (val?["errors"] is not JsonArray errs) continue;
                    foreach (var e in errs)
                    {
                        var msg = e?["message"]?.GetValue<string>() ?? "";
                        var details = e?["details"]?.GetValue<string>() ?? "";
                        var extra = e?["extra_info"] as JsonObject;
                        var input = extra?["input_name"]?.GetValue<string>();
                        var bad = extra?["received_value"]?.GetValue<string>();

                        var line = $"node {nodeId}";
                        if (!string.IsNullOrEmpty(input)) line += $" · input \"{input}\"";
                        if (!string.IsNullOrEmpty(bad)) line += $" = \"{bad}\"";
                        if (!string.IsNullOrEmpty(msg)) line += $" — {msg}";
                        if (!string.IsNullOrEmpty(details) && details != msg) line += $" ({details})";
                        parts.Add(line);
                    }
                }
            }

            return parts.Count > 0
                ? string.Join("  ·  ", parts)
                : $"submit failed (HTTP {httpStatus}): {body}";
        }
        catch
        {
            return $"submit failed (HTTP {httpStatus}): {body}";
        }
    }

    /// <summary>Fetch the history record for a completed prompt id.</summary>
    public async Task<JsonObject?> GetHistoryAsync(string promptId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"history/{Uri.EscapeDataString(promptId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json) as JsonObject;
        return root?[promptId] as JsonObject;
    }

    /// <summary>Download a file produced by ComfyUI (image/video/audio).</summary>
    public async Task DownloadFileAsync(string filename, string subfolder, string type, string destPath, CancellationToken ct = default)
    {
        var qs = $"filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        using var resp = await _http.GetAsync($"view?{qs}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    /// <summary>Cancel a queued or running prompt.</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "interrupt");
        await _http.SendAsync(req, ct);
    }

    /// <summary>
    /// Upload a local image into ComfyUI's input/ folder via /upload/image.
    /// Returns the server-side filename (which may be suffixed if ComfyUI
    /// renamed it to avoid collision). That filename is what the LoadImage
    /// node references in the workflow JSON.
    /// </summary>
    public async Task<string> UploadImageAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("reference image not found", localPath);

        await using var fs = File.OpenRead(localPath);
        using var content = new MultipartFormDataContent();
        var imageContent = new StreamContent(fs);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(GuessImageMime(localPath));
        content.Add(imageContent, "image", Path.GetFileName(localPath));
        content.Add(new StringContent("input"), "type");
        content.Add(new StringContent("true"), "overwrite");

        using var resp = await _http.PostAsync("upload/image", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ComfyUiException($"upload/image failed (HTTP {(int)resp.StatusCode}): {body}");
        var node = JsonNode.Parse(body) as JsonObject;
        return node?["name"]?.GetValue<string>() ?? Path.GetFileName(localPath);
    }

    private static string GuessImageMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Open a WebSocket and stream progress events. Yields one
    /// <see cref="ComfyUiEvent"/> per server message until the connection
    /// closes (server-side after "executed") or the cancellation token fires.
    /// </summary>
    public async IAsyncEnumerable<ComfyUiEvent> StreamProgressAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        var wsUri = new UriBuilder(_baseUri)
        {
            Scheme = _baseUri.Scheme == "https" ? "wss" : "ws",
            Path = (_baseUri.AbsolutePath.TrimEnd('/') + "/ws"),
            Query = $"clientId={_clientId}",
        }.Uri;
        await ws.ConnectAsync(wsUri, ct);
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // ComfyUI sends preview frames as binary — surface as a "preview" event.
                    yield return new ComfyUiEvent("preview", null, null);
                    sb.Clear();
                    break;
                }
                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
            }
            while (!result.EndOfMessage);

            if (sb.Length == 0) continue;
            ComfyUiEvent? evt;
            try
            {
                evt = ParseEvent(sb.ToString());
            }
            catch
            {
                evt = null;  // ignore malformed frame
            }
            if (evt is { } e)
                yield return e;
        }
    }

    private static ComfyUiEvent? ParseEvent(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject;
        var type = node?["type"]?.GetValue<string>();
        if (type is null) return null;
        var data = node?["data"] as JsonObject;
        var promptId = data?["prompt_id"]?.GetValue<string>();
        return new ComfyUiEvent(type, promptId, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

public sealed record ComfyUiHealth(
    bool Ok,
    string Status,
    string? Device = null,
    long VramFree = 0,
    long VramTotal = 0);

public sealed record ComfyUiEvent(string Type, string? PromptId, JsonObject? Data)
{
    public double? ProgressFraction
    {
        get
        {
            if (Type != "progress" || Data is null) return null;
            var v = Data["value"]?.GetValue<double?>();
            var max = Data["max"]?.GetValue<double?>();
            if (v is null || max is null || max == 0) return null;
            return (double)v / (double)max;
        }
    }

    public bool IsExecuted => Type == "executed";
    public bool IsExecuting => Type == "executing";
    public bool IsExecutionError => Type == "execution_error";
}

public sealed class ComfyUiException : Exception
{
    public ComfyUiException(string message) : base(message) { }
    public ComfyUiException(string message, Exception inner) : base(message, inner) { }
}
