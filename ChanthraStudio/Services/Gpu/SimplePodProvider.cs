using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.Gpu;

/// <summary>
/// SimplePod.ai marketplace adapter.
///
/// <b>Ported from a working integration.</b> The first version was written
/// against guessed field names and never ran; every call except the host was
/// wrong. The owner's aixman service (<c>src/lib/gpu/simplepod.ts</c>) has
/// rented real machines, and each shape below follows what it learned — the
/// comments say where a detail was only discovered on a live rental.
///
/// Two things about this vendor shape every design decision:
///
///  1. <b>Creation calls return an empty body.</b> POST /instances and
///     POST /instances/templates answer with nothing — no id. So we snapshot
///     the instance list first and diff it afterwards; templates are matched
///     by a name that fingerprints their content.
///
///  2. <b>Server-side filters are coarse.</b> <c>pricePerGpu[lte]</c> takes
///     whole dollars, so the real ceiling is enforced here.
///
/// Auth is the <c>X-AUTH-TOKEN</c> header — not <c>Authorization: Bearer</c>.
/// </summary>
public sealed class SimplePodProvider : IGpuRentalProvider
{
    /// <summary>The vendor's API host (overridable via <c>gpu:apiBase</c>).</summary>
    public const string DefaultApiBase = "https://api.simplepod.ai";

    private readonly string _apiBase;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>How long a freshly rented instance may take to appear in the list.</summary>
    private static readonly TimeSpan RentSettle = TimeSpan.FromSeconds(90);

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
        // The documented shape (and what aixman reads) is
        // { "rentalAvailability": { "balanceRental": 7.16, … } }. The older
        // guesses stay as a fallback; without the right one "Verify & save"
        // threw on every key, so no key could ever be stored.
        var root = json as JsonObject;
        var bal = FirstDecimal(root?["rentalAvailability"] as JsonObject, "balanceRental")
                  ?? FirstDecimal((root?["data"] as JsonObject) ?? (root?["summary"] as JsonObject) ?? root,
                                  "balance", "credits", "creditBalance", "accountBalance", "funds");
        if (bal is null)
            throw new GpuRentalException(
                "Connected, but the account summary had no balance field — the API shape may have changed.");
        return bal.Value;
    }

    // ----------------------------------------------------------------- market

    public async Task<IReadOnlyList<GpuOffer>> SearchMarketAsync(
        string apiKey, GpuFilter filter, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var qs = new StringBuilder("/instances/market/list?rentalStatus=active&order%5BpricePerGpu%5D=asc");
        // VRAM is filtered in MB: the API accepts 8192–524288. Sending GB (8,
        // 12, 24) was either rejected or ignored.
        qs.Append("&gpuMemorySize%5Bgte%5D=").Append((filter.MinVramGb * 1024).ToString(inv));
        qs.Append("&diskSize%5Bgte%5D=").Append(filter.MinDiskGb.ToString(inv));
        // Whole dollars only — round up so nothing inside the budget is lost,
        // then enforce the real ceiling below.
        qs.Append("&pricePerGpu%5Blte%5D=").Append(Math.Max(1, (int)Math.Ceiling(filter.MaxPricePerHourUsd)).ToString(inv));
        if (filter.MinDownloadMbps > 0)
            qs.Append("&downloadSpeedtest%5Bgte%5D=").Append(filter.MinDownloadMbps.ToString(inv));

        var json = await SendAsync(HttpMethod.Get, qs.ToString(), apiKey, null, ct);

        var offers = new List<GpuOffer>();
        foreach (var item in EnumerateItems(json))
        {
            var offer = ParseOffer(item, filter.MinDiskGb);
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
        // same rate as rendering. See GpuCostModel.
        return GpuCostModel.Rank(offers, filter)
            .Take(Math.Max(1, filter.MaxResults))
            .ToList();
    }

    private static GpuOffer? ParseOffer(JsonObject o, int diskGbWanted)
    {
        // Renting needs the row's instanceMarket IRI ("/instances/market/12"),
        // not its numeric id; a row without one cannot be rented at all.
        var marketRef = FirstString(o, "instanceMarket");
        if (string.IsNullOrEmpty(marketRef)) return null;
        if (o["isAvailableForDemand"] is JsonValue avail && avail.TryGetValue<bool>(out var available) && !available)
            return null;

        // Price and VRAM are load-bearing — no fallback value is safe.
        var price = FirstDecimal(o, "pricePerGpu");
        var vramMb = FirstInt(o, "gpuMemorySize");
        if (price is null || price <= 0 || vramMb is null || vramMb <= 0) return null;

        // Disk is priced per GB per month; bill the disk this rental will ask for.
        var diskGbMonth = FirstDecimal(o, "pricePerDiskSize") ?? 0m;
        var diskPerHour = diskGbMonth * diskGbWanted / 730m;

        return new GpuOffer
        {
            Id = FirstString(o, "id") ?? marketRef!,
            MarketRef = marketRef!,
            GpuModel = FirstString(o, "gpuModel") ?? "GPU",
            GpuCount = FirstInt(o, "gpuCount") ?? 1,
            VramGb = (int)Math.Round(vramMb.Value / 1024.0),
            DiskGb = FirstInt(o, "diskSize") ?? 0,
            PricePerHourUsd = price.Value,
            DiskPricePerHourUsd = decimal.Round(diskPerHour, 4),
            DownloadMbps = FirstInt(o, "downloadSpeedtest") ?? 0,
            Region = FirstString(o, "region", "country") ?? "",
            Reliability = FirstDouble(o, "sla") ?? 0,
        };
    }

    // ------------------------------------------------------------------- rent

    public async Task<string> RentAsync(string apiKey, GpuRentSpec spec, CancellationToken ct = default)
    {
        if (!spec.Name.StartsWith(GpuWorkerService.NamePrefix, StringComparison.Ordinal))
            throw new GpuRentalException(
                $"Refusing to rent an instance not named \"{GpuWorkerService.NamePrefix}…\" — " +
                "that prefix is what keeps the orphan sweep off other people's machines.");
        if (string.IsNullOrEmpty(spec.OfferMarketRef))
            throw new GpuRentalException("The picked offer has no market reference to rent it by.");

        var template = await EnsureTemplateAsync(apiKey, spec, ct);

        // POST /instances answers with nothing — capture what exists first so
        // we can tell which id is ours afterwards.
        var before = (await ListInstanceRowsAsync(apiKey, ct))
            .Select(r => FirstString(r, "id")).Where(id => id is not null).Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        var pending = new PendingRental(before, DateTime.UtcNow, spec.Name);

        var body = new JsonObject
        {
            ["gpuCount"] = spec.GpuCount,
            ["instanceMarket"] = spec.OfferMarketRef,
            ["instanceTemplate"] = template,
            // SimplePod runs a start script LINE BY LINE. The multi-line boot
            // script travels as one self-extracting line; see AsSingleLine.
            ["startScript"] = AsSingleLine(spec.StartScript),
        };
        if (spec.Env.Count > 0)
            body["envVariables"] = new JsonArray(spec.Env
                .Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["value"] = kv.Value }).ToArray());

        try
        {
            await SendAsync(HttpMethod.Post, "/instances", apiKey, body, ct);
        }
        catch (GpuRentalException ex) when (ex.StatusCode is >= 400 and < 500)
        {
            // A 4xx is a refusal — nothing was rented.
            throw;
        }
        catch (GpuRentalException ex)
        {
            // A timeout or a 5xx from the vendor's edge may have gone through
            // anyway: look for the machine before deciding.
            ActivityLog.Warn("gpu", "rent request did not complete cleanly, checking whether it went through: " + ex.Message);
        }

        var deadline = DateTime.UtcNow + RentSettle;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);

            IReadOnlyList<JsonObject> rows;
            try
            {
                rows = await ListInstanceRowsAsync(apiKey, ct);
            }
            catch (GpuRentalException ex)
            {
                // The list endpoint fails intermittently. One bad poll must
                // not abandon a machine that is already billing.
                ActivityLog.Warn("gpu", "instance list failed while confirming a rental, retrying: " + ex.Message);
                continue;
            }

            var fresh = rows.FirstOrDefault(r => FirstString(r, "id") is { } id && !before.Contains(id));
            if (fresh is null) continue;
            var freshId = FirstString(fresh, "id")!;

            // Name it so the orphan sweep can tell it is ours. A failure here
            // is logged, not thrown: the row we return the id to can still
            // terminate it, and throwing left a billing machine with no row
            // holding its id at all.
            try
            {
                await SendAsync(HttpMethod.Put, $"/instances/{Uri.EscapeDataString(freshId)}", apiKey,
                    new JsonObject { ["name"] = spec.Name }, ct);
            }
            catch (Exception ex)
            {
                ActivityLog.Error("gpu",
                    $"instance {freshId} could not be named {spec.Name}; the orphan sweep will not recognise it if its row is lost", ex);
            }
            return freshId;
        }

        throw new GpuRentUnconfirmedException(
            $"SimplePod may have accepted the rental, but no new instance appeared within {RentSettle.TotalSeconds:0}s. " +
            "The next checks will look for it and release it.", pending);
    }

    /// <summary>
    /// Tag what an unconfirmed rental produced so the orphan sweep can kill it.
    /// Errs hard toward not touching a machine: only one absent from the
    /// pre-order snapshot, created within minutes of the order, qualifies —
    /// and only the first such, since one order makes one machine.
    /// </summary>
    public async Task<int> AdoptUnconfirmedAsync(string apiKey, PendingRental pending, CancellationToken ct = default)
    {
        var before = new HashSet<string>(pending.Before, StringComparer.Ordinal);
        var rows = await ListInstanceRowsAsync(apiKey, ct);
        var windowStart = pending.OrderedAtUtc.AddMinutes(-1);           // vendor clock skew
        var windowEnd = pending.OrderedAtUtc + RentSettle + TimeSpan.FromMinutes(5);

        var first = rows
            .Select(r => (Row: r, Id: FirstString(r, "id"), Created: FirstDateTime(r, "createdAt")))
            .Where(x => x.Id is not null && !before.Contains(x.Id) && x.Created is { } c && c >= windowStart && c <= windowEnd)
            .OrderBy(x => x.Created)
            .FirstOrDefault();
        if (first.Row is null) return 0;
        if ((FirstString(first.Row, "name") ?? "").StartsWith(pending.NameTag, StringComparison.Ordinal)) return 1;

        await SendAsync(HttpMethod.Put, $"/instances/{Uri.EscapeDataString(first.Id!)}", apiKey,
            new JsonObject { ["name"] = pending.NameTag }, ct);
        return 1;
    }

    /// <summary>
    /// Find-or-create the private template describing our container.
    ///
    /// The name carries a hash of everything the template pins — image, tag,
    /// disk and ports — so a change to any of them gets a fresh template
    /// instead of silently reusing one that boots last month's image with too
    /// little disk. The template never holds the worker's token or boot
    /// script: it outlives the machine; those go with each order instead.
    /// </summary>
    private async Task<string> EnsureTemplateAsync(string apiKey, GpuRentSpec spec, CancellationToken ct)
    {
        var (image, tag) = SplitImage(spec.DockerImage);
        var ports = spec.ExposedPort.ToString(CultureInfo.InvariantCulture);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", image, tag, spec.DiskGb.ToString(CultureInfo.InvariantCulture), ports))))[..10].ToLowerInvariant();
        var name = $"chanthra-tpl-{fingerprint}";

        var existing = await FindTemplateByNameAsync(apiKey, name, ct);
        if (existing is not null) return $"/instances/templates/{existing}";

        var body = new JsonObject
        {
            ["name"] = name,
            ["imageName"] = image,
            ["defaultTag"] = tag,
            ["categoryName"] = "chanthra",
            ["diskSize"] = spec.DiskGb,
            ["exposePorts"] = ports,
            ["startScript"] = "",
            ["notes"] = "Managed by Chanthra Studio. Deleting this template does not stop running instances.",
            ["isPasswordProtected"] = false,
            ["isRunSshServerOn"] = false,
            ["isRunJupyterOn"] = false,
        };
        await SendAsync(HttpMethod.Post, "/instances/templates", apiKey, body, ct);

        var created = await FindTemplateByNameAsync(apiKey, name, ct);
        return created is not null
            ? $"/instances/templates/{created}"
            : throw new GpuRentalException($"Created template \"{name}\" but it did not appear in the template list.");
    }

    private async Task<string?> FindTemplateByNameAsync(string apiKey, string name, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"/instances/templates/list?itemsPerPage=100&search={Uri.EscapeDataString(name)}", apiKey, null, ct);
        foreach (var t in EnumerateItems(json))
        {
            if (!string.Equals(FirstString(t, "name"), name, StringComparison.Ordinal)) continue;
            var id = FirstString(t, "id");
            if (!string.IsNullOrEmpty(id)) return id;
        }
        return null;
    }

    /// <summary>"pytorch/pytorch:2.9.1-cuda13.0-cudnn9-runtime" → name + tag,
    /// minding a registry host with a port ("host:5000/img:tag").</summary>
    internal static (string Image, string Tag) SplitImage(string dockerImage)
    {
        var slash = dockerImage.LastIndexOf('/');
        var colon = dockerImage.LastIndexOf(':');
        return colon > slash ? (dockerImage[..colon], dockerImage[(colon + 1)..]) : (dockerImage, "latest");
    }

    /// <summary>Where the boot script is written inside the container.</summary>
    private const string BootScriptPath = "/workspace/chanthra-boot.sh";

    /// <summary>
    /// Turn a multi-line script into the single command SimplePod can run.
    ///
    /// SimplePod runs a start script one line at a time, not as a script —
    /// found on aixman's first real rental: a 13 KB bash script arrived as
    /// hundreds of unrelated commands (functions, heredocs and loops all
    /// broken), nothing started, and the machine sat idle while billing. So the
    /// script travels gzipped and base64-encoded in one line that writes it to
    /// a file and runs it with bash; base64 needs no quoting in any POSIX shell.
    /// </summary>
    public static string AsSingleLine(string script)
    {
        if (!script.Contains('\n')) return script;
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = new UTF8Encoding(false).GetBytes(script);
            gz.Write(bytes, 0, bytes.Length);
        }
        var encoded = Convert.ToBase64String(buffer.ToArray());
        return $"mkdir -p /workspace && echo {encoded} | base64 -d | gunzip > {BootScriptPath} && exec bash {BootScriptPath}";
    }

    // -------------------------------------------------------------- instances

    public async Task<GpuInstance?> GetInstanceAsync(string apiKey, string instanceId, CancellationToken ct = default)
    {
        try
        {
            var json = await SendAsync(HttpMethod.Get, $"/instances/{Uri.EscapeDataString(instanceId)}", apiKey, null, ct);
            return json is JsonObject obj && FirstString(obj, "id") is not null ? ParseInstance(obj) : null;
        }
        catch (GpuRentalException ex) when (ex.StatusCode == 404)
        {
            return null;   // already destroyed — not an error
        }
    }

    private async Task<IReadOnlyList<JsonObject>> ListInstanceRowsAsync(string apiKey, CancellationToken ct)
        => EnumerateItems(await SendAsync(HttpMethod.Get, "/instances/list?itemsPerPage=200", apiKey, null, ct)).ToList();

    public async Task<IReadOnlyList<GpuInstance>> ListInstancesAsync(string apiKey, CancellationToken ct = default)
        => (await ListInstanceRowsAsync(apiKey, ct)).Select(ParseInstance).ToList();

    public async Task TerminateAsync(string apiKey, string instanceId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"/instances/{Uri.EscapeDataString(instanceId)}", apiKey, null, ct);
        }
        catch (GpuRentalException ex) when (ex.StatusCode == 404)
        {
            // Already gone is the desired end state. Anything else propagates:
            // a swallowed error here is a machine that bills forever.
        }
    }

    private static GpuInstance ParseInstance(JsonObject o)
    {
        var raw = FirstString(o, "status") ?? "";
        var messages = Strings(o["errors"]).Concat(Strings(o["warnings"])).ToList();
        var ports = ParsePorts(o["ports"] ?? o["portMappings"] ?? o["exposePortMappings"]);
        ports.TryGetValue(GpuProvisioning.ProxyPort, out var endpoint);

        return new GpuInstance
        {
            Id = FirstString(o, "id") ?? "",
            Name = FirstString(o, "name")?.Trim() ?? "",
            RawStatus = raw,
            State = MapState(raw, messages.Count > 0),
            GpuModel = FirstString(o, "gpuModel") ?? "",
            PricePerHourUsd = FirstDecimal(o, "pricePerGpu") ?? 0m,
            StartedAt = FirstDateTime(o, "createdAt"),
            ProxyUrl = endpoint is not null && endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? endpoint : null,
            DirectUrl = endpoint is not null && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? endpoint : null,
            StatusMessage = messages.Count > 0 ? string.Join("; ", messages) : null,
        };
    }

    private static IEnumerable<string> Strings(JsonNode? node)
        => node is JsonArray arr
            ? arr.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null)
                 .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
            : Enumerable.Empty<string>();

    /// <summary>
    /// Normalise SimplePod's port mapping into internal port → public URL.
    ///
    /// What the instance detail really returns (first real rental, aixman
    /// 2026-09-12) is two lists keyed by <c>srcPort</c>, the container port:
    /// <c>{ direct: [{ srcPort, protocol: "http", url }], proxy: [{ srcPort,
    /// protocol: "https", url: "https://….trycloudflare.com" }] }</c>, where
    /// <c>protocol</c> reads "checking"/"closed" until something listens. The
    /// earlier parser only knew an object keyed by port, so no worker ever got
    /// an endpoint, sat "warming" to the timeout and was killed. A Cloudflare
    /// tunnel URL always wins over a bare host:port.
    /// </summary>
    internal static Dictionary<int, string> ParsePorts(JsonNode? raw)
    {
        var result = new Dictionary<int, string>();
        if (raw is JsonObject grouped && (grouped["direct"] is JsonArray || grouped["proxy"] is JsonArray))
        {
            foreach (var list in new[] { grouped["proxy"] as JsonArray, grouped["direct"] as JsonArray })
            {
                if (list is null) continue;
                foreach (var entry in list.OfType<JsonObject>())
                {
                    var port = FirstInt(entry, "srcPort");
                    var url = LiveUrl(entry);
                    if (port is > 0 && url is not null && !result.ContainsKey(port.Value)) result[port.Value] = url;
                }
            }
            return result;
        }

        // The documented object shape and a bare array are still accepted.
        IEnumerable<(string? Key, JsonObject Value)> entries = raw switch
        {
            JsonArray arr => arr.OfType<JsonObject>().Select(v => ((string?)null, v)),
            JsonObject obj => obj.Where(kv => kv.Value is JsonObject).Select(kv => ((string?)kv.Key, (JsonObject)kv.Value!)),
            _ => Enumerable.Empty<(string?, JsonObject)>(),
        };
        foreach (var (key, value) in entries)
        {
            int? internalPort = int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k
                : FirstInt(value, "internalPort", "containerPort", "port", "privatePort");
            if (internalPort is not > 0) continue;

            var proxyUrl = FirstString(value, "proxyUrl");
            if (!string.IsNullOrWhiteSpace(proxyUrl)) { result[internalPort.Value] = proxyUrl!.TrimEnd('/'); continue; }

            var host = FirstString(value, "ip", "host", "hostIp", "publicIp");
            var external = FirstInt(value, "externalPort", "hostPort", "publicPort", "mappedPort");
            if (!string.IsNullOrWhiteSpace(host) && external is > 0)
            {
                var scheme = FirstString(value, "protocol") == "https" ? "https" : "http";
                result[internalPort.Value] = $"{scheme}://{host}:{external}";
            }
        }
        return result;
    }

    /// <summary>A published address that is actually usable — not "closed" or "checking".</summary>
    private static string? LiveUrl(JsonObject entry)
    {
        var protocol = FirstString(entry, "protocol") ?? "";
        var url = FirstString(entry, "url")?.Trim() ?? "";
        if (protocol is not ("http" or "https")) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return url.TrimEnd('/');
    }

    private static GpuInstanceState MapState(string raw, bool hasErrors) => raw.ToLowerInvariant() switch
    {
        "active" or "running" or "ready" => GpuInstanceState.Running,
        "created" or "creating" or "pending" or "starting" or "provisioning" or "queued" => GpuInstanceState.Provisioning,
        "error" or "failed" or "unavailable" => GpuInstanceState.Failed,
        "paused" or "stopped" or "deleted" or "removed" or "expired" => GpuInstanceState.Stopped,
        // Unknown status with vendor-reported errors is treated as broken, so
        // the reaper releases it instead of waiting out the warm-up timeout.
        _ => hasErrors ? GpuInstanceState.Failed : GpuInstanceState.Provisioning,
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
            throw new GpuRentalException($"SimplePod timed out on {method} {path.Split('?')[0]}.");
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
                    : $"SimplePod {method} {path.Split('?')[0]} failed ({(int)resp.StatusCode}): {detail}",
                    (int)resp.StatusCode);
            }
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); }
            catch { return null; }   // creation endpoints answer with an empty body
        }
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no response body";
        try
        {
            var msg = FirstString(JsonNode.Parse(body) as JsonObject, "detail", "title", "message", "error");
            if (!string.IsNullOrWhiteSpace(msg)) return msg!.Length > 300 ? msg[..300] : msg;
        }
        catch { /* not JSON — fall through to the truncated raw body */ }
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    // ----------------------------------------------------- tolerant JSON reads

    /// <summary>Yields the item objects out of a bare array, or an object
    /// wrapping one under a known key (Hydra's "hydra:member" included).</summary>
    internal static IEnumerable<JsonObject> EnumerateItems(JsonNode? json)
    {
        JsonArray? arr = json as JsonArray;
        if (arr is null && json is JsonObject obj)
        {
            foreach (var key in new[] { "hydra:member", "member", "data", "items", "instances", "results", "list", "templates", "market" })
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
            // Numeric ids arrive unquoted.
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
