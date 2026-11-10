using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Outstanding sign-ins: capped per client network and overall, and checked before PI is asked.</summary>
public class SignInCapTests
{
    private static AuthorizeFlow From(WebFactory factory, string address)
    {
        var flow = new AuthorizeFlow(factory);
        flow.Browser.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return flow;
    }

    private static Task<HttpResponseMessage> PostStartAsync(AuthorizeFlow flow, string clientId)
        => flow.Browser.PostAsync("/authorize/start", new FormUrlEncodedContent(AuthorizeFlow.Query(clientId)!));

    [Fact]
    public async Task One_network_holds_at_most_three_sign_ins_and_a_refused_start_never_reaches_pi()
    {
        using var factory = new WebFactory();
        var flow = From(factory, "203.0.113.20");
        var clientId = await flow.RegisterAsync();
        for (var i = 0; i < AuthorizeEndpoints.MaxSessionsPerClient; i++)
            await flow.StartAsync(AuthorizeFlow.Query(clientId));

        var refused = await PostStartAsync(flow, clientId);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Too many sign-ins are in progress from your network", await refused.Content.ReadAsStringAsync());
        Assert.Equal(AuthorizeEndpoints.MaxSessionsPerClient, factory.Pi.ToPath("/api/device-activation/create-request").Count());
        // Another network is unaffected.
        await From(factory, "203.0.113.21").StartAsync(AuthorizeFlow.Query(clientId));
    }

    [Fact]
    public async Task At_the_global_cap_a_start_is_refused_before_pi_is_asked()
    {
        using var factory = new WebFactory();
        var flow = From(factory, "203.0.113.30");
        var clientId = await flow.RegisterAsync();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        for (var i = 0; i < AuthorizeEndpoints.MaxOutstandingSessions; i++)
            await store.TryCreateAsync(new SignInSession
            {
                Id = CodeProtector.NewCode(), ClientId = clientId, RedirectUri = AuthorizeFlow.ClaudeCallback, CodeChallenge = AuthorizeFlow.Challenge,
                PiActivationId = "act", PiActivationUrl = "https://pi.test/device/activate/act", CookieHash = "x",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), ClientKeyHash = "elsewhere-" + i
            }, int.MaxValue, int.MaxValue, DateTimeOffset.UtcNow, default);

        var refused = await PostStartAsync(flow, clientId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/create-request"));
    }

    private sealed class NoStartPi : IPiActivationClient
    {
        public Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken ct) => Task.FromResult<PiActivationStart?>(null);
        public Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken ct)
            => throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_start_pi_refuses_gives_its_slot_back()
    {
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<IPiActivationClient>(new NoStartPi()) };
        var flow = From(factory, "203.0.113.40");
        var clientId = await flow.RegisterAsync();

        // More failed starts than the per-network cap: each must release its reservation.
        for (var i = 0; i <= AuthorizeEndpoints.MaxSessionsPerClient; i++)
            Assert.Equal(HttpStatusCode.BadGateway, (await PostStartAsync(flow, clientId)).StatusCode);
    }

    private sealed class ThrowingSessionStore : ISessionStore
    {
        public Task<SessionCreate> TryCreateAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task<SignInSession?> GetAsync(string id, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task DeleteAsync(string id, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
    }

    public static readonly TheoryData<string> MalformedIds = new()
    {
        "x",
        new string('a', 42),
        new string('a', 44),
        new string('a', 42) + "/",
        new string('a', 42) + "#",
        new string('a', 42) + "\u0007",
        new string('a', 10_000)
    };

    [Theory]
    [MemberData(nameof(MalformedIds))]
    public async Task Malformed_session_ids_never_reach_the_store(string id)
    {
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<ISessionStore>(new ThrowingSessionStore()) };
        var flow = new AuthorizeFlow(factory);

        var response = await flow.CompleteAsync(id, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("This sign-in has expired", await response.Content.ReadAsStringAsync());
    }

    private sealed class ThrowingCodeStore : ICodeStore
    {
        public Task SaveAsync(StoredCode code, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct) => throw new InvalidOperationException("store touched");
        public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(MalformedIds))]
    public async Task Malformed_codes_never_reach_the_store(string code)
    {
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<ICodeStore>(new ThrowingCodeStore()) };

        var response = await factory.Client().PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = AuthorizeFlow.Verifier,
            ["client_id"] = "dcr_" + new string('a', 22),
            ["redirect_uri"] = AuthorizeFlow.ClaudeCallback
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", await response.Content.ReadAsStringAsync());
    }
}
