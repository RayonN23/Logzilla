using System;
using System.Security.Cryptography;
using System.Text;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// API keys and tokens, encrypted with Windows DPAPI for the signed-in user before
/// they touch the settings file. Another account, or the same file on another
/// machine, cannot read them. They are never written to a setup (.cnx) file.
/// </summary>
internal static class SecretStore
{
    private const string Prefix = "dpapi:";

    // Mixed into the encryption so another program using DPAPI for the same user
    // cannot decrypt Logzilla's values just by reading the settings file.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Logzilla.AI.v1");

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "";
        }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>The secret, or null when there is none or it cannot be decrypted here.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine("Read a stored secret: " + ex.Message);
            return null;
        }
    }
}
