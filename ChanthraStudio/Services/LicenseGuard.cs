using System;
using System.IO;
using System.Threading.Tasks;
using ChanthraStudio.Helpers;
using ChanthraStudio.Models;

namespace ChanthraStudio.Services;

/// <summary>
/// Singleton owner of the application's license state. Persists the
/// license key (DPAPI-protected) under <c>%APPDATA%\ChanthraStudio\</c>
/// and exposes <see cref="LicenseChanged"/> for UI bindings.
/// </summary>
public sealed class LicenseGuard
{
    public static LicenseGuard Instance { get; } = new();

    public LicenseInfo Current { get; private set; } = new();

    public event Action<LicenseInfo>? LicenseChanged;

    public bool IsLicensed => Current.IsValid;

    private const string KeyFileName = "license.key";

    private LicenseGuard() { }

    /// <summary>
    /// Boot path: load the persisted key (if any), validate against the
    /// server, and fall back to a HWID auto-check so a returning user on
    /// a re-imaged-but-same machine gets recognized without re-typing.
    /// </summary>
    public async Task InitializeAsync(string appVersion)
    {
        var saved = LoadSavedKey();
        if (!string.IsNullOrEmpty(saved))
        {
            var v = await LicenseClient.ValidateAsync(saved);
            if (v.IsValid)
            {
                Current = v;
                LicenseChanged?.Invoke(Current);
                return;
            }
        }

        // No saved key (or it failed) → ask the server if this machine
        // already has a binding from a prior install. This is the path
        // a fresh install on a previously-licensed machine takes.
        var auto = await LicenseClient.CheckMachineAsync();
        if (auto.IsValid && !string.IsNullOrEmpty(auto.LicenseKey))
        {
            SaveKey(auto.LicenseKey);
            Current = auto;
            LicenseChanged?.Invoke(Current);
            return;
        }

        Current = new LicenseInfo { Message = "license not activated" };
        LicenseChanged?.Invoke(Current);
    }

    public async Task<LicenseInfo> ActivateAsync(string licenseKey, string appVersion)
    {
        var trimmed = (licenseKey ?? "").Trim().ToUpperInvariant();
        var result = await LicenseClient.ActivateAsync(trimmed, appVersion);
        if (result.IsValid)
        {
            SaveKey(result.LicenseKey);
        }
        Current = result;
        LicenseChanged?.Invoke(Current);
        return result;
    }

    public async Task DeactivateAsync()
    {
        if (!string.IsNullOrEmpty(Current.LicenseKey))
        {
            await LicenseClient.DeactivateAsync(Current.LicenseKey);
        }
        DeleteKey();
        Current = new LicenseInfo { Message = "license not activated" };
        LicenseChanged?.Invoke(Current);
    }

    /// <summary>Re-validate the saved key against the server.</summary>
    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(Current.LicenseKey)) return;
        var v = await LicenseClient.ValidateAsync(Current.LicenseKey);
        Current = v;
        LicenseChanged?.Invoke(Current);
    }

    private static string KeyPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChanthraStudio");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, KeyFileName);
    }

    private static string? LoadSavedKey()
    {
        try
        {
            var p = KeyPath();
            if (!File.Exists(p)) return null;
            var ciphertext = File.ReadAllText(p).Trim();
            if (string.IsNullOrEmpty(ciphertext)) return null;
            return DpapiCrypto.Unprotect(ciphertext);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveKey(string licenseKey)
    {
        try
        {
            File.WriteAllText(KeyPath(), DpapiCrypto.Protect(licenseKey));
        }
        catch
        {
            // Best-effort persistence — failure here means user re-types
            // on next launch, not a security or correctness problem.
        }
    }

    private static void DeleteKey()
    {
        try { File.Delete(KeyPath()); }
        catch { /* best effort */ }
    }
}
