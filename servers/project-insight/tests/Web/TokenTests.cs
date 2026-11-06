using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Storage;
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
        var expected = (DateTime.SpecifyKind(PiStub.DefaultExpiry, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds;
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
    public async Task Two_concurrent_redemptions_of_one_code_give_exactly_one_token()
    {
        var bothTake = new Rendezvous();
        using var factory = new WebFactory
        {
            ExtraServices = s => s.AddSingleton<ICodeStore>(new RendezvousCodeStore(new MemoryCodeStore(), bothTake))
        };
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var code = await flow.CodeForAsync(clientId);

        bothTake.Arm();
        var responses = await Task.WhenAll(
            flow.Browser.PostAsync("/token", Form(code, clientId)),
            flow.Browser.PostAsync("/token", Form(code, clientId))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal("invalid_grant", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // The mismatch burned the code: the right client can't use it afterwards.
        Assert.Equal(HttpStatusCode.BadRequest, (await flow.Browser.PostAsync("/token", Form(code, clientId))).StatusCode);
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
        factory.Pi.ActivationResult = (PiStub.TokenA, PiStub.UserA, expiry.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var code = await flow.CodeForAsync(clientId);

        var json = await (await flow.Browser.PostAsync("/token", Form(code, clientId))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.InRange(json.GetProperty("expires_in").GetInt32(), 90 * 86400 - 120, 90 * 86400 + 5);
    }

    [Fact]
    public async Task Pi_expiry_missing_falls_back_to_the_configured_token_lifetime()
    {
        using var factory = new WebFactory();
        factory.Pi.ActivationResult = (PiStub.TokenA, PiStub.UserA, null);
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var code = await flow.CodeForAsync(clientId);

        var json = await (await flow.Browser.PostAsync("/token", Form(code, clientId))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.InRange(json.GetProperty("expires_in").GetInt32(), 90 * 86400 - 30, 90 * 86400 + 5);
    }

    [Fact]
    public async Task A_code_past_its_expiry_is_invalid_grant()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        var code = CodeProtector.NewCode();
        var payload = new CodePayload(PiStub.TokenA, DateTimeOffset.UtcNow.AddDays(30), "dcr_x", AuthorizeFlow.ClaudeCallback,
            AuthorizeFlow.Challenge, null, PiStub.UserA);
        await factory.Services.GetRequiredService<ICodeStore>().SaveAsync(new StoredCode(CodeProtector.Hash(code),
            CodeProtector.Seal(code, JsonSerializer.SerializeToUtf8Bytes(payload)), DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);

        var response = await client.PostAsync("/token", Form(code, "dcr_x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Non_form_body_is_invalid_request_and_errors_are_not_cacheable()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsync("/token", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task Multipart_body_is_invalid_request()
    {
        using var factory = new WebFactory();
        var multipart = new MultipartFormDataContent { { new StringContent("authorization_code"), "grant_type" } };
        var response = await factory.Client().PostAsync("/token", multipart);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Form_with_too_many_fields_is_a_400_not_a_500()
    {
        using var factory = new WebFactory();
        var fields = Enumerable.Range(0, 1100).ToDictionary(i => "f" + i, i => "v");
        var response = await factory.Client().PostAsync("/token", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_successful_exchange_touches_the_dcr_client_and_a_failed_one_does_not()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var owned = factory;
        var clients = factory.Services.GetRequiredService<IClientStore>();
        Assert.False((await clients.GetAsync(clientId, default))!.Used);

        await flow.Browser.PostAsync("/token", Form(code, clientId, verifier: new string('a', 43)));
        Assert.False((await clients.GetAsync(clientId, default))!.Used);

        var second = await flow.CodeForAsync(clientId);
        Assert.Equal(HttpStatusCode.OK, (await flow.Browser.PostAsync("/token", Form(second, clientId))).StatusCode);
        Assert.True((await clients.GetAsync(clientId, default))!.Used);
    }

    [Fact]
    public async Task A_failing_client_store_touch_does_not_fail_the_token_response()
    {
        using var factory = new WebFactory
        {
            ExtraServices = s => s.AddSingleton<IClientStore>(new TouchFailsStore(new MemoryClientStore()))
        };
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var code = await flow.CodeForAsync(clientId);

        var response = await flow.Browser.PostAsync("/token", Form(code, clientId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PiStub.TokenA, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
    }

    private sealed class TouchFailsStore(IClientStore inner) : IClientStore
    {
        public Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct) => inner.GetAsync(clientId, ct);
        public Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct) => inner.TryAddAsync(client, max, ct);
        public Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct) => throw new InvalidOperationException("store down");
        public Task PruneAsync(DateTimeOffset unusedSince, DateTimeOffset neverUsedSince, CancellationToken ct) => inner.PruneAsync(unusedSince, neverUsedSince, ct);
    }
}
