using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// SimplePod.ai marketplace adapter.
///
/// Two things about this vendor shape every design decision below:
///
///  1. <b>Creation calls return an empty body.</b> POST /instances and
///     POST /instances/templates both answer <c>{ }</c> — no id, no handle.
///     So we snapshot the list first, create, then diff the list to find
///     what appeared. Templates are matched by name instead, which is why
///     the template name has to be a stable unique key.
///
///  2. <b>Server-side filters are coarse.</b> <c>pricePerGpu[lte]</c> only
///     accepts small integers, so we ask for a generous slice and do the
///     real filtering here.
///
/// Auth is the <c>X-AUTH-TOKEN</c> header — not <c>Authorization: Bearer</c>.
///
/// Field names in the market/instance payloads are read leniently (vendors
/// rename them), but never <i>defaulted</i> leniently: an offer whose price
/// or VRAM we can't parse is dropped, not treated as free. Guessing zero on
/// a money field is how you rent a machine you didn't mean to.
/// </summary>
public sealed class SimplePodProvider : IGpuRentalProvider
{
    /// <summary>SimplePod runs on the SimpleMining API host (shared lineage).
    /// Overridable via the <c>gpu:apiBase</c> setting so a vendor move
    /// doesn't require a new build.</summary>
    public const string DefaultApiBase = "https://api.simplemining.net";

    private readonly string _apiBase;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public SimplePodProvider(string? apiBase = null)
    {
        _apiBase = string.IsNullOrWhiteSpace(apiBase) ? DefaultApiBase : apiBase!.TrimEnd('/');
    }

    public string Id => "simplepod";
    public string DisplayName => "SimplePod.ai";
    public string ConsoleUrl => "https://dash.simplepod.ai/";

    // ---------------------------------------------------------------- balance

    public async Task<decimal> GetBalanceAsync(string apiKey, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/instances/summary", apiKey, null, ct);
        // Vendors sometimes wrap the summary in a "data"/"summary" envelope.
        var root = json as JsonObject;
        var scope = (root?["data"] as JsonObject) ?? (root?["summary"] as JsonObject) ?? root;
        var bal = FirstDecimal(scope, "balance", "credits", "creditBalance", "accountBalance", "funds");
        if (bal is null)
            throw new GpuRentalException(
                "Connected, but the account summary had no balance field — the API shape may have changed.");
        return bal.Value;
    }

    // ----------------------------------------------------------------- market

    public async Task<IReadOnlyList<GpuOffer>> SearchMarketAsync(
        string apiKey, GpuFilter filter, CancellationToken ct = default)
    {
        // pricePerGpu[lte] takes a small integer only — round UP so we never
        // exclude a machine that's actually inside the user's budget, then
        // enforce the real ceiling client-side below.
        var coarsePrice = Math.Clamp((int)Math.Ceiling(filter.MaxPricePerHourUsd), 1, 5);

        var qs = new StringBuilder("/instances/market/list?rentalStatus=active");
        qs.Append("&gpuMemorySize[gte]=").Append(filter.MinVramGb);
        qs.Append("&diskSize[gte]=").Append(filter.MinDiskGb);
        qs.Append("&pricePerGpu[lte]=").Append(coarsePrice);
        if (filter.MinDownloadMbps > 0)
            qs.Append("&downloadSpeedtest[gte]=").Append(filter.MinDownloadMbps);

        var json = await SendAsync(HttpMethod.Get, qs.ToString(), apiKey, null, ct);

        var offers = new List<GpuOffer>();
        foreach (var item in EnumerateItems(json))
        {
            var offer = ParseOffer(item);
            if (offer is null) continue;                                  // unparseable price/VRAM → dropped
            // The ceiling is on what the meter charges, which includes disk.
            if (offer.TotalPricePerHourUsd > filter.MaxPricePerHourUsd) continue;
            if (offer.VramGb < filter.MinVramGb) continue;
            if (offer.DiskGb > 0 && offer.DiskGb < filter.MinDiskGb) continue;
            if (filter.MinDownloadMbps > 0 && offer.DownloadMbps > 0
                && offer.DownloadMbps < filter.MinDownloadMbps) continue;
            offers.Add(offer);
        }

        // Cheapest JOB first, not cheapest hour — warm-up is billed at the
        // same rate as rendering, so a slow link on a cheap box routinely
        // costs more than a fast link on a dearer one. See GpuCostModel.
        return GpuCostModel.Rank(offers, filter)
            .Take(Math.Max(1, filter.MaxResults))
            .ToList();
    }

    private static GpuOffer? ParseOffer(JsonObject o)
    {
        var id = FirstString(o, "id", "_id", "machineId", "hostId", "uuid");
        if (string.IsNullOrEmpty(id)) return null;

        // Price and VRAM are load-bearing — no fallback value is safe.
        var price = FirstDecimal(o, "pricePerGpu", "pricePerHour", "price", "costPerHour", "hourlyPrice");
        var vram = FirstInt(o, "gpuMemorySize", "gpuMemory", "vram", "vramSize", "gpuRam");
        if (price is null || vram is null || vram <= 0) return null;

        return new GpuOffer
        {
            Id = id!,
            GpuModel = FirstString(o, "gpuModel", "gpuName", "gpu", "model") ?? "GPU",
            GpuCount = FirstInt(o, "gpuCount", "gpus", "gpuQuantity") ?? 1,
            VramGb = NormaliseVramGb(vram.Value),
            DiskGb = FirstInt(o, "diskSize", "disk", "diskSpace", "storage") ?? 0,
            PricePerHourUsd = price.Value,
            // Storage is billed separately here. Missing → 0, which is the
            // one defaulting we allow on a money field: it is additive, so a
            // zero understates by exactly the amount the vendor didn't tell
            // us about, whereas dropping the whole offer would empty a market
            // over an optional field. See GpuOffer.DiskPricePerHourUsd for
            // the unit assumption still waiting on a real invoice.
            DiskPricePerHourUsd = FirstDecimal(o, "pricePerDiskSize", "pricePerDisk", "diskPrice") ?? 0m,
            DownloadMbps = FirstInt(o, "downloadSpeedtest", "downloadSpeed", "download") ?? 0,
            Region = FirstString(o, "region", "country", "location") ?? "",
            Reliability = FirstDouble(o, "sla", "reliability", "uptime") ?? 0,
        };
    }

    /// <summary>Vendors report VRAM in GB or MB depending on the field.
    /// Anything above 1024 is megabytes.</summary>
    private static int NormaliseVramGb(int raw) => raw > 1024 ? raw / 1024 : raw;

    // ------------------------------------------------------------------- rent

    public async Task<string> RentAsync(string apiKey, GpuRentSpec spec, CancellationToken ct = default)
    {
        if (!spec.Name.StartsWith(GpuWorkerService.NamePrefix, StringComparison.Ordinal))
            throw new GpuRentalException(
                $"Refusing to rent an instance not named \"{GpuWorkerService.NamePrefix}…\" — " +
                "that prefix is what keeps the orphan sweep off other people's machines.");

        var templateId = await EnsureTemplateAsync(apiKey, spec, ct);

        // POST /instances answers {} — capture what exists first so we can
        // tell which id is ours afterwards.
        var before = (await ListInstancesAsync(apiKey, ct)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        var body = new JsonObject
        {
            ["gpuCount"] = spec.GpuCount,
            ["instanceMarket"] = spec.OfferId,
            ["instanceTemplate"] = templateId,
            ["startScript"] = spec.StartScript,
            ["envVariables"] = new JsonArray(
                spec.Env.Select(kv => (JsonNode)new JsonObject
                {
                    ["name"] = kv.Key,
                    ["value"] = kv.Value,
                }).ToArray()),
        };

        await SendAsync(HttpMethod.Post, "/instances", apiKey, body, ct);

        // Diff-poll for the new id. The vendor takes a few seconds to make
        // the instance visible; 90s is generous but a miss here means a
        // machine on the meter that we don't have a row for.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);

            var now = await ListInstancesAsync(apiKey, ct);
            var fresh = now.FirstOrDefault(i => !before.Contains(i.Id));
            if (fresh is null) continue;

            // The vendor won't take a name at creation time, so stamp it now.
            // If this fails the instance still exists and is still billing —
            // surface it loudly rather than returning a machine the sweep
            // will later refuse to touch (unnamed = untouchable, by design).
            try
            {
                await SendAsync(HttpMethod.Put, $"/instances/{Uri.EscapeDataString(fresh.Id)}", apiKey,
                    new JsonObject { ["name"] = spec.Name }, ct);
            }
            catch (Exception ex)
            {
                throw new GpuRentalException(
                    $"Rented instance {fresh.Id} but could not name it: {ex.Message}. " +
                    "It is billing now — terminate it from the GPU panel or the vendor console.", ex);
            }

            return fresh.Id;
        }

        throw new GpuRentalException(
            "Rent request accepted but no new instance appeared within 90s. " +
            "Check the SimplePod dashboard — if a machine did start, it is billing.");
    }

    /// <summary>
    /// Templates are also created with an empty response body, so the name is
    /// the handle: look it up first, create only if missing, then look it up
    /// again. Same image+ports always resolves to the same template.
    /// </summary>
    private async Task<string> EnsureTemplateAsync(string apiKey, GpuRentSpec spec, CancellationToken ct)
    {
        var name = TemplateName(spec);

        var existing = await FindTemplateByNameAsync(apiKey, name, ct);
        if (existing is not null) return existing;

        var body = new JsonObject
        {
            ["name"] = name,
            ["image"] = spec.DockerImage,
            ["ports"] = spec.ExposedPort.ToString(CultureInfo.InvariantCulture),
            ["isPublic"] = false,
        };
        await SendAsync(HttpMethod.Post, "/instances/templates", apiKey, body, ct);

        var created = await FindTemplateByNameAsync(apiKey, name, ct);
        return created ?? throw new GpuRentalException(
            $"Created template \"{name}\" but it did not appear in the template list.");
    }

    private static string TemplateName(GpuRentSpec spec)
    {
        // Stable per image+port so repeat rentals reuse one template rather
        // than littering the account with near-duplicates.
        var imageSlug = new string(spec.DockerImage
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        if (imageSlug.Length > 40) imageSlug = imageSlug[..40];
        return $"{GpuWorkerService.NamePrefix}{imageSlug}-{spec.ExposedPort}";
    }

    private async Task<string?> FindTemplateByNameAsync(string apiKey, string name, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/instances/templates", apiKey, null, ct);
        foreach (var t in EnumerateItems(json))
        {
            if (!string.Equals(FirstString(t, "name", "templateName"), name, StringComparison.Ordinal))
                continue;
            var id = FirstString(t, "id", "_id", "templateId", "uuid");
            if (!string.IsNullOrEmpty(id)) return id;
        }
        return null;
    }

    // -------------------------------------------------------------- instances

    public async Task<GpuInstance?> GetInstanceAsync(string apiKey, string instanceId, CancellationToken ct = default)
    {
        try
        {
            var json = await SendAsync(HttpMethod.Get, $"/instances/{Uri.EscapeDataString(instanceId)}", apiKey, null, ct);
            var obj = json as JsonObject ?? (json?["instance"] as JsonObject) ?? (json?["data"] as JsonObject);
            return obj is null ? null : ParseInstance(obj);
        }
        catch (GpuRentalException ex) when (ex.Message.Contains("404"))
        {
            return null;   // already destroyed — not an error
        }
    }

    public async Task<IReadOnlyList<GpuInstance>> ListInstancesAsync(string apiKey, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/instances/list", apiKey, null, ct);
        return EnumerateItems(json).Select(ParseInstance).ToList();
    }

    public async Task TerminateAsync(string apiKey, string instanceId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"/instances/{Uri.EscapeDataString(instanceId)}", apiKey, null, ct);
        }
        catch (GpuRentalException ex) when (ex.Message.Contains("404"))
        {
            // Already gone. Terminate is idempotent by contract — the caller
            // only cares that the meter has stopped.
        }
    }

    private static GpuInstance ParseInstance(JsonObject o)
    {
        var raw = FirstString(o, "status", "state", "rentalStatus", "instanceStatus") ?? "";
        var inst = new GpuInstance
        {
            Id = FirstString(o, "id", "_id", "instanceId", "uuid") ?? "",
            Name = FirstString(o, "name", "instanceName", "label") ?? "",
            RawStatus = raw,
            State = MapState(raw),
            GpuModel = FirstString(o, "gpuModel", "gpuName", "gpu", "model") ?? "",
            PricePerHourUsd = FirstDecimal(o, "pricePerGpu", "pricePerHour", "price", "costPerHour") ?? 0m,
            StartedAt = FirstDateTime(o, "startedAt", "createdAt", "rentedAt", "created"),
        };

        var (proxy, direct) = ParsePorts(o, GpuProvisioning.ProxyPort);
        inst.ProxyUrl = proxy;
        inst.DirectUrl = direct;
        return inst;
    }

    /// <summary>
    /// Pulls the public endpoint for <paramref name="wantedPort"/> out of the
    /// instance payload. SimplePod exposes each port two ways — a Cloudflare
    /// tunnel (HTTPS, preferred) and a direct host:port — under a "ports" map
    /// keyed by the container port.
    /// </summary>
    internal static (string? proxyUrl, string? directUrl) ParsePorts(JsonObject o, int wantedPort)
    {
        var portsNode = o["ports"] ?? o["portMappings"] ?? o["mapPort"];
        var key = wantedPort.ToString(CultureInfo.InvariantCulture);

        JsonObject? entry = null;
        if (portsNode is JsonObject map)
        {
            entry = map[key] as JsonObject;
            // Some builds key by the *host* port instead — fall back to any
            // entry whose declared internal port matches.
            entry ??= map.Select(kv => kv.Value as JsonObject)
                         .FirstOrDefault(e => e is not null
                             && (FirstInt(e, "internalPort", "containerPort", "port") == wantedPort));
        }
        else if (portsNode is JsonArray arr)
        {
            entry = arr.Select(n => n as JsonObject)
                       .FirstOrDefault(e => e is not null
                           && (FirstInt(e, "internalPort", "containerPort", "port") == wantedPort));
        }

        if (entry is null) return (null, null);

        var proxy = FirstString(entry, "proxyUrl", "proxy", "publicUrl", "url");
        if (!string.IsNullOrWhiteSpace(proxy) && !proxy!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            proxy = "https://" + proxy;

        string? direct = null;
        var host = FirstString(entry, "host", "ip", "publicIp", "address")
                   ?? FirstString(o, "ip", "publicIp", "host");
        var externalPort = FirstInt(entry, "externalPort", "hostPort", "publicPort");
        if (!string.IsNullOrWhiteSpace(host) && externalPort is > 0)
            direct = $"http://{host}:{externalPort}";

        return (string.IsNullOrWhiteSpace(proxy) ? null : proxy, direct);
    }

    private static GpuInstanceState MapState(string raw) => raw.ToLowerInvariant() switch
    {
        "running" or "active" or "online" or "ready" => GpuInstanceState.Running,
        "provisioning" or "starting" or "pending" or "creating" or "queued" or "booting"
            => GpuInstanceState.Provisioning,
        "stopped" or "terminated" or "destroyed" or "deleted" or "inactive" or "ended"
            => GpuInstanceState.Stopped,
        "failed" or "error" or "crashed" => GpuInstanceState.Failed,
        _ => GpuInstanceState.Unknown,
    };

    // ------------------------------------------------------------------- HTTP

    private async Task<JsonNode?> SendAsync(
        HttpMethod method, string path, string apiKey, JsonNode? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new GpuRentalException("SimplePod API key is missing — paste it in the GPU panel.");

        using var req = new HttpRequestMessage(method, _apiBase + path);
        req.Headers.TryAddWithoutValidation("X-AUTH-TOKEN", apiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GpuRentalException($"SimplePod timed out on {method} {path}.");
        }
        catch (HttpRequestException ex)
        {
            throw new GpuRentalException($"Could not reach SimplePod ({_apiBase}): {ex.Message}", ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = ExtractError(text);
                // Never echo the request — the API key rides in the headers and
                // a verbatim dump is how keys end up in log files.
                throw new GpuRentalException(resp.StatusCode == HttpStatusCode.Unauthorized
                    ? "SimplePod rejected the API key (401). Re-paste it in the GPU panel."
                    : $"SimplePod {method} {path} failed ({(int)resp.StatusCode}): {detail}");
            }
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); }
            catch { return null; }   // creation endpoints answer with a bare {} or empty body
        }
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no response body";
        try
        {
            var n = JsonNode.Parse(body);
            var msg = FirstString(n as JsonObject, "message", "error", "detail", "title");
            if (!string.IsNullOrWhiteSpace(msg)) return msg!;
        }
        catch { /* not JSON — fall through to the truncated raw body */ }
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    // ----------------------------------------------------- tolerant JSON reads

    /// <summary>Yields the item objects out of whichever envelope the vendor
    /// used — a bare array, or an object wrapping one under a known key.</summary>
    internal static IEnumerable<JsonObject> EnumerateItems(JsonNode? json)
    {
        JsonArray? arr = json as JsonArray;
        if (arr is null && json is JsonObject obj)
        {
            foreach (var key in new[] { "data", "items", "instances", "results", "list", "templates", "market" })
            {
                if (obj[key] is JsonArray a) { arr = a; break; }
            }
        }
        if (arr is null) yield break;
        foreach (var n in arr)
            if (n is JsonObject o) yield return o;
    }

    internal static string? FirstString(JsonObject? o, params string[] keys)
    {
        if (o is null) return null;
        foreach (var k in keys)
        {
            if (o[k] is not JsonValue v) continue;
            if (v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
            // Numeric ids arrive unquoted often enough to be worth handling.
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }

    internal static decimal? FirstDecimal(JsonObject? o, params string[] keys)
    {
        if (o is null) return null;
        foreach (var k in keys)
        {
            if (o[k] is not JsonValue v) continue;
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<double>(out var dbl)) return (decimal)dbl;
            if (v.TryGetValue<string>(out var s)
                && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    internal static int? FirstInt(JsonObject? o, params string[] keys)
    {
        if (o is null) return null;
        foreach (var k in keys)
        {
            if (o[k] is not JsonValue v) continue;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
            if (v.TryGetValue<string>(out var s)
                && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    internal static double? FirstDouble(JsonObject? o, params string[] keys)
    {
        var d = FirstDecimal(o, keys);
        return d is null ? null : (double)d.Value;
    }

    internal static DateTime? FirstDateTime(JsonObject? o, params string[] keys)
    {
        if (o is null) return null;
        foreach (var k in keys)
        {
            if (o[k] is not JsonValue v) continue;
            if (v.TryGetValue<string>(out var s)
                && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return dt;
            if (v.TryGetValue<long>(out var epoch))
            {
                // Seconds vs milliseconds — anything past year 2900 in seconds is ms.
                return epoch > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
        }
        return null;
    }
}

/// <summary>Explicit list of marketplaces, mirroring <c>ProviderRegistry</c>.</summary>
public static class GpuRentalRegistry
{
    public static IReadOnlyList<IGpuRentalProvider> All(string? apiBase = null) => new IGpuRentalProvider[]
    {
        new SimplePodProvider(apiBase),
    };

    public static IGpuRentalProvider Resolve(string? id, string? apiBase = null)
        => All(apiBase).FirstOrDefault(p => p.Id == id) ?? All(apiBase)[0];
}
