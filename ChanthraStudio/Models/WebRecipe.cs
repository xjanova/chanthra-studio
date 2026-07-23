using System.Collections.Generic;

namespace ChanthraStudio.Models;

/// <summary>
/// A saved automation recipe for one web tool: the ordered steps that drive
/// its page (fill prompt, upload image, pick model, click Generate, wait,
/// download). Authored by teach mode and executed by the recipe runner.
///
/// Selectors are stored flattened on each step (<see cref="RecipeStep.Css"/>
/// + optional <see cref="RecipeStep.Text"/>) so the JSON stays simple and the
/// teach recorder can write it directly. Magnific uses random GUID ids on some
/// controls, so a text match is often the stable anchor — that is why every
/// step carries both a CSS query and an optional visible-text filter.
/// </summary>
public sealed class WebRecipe
{
    public string SiteId { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string Name { get; set; } = "";
    public string HomeUrl { get; set; } = "";
    public int Version { get; set; } = 1;
    public List<string>? Notes { get; set; }
    public List<RecipeStep> Steps { get; set; } = new();
}

/// <summary>
/// One step. <see cref="Css"/> selects candidate elements; if <see cref="Text"/>
/// is set, the runner narrows to the candidate whose visible text contains it.
/// <see cref="Value"/> holds a literal or a placeholder (<c>{{prompt}}</c>,
/// <c>{{image}}</c>, <c>{{model}}</c>, <c>{{aspect}}</c>).
/// </summary>
public sealed class RecipeStep
{
    public string Kind { get; set; } = "Click";
    public string Label { get; set; } = "";
    public string? Css { get; set; }
    public string? Text { get; set; }
    public string? Value { get; set; }
    public bool Optional { get; set; }
    public bool WaitForEnabled { get; set; }
    public int? WaitMs { get; set; }
}

/// <summary>The step kinds the teach recorder and runner understand.</summary>
public static class RecipeStepKinds
{
    public const string Fill = "Fill";
    public const string Upload = "Upload";
    public const string SelectModel = "SelectModel";
    public const string SelectOption = "SelectOption";
    public const string Click = "Click";
    public const string WaitFor = "WaitFor";
    public const string Download = "Download";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Fill, Upload, SelectModel, SelectOption, Click, WaitFor, Download,
    };
}
