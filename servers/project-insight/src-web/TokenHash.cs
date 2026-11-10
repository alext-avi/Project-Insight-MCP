using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web;

/// <summary>Keys for caches and stores that must never hold a token or code itself.</summary>
public static class TokenHash
{
    public static string Of(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// The stored key of a client's network (an IPv4 address or IPv6 /64) for the per-network sign-in cap: an HMAC under
/// a random key made when the process starts. A plain hash of an IPv4 address is reversed by hashing all 2^32 of
/// them; this one can't be without the key, which is never stored. Sign-in sessions live 15 minutes, so a restart
/// only resets the per-network counts. The server runs one instance by design (bicep/main.bicep pins the plan to
/// one), so no instance needs another's key.
/// </summary>
public static class NetworkKey
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static string Of(string network) => Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(network)));
}
