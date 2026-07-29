using System.Security.Cryptography;
using System.Text;

namespace Deck.Core.Servers;

/// <summary>
/// Encrypts stored passwords with Windows DPAPI under the current user (C9). Nothing in the config
/// file is readable by another account, and no key material ships with Deck.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Still says SIRS, deliberately. This is DPAPI entropy, not a secret — it only has to match
    /// whatever encrypted the value. The Deck was forked from SIRS, so someone moving across can
    /// copy their <c>servers.json</c> over and their passwords still decrypt. Renaming it would
    /// silently turn every carried-over password into a blank field, which is a worse outcome than
    /// an old name in one string.
    /// </summary>
    private static readonly byte[] Entropy = "SIRS.ServerPassword.v1"u8.ToArray();

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException)
        {
            // Extremely rare, but a password we cannot store is better than a crash on save.
            return null;
        }
    }

    public static string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return null;

        try
        {
            var encrypted = Convert.FromBase64String(cipherText);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Config copied from another machine or user account. The caller re-prompts.
            return null;
        }
    }
}
