using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Polls the GitHub Releases API for new versions, downloads the asset
/// with progress reporting, and applies the update by extracting over
/// the install directory and relaunching.
///
/// Version comparison uses semantic-ish ordering: tag names like
/// <c>v0.2.0</c> are stripped of their leading <c>v</c> and parsed as
/// <see cref="Version"/>. Pre-release suffixes (<c>-beta</c>) are
/// dropped — we ship release-only updates through this channel.
/// </summary>
public static class UpdateService
{
    public const string Owner = "xjanova";
    public const string Repo = "chanthra-studio";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ChanthraStudio-Updater/1.0");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Fetches /releases/latest and returns an <see cref="UpdateInfo"/>.
    /// Returns null if the network call fails or no release exists.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static UpdateInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var publishedAt = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(p.GetString(), out var when) ? when : DateTimeOffset.MinValue;

        // Pick the first .zip asset; fall back to .exe so single-file
        // builds still work without a zip wrapper.
        string downloadUrl = "", assetName = "";
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var aname = a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(aname)) continue;
                var aurl = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                var asize = a.TryGetProperty("size", out var asz) && asz.ValueKind == JsonValueKind.Number ? asz.GetInt64() : 0;
                if (aname.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = aurl; assetName = aname; size = asize; break;
                }
                if (aname.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = aurl; assetName = aname; size = asize;
                }
            }
        }

        var current = CurrentVersion();
        var latest = NormaliseVersion(tag);
        return new UpdateInfo
        {
            CurrentVersion = current,
            LatestVersion = latest,
            HasUpdate = CompareSemver(current, latest) < 0,
            ReleaseName = string.IsNullOrEmpty(name) ? tag : name,
            Notes = body,
            PublishedAt = publishedAt,
            DownloadUrl = downloadUrl,
            AssetName = assetName,
            AssetSizeBytes = size,
        };
    }

    public static string NormaliseVersion(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "0.0.0";
        var t = tag.TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        return dash >= 0 ? t[..dash] : t;
    }

    public static int CompareSemver(string a, string b)
    {
        Version va = Version.TryParse(NormaliseVersion(a), out var pa) ? pa : new Version(0, 0, 0);
        Version vb = Version.TryParse(NormaliseVersion(b), out var pb) ? pb : new Version(0, 0, 0);
        // Always compare 3-part — Version uses -1 for "missing" components which sorts lower than 0.
        var na = new Version(va.Major, va.Minor, Math.Max(0, va.Build));
        var nb = new Version(vb.Major, vb.Minor, Math.Max(0, vb.Build));
        return na.CompareTo(nb);
    }

    /// <summary>
    /// Streams the asset to a temp file, reporting bytes downloaded and
    /// total bytes via the progress callback. Returns the path to the
    /// downloaded file, or null on cancel/failure.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        UpdateInfo info,
        IProgress<(long Downloaded, long Total)> progress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl)) return null;

        var tmpDir = Path.Combine(Path.GetTempPath(), "ChanthraStudio.Update");
        Directory.CreateDirectory(tmpDir);
        var localPath = Path.Combine(tmpDir, info.AssetName);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            long total = resp.Content.Headers.ContentLength ?? info.AssetSizeBytes;
            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var dst = File.Create(localPath);

            var buf = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                progress.Report((downloaded, total));
            }
            return localPath;
        }
        catch
        {
            try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Applies a downloaded update by spawning a small batch helper that
    /// waits for the current process to exit, then unzips/copies into the
    /// install dir, then relaunches the app. We can't overwrite our own
    /// .exe while it's running, so the helper does the swap and we exit.
    /// </summary>
    public static void ApplyAndRestart(string downloadedPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\');
        var pid = Environment.ProcessId;
        var exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "ChanthraStudio.exe");

        var helperPath = Path.Combine(Path.GetTempPath(), "chanthra-studio-update.cmd");

        // The helper:
        //  1. waits for our PID to exit
        //  2. extracts the zip on top of installDir (preserving user data)
        //  3. relaunches the app, then removes itself
        var script = $@"@echo off
chcp 65001 > nul
:wait
tasklist /FI ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto wait
)
timeout /t 1 /nobreak > nul
{(downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
    ? $@"powershell -NoProfile -ExecutionPolicy Bypass -Command ""Expand-Archive -Force -LiteralPath '{downloadedPath}' -DestinationPath '{installDir}'"""
    : $@"copy /Y ""{downloadedPath}"" ""{installDir}\\{exeName}""")}
start """" ""{installDir}\{exeName}""
del ""%~f0""
";
        File.WriteAllText(helperPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C \"{helperPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
    }

    /// <summary>
    /// Used as a fallback for users without a license — opens the
    /// release page in a browser instead of streaming the asset.
    /// </summary>
    public static void OpenReleasePage()
    {
        var url = $"https://github.com/{Owner}/{Repo}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
