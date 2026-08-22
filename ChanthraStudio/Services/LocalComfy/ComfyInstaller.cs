using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

namespace ChanthraStudio.Services.LocalComfy;

/// <summary>Progress while the engine is being installed.</summary>
/// <param name="Stage">"download" · "extract" · "configure" · "done".</param>
/// <param name="Message">A line the user can read.</param>
/// <param name="Fraction">0..1 across the whole install.</param>
public sealed record ComfyInstallProgress(string Stage, string Message, double Fraction);

/// <summary>What a completed install recorded about itself.</summary>
public sealed class ComfyInstallStamp
{
    public string Tag { get; set; } = "";
    public string Flavour { get; set; } = "";
    public string AssetName { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime InstalledAtUtc { get; set; }
}

/// <summary>
/// Downloads and unpacks the official ComfyUI Windows portable into the
/// studio's own engine folder.
///
/// <b>Nothing here touches the machine outside that folder.</b> No installer,
/// no PATH entry, no registry, no system Python. Uninstalling the engine is
/// deleting a directory, which is also what makes a failed install safe to
/// retry.
/// </summary>
public sealed class ComfyInstaller
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Free space required beyond the archive itself: the extracted
    /// tree is roughly three times the compressed size, and both exist on disk
    /// at once during extraction.</summary>
    private const double ExtractExpansionFactor = 3.0;

    /// <summary>
    /// Install (or repair) the engine.
    /// </summary>
    public async Task<ComfyInstallStamp> InstallAsync(
        string root, ComfyFlavour flavour,
        IProgress<ComfyInstallProgress>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ComfyPaths.CacheDir(root));

        progress?.Report(new ComfyInstallProgress("download", "กำลังหาเวอร์ชันล่าสุด…", 0.01));
        var asset = await new ComfyRelease().ResolveAsync(flavour, ct);

        EnsureDiskSpace(root, asset);

        var archive = Path.Combine(ComfyPaths.CacheDir(root), asset.AssetName);
        await DownloadAsync(asset, archive, progress, ct);

        // Extract into a staging folder and swap it in. Extracting straight
        // over a previous install would leave a half-old, half-new tree behind
        // if it failed part-way — and that tree would still look installed.
        var staging = Path.Combine(root, "staging");
        SafeDelete(staging);
        progress?.Report(new ComfyInstallProgress("extract", "กำลังแตกไฟล์…", 0.55));
        ExtractSevenZip(archive, staging, progress, ct);

        var portableSource = FindPortableRoot(staging)
            ?? throw new InvalidOperationException(
                "The downloaded archive did not contain a ComfyUI portable folder — "
                + "the release layout may have changed.");

        var target = ComfyPaths.PortableDir(root);
        progress?.Report(new ComfyInstallProgress("configure", "กำลังติดตั้ง…", 0.92));
        SafeDelete(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(portableSource, target);
        SafeDelete(staging);

        if (!File.Exists(ComfyPaths.PythonExe(root)))
            throw new InvalidOperationException(
                $"Install finished but {ComfyPaths.PythonExe(root)} is missing — the archive layout was not what we expected.");
        if (!File.Exists(ComfyPaths.MainPy(root)))
            throw new InvalidOperationException(
                $"Install finished but {ComfyPaths.MainPy(root)} is missing — the archive layout was not what we expected.");

        WriteModelPathsConfig(root);

        var stamp = new ComfyInstallStamp
        {
            Tag = asset.Tag,
            Flavour = flavour.ToString(),
            AssetName = asset.AssetName,
            Bytes = asset.Bytes,
            InstalledAtUtc = DateTime.UtcNow,
        };
        File.WriteAllText(ComfyPaths.StampFile(root),
            JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));

        // The archive is 2 GB of no further use once unpacked. Keeping it would
        // double the engine's footprint for a re-download that takes minutes.
        TryDelete(archive);

        progress?.Report(new ComfyInstallProgress("done", $"ติดตั้ง ComfyUI {asset.Tag} เรียบร้อย", 1.0));
        return stamp;
    }

    /// <summary>Read an existing install's stamp, or null when there is none.</summary>
    public static ComfyInstallStamp? ReadStamp(string root)
    {
        try
        {
            var file = ComfyPaths.StampFile(root);
            if (!File.Exists(file)) return null;
            // The stamp is only trustworthy if the files it describes are still
            // there — a user who deleted half the folder should get "not
            // installed", not a crash on start.
            if (!File.Exists(ComfyPaths.PythonExe(root)) || !File.Exists(ComfyPaths.MainPy(root)))
                return null;
            return JsonSerializer.Deserialize<ComfyInstallStamp>(File.ReadAllText(file));
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------- internals

    private static void EnsureDiskSpace(string root, ComfyReleaseAsset asset)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            var needed = (long)(asset.Bytes * (1 + ExtractExpansionFactor));
            if (drive.AvailableFreeSpace < needed)
                throw new InvalidOperationException(
                    $"ต้องการพื้นที่ว่างอย่างน้อย {needed / 1073741824.0:0.0} GB บนไดรฟ์ {drive.Name} "
                    + $"(ตอนนี้เหลือ {drive.AvailableFreeSpace / 1073741824.0:0.0} GB) — "
                    + "ย้ายโฟลเดอร์เอนจินไปไดรฟ์อื่นได้ในหน้า ComfyUI");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Network paths and junctions can make this unanswerable. Running
            // out of space later is a worse error than not checking, but
            // refusing to install because we couldn't measure is worse still.
            ActivityLog.Warn("comfy", "could not check free space: " + ex.Message);
        }
    }

    private static async Task DownloadAsync(
        ComfyReleaseAsset asset, string dest,
        IProgress<ComfyInstallProgress>? progress, CancellationToken ct)
    {
        // Resume a partial file. Two gigabytes over a domestic link is long
        // enough that "start again from zero" is a real cost.
        long existing = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        if (existing == asset.Bytes)
        {
            progress?.Report(new ComfyInstallProgress("download", "ใช้ไฟล์ที่โหลดไว้แล้ว", 0.5));
            return;
        }
        if (existing > asset.Bytes) { TryDelete(dest); existing = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        // A server that ignores the range header answers 200 with the whole
        // file; appending that to our partial would produce a corrupt archive
        // whose only symptom is a cryptic extraction failure.
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            TryDelete(dest);
            existing = 0;
        }
        resp.EnsureSuccessStatusCode();

        var mode = existing > 0 ? FileMode.Append : FileMode.Create;
        await using (var file = new FileStream(dest, mode, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        await using (var net = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[1 << 20];
            long done = existing;
            var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await net.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;

                if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 400) continue;
                lastReport = DateTime.UtcNow;
                progress?.Report(new ComfyInstallProgress("download",
                    $"ดาวน์โหลด {done / 1073741824.0:0.00} / {asset.Gb:0.00} GB",
                    0.5 * done / Math.Max(1, asset.Bytes)));
            }
        }

        var landed = new FileInfo(dest).Length;
        if (landed != asset.Bytes)
        {
            // Size is the only integrity signal the release metadata gives us.
            // A short file here means a truncated download, and the next step
            // would fail deep inside the 7z reader with nothing to explain it.
            TryDelete(dest);
            throw new InvalidOperationException(
                $"ดาวน์โหลดไม่ครบ: ได้ {landed:N0} ไบต์ ควรได้ {asset.Bytes:N0} — ลองใหม่อีกครั้ง");
        }
    }

    /// <summary>
    /// Unpack the portable.
    ///
    /// <b>Windows' own tar.exe first.</b> Since Windows 10 1803 the system
    /// ships bsdtar/libarchive at <c>%SystemRoot%\System32\tar.exe</c>, and it
    /// reads 7z. That matters here specifically: this archive is compressed as
    /// one solid block with a <b>BCJ2</b> branch filter and a 768 MB
    /// dictionary, and BCJ2 is exactly the filter managed 7z readers tend not
    /// to implement — including SharpCompress. Reaching for the managed
    /// library first would fail on the one archive this method exists to open.
    /// The managed path stays as a fallback for a machine without tar.exe.
    /// </summary>
    private static void ExtractSevenZip(
        string archivePath, string destination,
        IProgress<ComfyInstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        if (TryExtractWithSystemTar(archivePath, destination, progress, ct)) return;

        ActivityLog.Warn("comfy", "tar.exe unavailable or failed — falling back to the managed 7z reader");
        ExtractWithSharpCompress(archivePath, destination, progress, ct);
    }

    private static bool TryExtractWithSystemTar(
        string archivePath, string destination,
        IProgress<ComfyInstallProgress>? progress, CancellationToken ct)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar)) return false;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tar,
                // -x extract, -f archive, -C into. bsdtar sniffs the format,
                // so no flag names 7z.
                Arguments = $"-xf \"{archivePath}\" -C \"{destination}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;

            // tar gives no progress, and this step runs for minutes on a 4.4 GB
            // expansion. Watching the destination grow is not exact, but it is
            // an honest moving number rather than a frozen bar.
            var watcher = Task.Run(async () =>
            {
                while (!proc.HasExited)
                {
                    try
                    {
                        var bytes = DirectorySize(destination);
                        progress?.Report(new ComfyInstallProgress("extract",
                            $"แตกไฟล์ {bytes / 1073741824.0:0.00} GB", 0.55 + 0.35 * Math.Min(1, bytes / 4_400_000_000.0)));
                    }
                    catch { }
                    await Task.Delay(1000);
                }
            }, ct);

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            try { watcher.Wait(2000, ct); } catch { }

            if (proc.ExitCode == 0) return true;

            ActivityLog.Warn("comfy", $"tar.exe exited {proc.ExitCode}: {stderr.Trim()}");
            // Leave nothing half-extracted for the fallback to trip over.
            SafeDelete(destination);
            Directory.CreateDirectory(destination);
            return false;
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("comfy", "tar.exe failed: " + ex.Message);
            return false;
        }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            // Files appear and vanish under us while tar runs; a failed
            // measurement is a skipped progress tick, not an error.
            return 0;
        }
    }

    private static void ExtractWithSharpCompress(
        string archivePath, string destination,
        IProgress<ComfyInstallProgress>? progress, CancellationToken ct)
    {
        using var archive = SevenZipArchive.Open(archivePath);

        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        long total = entries.Sum(e => Math.Max(0, e.Size));
        long done = 0;
        var lastReport = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var relative = entry.Key ?? "";
            // Refuse anything that would escape the destination. This archive
            // comes from a trusted release, but an extractor that only behaves
            // for trusted input is an extractor waiting to be pointed at
            // something else.
            var full = Path.GetFullPath(Path.Combine(destination, relative));
            if (!full.StartsWith(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Archive entry escapes the target folder: {relative}");

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var input = entry.OpenEntryStream())
            using (var output = File.Create(full))
                input.CopyTo(output);

            done += Math.Max(0, entry.Size);
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 400) continue;
            lastReport = DateTime.UtcNow;
            progress?.Report(new ComfyInstallProgress("extract",
                $"แตกไฟล์ {done / 1073741824.0:0.00} / {total / 1073741824.0:0.00} GB",
                0.55 + 0.35 * done / Math.Max(1, total)));
        }
    }

    /// <summary>
    /// The archive wraps everything in a top-level folder whose name has
    /// changed across releases, so it is located by content — the folder that
    /// contains both <c>python_embeded</c> and <c>ComfyUI</c> — rather than by
    /// a name we would have to keep in step.
    /// </summary>
    private static string? FindPortableRoot(string staging)
    {
        bool Looks(string dir)
            => Directory.Exists(Path.Combine(dir, "python_embeded"))
            && Directory.Exists(Path.Combine(dir, "ComfyUI"));

        if (Looks(staging)) return staging;
        return Directory.EnumerateDirectories(staging).FirstOrDefault(Looks);
    }

    /// <summary>
    /// Point ComfyUI at the studio's model folder instead of its own.
    ///
    /// Written as <c>extra_model_paths.yaml</c>, which ComfyUI reads from its
    /// own directory at startup. This is what lets the engine be deleted and
    /// reinstalled without re-downloading tens of gigabytes of weights, and
    /// what lets the rented-GPU route and the local one agree about what a
    /// model is called.
    /// </summary>
    private static void WriteModelPathsConfig(string root)
    {
        var models = ComfyPaths.ModelsDir();
        var kinds = new[]
        {
            "checkpoints", "diffusion_models", "unet", "vae", "loras",
            "text_encoders", "clip", "clip_vision", "controlnet", "upscale_models",
            "embeddings", "style_models", "gligen", "hypernetworks", "photomaker",
        };
        foreach (var k in kinds) Directory.CreateDirectory(Path.Combine(models, k));

        var lines = new List<string>
        {
            "# Generated by Chanthra Studio. Points ComfyUI at the studio's own",
            "# model folder so weights survive an engine reinstall.",
            "chanthra:",
            $"    base_path: {models}",
        };
        lines.AddRange(kinds.Select(k => $"    {k}: {k}"));

        File.WriteAllText(Path.Combine(ComfyPaths.ComfyDir(root), "extra_model_paths.yaml"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void SafeDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { ActivityLog.Warn("comfy", $"could not remove {dir}: {ex.Message}"); }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
    }
}
