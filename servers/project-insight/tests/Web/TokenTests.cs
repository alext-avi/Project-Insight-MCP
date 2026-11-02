using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class TokenTests
{
    private static FormUrlEncodedContent Form(string code, string clientId, string? verifier = AuthorizeFlow.Verifier,
        string redirect = AuthorizeFlow.ClaudeCallback, string? resource = "https://mcp.test/mcp")
        => new(new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["resource"] = resource
        }.Where(kv => kv.Value is not null)!);

    private static async Task<(WebFactory, AuthorizeFlow, string ClientId, string Code)> SignedInAsync()
    {
        var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        return (factory, flow, clientId, await flow.CodeForAsync(clientId));
    }

    [Fact]
    public async Task Code_and_verifier_give_the_pi_token_with_its_expiry_and_no_refresh_token()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        var response = await flow.Browser.PostAsync("/token", Form(code, clientId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PiStub.TokenA, json.GetProperty("access_token").GetString());
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        Assert.False(json.TryGetProperty("refresh_token", out _));
        var expected = (new DateTimeOffset(2027, 1, 28, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UtcNow).TotalSeconds;
        Assert.InRange(json.GetProperty("expires_in").GetInt32(), expected - 60, expected + 60);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        Assert.Equal(HttpStatusCode.OK, (await flow.Browser.PostAsync("/token", Form(code, clientId))).StatusCode);

        var again = await flow.Browser.PostAsync("/token", Form(code, clientId));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_verifier_is_invalid_grant_and_burns_the_code()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        var wrong = await flow.Browser.PostAsync("/token", Form(code, clientId, verifier: new string('a', 43)));
        Assert.Equal("invalid_grant", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await flow.Browser.PostAsync("/token", Form(code, clientId))).StatusCode);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("redirect")]
    public async Task Mismatched_client_or_redirect_is_invalid_grant(string which)
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        var response = await flow.Browser.PostAsync("/token", which == "client"
            ? Form(code, "dcr_someone_else")
            : Form(code, clientId, redirect: "https://chatgpt.com/connector_platform_oauth_redirect"));

        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("https://mcp.test/mcp/")]
    [InlineData("https://MCP.TEST/mcp")]
    public async Task Resource_with_a_trailing_slash_or_upper_case_host_is_accepted(string resource)
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        Assert.Equal(HttpStatusCode.OK, (await flow.Browser.PostAsync("/token", Form(code, clientId, resource: resource))).StatusCode);
    }

    [Fact]
    public async Task Other_resource_is_invalid_target()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        var response = await flow.Browser.PostAsync("/token", Form(code, clientId, resource: "https://other.test/mcp"));
        Assert.Equal("invalid_target", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refresh_token_grant_is_unsupported()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "x"
        }));
        Assert.Equal("unsupported_grant_type", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Pi_expiry_without_a_utc_marker_is_read_as_utc()
    {
        using var factory = new WebFactory();
        var expiry = DateTime.UtcNow.AddDays(90);
        factory.Pi.ActivationResult = (PiStub.TokenA, PiStub.UserA, expiry.ToString("yyyy-MM-ddTHH:mm:ss"));
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var code = await flow.CodeForAsync(clientId);

        var json = await (await flow.Browser.PostAsync("/token", Form(code, clientId))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.InRange(json.GetProperty("expires_in").GetInt32(), 90 * 86400 - 120, 90 * 86400 + 5);
    }
}
