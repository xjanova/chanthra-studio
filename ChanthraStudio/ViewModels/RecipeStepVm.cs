using ChanthraStudio.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// Editable view of one <see cref="RecipeStep"/> for the teach panel. The user
/// can adjust the auto-guessed Kind/Label/Value before saving the recipe.
/// Manual SetProperty (the source generators are banned in this project).
/// </summary>
public sealed class RecipeStepVm : ObservableObject
{
    public RecipeStepVm() { }

    public RecipeStepVm(RecipeStep s)
    {
        _kind = string.IsNullOrEmpty(s.Kind) ? RecipeStepKinds.Click : s.Kind;
        _label = s.Label;
        _css = s.Css;
        _text = s.Text;
        _value = s.Value;
        _optional = s.Optional;
        _waitForEnabled = s.WaitForEnabled;
        _waitMs = s.WaitMs;
    }

    private string _kind = RecipeStepKinds.Click;
    public string Kind { get => _kind; set => SetProperty(ref _kind, value); }

    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }

    private string? _css;
    public string? Css { get => _css; set => SetProperty(ref _css, value); }

    private string? _text;
    public string? Text { get => _text; set => SetProperty(ref _text, value); }

    private string? _value;
    public string? Value { get => _value; set => SetProperty(ref _value, value); }

    private bool _optional;
    public bool Optional { get => _optional; set => SetProperty(ref _optional, value); }

    private bool _waitForEnabled;
    public bool WaitForEnabled { get => _waitForEnabled; set => SetProperty(ref _waitForEnabled, value); }

    private int? _waitMs;
    public int? WaitMs { get => _waitMs; set => SetProperty(ref _waitMs, value); }

    public RecipeStep ToModel() => new()
    {
        Kind = Kind,
        Label = Label,
        Css = string.IsNullOrWhiteSpace(Css) ? null : Css,
        Text = string.IsNullOrWhiteSpace(Text) ? null : Text,
        Value = string.IsNullOrWhiteSpace(Value) ? null : Value,
        Optional = Optional,
        WaitForEnabled = WaitForEnabled,
        WaitMs = WaitMs,
    };
}
