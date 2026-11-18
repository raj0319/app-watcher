using System.Security.Cryptography;
using System.Text;

namespace AppWatcher.Core;

/// <summary>Settings password: PBKDF2-SHA256 with a random salt, then the whole record is DPAPI-protected.</summary>
public static class PasswordHasher
{
    private const int Iterations = 120_000;

    public static string CreateRecord(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string record, string password)
    {
        var parts = record.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

/// <summary>Windows DPAPI wrapper. Protected blobs can only be read on this PC by this user account.</summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AppWatcher.v1");

    public static string Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try
        {
            if (blob.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(blob[6..]));
            if (blob.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob[6..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Log.Error("Could not unprotect a secret", ex);
        }
        return null;
    }
}
