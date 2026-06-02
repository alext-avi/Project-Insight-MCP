using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Services;

/// <summary>
/// RFC 7636 PKCE verification. We support S256 only (Claude.ai uses S256;
/// the "plain" method is discouraged and offers no benefit).
/// </summary>
public static class PkceVerifier
{
    public static bool Verify(string codeVerifier, string codeChallenge, string method)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
            return false;

        if (!string.Equals(method, "S256", StringComparison.Ordinal))
            return false;

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var computed = Base64UrlEncode(hash);
        // Constant-time compare to avoid timing side channels.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(codeChallenge));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
