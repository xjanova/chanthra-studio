using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ChanthraStudio.Models;
using ChanthraStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChanthraStudio.ViewModels;

public sealed class UsageBucket : ObservableObject
{
    public string Label { get; init; } = "";
    public string Sub { get; init; } = "";
    public string AmountLabel { get; init; } = "฿0.00";
    public long CallCount { get; init; }
}

public sealed class UsageBreakdown : ObservableObject
{
    public string Label { get; init; } = "";
    public string AmountLabel { get; init; } = "฿0.00";
    public double WidthFraction { get; init; }
    public long CallCount { get; init; }
}

public sealed class UsageViewModel : ObservableObject
{
    private readonly StudioContext? _ctx;

    public ObservableCollection<UsageBucket> Buckets { get; } = new();
    public ObservableCollection<UsageBreakdown> ByProvider { get; } = new();
    public ObservableCollection<UsageBreakdown> ByModel { get; } = new();
    public ObservableCollection<UsageEvent> RecentEvents { get; } = new();

    private double _fxRate;
    public double FxRate
    {
        get => _fxRate;
        set
        {
            if (SetProperty(ref _fxRate, value) && _ctx is not null)
                _ctx.Tracker.CurrentFxRate = value;
        }
    }

    public string FxRateLabel => $"1 USD = ฿{_fxRate:N2}";

    public IRelayCommand RefreshCommand { get; }

    public UsageViewModel() : this(null) { }

    public UsageViewModel(StudioContext? ctx)
    {
        _ctx = ctx;
        RefreshCommand = new RelayCommand(Refresh);
        if (_ctx is not null)
        {
            _fxRate = _ctx.Tracker.CurrentFxRate;
            _ctx.Tracker.Recorded += _ => Refresh();
            Refresh();
        }
        else
        {
            // Design-time placeholders so the XAML preview is alive.
            _fxRate = 36;
            Buckets.Add(new UsageBucket { Label = "TODAY", AmountLabel = "฿24.30", Sub = "12 calls", CallCount = 12 });
            Buckets.Add(new UsageBucket { Label = "WEEK", AmountLabel = "฿182.50", Sub = "78 calls", CallCount = 78 });
            Buckets.Add(new UsageBucket { Label = "MONTH", AmountLabel = "฿612.40", Sub = "260 calls", CallCount = 260 });
            Buckets.Add(new UsageBucket { Label = "ALL TIME", AmountLabel = "฿1,420.10", Sub = "612 calls", CallCount = 612 });
            ByProvider.Add(new UsageBreakdown { Label = "openai", AmountLabel = "฿820.40", WidthFraction = 0.6, CallCount = 320 });
            ByProvider.Add(new UsageBreakdown { Label = "anthropic", AmountLabel = "฿320.10", WidthFraction = 0.23, CallCount = 140 });
            ByProvider.Add(new UsageBreakdown { Label = "replicate", AmountLabel = "฿180.00", WidthFraction = 0.13, CallCount = 80 });
            ByProvider.Add(new UsageBreakdown { Label = "elevenlabs", AmountLabel = "฿99.60", WidthFraction = 0.07, CallCount = 72 });
        }
    }

    private void Refresh()
    {
        if (_ctx is null) return;
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui is not null && !ui.CheckAccess()) { ui.Invoke(Refresh); return; }

        var nowLocal = DateTimeOffset.Now;
        var startOfDay = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset).ToUniversalTime();
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var monthStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Offset).ToUniversalTime();
        var allTime = DateTimeOffset.FromUnixTimeSeconds(0);

        var d = _ctx.Usage.Total(startOfDay);
        var w = _ctx.Usage.Total(weekAgo);
        var m = _ctx.Usage.Total(monthStart);
        var a = _ctx.Usage.Total(allTime);

        Buckets.Clear();
        Buckets.Add(new UsageBucket { Label = "TODAY",    AmountLabel = $"฿{d.Thb:N2}", Sub = $"{d.Calls} calls", CallCount = d.Calls });
        Buckets.Add(new UsageBucket { Label = "7 DAYS",   AmountLabel = $"฿{w.Thb:N2}", Sub = $"{w.Calls} calls", CallCount = w.Calls });
        Buckets.Add(new UsageBucket { Label = "MONTH",    AmountLabel = $"฿{m.Thb:N2}", Sub = $"{m.Calls} calls", CallCount = m.Calls });
        Buckets.Add(new UsageBucket { Label = "ALL TIME", AmountLabel = $"฿{a.Thb:N2}", Sub = $"{a.Calls} calls", CallCount = a.Calls });

        // Provider breakdown — last 30 days
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var byProv = _ctx.Usage.TotalByProvider(since);
        var max = byProv.Count == 0 ? 1.0 : byProv.Max(b => b.Thb);
        ByProvider.Clear();
        foreach (var (provider, thb, calls) in byProv)
        {
            ByProvider.Add(new UsageBreakdown
            {
                Label = provider,
                AmountLabel = $"฿{thb:N2}",
                WidthFraction = max <= 0 ? 0 : thb / max,
                CallCount = calls,
            });
        }

        // Model breakdown — top 12 of last 30 days
        var byModel = _ctx.Usage.TotalByModel(since);
        var maxM = byModel.Count == 0 ? 1.0 : byModel.Max(b => b.Thb);
        ByModel.Clear();
        foreach (var (model, thb, calls) in byModel)
        {
            ByModel.Add(new UsageBreakdown
            {
                Label = model,
                AmountLabel = $"฿{thb:N2}",
                WidthFraction = maxM <= 0 ? 0 : thb / maxM,
                CallCount = calls,
            });
        }

        // Recent events (last 50)
        RecentEvents.Clear();
        foreach (var e in _ctx.Usage.Recent(50)) RecentEvents.Add(e);

        OnPropertyChanged(nameof(FxRateLabel));
    }
}
