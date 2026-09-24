using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Asks xman4289.com — the site that sells the license — for a newer
/// version, downloads the zip from xman4289.com with progress reporting,
/// verifies it, and applies the update by extracting over the install
/// directory and relaunching.
///
/// Owner rule (2026-09-24): the app is downloaded only from xman4289.com
/// and customers must never learn where the source lives, so nothing here
/// talks to (or names) the code host. The site serves the release file
/// itself: <c>/api/v1/product/{slug}/update/check</c> announces the version,
/// size and SHA-256, and the download URL it returns streams exactly that
/// version's file.
///
/// Version comparison uses semantic-ish ordering: versions like
/// <c>v0.2.0</c> are stripped of their leading <c>v</c> and parsed as
/// <see cref="Version"/>. Pre-release suffixes (<c>-beta</c>) are
/// dropped — we ship release-only updates through this channel.
/// </summary>
public static class UpdateService
{
    /// <summary>The only host the updater accepts a file from.</summary>
    public const string SiteHost = "xman4289.com";

    public static string CheckUrl =>
        $"{LicenseClient.BaseUrl}/api/v1/product/{LicenseClient.ProductSlug}/update/check";

    /// <summary>Streams the newest zip — what the manual-download button opens.</summary>
    public static string DownloadPageUrl => $"{LicenseClient.BaseUrl}/chanthra-studio/download";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No redirects: the site streams the file itself, so a redirect means
        // something is wrong (a login page, a mirror) and the bytes would not
        // be the ones update/check vouched for.
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ChanthraStudio-Updater/1.0");
        return c;
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Asks update/check about the installed version and returns an
    /// <see cref="UpdateInfo"/>. Returns null if the network call fails or
    /// the answer can't be read.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var current = CurrentVersion();
            var url = $"{CheckUrl}?current_version={Uri.EscapeDataString(current)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, current);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the update/check answer:
    /// <c>{ has_update, latest_version, download_url, changelog, sha256, file_size, filename }</c>.
    /// <c>download_url</c> is only filled when there is an update, and is only
    /// taken when it points at <see cref="SiteHost"/> over https.
    /// </summary>
    public static UpdateInfo Parse(string json, string current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var latest = NormaliseVersion(Str(root, "latest_version") ?? "");
        var sha = Str(root, "sha256")?.Trim().ToLowerInvariant();
        var size = root.TryGetProperty("file_size", out var fs) && fs.ValueKind == JsonValueKind.Number
            && fs.TryGetInt64(out var n) ? n : 0;
        var url = Str(root, "download_url") ?? "";

        // An empty latest_version means the site has no release yet — nothing to offer.
        var hasUpdate = !string.IsNullOrEmpty(Str(root, "latest_version")) && CompareSemver(current, latest) < 0;

        return new UpdateInfo
        {
            CurrentVersion = current,
            LatestVersion = latest,
            HasUpdate = hasUpdate,
            ReleaseName = $"Chanthra Studio v{latest}",
            Notes = Str(root, "changelog") ?? "",
            DownloadUrl = IsSiteUrl(url) ? url : "",
            AssetName = Str(root, "filename") ?? "",
            AssetSizeBytes = size,
            AssetSha256 = sha is { Length: 64 } && sha.All(Uri.IsHexDigit) ? sha : null,
        };
    }

    /// <summary>https on xman4289.com (or www.) — the only place a file may come from.</summary>
    public static bool IsSiteUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals(SiteHost, StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("www." + SiteHost, StringComparison.OrdinalIgnoreCase));

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
    /// Streams the file to a temp file, reporting bytes downloaded and
    /// total bytes via the progress callback. Returns the path to the
    /// downloaded file, or null on cancel/failure. Throws when the file
    /// arrived but is not the one xman4289.com announced, or the site
    /// answered with something that isn't the file — the caller shows the
    /// message and must not install anything.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        UpdateInfo info,
        IProgress<(long Downloaded, long Total)> progress,
        CancellationToken ct)
    {
        if (!IsSiteUrl(info.DownloadUrl)) return null;

        var tmpDir = Path.Combine(Path.GetTempPath(), "ChanthraStudio.Update");
        Directory.CreateDirectory(tmpDir);
        // The name comes from the update/check JSON and ends up inside a batch
        // script and a PowerShell command line; keep it to plain characters.
        var safeName = string.Concat(Path.GetFileName(info.AssetName)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        if (string.IsNullOrEmpty(safeName)) safeName = "update.zip";
        var localPath = Path.Combine(tmpDir, safeName);
        var partPath = localPath + ".part";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // The site caps how many files it streams at once and answers 503 when full.
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new InvalidDataException("เซิร์ฟเวอร์ดาวน์โหลดไม่ว่างชั่วคราว — ลองใหม่ในอีกสักครู่");
            if (!resp.IsSuccessStatusCode) return null;

            // A web page or a JSON error is not an update, whatever its status.
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("เซิร์ฟเวอร์ไม่ได้ส่งไฟล์อัปเดตมา — ลองใหม่ภายหลัง");

            long total = resp.Content.Headers.ContentLength ?? info.AssetSizeBytes;
            long downloaded = 0;
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var dst = File.Create(partPath))
            {
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buf, 0, read);
                    downloaded += read;
                    progress.Report((downloaded, total));
                }
            }

            // It is about to be unpacked over the program itself: a cut-off
            // download, or bytes that are not what update/check announced,
            // must never get that far.
            if (info.AssetSizeBytes > 0 && downloaded != info.AssetSizeBytes)
                throw new InvalidDataException(
                    $"ไฟล์อัปเดตไม่ครบ ({downloaded:N0} จาก {info.AssetSizeBytes:N0} ไบต์) — ลองดาวน์โหลดใหม่");
            if (!string.IsNullOrEmpty(info.AssetSha256))
            {
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actual != info.AssetSha256)
                    throw new InvalidDataException(
                        "ไฟล์อัปเดตไม่ตรงกับที่ xman4289.com ประกาศไว้ (SHA-256 ไม่ตรง) — ไม่ติดตั้ง ลองใหม่ภายหลัง");
            }

            File.Move(partPath, localPath, overwrite: true);
            return localPath;
        }
        catch (InvalidDataException)
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            throw;
        }
        catch
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
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
        // The helper runs after this process is gone; it leaves its verdict
        // here, since a failed unpack otherwise relaunched the old version
        // without a trace.
        var updateLog = Path.Combine(AppPaths.LogsFolder, "update.log");

        // Paths go into a batch file (where % expands) and a single-quoted
        // PowerShell string (where ' ends it) — an install folder with a quote
        // or a percent sign in its name broke the update, or worse.
        static string Cmd(string s) => s.Replace("%", "%%");
        // PowerShell also ends a single-quoted string at the typographic
        // quotes (U+2018/U+2019/U+201A/U+201B); double those too.
        static string Ps(string s) => s.Replace("'", "''").Replace("‘", "‘‘")
            .Replace("’", "’’").Replace("‚", "‚‚").Replace("‛", "‛‛");

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
    ? $@"powershell -NoProfile -ExecutionPolicy Bypass -Command ""Expand-Archive -Force -LiteralPath '{Cmd(Ps(downloadedPath))}' -DestinationPath '{Cmd(Ps(installDir))}'"""
    : $@"copy /Y ""{Cmd(downloadedPath)}"" ""{Cmd(installDir)}\\{Cmd(exeName)}""")}
if errorlevel 1 (echo %date% %time% update FAILED to install {Cmd(Path.GetFileName(downloadedPath))}>> ""{Cmd(updateLog)}"") else (echo %date% %time% update installed {Cmd(Path.GetFileName(downloadedPath))}>> ""{Cmd(updateLog)}"")
start """" ""{Cmd(installDir)}\{Cmd(exeName)}""
del ""%~f0""
";
        File.WriteAllText(helperPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // /S + an extra pair of quotes: with a plain /C "path", a temp
            // folder containing & ( ) or ^ made cmd strip the quotes and the
            // helper never ran — the app closed with no update and no restart.
            Arguments = $"/D /S /C \"\"{helperPath}\"\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
    }

    /// <summary>
    /// For users without a license (auto-install is license-only): opens the
    /// download on xman4289.com in the browser, which saves the newest zip
    /// for extracting over the install folder by hand.
    /// </summary>
    public static void OpenDownloadPage()
    {
        try { Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true }); } catch { }
    }
}
