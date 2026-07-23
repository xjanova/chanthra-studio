using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>
/// State + commands for the Web Studio tab. The embedded WebView2 is a visual
/// owned by the view's code-behind (not data), so this VM holds the site/tool
/// selection, address-bar text, status line, and — for teach mode — the list
/// of captured steps. It raises <see cref="NavIntent"/> and
/// <see cref="TeachModeChanged"/> for the code-behind to act on.
///
/// Manual <c>SetProperty</c> properties are used deliberately — the
/// CommunityToolkit <c>[ObservableProperty]</c> source generator breaks the
/// WPF "_wpftmp" XAML compile pass in this project.
/// </summary>
public sealed class WebStudioViewModel : ObservableObject
{
    private readonly WebStudioService? _service;
    private readonly WebRecipeRepository? _recipes;

    public WebStudioService? Service => _service;
    public IReadOnlyList<WebSite> Sites { get; }
    public IReadOnlyList<string> StepKinds => RecipeStepKinds.All;

    /// <summary>
    /// Switching site needs a per-site WebView2 re-init — the user-data folder
    /// (and thus the login/cookie jar) is fixed once the browser is created,
    /// so loading a second site in the same control would write its cookies
    /// into the first site's profile. Slice A/B do not re-init yet, so the
    /// site picker stays read-only until the catalog has more than one site
    /// AND that teardown/recreate is wired.
    /// </summary>
    public bool CanSwitchSite => Sites.Count > 1;

    /// <summary>Steps captured by teach mode (editable before saving).</summary>
    public ObservableCollection<RecipeStepVm> CapturedSteps { get; } = new();
    public bool HasCapturedSteps => CapturedSteps.Count > 0;

    /// <summary>Raised when the VM wants the embedded browser to act.</summary>
    public event Action<WebNavIntent>? NavIntent;

    /// <summary>Raised when teach mode is toggled (code-behind injects/removes the overlay).</summary>
    public event Action<bool>? TeachModeChanged;

    // App.Current is null at design-time; the parameterless ctor chains through.
    public WebStudioViewModel() : this(null) { }

    public WebStudioViewModel(StudioContext? studio)
    {
        _service = studio?.WebStudio;
        _recipes = studio?.WebRecipes;
        Sites = _service?.Sites ?? WebSiteCatalog.All;
        _selectedSite = Sites.FirstOrDefault();
        _selectedTool = _selectedSite?.Tools.FirstOrDefault();
        _addressText = _selectedTool?.Url ?? _selectedSite?.HomeUrl ?? "";

        GoCommand = new RelayCommand(() => Navigate(AddressText));
        OpenHomeCommand = new RelayCommand(() => { if (SelectedSite is not null) Navigate(SelectedSite.HomeUrl); });
        BackCommand = new RelayCommand(() => NavIntent?.Invoke(WebNavIntent.Back()));
        ForwardCommand = new RelayCommand(() => NavIntent?.Invoke(WebNavIntent.Forward()));
        ReloadCommand = new RelayCommand(() => NavIntent?.Invoke(WebNavIntent.Reload()));
        SaveRecipeCommand = new RelayCommand(SaveRecipe);
        ClearStepsCommand = new RelayCommand(() => { CapturedSteps.Clear(); OnPropertyChanged(nameof(HasCapturedSteps)); });
        RemoveStepCommand = new RelayCommand<RecipeStepVm>(s => { if (s is not null) { CapturedSteps.Remove(s); OnPropertyChanged(nameof(HasCapturedSteps)); } });
        LoadRecipeCommand = new RelayCommand(LoadRecipe);
        RunCommand = new RelayCommand(() => { if (CanRun) RunRequested?.Invoke(); });
        StopCommand = new RelayCommand(() => CancelRequested?.Invoke());
        BrowseImageCommand = new RelayCommand(BrowseImage);
        ClearImageCommand = new RelayCommand(() => RunImagePath = null);
    }

    public IRelayCommand GoCommand { get; }
    public IRelayCommand OpenHomeCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand ReloadCommand { get; }
    public IRelayCommand SaveRecipeCommand { get; }
    public IRelayCommand ClearStepsCommand { get; }
    public IRelayCommand<RecipeStepVm> RemoveStepCommand { get; }
    public IRelayCommand LoadRecipeCommand { get; }
    public IRelayCommand RunCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand BrowseImageCommand { get; }
    public IRelayCommand ClearImageCommand { get; }

    private WebSite? _selectedSite;
    public WebSite? SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (!SetProperty(ref _selectedSite, value)) return;
            OnPropertyChanged(nameof(Tools));
            SelectedTool = value?.Tools.FirstOrDefault();
        }
    }

    public IReadOnlyList<WebTool> Tools => SelectedSite?.Tools ?? Array.Empty<WebTool>();

    private WebTool? _selectedTool;
    public WebTool? SelectedTool
    {
        get => _selectedTool;
        set { if (SetProperty(ref _selectedTool, value) && value is not null) Navigate(value.Url); }
    }

    private string _addressText = "";
    public string AddressText { get => _addressText; set => SetProperty(ref _addressText, value); }

    private string _statusText = "พร้อม";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    private bool _isBrowserReady;
    /// <summary>True once the WebView2 has initialised. The toolbar binds its
    /// IsEnabled to this so the nav buttons aren't live-but-dead during init
    /// or after a runtime-missing failure.</summary>
    public bool IsBrowserReady
    {
        get => _isBrowserReady;
        set { if (SetProperty(ref _isBrowserReady, value)) { OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(ToolbarEnabled)); } }
    }

    private bool _teachMode;
    public bool TeachMode
    {
        get => _teachMode;
        set
        {
            if (!SetProperty(ref _teachMode, value)) return;
            if (value) RunPanelOpen = false; // the two side panels share the column
            StatusText = value
                ? "โหมดสอน: คลิกองค์ประกอบบนหน้าเว็บเพื่อจับ selector"
                : "ออกจากโหมดสอน";
            TeachModeChanged?.Invoke(value);
        }
    }

    // ── run a recipe ───────────────────────────────────────────────────────────

    private bool _runPanelOpen;
    public bool RunPanelOpen
    {
        get => _runPanelOpen;
        set { if (SetProperty(ref _runPanelOpen, value) && value) TeachMode = false; }
    }

    private string _runPrompt = "";
    public string RunPrompt { get => _runPrompt; set => SetProperty(ref _runPrompt, value); }

    private string _runModel = "";
    public string RunModel { get => _runModel; set => SetProperty(ref _runModel, value); }

    private string? _runImagePath;
    public string? RunImagePath { get => _runImagePath; set => SetProperty(ref _runImagePath, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { if (SetProperty(ref _isRunning, value)) { OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(ToolbarEnabled)); } }
    }

    public bool CanRun => !IsRunning && IsBrowserReady;

    /// <summary>Toolbar/nav is locked while a run is in flight so a mid-run
    /// navigation can't wipe the injected runner or fire a step on the wrong page.</summary>
    public bool ToolbarEnabled => IsBrowserReady && !IsRunning;

    /// <summary>Raised when the user asks to run the current tool's recipe; the
    /// code-behind drives the live WebView2.</summary>
    public event Action? RunRequested;

    /// <summary>Raised when the user asks to stop an in-flight run.</summary>
    public event Action? CancelRequested;

    public WebRecipe? CurrentRecipe() =>
        (_recipes is null || SelectedSite is null || SelectedTool is null)
            ? null
            : _recipes.Find(SelectedSite.Id, SelectedTool.Id);

    public IReadOnlyDictionary<string, string> BuildRunInputs() => new Dictionary<string, string>
    {
        ["prompt"] = RunPrompt ?? "",
        ["image"] = RunImagePath ?? "",
        ["model"] = RunModel ?? "",
        ["aspect"] = "",
    };

    private void BrowseImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "รูปภาพ|*.png;*.jpg;*.jpeg;*.webp;*.gif|ทั้งหมด|*.*",
        };
        if (dlg.ShowDialog() == true) RunImagePath = dlg.FileName;
    }

    // ── teach capture (called from the code-behind on the UI thread) ──────────

    private const int MaxCapturedSteps = 300;

    public void AddCapturedStep(string css, string text, string kind, string label)
    {
        if (CapturedSteps.Count >= MaxCapturedSteps)
        {
            StatusText = "ขั้นตอนเยอะเกินไป — บันทึกหรือล้างก่อน";
            return;
        }

        // The page controls these strings; clamp length + strip control chars so
        // a spamming/hostile page can't bloat the recipe file or the UI list.
        css = Clamp(css, 512);
        text = Clamp(text, 256);
        label = Clamp(label, 256);
        kind = string.IsNullOrEmpty(kind) ? RecipeStepKinds.Click : kind;

        var value = kind switch
        {
            RecipeStepKinds.Fill => "{{prompt}}",
            RecipeStepKinds.Upload => "{{image}}",
            RecipeStepKinds.SelectModel => "{{model}}",
            _ => (string?)null,
        };
        CapturedSteps.Add(new RecipeStepVm
        {
            Kind = kind,
            Label = string.IsNullOrWhiteSpace(label) ? kind : label,
            Css = css,
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            Value = value,
            WaitForEnabled = kind == RecipeStepKinds.Click,
        });
        OnPropertyChanged(nameof(HasCapturedSteps));
        StatusText = $"จับได้: {label}";
    }

    private static string Clamp(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s)
        {
            if (c >= ' ' || c == '\t') sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }

    private void SaveRecipe()
    {
        if (_recipes is null || CapturedSteps.Count == 0)
        {
            StatusText = "ยังไม่มีขั้นตอนให้บันทึก";
            return;
        }
        try
        {
            var siteId = SelectedSite?.Id ?? "site";
            var toolId = SelectedTool?.Id ?? "tool";
            // Carry forward authored metadata (hand-written notes, version) so a
            // teach-mode overwrite doesn't wipe the curated seed recipe.
            var prior = _recipes.Find(siteId, toolId);
            var recipe = new WebRecipe
            {
                SiteId = siteId,
                ToolId = toolId,
                Name = $"{SelectedSite?.DisplayName} — {SelectedTool?.Name}".Trim(' ', '—'),
                HomeUrl = SelectedTool?.Url ?? SelectedSite?.HomeUrl ?? "",
                Version = prior?.Version ?? 1,
                Notes = prior?.Notes,
                Steps = CapturedSteps.Select(s => s.ToModel()).ToList(),
            };
            var path = _recipes.Save(recipe);
            StatusText = $"บันทึก recipe แล้ว ({recipe.Steps.Count} ขั้นตอน): {path}";
        }
        catch
        {
            StatusText = "บันทึก recipe ไม่สำเร็จ";
        }
    }

    private void LoadRecipe()
    {
        if (_recipes is null || SelectedSite is null || SelectedTool is null) return;
        var r = _recipes.Find(SelectedSite.Id, SelectedTool.Id);
        CapturedSteps.Clear();
        if (r is not null)
        {
            foreach (var s in r.Steps) CapturedSteps.Add(new RecipeStepVm(s));
            StatusText = $"โหลด recipe ({r.Steps.Count} ขั้นตอน) — แก้แล้วบันทึกทับได้";
        }
        else
        {
            StatusText = "ยังไม่มี recipe ของเครื่องมือนี้ — เริ่มสอนได้เลย";
        }
        OnPropertyChanged(nameof(HasCapturedSteps));
    }

    // ── navigation ────────────────────────────────────────────────────────────

    private void Navigate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;

        // Scheme allowlist: never let a typed/pasted/recipe-supplied
        // file:// / view-source: / data: URL load inside the WebView2 that
        // holds the site's authenticated cookies.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            StatusText = "ที่อยู่ไม่ถูกต้อง — รองรับเฉพาะ http / https";
            return;
        }

        AddressText = uri.ToString();
        NavIntent?.Invoke(WebNavIntent.Go(uri.ToString()));
    }

    /// <summary>Called by the view when the browser URL changes, to sync the
    /// address bar without re-triggering navigation.</summary>
    public void OnBrowserUrlChanged(string url)
    {
        _addressText = url;
        OnPropertyChanged(nameof(AddressText));
    }

    /// <summary>
    /// Strip query + fragment so OAuth/SSO/magic-link secrets (<c>?code=</c>,
    /// <c>#access_token=</c>, reset tokens…) never land in the visible status
    /// line. The address bar still shows the live URL (standard browser
    /// behaviour and needed to navigate), but the status text must not echo
    /// credentials that would survive on screen / in a screen-share.
    /// </summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var bare = uri.GetLeftPart(UriPartial.Path);
        var hasParams = !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment);
        return hasParams ? bare + " …(ซ่อนพารามิเตอร์)" : bare;
    }
}

/// <summary>A navigation request from the VM to the embedded browser.</summary>
public readonly record struct WebNavIntent(WebNavKind Kind, string? Url)
{
    public static WebNavIntent Go(string url) => new(WebNavKind.Go, url);
    public static WebNavIntent Back() => new(WebNavKind.Back, null);
    public static WebNavIntent Forward() => new(WebNavKind.Forward, null);
    public static WebNavIntent Reload() => new(WebNavKind.Reload, null);
}

public enum WebNavKind { Go, Back, Forward, Reload }
