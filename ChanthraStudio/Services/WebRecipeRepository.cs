using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Loads and saves <see cref="WebRecipe"/> JSON files. Recipes live next to
/// the .exe under <c>Assets/WebRecipes</c> (shipped as Content, editable on
/// disk) so teach mode can write/repair them at runtime — same pattern as the
/// ComfyUI WorkflowRepository.
/// </summary>
public sealed class WebRecipeRepository
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string RecipesFolder
    {
        get
        {
            var p = Path.Combine(AppPaths.Root, "Assets", "WebRecipes");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public IReadOnlyList<WebRecipe> LoadAll()
    {
        var list = new List<WebRecipe>();
        foreach (var f in Directory.EnumerateFiles(RecipesFolder, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<WebRecipe>(File.ReadAllText(f), ReadOpts);
                if (r is not null && !string.IsNullOrEmpty(r.SiteId)) list.Add(r);
            }
            catch { /* skip malformed recipe files */ }
        }
        return list;
    }

    public WebRecipe? Find(string siteId, string toolId) =>
        LoadAll().FirstOrDefault(r => r.SiteId == siteId && r.ToolId == toolId);

    public string Save(WebRecipe recipe)
    {
        var name = $"{Sanitize(recipe.SiteId)}-{Sanitize(recipe.ToolId)}.json";
        var path = Path.Combine(RecipesFolder, name);
        File.WriteAllText(path, JsonSerializer.Serialize(recipe, WriteOpts));
        return path;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "recipe" : clean;
    }
}
