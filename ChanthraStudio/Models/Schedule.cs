using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

public enum ScheduleKind
{
    /// <summary>Spec is a comma-separated list of HH:mm slots ("08:00,12:00,18:00").</summary>
    DailySlots,
    /// <summary>Spec is an integer number of minutes between fires ("60").</summary>
    Interval,
}

/// <summary>
/// One recurring auto-generation job. Maps 1:1 to a row in the
/// <c>schedules</c> table (schema v3). The ScheduleService scans rows
/// where <c>is_enabled=1</c> and <c>next_fire_at &lt;= now()</c>, fires
/// the generation, then re-computes <see cref="NextFireAt"/>.
/// </summary>
public sealed class Schedule : ObservableObject
{
    public long Id { get; set; }

    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _promptTemplate = "";
    public string PromptTemplate { get => _promptTemplate; set => SetProperty(ref _promptTemplate, value); }

    private string _negativePrompt = "";
    public string NegativePrompt { get => _negativePrompt; set => SetProperty(ref _negativePrompt, value); }

    private string _workflow = "";
    public string Workflow { get => _workflow; set => SetProperty(ref _workflow, value); }

    /// <summary>"comfyui" or "replicate".</summary>
    private string _route = "comfyui";
    public string Route { get => _route; set => SetProperty(ref _route, value); }

    private string _styleId = "empress";
    public string StyleId { get => _styleId; set => SetProperty(ref _styleId, value); }

    private AspectRatio _aspect = AspectRatio.Wide;
    public AspectRatio Aspect { get => _aspect; set => SetProperty(ref _aspect, value); }

    private CamMode _camera = CamMode.Push;
    public CamMode Camera { get => _camera; set => SetProperty(ref _camera, value); }

    private double _durationSec = 8;
    public double DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, value); }

    private double _motion = 0.7;
    public double Motion { get => _motion; set => SetProperty(ref _motion, value); }

    private ScheduleKind _kind = ScheduleKind.DailySlots;
    public ScheduleKind Kind
    {
        get => _kind;
        set { if (SetProperty(ref _kind, value)) OnPropertyChanged(nameof(KindLabel)); }
    }

    /// <summary>Either "08:00,12:00,18:00" (DailySlots) or "60" (Interval minutes).</summary>
    private string _spec = "08:00,18:00";
    public string Spec { get => _spec; set => SetProperty(ref _spec, value); }

    private bool _autoPost;
    public bool AutoPost { get => _autoPost; set => SetProperty(ref _autoPost, value); }

    /// <summary>Provider id of the posting target ("facebook", "webhook"), or empty.</summary>
    private string _postTarget = "";
    public string PostTarget { get => _postTarget; set => SetProperty(ref _postTarget, value); }

    private DateTimeOffset? _lastFireAt;
    public DateTimeOffset? LastFireAt
    {
        get => _lastFireAt;
        set { if (SetProperty(ref _lastFireAt, value)) OnPropertyChanged(nameof(LastFireLabel)); }
    }

    private DateTimeOffset? _nextFireAt;
    public DateTimeOffset? NextFireAt
    {
        get => _nextFireAt;
        set
        {
            if (SetProperty(ref _nextFireAt, value))
            {
                OnPropertyChanged(nameof(NextFireLabel));
                OnPropertyChanged(nameof(NextFireCountdown));
            }
        }
    }

    private bool _isEnabled = true;
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string KindLabel => _kind == ScheduleKind.DailySlots ? "DAILY · SLOTS" : "INTERVAL";
    public string LastFireLabel => _lastFireAt is null ? "never" : _lastFireAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string NextFireLabel => _nextFireAt is null ? "—" : _nextFireAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string NextFireCountdown
    {
        get
        {
            if (_nextFireAt is null) return "—";
            var span = _nextFireAt.Value - DateTimeOffset.UtcNow;
            if (span.TotalSeconds < 0) return "due now";
            if (span.TotalMinutes < 1) return "<1 min";
            if (span.TotalHours < 1) return $"in {span.Minutes} min";
            if (span.TotalDays < 1) return $"in {span.Hours}h {span.Minutes}m";
            return $"in {(int)span.TotalDays}d {span.Hours}h";
        }
    }

    /// <summary>Compute the next fire time AFTER <paramref name="reference"/>, based on Kind + Spec.</summary>
    public DateTimeOffset? ComputeNextFireAt(DateTimeOffset reference)
    {
        if (Kind == ScheduleKind.Interval)
        {
            if (!int.TryParse(Spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                return null;
            return reference.AddMinutes(minutes);
        }

        // DailySlots — pick the next HH:mm > reference (in local time)
        var slots = ParseSlots(Spec);
        if (slots.Count == 0) return null;

        var local = reference.ToLocalTime();
        foreach (var slot in slots)
        {
            var candidate = new DateTime(local.Year, local.Month, local.Day, slot.Hour, slot.Minute, 0, DateTimeKind.Local);
            if (candidate > local.DateTime) return new DateTimeOffset(candidate);
        }
        // All slots for today have passed — first slot of tomorrow.
        var tomorrow = local.AddDays(1).Date;
        var first = slots[0];
        return new DateTimeOffset(new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, first.Hour, first.Minute, 0, DateTimeKind.Local));
    }

    public static List<(int Hour, int Minute)> ParseSlots(string spec)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(spec)) return result;
        foreach (var raw in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(':');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) continue;
            if (h < 0 || h > 23 || m < 0 || m > 59) continue;
            result.Add((h, m));
        }
        return result.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
    }
}

public sealed class ScheduleRun : ObservableObject
{
    public long Id { get; set; }
    public long ScheduleId { get; set; }
    public DateTimeOffset FiredAt { get; set; }
    public string? JobId { get; set; }

    /// <summary>"queued" | "running" | "done" | "error" | "skipped".</summary>
    public string Status { get; set; } = "";

    public string? ErrorMessage { get; set; }
}
