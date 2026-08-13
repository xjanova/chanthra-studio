using System;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Gpu;

/// <summary>Boot progress as the worker's own proxy reports it.</summary>
public sealed record GpuWorkerStatusReport(
    bool Reachable,
    string Stage,
    string? Detail,
    string? Error,
    string? TransportError = null)
{
    public bool IsReady => Reachable && Stage == "ready";
    public bool IsFailed => Reachable && Stage == "failed";
}

/// <summary>
/// Talks to a worker's auth proxy — the status endpoint only. Actual render
/// traffic goes through the existing <c>ComfyUiClient</c>, which is the
/// point of the whole design: the rented box is just another ComfyUI server
/// as far as the generation pipeline is concerned.
/// </summary>
public static class GpuWorkerClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<GpuWorkerStatusReport> ProbeAsync(
        string endpointUrl, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            return new GpuWorkerStatusReport(false, "unknown", null, null, "no endpoint yet");

        var url = endpointUrl.TrimEnd('/') + GpuProvisioning.StatusPath;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The proxy is alive but rejects us. Almost always means the
                // stored token can't be decrypted (row created under another
                // Windows account) — recoverable only by replacing the box.
                return new GpuWorkerStatusReport(false, "unknown", null,
                    "worker rejected our token", "401");
            }

            var node = JsonNode.Parse(body) as JsonObject;
            var stage = node?["stage"]?.GetValue<string>() ?? "unknown";
            var detail = node?["detail"]?.GetValue<string>();
            var error = node?["error"]?.GetValue<string>();
            return new GpuWorkerStatusReport(
                true, stage,
                string.IsNullOrWhiteSpace(detail) ? null : detail,
                string.IsNullOrWhiteSpace(error) ? null : error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not reachable yet is the normal case for the first minutes —
            // the caller decides when "not yet" becomes "too long".
            return new GpuWorkerStatusReport(false, "unknown", null, null, ex.Message);
        }
    }
}
