using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChanthraStudio.Helpers;

namespace ChanthraStudio.Services;

/// <summary>
/// All persisted user settings. API keys are DPAPI-encrypted on save,
/// decrypted lazily when accessed via <see cref="ApiKey"/>. The raw JSON file
/// only ever contains the ciphertext blobs — safe to back up, sync, or
/// inspect (the keys are useless without the user's Windows account).
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("comfyUiUrl")]
    public string ComfyUiUrl { get; set; } = "http://127.0.0.1:8188";

    [JsonPropertyName("encryptedKeys")]
    public Dictionary<string, string> EncryptedKeys { get; set; } = new();

    [JsonPropertyName("activeLlm")]
    public string ActiveLlm { get; set; } = "gemini";

    [JsonPropertyName("activeVideo")]
    public string ActiveVideo { get; set; } = "comfyui";

    [JsonPropertyName("postFacebookPageId")]
    public string PostFacebookPageId { get; set; } = "";

    [JsonPropertyName("postWebhookUrl")]
    public string PostWebhookUrl { get; set; } = "";

    [JsonPropertyName("autosaveSeconds")]
    public int AutosaveSeconds { get; set; } = 30;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "lunar";

    /// <summary>Reads a decrypted API key. Empty string if unset.</summary>
    [JsonIgnore]
    public string this[string providerId]
    {
        get => EncryptedKeys.TryGetValue(providerId, out var ct) ? DpapiCrypto.Unprotect(ct) : "";
    }

    /// <summary>Stores an API key. Pass empty/null to remove.</summary>
    public void SetApiKey(string providerId, string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            EncryptedKeys.Remove(providerId);
        else
            EncryptedKeys[providerId] = DpapiCrypto.Protect(plaintext);
    }

    public bool HasApiKey(string providerId)
        => EncryptedKeys.TryGetValue(providerId, out var ct) && !string.IsNullOrEmpty(ct);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable — start fresh rather than crash. User loses prefs.
            return new AppSettings();
        }
    }

    public void Save()
    {
        var path = AppPaths.SettingsFile;
        var json = JsonSerializer.Serialize(this, JsonOpts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
