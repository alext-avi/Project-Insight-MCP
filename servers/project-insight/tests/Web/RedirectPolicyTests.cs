using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class RedirectPolicyTests
{
    private static readonly RedirectPolicy Policy = new(Options.Create(new AuthOptions()));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://CLAUDE.AI/api/mcp/auth_callback")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect")]
    [InlineData("http://localhost:3118/callback")]
    [InlineData("http://127.0.0.1:50000/callback")]
    [InlineData("http://[::1]:8080/callback")]
    [InlineData("http://localhost/callback")]
    public void Allowed(string uri) => Assert.True(Policy.IsAllowed(uri));

    [Theory]
    [InlineData("https://claude.ai/other")]
    [InlineData("https://claude.ai/api/mcp/auth_callback/")]
    [InlineData("https://claude.ai/API/mcp/auth_callback")]
    [InlineData("https://claude.ai.evil.com/api/mcp/auth_callback")]
    [InlineData("https://claude.ai@evil.com/api/mcp/auth_callback")]
    [InlineData("https://evil.com@claude.ai/api/mcp/auth_callback")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect/../x")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect%2F..%2Fx")]
    [InlineData("https://claude.ai/api/mcp/auth_callback#frag")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?x=1")]
    [InlineData("https://claude.ai:8443/api/mcp/auth_callback")]
    [InlineData("http://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://localhost:3118/callback")]
    [InlineData("http://localhost:3118/other")]
    [InlineData("http://localhost.evil.com/callback")]
    [InlineData("http://10.0.0.1/callback")]
    [InlineData("javascript:alert(1)")]
    [InlineData(" https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback ")]
    [InlineData("https://claude.ai/api/mcp/auth_callback\r\n")]
    [InlineData("\thttps://claude.ai/api/mcp/auth_callback")]
    [InlineData("http://localhost:3118/callback?")]
    [InlineData("")]
    [InlineData(null)]
    public void Refused(string? uri) => Assert.False(Policy.IsAllowed(uri));

    [Fact]
    public void Loopback_matches_a_registered_loopback_on_any_port()
        => Assert.True(RedirectPolicy.Matches("http://127.0.0.1:61000/callback", ["http://127.0.0.1/callback"]));

    [Fact]
    public void Loopback_does_not_match_a_different_loopback_host()
        => Assert.False(RedirectPolicy.Matches("http://localhost:61000/callback", ["http://127.0.0.1/callback"]));

    [Fact]
    public void Https_must_match_a_registered_uri_exactly()
    {
        Assert.True(RedirectPolicy.Matches("https://claude.ai/api/mcp/auth_callback", ["https://claude.ai/api/mcp/auth_callback"]));
        Assert.False(RedirectPolicy.Matches("https://claude.ai/api/mcp/auth_callback", ["https://chatgpt.com/connector_platform_oauth_redirect"]));
    }

    [Fact]
    public void Loopback_candidate_does_not_match_an_https_only_registration()
        => Assert.False(RedirectPolicy.Matches("http://localhost:61000/callback", ["https://claude.ai/api/mcp/auth_callback"]));
}
