using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services.Gpu;

namespace ChanthraStudio.Services.LocalComfy;

/// <summary>Progress for one weight file.</summary>
public sealed record ModelDownloadProgress(string FileName, double Fraction, string Message);

/// <summary>
/// Fetches model weights into the studio's own model folder.
///
/// <b>The catalog is shared with the rented-GPU route on purpose.</b> Those
/// URLs and sizes were verified against Hugging Face file by file, and a
/// second list would drift from the first — with the failure landing hours
/// later inside a render, on whichever route happened to have the stale copy.
/// One list, two consumers: the boot script on a rented box, and this on the
/// user's own machine.
/// </summary>
public sealed class ComfyModelInstaller
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Every profile in the catalog, as installable bundles.</summary>
    public static IReadOnlyList<GpuModelProfile> Profiles => GpuModelCatalog.All;

    /// <summary>Absolute path a catalog file would occupy locally.</summary>
    public static string PathFor(GpuWeightFile file)
        => Path.Combine(ComfyPaths.ModelFolder(file.Folder), file.FileName);

    /// <summary>
    /// True when the file is present at close to its catalog size.
    ///
    /// Size rather than existence: a cancelled download leaves a partial file
    /// that exists, and treating that as installed produces a render that fails
    /// on a corrupt checkpoint with no hint as to why.
    /// </summary>
    public static bool IsInstalled(GpuWeightFile file)
    {
        var path = PathFor(file);
        if (!File.Exists(path)) return false;
        var min = (long)(file.SizeGb * 1073741824.0 * 0.9);
        return new FileInfo(path).Length >= min;
    }

    public static bool IsInstalled(GpuModelProfile profile) => profile.Files.All(IsInstalled);

    /// <summary>Gigabytes still to fetch for this profile.</summary>
    public static double RemainingGb(GpuModelProfile profile)
        => profile.Files.Where(f => !IsInstalled(f)).Sum(f => f.SizeGb);

    /// <summary>
    /// Download every file the profile needs that isn't already there.
    /// </summary>
    public async Task DownloadAsync(
        GpuModelProfile profile, string? hfToken,
        IProgress<ModelDownloadProgress>? progress, CancellationToken ct = default)
    {
        var missing = profile.Files.Where(f => !IsInstalled(f)).ToList();
        if (missing.Count == 0) return;

        EnsureDiskSpace(missing.Sum(f => f.SizeGb));

        for (var i = 0; i < missing.Count; i++)
        {
            var file = missing[i];
            var slot = i;
            var inner = new Progress<double>(f =>
                progress?.Report(new ModelDownloadProgress(
                    file.FileName,
                    (slot + f) / missing.Count,
                    $"{file.FileName} · {f * file.SizeGb:0.00} / {file.SizeGb:0.00} GB")));

            await DownloadOneAsync(file, hfToken, inner, ct);
        }

        progress?.Report(new ModelDownloadProgress("", 1, $"{profile.DisplayName} พร้อมใช้งาน"));
    }

    private static void EnsureDiskSpace(double neededGb)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ComfyPaths.ModelsDir()))!);
            var needed = (long)(neededGb * 1073741824.0 * 1.05);
            if (drive.AvailableFreeSpace < needed)
                throw new InvalidOperationException(
                    $"ต้องการพื้นที่ว่าง {neededGb:0.0} GB บนไดรฟ์ {drive.Name} "
                    + $"แต่เหลือ {drive.AvailableFreeSpace / 1073741824.0:0.0} GB");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            ActivityLog.Warn("comfy", "could not check free space for models: " + ex.Message);
        }
    }

    private static async Task DownloadOneAsync(
        GpuWeightFile file, string? hfToken,
        IProgress<double>? progress, CancellationToken ct)
    {
        var dest = PathFor(file);
        var part = dest + ".part";

        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        var expected = (long)(file.SizeGb * 1073741824.0);

        using var req = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        // Pass a token even for public repos: it moves the request from the
        // anonymous rate-limit bucket to the account's, and the anonymous one
        // is the usual cause of a mid-download failure on a big pull.
        if (!string.IsNullOrWhiteSpace(hfToken))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hfToken);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            // The server ignored the range; appending would corrupt the file.
            TryDelete(part);
            existing = 0;
        }
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"โหลด {file.FileName} ไม่สำเร็จ (HTTP {(int)resp.StatusCode}) — "
                + (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "ไฟล์นี้เป็น repo แบบ gated ต้องใส่ Hugging Face token ในหน้า GPU"
                    : "URL อาจย้ายที่แล้ว"));

        var total = existing + (resp.Content.Headers.ContentLength ?? Math.Max(0, expected - existing));

        await using (var outFile = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create,
                                                  FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        await using (var net = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[1 << 20];
            long done = existing;
            var last = DateTime.UtcNow;
            int read;
            while ((read = await net.ReadAsync(buffer, ct)) > 0)
            {
                await outFile.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds < 400) continue;
                last = DateTime.UtcNow;
                progress?.Report(Math.Clamp(done / (double)Math.Max(1, total), 0, 1));
            }
        }

        // A gated or moved URL answers 200 with an HTML page. It lands as a
        // .safetensors and only fails much later, inside a render, as a parse
        // error nobody can trace back to here.
        var landed = new FileInfo(part).Length;
        var min = (long)(expected * 0.9);
        if (landed < min)
        {
            TryDelete(part);
            throw new InvalidOperationException(
                $"{file.FileName} ได้มา {landed:N0} ไบต์ ควรได้ราว {expected:N0} — URL อาจถูกปิดหรือย้าย");
        }

        // Rename last: the file only becomes "installed" once it is complete,
        // so a cancelled download is resumable rather than poisonous.
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(part, dest);
        progress?.Report(1);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
