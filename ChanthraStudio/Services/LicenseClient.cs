using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// HTTP client for the generic Product License API on xman4289.com.
/// Maps the JSON envelope returned by ProductLicenseController into
/// <see cref="LicenseInfo"/>.
/// </summary>
public sealed class LicenseClient
{
    public const string BaseUrl = "https://xman4289.com";
    public const string ProductSlug = "chanthra-studio";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Checks whether the current machine already has an active license
    /// on the server (no key required). Used at startup so a returning
    /// user doesn't have to re-enter their key.
    /// </summary>
    public static async Task<LicenseInfo> CheckMachineAsync()
    {
        try
        {
            var body = new { machine_id = MachineFingerprint.Get() };
            var resp = await _http.PostAsJsonAsync(Url("check-machine"), body);
            var json = await resp.Content.ReadAsStringAsync();
            return ParseHasLicense(json);
        }
        catch (Exception ex)
        {
            return new LicenseInfo { IsValid = false, Message = $"network error: {ex.Message}" };
        }
    }

    public static async Task<LicenseInfo> ActivateAsync(string licenseKey, string appVersion)
    {
        try
        {
            var fp = MachineFingerprint.Get();
            var body = new
            {
                license_key = licenseKey,
                machine_id = fp,
                machine_fingerprint = fp,
                app_version = appVersion,
            };
            var resp = await _http.PostAsJsonAsync(Url("activate"), body);
            var json = await resp.Content.ReadAsStringAsync();
            return ParseSuccessData(json, fallbackKey: licenseKey);
        }
        catch (Exception ex)
        {
            return new LicenseInfo { IsValid = false, LicenseKey = licenseKey, Message = $"network error: {ex.Message}" };
        }
    }

    public static async Task<LicenseInfo> ValidateAsync(string licenseKey)
    {
        try
        {
            var body = new
            {
                license_key = licenseKey,
                machine_id = MachineFingerprint.Get(),
            };
            var resp = await _http.PostAsJsonAsync(Url("validate"), body);
            var json = await resp.Content.ReadAsStringAsync();
            return ParseValidateResponse(json, fallbackKey: licenseKey);
        }
        catch (Exception ex)
        {
            return new LicenseInfo { IsValid = false, LicenseKey = licenseKey, Message = $"network error: {ex.Message}" };
        }
    }

    public static async Task<bool> DeactivateAsync(string licenseKey)
    {
        try
        {
            var body = new
            {
                license_key = licenseKey,
                machine_id = MachineFingerprint.Get(),
            };
            var resp = await _http.PostAsJsonAsync(Url("deactivate"), body);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string Url(string action) => $"{BaseUrl}/api/v1/product/{ProductSlug}/{action}";

    /// <summary>
    /// Parses { success, has_license, data: { license_key, license_type, status, expires_at, days_remaining, ... } }
    /// from /check-machine.
    /// </summary>
    private static LicenseInfo ParseHasLicense(string json)
    {
        var info = new LicenseInfo();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("has_license", out var hl) && hl.GetBoolean() &&
                root.TryGetProperty("data", out var data))
            {
                FillFromDataElement(info, data);
                info.IsValid = !info.Status.Equals("expired", StringComparison.OrdinalIgnoreCase) &&
                               !info.Status.Equals("revoked", StringComparison.OrdinalIgnoreCase);
                return info;
            }

            if (root.TryGetProperty("message", out var msg))
                info.Message = msg.GetString() ?? "";
        }
        catch (Exception ex)
        {
            info.Message = $"parse error: {ex.Message}";
        }
        return info;
    }

    /// <summary>
    /// Parses /activate, which returns { success, message, data: { license_key, license_type, expires_at, days_remaining } }
    /// on success or { success: false, error_code, message } on failure.
    /// </summary>
    private static LicenseInfo ParseSuccessData(string json, string fallbackKey)
    {
        var info = new LicenseInfo { LicenseKey = fallbackKey };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            if (root.TryGetProperty("message", out var msg)) info.Message = msg.GetString() ?? "";

            if (success && root.TryGetProperty("data", out var data))
            {
                FillFromDataElement(info, data);
                info.IsValid = true;
                if (string.IsNullOrEmpty(info.Status)) info.Status = "active";
            }
            else
            {
                info.IsValid = false;
                if (root.TryGetProperty("error_code", out var ec))
                    info.Message = $"{ec.GetString()}: {info.Message}";
            }
        }
        catch (Exception ex)
        {
            info.Message = $"parse error: {ex.Message}";
        }
        return info;
    }

    /// <summary>
    /// Parses /validate, which returns { success, is_valid, data: { ... } }.
    /// </summary>
    private static LicenseInfo ParseValidateResponse(string json, string fallbackKey)
    {
        var info = new LicenseInfo { LicenseKey = fallbackKey };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("is_valid", out var iv) && iv.GetBoolean() &&
                root.TryGetProperty("data", out var data))
            {
                FillFromDataElement(info, data);
                info.IsValid = true;
                return info;
            }

            info.IsValid = false;
            if (root.TryGetProperty("message", out var msg))
                info.Message = msg.GetString() ?? "";
        }
        catch (Exception ex)
        {
            info.Message = $"parse error: {ex.Message}";
        }
        return info;
    }

    /// <summary>
    /// Shared mapper for the inner "data" object that all three endpoints
    /// return when a license is present.
    /// </summary>
    private static void FillFromDataElement(LicenseInfo info, JsonElement data)
    {
        if (data.TryGetProperty("license_key", out var lk)) info.LicenseKey = lk.GetString() ?? info.LicenseKey;
        if (data.TryGetProperty("license_type", out var lt)) info.LicenseType = lt.GetString() ?? "";
        if (data.TryGetProperty("status", out var st)) info.Status = st.GetString() ?? "";
        if (data.TryGetProperty("days_remaining", out var dr) && dr.ValueKind == JsonValueKind.Number)
            info.DaysRemaining = dr.GetInt32();
        if (data.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(ex.GetString(), out var when)) info.ExpiresAt = when;
        }
    }
}
