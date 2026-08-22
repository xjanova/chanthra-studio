using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChanthraStudio.Services.LocalComfy;

/// <summary>Which vendor's build to install.</summary>
public enum ComfyFlavour
{
    /// <summary>NVIDIA, newest CUDA. What most machines want.</summary>
    Nvidia,
    /// <summary>NVIDIA on an older driver — the cu126 build exists precisely
    /// for cards or drivers the newest CUDA runtime will not start on.</summary>
    NvidiaOlderCuda,
    Amd,
    Intel,
}

/// <summary>One downloadable ComfyUI portable build.</summary>
/// <param name="Tag">Release tag, e.g. "v0.33.1".</param>
/// <param name="AssetName">File name of the .7z asset.</param>
/// <param name="Url">Asset download URL (a redirect to a presigned CDN URL).</param>
/// <param name="Bytes">Exact size from the release metadata.</param>
public sealed record ComfyReleaseAsset(string Tag, string AssetName, string Url, long Bytes)
{
    public double Gb => Bytes / 1073741824.0;
}

/// <summary>
/// Resolves the official ComfyUI Windows portable build to install.
///
/// <b>Why the portable archive rather than assembling one.</b> The alternative
/// is an embeddable Python, then get-pip, then a multi-gigabyte torch install
/// from the CUDA wheel index, then ComfyUI's requirements — four network steps
/// that can each fail differently on a stranger's machine, and a torch/CUDA
/// pairing we would be choosing on their behalf. The portable is one file that
/// already contains a matched Python, torch and ComfyUI, published by the
/// project itself, in a vendor-specific build. One download, one extraction,
/// no compiler, no PATH, nothing installed system-wide.
///
/// <b>The repository moved.</b> <c>comfyanonymous/ComfyUI</c> now redirects to
/// <c>Comfy-Org/ComfyUI</c>. The GitHub API returns 301 rather than following
/// it, so the old path silently yields nothing useful unless redirects are
/// followed — this class uses the current name directly.
/// </summary>
public sealed class ComfyRelease
{
    private const string ReleasesApi = "https://api.github.com/repos/Comfy-Org/ComfyUI/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects API requests with no user agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChanthraStudio", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The asset name each flavour looks for.</summary>
    public static string AssetNameFor(ComfyFlavour flavour) => flavour switch
    {
        ComfyFlavour.Nvidia => "ComfyUI_windows_portable_nvidia.7z",
        ComfyFlavour.NvidiaOlderCuda => "ComfyUI_windows_portable_nvidia_cu126.7z",
        ComfyFlavour.Amd => "ComfyUI_windows_portable_amd.7z",
        ComfyFlavour.Intel => "ComfyUI_windows_portable_intel.7z",
        _ => "ComfyUI_windows_portable_nvidia.7z",
    };

    public static string DescribeFlavour(ComfyFlavour flavour) => flavour switch
    {
        ComfyFlavour.Nvidia => "NVIDIA · CUDA ล่าสุด",
        ComfyFlavour.NvidiaOlderCuda => "NVIDIA · CUDA 12.6 (ไดรเวอร์เก่า)",
        ComfyFlavour.Amd => "AMD",
        ComfyFlavour.Intel => "Intel",
        _ => flavour.ToString(),
    };

    /// <summary>
    /// Find the newest release that actually carries the asset for this
    /// flavour.
    ///
    /// Not just "the latest release": a release can ship without one of the
    /// vendor builds, and installing nothing while reporting success is worse
    /// than installing a slightly older build that exists. Walks back through
    /// recent releases until it finds one.
    /// </summary>
    public async Task<ComfyReleaseAsset> ResolveAsync(ComfyFlavour flavour, CancellationToken ct = default)
    {
        var wanted = AssetNameFor(flavour);

        using var resp = await Http.GetAsync($"{ReleasesApi}?per_page=10", ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not read ComfyUI's release list (HTTP {(int)resp.StatusCode}). "
                + "Check the internet connection, or install ComfyUI yourself and point the studio at it.");

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (JsonNode.Parse(body) is not JsonArray releases)
            throw new InvalidOperationException("ComfyUI's release list was not in the expected shape.");

        var tried = new List<string>();
        foreach (var node in releases)
        {
            if (node is not JsonObject release) continue;
            if (release["draft"]?.GetValue<bool>() == true) continue;
            if (release["prerelease"]?.GetValue<bool>() == true) continue;

            var tag = release["tag_name"]?.GetValue<string>() ?? "";
            tried.Add(tag);

            if (release["assets"] is not JsonArray assets) continue;
            foreach (var a in assets)
            {
                if (a is not JsonObject asset) continue;
                if (asset["name"]?.GetValue<string>() != wanted) continue;

                var url = asset["browser_download_url"]?.GetValue<string>();
                var size = asset["size"]?.GetValue<long>() ?? 0;
                // A zero size means the metadata is not telling us how big the
                // download is, and every progress bar and disk-space check
                // downstream depends on that number being real.
                if (string.IsNullOrEmpty(url) || size <= 0) continue;

                return new ComfyReleaseAsset(tag, wanted, url!, size);
            }
        }

        throw new InvalidOperationException(
            $"None of the last {tried.Count} ComfyUI releases ({string.Join(", ", tried.Take(3))}…) "
            + $"ship {wanted}. The build for this GPU may have been discontinued — "
            + "try another flavour in the engine card, or use the rented-GPU route.");
    }
}
