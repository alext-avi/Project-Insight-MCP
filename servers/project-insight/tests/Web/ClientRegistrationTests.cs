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

    [Theory]
    [InlineData("""{"redirect_uris":["\uD800"]}""")]
    [InlineData("""{"redirect_uris":["http://127.0.0.1/callback"],"client_name":"\uD800"}""")]
    public async Task Lone_surrogates_are_a_400_not_a_500(string json)
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsync("/register", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Response_omits_client_name_when_none_was_sent()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new { redirect_uris = new[] { "http://127.0.0.1/callback" } });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("client_name", out _));
    }

    [Theory]
    [InlineData("dcr_a/b")]
    [InlineData("dcr_#fragment")]
    [InlineData("dcr_\u0001control")]
    [InlineData("dcr_short")]
    [InlineData("anything-else")]
    [InlineData("dcr_AAAAAAAAAAAAAAAAAAAAAA\n")]
    public async Task Malformed_client_ids_never_reach_the_store(string id) => await NeverReachesStore(id);

    [Fact]
    public async Task Huge_client_ids_never_reach_the_store() => await NeverReachesStore("dcr_" + new string('a', 2048));

    private static async Task NeverReachesStore(string id)
    {
        using var factory = new WebFactory();
        _ = factory.Client();
        var resolver = new ClientResolver(new ThrowingStore(), factory.Services.GetRequiredService<CimdFetcher>(), factory.Services.GetRequiredService<RedirectPolicy>());
        Assert.Null(await resolver.ResolveAsync(id, default));
    }

    private sealed class ThrowingStore : ProjectInsightMCP.Web.Storage.IClientStore
    {
        public Task<ProjectInsightMCP.Web.Storage.RegisteredClient?> GetAsync(string clientId, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task<bool> TryAddAsync(ProjectInsightMCP.Web.Storage.RegisteredClient client, int max, CancellationToken ct) => throw new InvalidOperationException();
        public Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct) => throw new InvalidOperationException();
        public Task PruneAsync(DateTimeOffset unusedSince, CancellationToken ct) => throw new InvalidOperationException();
    }
}
