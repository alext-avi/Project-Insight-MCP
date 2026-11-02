using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class ClientRegistrationTests
{
    [Fact]
    public async Task Registration_with_allowed_redirects_returns_a_client_id_that_resolves()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new
        {
            client_name = "Codex",
            redirect_uris = new[] { "http://127.0.0.1/callback" },
            token_endpoint_auth_method = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = body.GetProperty("client_id").GetString()!;
        var client = await factory.Services.GetRequiredService<ClientResolver>().ResolveAsync(clientId, default);
        Assert.Equal(["http://127.0.0.1/callback"], client!.RedirectUris);
        Assert.False(client.IsMetadataDocument);
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("https://claude.ai/other")]
    public async Task Registration_with_a_redirect_off_the_allowlist_is_refused(string redirect)
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new { redirect_uris = new[] { redirect } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Confidential_clients_are_refused()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" },
            token_endpoint_auth_method = "client_secret_basic"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_client_id_does_not_resolve()
    {
        using var factory = new WebFactory();
        _ = factory.Client();
        Assert.Null(await factory.Services.GetRequiredService<ClientResolver>().ResolveAsync("dcr_made_up", default));
    }
}
