using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Authorization codes are 256 random bits. Storage keys them by SHA-256 and holds the payload
/// (which contains the PI token) encrypted under a key derived from the code itself, so reading
/// storage never yields a usable token. Only the client that received the code can open it.
/// </summary>
public static class CodeProtector
{
    private static readonly byte[] Info = "pi-mcp authorization code v1"u8.ToArray();
    private const int NonceSize = 12, TagSize = 16;

    public static string NewCode() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string code) => TokenHash.Of(code);

    public static byte[] Seal(string code, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(Key(code), TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public static byte[]? Open(string code, byte[] sealedPayload)
    {
        if (sealedPayload.Length < NonceSize + TagSize)
            return null;
        var plain = new byte[sealedPayload.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(Key(code), TagSize);
            aes.Decrypt(sealedPayload.AsSpan(0, NonceSize), sealedPayload.AsSpan(NonceSize + TagSize),
                sealedPayload.AsSpan(NonceSize, TagSize), plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private static byte[] Key(string code)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(code), 32, salt: [], info: Info);
}
