using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.TimeChanges;

public static class DraftIds
{
    // No 0/1/o/l, so ids read back unambiguously.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string NewDraftId() => "d_" + RandomNumberGenerator.GetString(Alphabet, 10);

    public static string NewConfirmToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenMatches(string token, string hash)
        => !string.IsNullOrEmpty(token) &&
           CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));
}
