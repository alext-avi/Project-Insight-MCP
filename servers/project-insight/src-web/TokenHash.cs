using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web;

/// <summary>Keys for caches and stores that must never hold a token or code itself.</summary>
public static class TokenHash
{
    public static string Of(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
