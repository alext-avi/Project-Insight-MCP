using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>RFC 7636, S256 only.</summary>
public static class Pkce
{
    public static bool IsValidChallenge(string? challenge)
        => challenge is { Length: 43 } && challenge.All(IsUnreservedBase64Url);

    public static bool Verify(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128 || !verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            return false;
        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static bool IsUnreservedBase64Url(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
}
