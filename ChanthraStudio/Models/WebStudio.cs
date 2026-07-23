using System.Collections.Generic;
using System.Linq;

namespace ChanthraStudio.Models;

/// <summary>
/// A single automatable destination inside a web site (e.g. Magnific's
/// "Image Generator" tool). The <see cref="Url"/> is opened in the embedded
/// WebView2 of the Web Studio tab; a matching recipe under
/// <c>Assets/WebRecipes</c> drives it (added in a later slice).
/// </summary>
public sealed record WebTool(string Id, string Name, string Url, string? Note = null)
{
    // A global ComboBox style overrides DisplayMemberPath, so make the record's
    // own text its friendly name (what the picker shows).
    public override string ToString() => Name;
}

/// <summary>
/// A web service the user drives through its <b>website</b> rather than an
/// API — typically because the "unlimited" subscription is only honoured in
/// the browser. Each site gets its own persistent WebView2 profile so the
/// login survives restarts (we keep the browser's own encrypted cookie
/// store — never the password).
/// </summary>
public sealed record WebSite(
    string Id,
    string DisplayName,
    string HomeUrl,
    IReadOnlyList<WebTool> Tools,
    string? Note = null)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Catalog of supported web-automation sites. Magnific is the pilot; its
/// tools were verified live against the signed-in account on 2026-06-28.
/// More sites/tools are added as their recipes are captured via teach mode.
/// </summary>
public static class WebSiteCatalog
{
    public static IReadOnlyList<WebSite> All { get; } = new[]
    {
        new WebSite(
            Id: "magnific",
            DisplayName: "Magnific (Freepik suite)",
            HomeUrl: "https://www.magnific.com/app",
            Tools: new[]
            {
                new WebTool(
                    "image-generator",
                    "Image Generator",
                    "https://www.magnific.com/app/ai-image-generator",
                    "text→image + reference images · models: Flux/Mystic/Seedream/Grok/…"),
            },
            Note: "ใช้สิทธิ์ unlimited ผ่านเว็บ · ล็อกอินครั้งเดียว โปรไฟล์เก็บถาวร"),
    };

    public static WebSite? FindById(string id) => All.FirstOrDefault(s => s.Id == id);
}
