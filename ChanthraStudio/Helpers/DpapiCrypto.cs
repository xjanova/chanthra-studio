using System;
using System.Security.Cryptography;
using System.Text;

namespace ChanthraStudio.Helpers;

/// <summary>
/// Wraps the Windows Data Protection API.
/// Per-user scope: ciphertext can only be decrypted by the same Windows account
/// that encrypted it. This is exactly what we want for stored API keys —
/// stealing the settings file off the disk is useless without the user's
/// credentials.
/// </summary>
public static class DpapiCrypto
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("chanthra-studio.lunar-atelier.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var data = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";
        try
        {
            var encrypted = Convert.FromBase64String(ciphertext);
            var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            // Settings file copied across user accounts — fail silent, user re-enters key.
            return "";
        }
        catch (FormatException)
        {
            // Plain string accidentally stored where ciphertext expected.
            return "";
        }
    }
}
