using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChanthraStudio.Services;
using ChanthraStudio.Services.Gpu;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

/// <summary>One rentable profile, as the picker shows it.</summary>
public sealed class GpuProfileRow : ObservableObject
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string SpecLabel { get; init; } = "";
    public string WorkflowsLabel { get; init; } = "";
    public bool RequiresHfToken { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// The GPU control room: key setup, spending limits, what is running right
/// now and what it has cost.
///
/// Every figure here is read from the worker rows, never estimated. The one
/// number that matters most is the overhead split — a rented card bills for
/// its warm-up and its idle minutes exactly like it bills for rendering, and
/// a panel that only showed render cost would flatter the feature into
/// looking cheaper than the invoice.
/// </summary>
public sealed class GpuViewModel : ObservableObject, IDisposable
{
    private readonly StudioContext? _ctx;
    private readonly GpuWorkerService? _gpu;
    private readonly System.Windows.Threading.DispatcherTimer? _timer;
    private GpuGuardrails _guard = new();
    private bool _loading = true;

    public ObservableCollection<GpuWorker> LiveWorkers { get; } = new();
    public ObservableCollection<GpuWorker> RecentWorkers { get; } = new();
    public ObservableCollection<GpuProfileRow> Profiles { get; } = new();
    public ObservableCollection<GpuOffer> MarketOffers { get; } = new();

    public IAsyncRelayCommand VerifyKeyCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand BrowseMarketCommand { get; }
    public IAsyncRelayCommand TerminateAllCommand { get; }
    public IAsyncRelayCommand SweepOrphansCommand { get; }
    public IAsyncRelayCommand<GpuWorker> TerminateCommand { get; }
    public IRelayCommand<string> SelectProfileCommand { get; }
    public IRelayCommand OpenConsoleCommand { get; }
    public IRelayCommand OpenProfilesFileCommand { get; }

    /// <summary>Design-time ctor — the XAML designer must not bootstrap SQLite.</summary>
    public GpuViewModel() : this(null) { }

    public GpuViewModel(StudioContext? ctx)
    {
        _ctx = ctx;
        _gpu = ctx?.GpuWorkers;

        VerifyKeyCommand = new AsyncRelayCommand(VerifyKeyAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        BrowseMarketCommand = new AsyncRelayCommand(BrowseMarketAsync);
        TerminateAllCommand = new AsyncRelayCommand(TerminateAllAsync);
        SweepOrphansCommand = new AsyncRelayCommand(SweepOrphansAsync);
        TerminateCommand = new AsyncRelayCommand<GpuWorker>(TerminateOneAsync);
        SelectProfileCommand = new RelayCommand<string>(SelectProfile);
        OpenConsoleCommand = new RelayCommand(() => OpenUrl("https://dash.simplepod.ai/"));
        OpenProfilesFileCommand = new RelayCommand(OpenProfilesFile);

        LoadProfiles();

        if (_ctx is null || _gpu is null) { _loading = false; return; }

        _guard = GpuGuardrails.Load(_ctx.Settings);
        _apiKeyDraft = _ctx.Settings["simplepod"];
        _hfTokenDraft = _ctx.Settings["huggingface"];
        SelectProfile(_guard.ProfileKey);
        _loading = false;

        _gpu.WorkersChanged += OnWorkersChanged;

        // A rented machine's cost changes every second it stays up, so the
        // meter has to move on its own rather than only on events.
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _timer.Tick += (_, _) => Reload();
        _timer.Start();

        Reload();
    }

    // ================================================================= setup

    private string _apiKeyDraft = "";
    public string ApiKeyDraft
    {
        get => _apiKeyDraft;
        set { if (SetProperty(ref _apiKeyDraft, value)) OnPropertyChanged(nameof(HasApiKey)); }
    }

    private string _hfTokenDraft = "";
    public string HfTokenDraft
    {
        get => _hfTokenDraft;
        set => SetProperty(ref _hfTokenDraft, value);
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKeyDraft);

    private string _balanceLabel = "—";
    public string BalanceLabel { get => _balanceLabel; private set => SetProperty(ref _balanceLabel, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    /// <summary>"" | ok | warn | err | busy</summary>
    private string _statusKind = "";
    public string StatusKind
    {
        get => _statusKind;
        private set { if (SetProperty(ref _statusKind, value)) OnPropertyChanged(nameof(StatusBrushKey)); }
    }

    /// <summary>Brush resource name for the status line. Resolved via the
    /// ResourceLookup converter rather than StatusToBrush, which speaks the
    /// ShotStatus vocabulary and would render every one of these grey.</summary>
    public string StatusBrushKey => _statusKind switch
    {
        "ok" => "BrushOk",
        "warn" => "BrushWarn",
        "err" => "BrushErr",
        "busy" => "BrushGold",
        _ => "BrushText3",
    };

    // ============================================================ guardrails

    public bool Enabled
    {
        get => _guard.Enabled;
        set { _guard.Enabled = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(ArmedLabel)); }
    }

    public string ArmedLabel => Enabled
        ? "Armed — the Composer's \"Rented GPU\" route may rent machines."
        : "Off — nothing will be rented. Existing machines are still watched and released.";

    public bool TerminateOnExit
    {
        get => _guard.TerminateOnExit;
        set { _guard.TerminateOnExit = value; Persist(); OnPropertyChanged(); }
    }

    public string MaxPricePerHourUsd
    {
        get => Num(_guard.MaxPricePerHourUsd);
        set { if (TryMoney(value, out var v)) { _guard.MaxPricePerHourUsd = v; Persist(); } OnPropertyChanged(); }
    }

    public string DailyBudgetUsd
    {
        get => Num(_guard.DailyBudgetUsd);
        set { if (TryMoney(value, out var v)) { _guard.DailyBudgetUsd = v; Persist(); } OnPropertyChanged(); RefreshKpis(); }
    }

    public string MaxConcurrentWorkers
    {
        get => _guard.MaxConcurrentWorkers.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 1, 8, out var v)) { _guard.MaxConcurrentWorkers = v; Persist(); } OnPropertyChanged(); }
    }

    public string IdleTimeoutMinutes
    {
        get => _guard.IdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 1, 240, out var v)) { _guard.IdleTimeoutMinutes = v; Persist(); } OnPropertyChanged(); }
    }

    public string MaxLifetimeMinutes
    {
        get => _guard.MaxLifetimeMinutes.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 5, 1440, out var v)) { _guard.MaxLifetimeMinutes = v; Persist(); } OnPropertyChanged(); }
    }

    public string WarmupTimeoutMinutes
    {
        get => _guard.WarmupTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 5, 240, out var v)) { _guard.WarmupTimeoutMinutes = v; Persist(); } OnPropertyChanged(); }
    }

    public string MinDownloadMbps
    {
        get => _guard.MinDownloadMbps.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 0, 10000, out var v)) { _guard.MinDownloadMbps = v; Persist(); } OnPropertyChanged(); }
    }

    /// <summary>
    /// Not a limit — the length of a typical run, used to rank offers by what
    /// the whole job costs instead of by hourly rate. Lower it and machines
    /// with fast links win (warm-up dominates a short session); raise it and
    /// the cheap hourly rate wins (warm-up amortises away).
    /// </summary>
    public string TypicalSessionMinutes
    {
        get => _guard.TypicalSessionMinutes.ToString(CultureInfo.InvariantCulture);
        set { if (TryInt(value, 1, 1440, out var v)) { _guard.TypicalSessionMinutes = v; Persist(); } OnPropertyChanged(); }
    }

    private void Persist()
    {
        if (_loading || _ctx is null) return;
        try { _guard.SaveTo(_ctx.Settings); }
        catch (Exception ex) { Say("could not save settings: " + ex.Message, "err"); }
    }

    // =============================================================== metrics

    private string _spentTodayLabel = "$0.00";
    public string SpentTodayLabel { get => _spentTodayLabel; private set => SetProperty(ref _spentTodayLabel, value); }

    private string _budgetRemainingLabel = "—";
    public string BudgetRemainingLabel { get => _budgetRemainingLabel; private set => SetProperty(ref _budgetRemainingLabel, value); }

    private double _budgetFraction;
    public double BudgetFraction { get => _budgetFraction; private set => SetProperty(ref _budgetFraction, value); }

    private string _renderCostLabel = "$0.00";
    public string RenderCostLabel { get => _renderCostLabel; private set => SetProperty(ref _renderCostLabel, value); }

    private string _totalCostLabel = "$0.00";
    public string TotalCostLabel { get => _totalCostLabel; private set => SetProperty(ref _totalCostLabel, value); }

    private string _overheadLabel = "—";
    public string OverheadLabel { get => _overheadLabel; private set => SetProperty(ref _overheadLabel, value); }

    private double _utilisationFraction;
    public double UtilisationFraction { get => _utilisationFraction; private set => SetProperty(ref _utilisationFraction, value); }

    private string _utilisationLabel = "—";
    public string UtilisationLabel { get => _utilisationLabel; private set => SetProperty(ref _utilisationLabel, value); }

    private string _overheadHint = "";
    public string OverheadHint { get => _overheadHint; private set => SetProperty(ref _overheadHint, value); }

    private bool _hasLiveWorkers;
    public bool HasLiveWorkers { get => _hasLiveWorkers; private set => SetProperty(ref _hasLiveWorkers, value); }

    private bool _hasHistory;
    public bool HasHistory { get => _hasHistory; private set => SetProperty(ref _hasHistory, value); }

    private string _lastActivity = "";
    public string LastActivity { get => _lastActivity; private set => SetProperty(ref _lastActivity, value); }

    // ================================================================ actions

    private void OnWorkersChanged()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Reload();
        else d.BeginInvoke(new Action(Reload));
    }

    /// <summary>Re-read worker rows and recompute every figure on the page.</summary>
    public void Reload()
    {
        if (_gpu is null) return;
        try
        {
            var live = _gpu.Repository.Live();
            SyncList(LiveWorkers, live);
            HasLiveWorkers = live.Count > 0;

            var recent = _gpu.Repository.Recent(30);
            SyncList(RecentWorkers, recent);
            HasHistory = recent.Count > 0;

            LastActivity = _gpu.LastActivity ?? "";
            RefreshKpis(recent);
        }
        catch (Exception ex)
        {
            Say("could not read worker history: " + ex.Message, "err");
        }
    }

    private void RefreshKpis(System.Collections.Generic.IReadOnlyList<GpuWorker>? recent = null)
    {
        if (_gpu is null) return;
        recent ??= _gpu.Repository.Recent(30);

        var spentToday = _gpu.Repository.SpendToday();
        SpentTodayLabel = Money(spentToday);

        var budget = _guard.DailyBudgetUsd;
        if (budget > 0)
        {
            var remaining = Math.Max(0m, budget - spentToday);
            BudgetRemainingLabel = $"{Money(remaining)} of {Money(budget)} left today";
            BudgetFraction = Math.Clamp((double)(spentToday / budget), 0, 1);
        }
        else
        {
            BudgetRemainingLabel = "no daily cap set";
            BudgetFraction = 0;
        }

        var total = recent.Sum(w => w.TotalCostUsd);
        var render = recent.Sum(w => w.RenderCostUsd);
        TotalCostLabel = Money(total);
        RenderCostLabel = Money(render);

        if (total > 0)
        {
            var util = (double)(render / total);
            UtilisationFraction = Math.Clamp(util, 0, 1);
            UtilisationLabel = (util * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            OverheadLabel = ((1 - util) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            OverheadHint = util < 0.25
                ? "Most of the bill is warm-up and idle time. Longer batches per rental, or a lighter profile, would spend better."
                : "Warm-up and idle time as a share of the bill.";
        }
        else
        {
            UtilisationFraction = 0;
            UtilisationLabel = "—";
            OverheadLabel = "—";
            OverheadHint = "Nothing rented yet.";
        }
    }

    private async Task VerifyKeyAsync()
    {
        if (_ctx is null) return;
        if (string.IsNullOrWhiteSpace(ApiKeyDraft))
        {
            Say("Paste a SimplePod API key first.", "warn");
            return;
        }

        Say("checking the key…", "busy");
        try
        {
            var provider = GpuRentalRegistry.Resolve(_guard.ProviderId, _guard.ApiBase);
            var balance = await provider.GetBalanceAsync(ApiKeyDraft.Trim());

            // Only persist a key the vendor actually accepted — storing a bad
            // one makes every later failure look like a different problem.
            _ctx.Settings.SetApiKey("simplepod", ApiKeyDraft.Trim());
            if (!string.IsNullOrWhiteSpace(HfTokenDraft))
                _ctx.Settings.SetApiKey("huggingface", HfTokenDraft.Trim());
            _ctx.Settings.Save();

            BalanceLabel = Money(balance);
            OnPropertyChanged(nameof(HasApiKey));
            Say(balance <= 0
                ? "Key works, but the account balance is zero — top it up before renting."
                : $"Key saved. Balance {Money(balance)}.",
                balance <= 0 ? "warn" : "ok");
        }
        catch (Exception ex)
        {
            Say(ex.Message, "err");
        }
    }

    private async Task RefreshAsync()
    {
        if (_gpu is null) return;
        Say("refreshing…", "busy");
        try
        {
            await _gpu.TickAsync();
            Reload();
            Say("up to date.", "ok");
        }
        catch (Exception ex) { Say(ex.Message, "err"); }
    }

    private async Task BrowseMarketAsync()
    {
        if (_gpu is null || _ctx is null) return;
        var profile = GpuModelCatalog.Find(_guard.ProfileKey);
        if (profile is null) { Say("Pick a profile first.", "warn"); return; }
        if (!_gpu.HasApiKey) { Say("Save a working API key first.", "warn"); return; }

        Say("searching the market…", "busy");
        try
        {
            var provider = GpuRentalRegistry.Resolve(_guard.ProviderId, _guard.ApiBase);
            var offers = await provider.SearchMarketAsync(_gpu.ApiKey, _guard.ToFilter(profile));
            MarketOffers.Clear();
            foreach (var o in offers.Take(12)) MarketOffers.Add(o);
            Say(offers.Count == 0
                ? $"No machine matches {profile.DisplayName} under ${_guard.MaxPricePerHourUsd:0.00}/hr. Raise the ceiling or lower the speed floor."
                : $"{offers.Count} machine(s) available — cheapest ${offers[0].PricePerHourUsd:0.00}/hr.",
                offers.Count == 0 ? "warn" : "ok");
        }
        catch (Exception ex) { Say(ex.Message, "err"); }
    }

    private async Task TerminateOneAsync(GpuWorker? w)
    {
        if (_gpu is null || w is null) return;

        // Both of these throw away money that has already been spent — a
        // half-finished 16 GB download costs exactly as much as a finished
        // one. Neither should happen on a single stray click.
        var warning = w.Status switch
        {
            GpuWorkerStatus.Busy =>
                $"{w.Name} is rendering right now.\n\nReleasing it throws that render away — " +
                $"you have already paid the {w.CostLabel} it has used.",
            GpuWorkerStatus.Warming =>
                $"{w.Name} is still setting itself up ({w.StageLabel}).\n\n" +
                $"The {w.CostLabel} spent so far is gone either way, and starting again " +
                "means paying for the whole warm-up a second time.",
            _ => null,
        };
        if (warning is not null && !Confirm(warning + "\n\nRelease it anyway?")) return;

        Say($"releasing {w.Name}…", "busy");
        try
        {
            await _gpu.TerminateAsync(w, "released from the GPU panel");
            Reload();
            Say($"{w.Name} released.", "ok");
        }
        catch (Exception ex) { Say(ex.Message, "err"); }
    }

    private async Task TerminateAllAsync()
    {
        if (_gpu is null) return;
        var live = _gpu.Repository.Live();
        if (live.Count == 0) { Say("Nothing is running.", "warn"); return; }
        var busy = live.Count(w => w.Status == GpuWorkerStatus.Busy);
        var warn = busy > 0 ? $"\n\n{busy} of them is rendering — that work will be lost." : "";
        if (!Confirm($"Release {live.Count} machine(s) and stop the meter?{warn}")) return;

        Say("releasing…", "busy");
        try
        {
            var n = await _gpu.TerminateAllAsync("released from the GPU panel");
            Reload();
            Say($"released {n} machine(s).", "ok");
        }
        catch (Exception ex) { Say(ex.Message, "err"); }
    }

    private async Task SweepOrphansAsync()
    {
        if (_gpu is null) return;
        if (!_gpu.HasApiKey) { Say("Save a working API key first.", "warn"); return; }
        Say("looking for stray machines…", "busy");
        try
        {
            var n = await _gpu.SweepOrphansAsync();
            Reload();
            Say(n == 0
                ? "No stray machines found. Anything not named chanthra-… is left alone."
                : $"Released {n} stray machine(s).", "ok");
        }
        catch (Exception ex) { Say(ex.Message, "err"); }
    }

    private void SelectProfile(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var row = Profiles.FirstOrDefault(p => p.Key == key);
        if (row is null) return;
        foreach (var p in Profiles) p.IsSelected = ReferenceEquals(p, row);
        _guard.ProfileKey = row.Key;
        Persist();
        OnPropertyChanged(nameof(SelectedProfileHint));
        MarketOffers.Clear();
    }

    public string SelectedProfileHint
    {
        get
        {
            var p = GpuModelCatalog.Find(_guard.ProfileKey);
            if (p is null) return "";
            var eta = p.EstimatedWarmupMinutes(_guard.MinDownloadMbps);
            return $"{p.TotalWeightsGb:0.#} GB of weights · needs ≥{p.MinVramGb} GB VRAM and ≥{p.RequiredDiskGb} GB disk · " +
                   $"about {eta} min of paid warm-up before the first frame";
        }
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in GpuModelCatalog.All)
        {
            Profiles.Add(new GpuProfileRow
            {
                Key = p.Key,
                DisplayName = p.DisplayName,
                Description = p.Description,
                SpecLabel = $"≥{p.MinVramGb} GB VRAM · {p.TotalWeightsGb:0.#} GB weights",
                WorkflowsLabel = p.Workflows.Count == 0 ? "" : "runs: " + string.Join(", ", p.Workflows),
                RequiresHfToken = p.RequiresHfToken,
            });
        }
    }

    private void OpenProfilesFile()
    {
        try
        {
            if (!System.IO.File.Exists(GpuModelCatalog.OverridesPath))
                GpuModelCatalog.ExportBuiltInsTo(GpuModelCatalog.OverridesPath);
            GpuModelCatalog.Invalidate();
            OpenUrl(GpuModelCatalog.OverridesPath);
            Say("Edit the file, then reopen this panel to pick up changes.", "ok");
        }
        catch (Exception ex) { Say(ex.Message, "err"); }
    }

    // ================================================================= helpers

    private static void SyncList(ObservableCollection<GpuWorker> target,
                                 System.Collections.Generic.IReadOnlyList<GpuWorker> source)
    {
        target.Clear();
        foreach (var w in source) target.Add(w);
    }

    private void Say(string message, string kind)
    {
        StatusMessage = message;
        StatusKind = kind;
    }

    private static bool Confirm(string message)
        => System.Windows.MessageBox.Show(message, "Chanthra Studio · rented GPU",
               System.Windows.MessageBoxButton.OKCancel,
               System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    private static string Money(decimal v) => "$" + v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Num(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryMoney(string? s, out decimal v)
        => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0;

    private static bool TryInt(string? s, int min, int max, out int v)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return false;
        v = Math.Clamp(v, min, max);
        return true;
    }

    private static void OpenUrl(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { /* no browser / no association — not worth an error dialog */ }
    }

    public void Dispose()
    {
        _timer?.Stop();
        if (_gpu is not null) _gpu.WorkersChanged -= OnWorkersChanged;
    }
}
