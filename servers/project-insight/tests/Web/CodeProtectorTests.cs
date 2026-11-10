using System.Text;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class CodeProtectorTests
{
    [Fact]
    public void Sealed_payload_opens_only_with_its_code()
    {
        var code = CodeProtector.NewCode();
        var other = CodeProtector.NewCode();
        var sealedPayload = CodeProtector.Seal(code, Encoding.UTF8.GetBytes("pi-token-secret"));

        Assert.Equal("pi-token-secret", Encoding.UTF8.GetString(CodeProtector.Open(code, sealedPayload)!));
        Assert.Null(CodeProtector.Open(other, sealedPayload));
        Assert.DoesNotContain("pi-token-secret", Encoding.UTF8.GetString(sealedPayload));
    }

    [Fact]
    public void Tampered_payload_does_not_open()
    {
        var code = CodeProtector.NewCode();
        var sealedPayload = CodeProtector.Seal(code, [1, 2, 3, 4]);
        sealedPayload[^1] ^= 0xFF;

        Assert.Null(CodeProtector.Open(code, sealedPayload));
    }

    [Fact]
    public void Codes_are_256_bit_and_the_hash_is_not_the_code()
    {
        var code = CodeProtector.NewCode();
        Assert.Equal(43, code.Length);
        Assert.NotEqual(code, CodeProtector.Hash(code));
        Assert.Equal(64, CodeProtector.Hash(code).Length);
    }

    [Theory]
    [InlineData(0)]  // nonce
    [InlineData(12)] // tag
    public void Flipped_nonce_or_tag_byte_does_not_open(int index)
    {
        var code = CodeProtector.NewCode();
        var sealedPayload = CodeProtector.Seal(code, [1, 2, 3, 4]);
        sealedPayload[index] ^= 0x01;

        Assert.Null(CodeProtector.Open(code, sealedPayload));
    }

    [Fact]
    public void Payload_shorter_than_nonce_and_tag_does_not_open()
        => Assert.Null(CodeProtector.Open(CodeProtector.NewCode(), new byte[27]));

    [Fact]
    public void Two_codes_differ() => Assert.NotEqual(CodeProtector.NewCode(), CodeProtector.NewCode());
}
