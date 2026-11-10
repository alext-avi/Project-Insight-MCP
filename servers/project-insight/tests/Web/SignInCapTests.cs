using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Outstanding sign-ins: capped per client network and overall, and checked before PI is asked.</summary>
public class SignInCapTests
{
    private static AuthorizeFlow From(WebFactory factory, string address, bool cookies = true)
    {
        var flow = new AuthorizeFlow(factory, cookies);
        flow.Browser.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return flow;
    }

    private static Task<HttpResponseMessage> PostStartAsync(AuthorizeFlow flow, string clientId)
        => flow.Browser.PostAsync("/authorize/start", new FormUrlEncodedContent(AuthorizeFlow.Query(clientId)!));

    [Fact]
    public async Task One_network_holds_at_most_three_cookie_less_sign_ins_and_a_refused_start_never_reaches_pi()
    {
        using var factory = new WebFactory();
        // A client that drops the cookie supersedes nothing: each start holds a slot.
        var flow = From(factory, "203.0.113.20", cookies: false);
        var clientId = await flow.RegisterAsync();
        for (var i = 0; i < AuthorizeEndpoints.MaxSessionsPerClient; i++)
            await flow.StartAsync(AuthorizeFlow.Query(clientId));

        var refused = await PostStartAsync(flow, clientId);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Too many sign-ins are in progress from your network", await refused.Content.ReadAsStringAsync());
        Assert.Equal("900", refused.Headers.RetryAfter?.ToString());
        Assert.Equal(AuthorizeEndpoints.MaxSessionsPerClient, factory.Pi.ToPath("/api/device-activation/create-request").Count());
        // Another network is unaffected.
        await From(factory, "203.0.113.21").StartAsync(AuthorizeFlow.Query(clientId));
    }

    [Fact]
    public async Task A_browser_starting_again_supersedes_its_earlier_sign_in_so_retries_never_hit_the_cap()
    {
        using var factory = new WebFactory();
        var flow = From(factory, "203.0.113.22");
        var clientId = await flow.RegisterAsync();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var ids = new List<string>();

        for (var i = 0; i < 5; i++)
            ids.Add(await flow.StartAsync(AuthorizeFlow.Query(clientId)));

        foreach (var earlier in ids[..^1])
            Assert.Null(await store.GetAsync(earlier, default));
        Assert.Equal(HttpStatusCode.Redirect, (await flow.CompleteAsync(ids[^1], PiStub.ActivationCode)).StatusCode);
    }

    [Fact]
    public async Task A_start_supersedes_only_the_sign_in_bound_to_its_own_cookie()
    {
        using var factory = new WebFactory();
        var clientId = await From(factory, "203.0.113.23").RegisterAsync();
        var other = From(factory, "203.0.113.23");
        var otherId = await other.StartAsync(AuthorizeFlow.Query(clientId));
        var mine = From(factory, "203.0.113.23");

        for (var i = 0; i < 3; i++)
            await mine.StartAsync(AuthorizeFlow.Query(clientId));

        Assert.Equal(HttpStatusCode.Redirect, (await other.CompleteAsync(otherId, PiStub.ActivationCode)).StatusCode);
    }

    [Fact]
    public async Task The_network_key_is_a_keyed_hash_not_a_plain_hash_of_the_address()
    {
        using var factory = new WebFactory();
        var flow = From(factory, "203.0.113.24");
        var id = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var stored = (await factory.Services.GetRequiredService<ISessionStore>().GetAsync(id, default))!.ClientKeyHash;

        Assert.NotEqual(ProjectInsightMCP.Web.TokenHash.Of("203.0.113.24"), stored);
        Assert.Equal(ProjectInsightMCP.Web.NetworkKey.Of("203.0.113.24"), stored);
        Assert.Equal(64, stored!.Length);
    }

    /// <summary>The store accepts the reservation, then the client goes away before the reply reaches the server.</summary>
    private sealed class AbortAfterReserveStore(ISessionStore inner) : ISessionStore
    {
        public TaskCompletionSource Reserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionCreate> TryCreateAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct)
        {
            var created = await inner.TryCreateAsync(session, maxOutstanding, maxPerClient, now, default);
            Reserved.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            return created;
        }

        public Task<SignInSession?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct) => inner.TryReplaceAsync(updated, ct);
        public async Task DeleteAsync(string id, CancellationToken ct)
        {
            await inner.DeleteAsync(id, ct);
            Released.TrySetResult();
        }
        public Task DeleteByCookieAsync(string cookieHash, CancellationToken ct) => inner.DeleteByCookieAsync(cookieHash, ct);
        public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => inner.PruneAsync(now, ct);
    }

    [Fact]
    public async Task A_start_aborted_after_the_reservation_gives_its_slot_back()
    {
        var inner = new MemorySessionStore();
        var store = new AbortAfterReserveStore(inner);
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<ISessionStore>(store) };
        var flow = From(factory, "203.0.113.25");
        var clientId = await flow.RegisterAsync();
        using var abort = new CancellationTokenSource();

        var post = flow.Browser.PostAsync("/authorize/start", new FormUrlEncodedContent(AuthorizeFlow.Query(clientId)!), abort.Token);
        await store.Reserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => post);

        await store.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Nothing is held: a store allowing one session takes a new one.
        Assert.Equal(SessionCreate.Created, await inner.TryCreateAsync(new SignInSession
        {
            Id = CodeProtector.NewCode(), ClientId = clientId, RedirectUri = AuthorizeFlow.ClaudeCallback, CodeChallenge = AuthorizeFlow.Challenge,
            PiActivationId = "act", PiActivationUrl = "https://pi.test/device/activate/act", CookieHash = "x",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        }, 1, 1, DateTimeOffset.UtcNow, default));
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
        Assert.Equal("300", refused.Headers.RetryAfter?.ToString());
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
        public Task DeleteByCookieAsync(string cookieHash, CancellationToken ct) => throw new InvalidOperationException("store touched");
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
