using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChanthraStudio.Models;

/// <summary>
/// Snapshot of the current license state, mirrored from the
/// xman4289.com /api/v1/product/{slug}/* responses. Bound by the
/// activation dialog, the settings page, and the status bar.
/// </summary>
public sealed class LicenseInfo : ObservableObject
{
    private bool _isValid;
    public bool IsValid { get => _isValid; set => SetProperty(ref _isValid, value); }

    private string _licenseKey = "";
    public string LicenseKey { get => _licenseKey; set => SetProperty(ref _licenseKey, value); }

    /// <summary>"active", "expired", "revoked", "demo", "free", "" when no license.</summary>
    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    /// <summary>"lifetime", "yearly", "monthly", "weekly", "daily", "demo", "free".</summary>
    private string _licenseType = "";
    public string LicenseType { get => _licenseType; set => SetProperty(ref _licenseType, value); }

    private DateTimeOffset? _expiresAt;
    public DateTimeOffset? ExpiresAt { get => _expiresAt; set => SetProperty(ref _expiresAt, value); }

    private int _daysRemaining;
    public int DaysRemaining { get => _daysRemaining; set => SetProperty(ref _daysRemaining, value); }

    /// <summary>Human-readable error/message returned by the server, if any.</summary>
    private string _message = "";
    public string Message { get => _message; set => SetProperty(ref _message, value); }

    public bool HasKey => !string.IsNullOrWhiteSpace(_licenseKey);

    public string DisplayLine => _isValid
        ? $"{LicenseType.ToUpperInvariant()}  ·  {(_expiresAt is null ? "perpetual" : $"expires {_expiresAt:yyyy-MM-dd}")}"
        : (string.IsNullOrEmpty(_message) ? "not activated" : _message);
}
