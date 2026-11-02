using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class PkceTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Rfc7636_vector_verifies() => Assert.True(Pkce.Verify(Verifier, Challenge));

    [Fact]
    public void Wrong_verifier_fails() => Assert.False(Pkce.Verify(Verifier[..^1] + "A", Challenge));

    [Theory]
    [InlineData("short")]
    [InlineData("has space in it and is long enough to pass the length check ok")]
    public void Malformed_verifier_fails(string verifier) => Assert.False(Pkce.Verify(verifier, Challenge));

    [Theory]
    [InlineData(Challenge, true)]
    [InlineData("tooshort", false)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGF3cMjXm+", false)]
    [InlineData(null, false)]
    public void Challenge_must_be_43_base64url_characters(string? challenge, bool valid)
        => Assert.Equal(valid, Pkce.IsValidChallenge(challenge));
}
