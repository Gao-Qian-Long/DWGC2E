using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Cryptographic helpers for license management.
/// Handles encryption/decryption, key derivation, hashing, and payload validation.
/// </summary>
internal static class LicenseCrypto
{
    // Derived at runtime from split fragments to avoid trivial string scanning in binaries.
    private static readonly string SecretKey = BuildKey();

    private static string BuildKey()
    {
        var a = "DWG-Trans";
        var b = "lator-202";
        var c = "6-Secret-";
        var d = "Key-v1";
        return string.Concat(a, b, c, d);
    }

    public static byte[] Encrypt(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        return ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
    }

    public static string Decrypt(byte[] cipherData)
    {
        var plainBytes = ProtectedData.Unprotect(cipherData, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public static byte[] DeriveKey(string password, int keyBytes)
    {
        var saltPhrase = string.Concat("DwgTrans", "lator-Li", "cense-Sa", "lt-v1");
        var salt = Encoding.UTF8.GetBytes(saltPhrase);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(keyBytes);
    }

    public static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    public static string? DecodePayload(string payload)
    {
        try
        {
            var bytes = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static bool ValidateChecksum(string payload)
    {
        var parts = payload.Split('|');
        if (parts.Length < 2) return false;

        var data = string.Join("|", parts[..^1]);
        var expectedBytes = DeriveKey(data + SecretKey, 4);
        var expectedHash = Convert.ToHexString(expectedBytes);
        return parts[^1] == expectedHash;
    }
}
