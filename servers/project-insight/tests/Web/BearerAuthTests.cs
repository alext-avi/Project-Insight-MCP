using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class BearerAuthTests
{
    private static HttpRequestMessage McpPost(string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Unauthenticated_call_gets_401_pointing_at_resource_metadata()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/mcp\", scope=\"pi\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Token_pi_rejects_gets_401_and_is_remembered_for_a_minute()
    {
        using var factory = new WebFactory();
        var client = factory.Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(McpPost(PiStub.RejectedToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(McpPost(PiStub.RejectedToken))).StatusCode);

        Assert.Single(factory.Pi.ToPath("/api/user/me"));
    }

    [Fact]
    public async Task Pi_outage_gets_503_with_retry_after_and_no_sign_in_challenge()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.BrokenToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData("comma,inside")]
    public async Task Malformed_bearer_is_refused_without_calling_pi(string token)
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/user/me"));
    }

    [Fact]
    public async Task Pi_user_not_on_the_allowlist_gets_403()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.StrangerToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Allowlist_ignores_the_case_of_pi_user_ids()
    {
        // The stub returns user B's id in upper case; the allowlist has it in lower case.
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenB);

        Assert.NotEmpty(await mcp.ListToolsAsync());
    }

    [Fact]
    public async Task Valid_token_reaches_the_mcp_server()
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        Assert.Contains(await mcp.ListToolsAsync(), t => t.Name == "search_projects");
    }

    [Fact]
    public async Task Request_from_an_unknown_origin_gets_403()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.TokenA, origin: "https://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/user/me"));
    }

    [Fact]
    public async Task Request_from_an_allowed_origin_is_served()
    {
        using var factory = new WebFactory();
        factory.Settings["Auth:AllowedOrigins:0"] = "https://claude.ai";
        var response = await factory.Client().SendAsync(McpPost(PiStub.TokenA, origin: "https://claude.ai"));

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Token_in_the_query_string_is_not_accepted()
    {
        using var factory = new WebFactory();
        var request = McpPost(token: null);
        request.RequestUri = new Uri("/mcp?access_token=" + PiStub.TokenA, UriKind.Relative);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().SendAsync(request)).StatusCode);
    }
}
