using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ChanthraStudio.Models;
using Microsoft.Web.WebView2.Core;

namespace ChanthraStudio.Services;

/// <summary>
/// Owns the embedded-browser side of the Web Studio tab.
///
/// Each site gets its own persistent WebView2 <b>user-data folder</b> under
/// <c>{AppPaths.Root}/WebProfiles/{siteId}</c>, so a login done once survives
/// restarts: we store the browser's own (DPAPI-encrypted) cookie store, never
/// the password. One <see cref="CoreWebView2Environment"/> is created per
/// profile folder and cached for the app's lifetime — multiple WebView2
/// controls may share a single environment.
///
/// This mirrors the proven pattern shipped in the BrainX dashboard
/// (hidden probe + visible login window sharing one UserDataFolder); note
/// that Chrome 127+ locks its live Cookies file, so a WebView2 that owns its
/// own profile is the correct approach rather than reading the user's Chrome.
/// </summary>
public sealed class WebStudioService
{
    private readonly Dictionary<string, CoreWebView2Environment> _envs = new();

    public IReadOnlyList<WebSite> Sites => WebSiteCatalog.All;

    /// <summary>Per-site WebView2 user-data folder (login + cookies live here).</summary>
    public string ProfileFolder(string siteId)
    {
        var p = Path.Combine(AppPaths.Root, "WebProfiles", siteId);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Where intercepted browser downloads are redirected.</summary>
    public string DownloadsFolder
    {
        get
        {
            var p = Path.Combine(AppPaths.Root, "WebDownloads");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    /// <summary>
    /// Lazily create (and cache) the WebView2 environment rooted at the
    /// site's persistent profile folder. Keyed by folder so each site stays
    /// isolated. <c>browserExecutableFolder: null</c> uses the installed
    /// Microsoft Edge WebView2 runtime (present on Windows 11 by default).
    /// </summary>
    public async Task<CoreWebView2Environment> GetEnvironmentAsync(string siteId)
    {
        var folder = ProfileFolder(siteId);
        if (_envs.TryGetValue(folder, out var existing))
            return existing;

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: folder,
            options: new CoreWebView2EnvironmentOptions());
        _envs[folder] = env;
        return env;
    }
}
