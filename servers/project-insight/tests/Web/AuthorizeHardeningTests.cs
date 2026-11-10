using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class AuthorizeHardeningTests
{
    private sealed class FakeActivation(Func<CancellationToken, Task<PiActivationResult>> complete) : IPiActivationClient
    {
        public CancellationToken? SeenToken { get; private set; }
        public Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken ct)
            => Task.FromResult<PiActivationStart?>(new PiActivationStart("act-1", new Uri("https://pi.test/device/activate/act-1")));
        public Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken ct)
        {
            SeenToken = ct;
            return complete(ct);
        }
    }

    private static WebFactory With(FakeActivation fake)
        => new() { ExtraServices = s => s.AddSingleton<IPiActivationClient>(fake) };

    private static Task<SignInSession?> Session(WebFactory f, string id)
        => f.Services.GetRequiredService<ISessionStore>().GetAsync(id, default);

    [Theory]
    [InlineData("io")]
    [InlineData("cancelled")]
    [InlineData("other")]
    public async Task Failure_after_the_claim_puts_the_session_back_to_pending_and_never_500s(string kind)
    {
        var fake = new FakeActivation(_ => kind switch
        {
            "io" => throw new IOException("reset"),
            "cancelled" => throw new OperationCanceledException(),
            _ => throw new InvalidOperationException("boom")
        });
        using var factory = With(fake);
        var flow = new AuthorizeFlow(factory);
        var id = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(id, PiStub.ActivationCode);

        Assert.True((int)response.StatusCode < 500 || response.StatusCode == HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var s = await Session(factory, id);
        Assert.Equal(SessionStatus.Pending, s!.Status);
        Assert.Equal(0, s.Attempts);
    }

    private sealed class AbortAfterClaimStore(ISessionStore inner) : ISessionStore
    {
        private int _claimed;
        public Task<SessionCreate> TryCreateAsync(SignInSession s, int max, int maxPerClient, DateTimeOffset now, CancellationToken ct)
            => inner.TryCreateAsync(s, max, maxPerClient, now, ct);
        // The first read after the claim is the re-read right after it: that one is aborted.
        public Task<SignInSession?> GetAsync(string id, CancellationToken ct)
            => Interlocked.Exchange(ref _claimed, 0) == 1 ? throw new OperationCanceledException() : inner.GetAsync(id, ct);
        public async Task<bool> TryReplaceAsync(SignInSession u, CancellationToken ct)
        {
            var replaced = await inner.TryReplaceAsync(u, ct);
            if (replaced && u.Status == SessionStatus.Completing)
                Interlocked.Exchange(ref _claimed, 1);
            return replaced;
        }
        public Task DeleteAsync(string id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task DeleteByCookieAsync(string cookieHash, CancellationToken ct) => inner.DeleteByCookieAsync(cookieHash, ct);
        public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => inner.PruneAsync(now, ct);
    }

    [Fact]
    public async Task Abort_on_the_re_read_right_after_the_claim_leaves_the_session_pending()
    {
        using var factory = new WebFactory
        {
            ExtraServices = s => s.AddSingleton<ISessionStore>(new AbortAfterClaimStore(new MemorySessionStore()))
        };
        var flow = new AuthorizeFlow(factory);
        var id = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(id, PiStub.ActivationCode);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        var s = await Session(factory, id);
        Assert.Equal(SessionStatus.Pending, s!.Status);
        Assert.Equal(0, s.Attempts);
    }

    [Fact]
    public async Task Pi_call_after_the_claim_does_not_observe_the_request_abort_token()
    {
        var fake = new FakeActivation(_ => Task.FromResult(new PiActivationResult(PiActivationOutcome.Ok, PiStub.TokenA, PiStub.UserA, null)));
        using var factory = With(fake);
        var flow = new AuthorizeFlow(factory);
        var id = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        await flow.CompleteAsync(id, PiStub.ActivationCode);

        Assert.False(fake.SeenToken!.Value.CanBeCanceled);
    }

    [Fact]
    public async Task Unavailable_pi_restores_pending_without_counting_an_attempt_and_retry_works()
    {
        var calls = 0;
        var fake = new FakeActivation(_ => Task.FromResult(++calls == 1
            ? new PiActivationResult(PiActivationOutcome.Unavailable)
            : new PiActivationResult(PiActivationOutcome.Ok, PiStub.TokenA, PiStub.UserA, null)));
        using var factory = With(fake);
        var flow = new AuthorizeFlow(factory);
        var id = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var first = await flow.CompleteAsync(id, PiStub.ActivationCode);
        var s = await Session(factory, id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("didn't respond", await first.Content.ReadAsStringAsync());
        Assert.Equal(SessionStatus.Pending, s!.Status);
        Assert.Equal(0, s.Attempts);
        Assert.Equal(HttpStatusCode.Redirect, (await flow.CompleteAsync(id, PiStub.ActivationCode)).StatusCode);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Sec-Fetch-Site", "none")]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Origin", "http://mcp.test")]
    [InlineData("Origin", "null")]
    public async Task Cross_site_posts_are_refused_before_any_pi_call(string header, string value)
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        var id = await flow.StartAsync(query);
        var before = factory.Pi.ToPath("/api/device-activation/create-request").Count();

        var start = new HttpRequestMessage(HttpMethod.Post, "/authorize/start") { Content = new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!) };
        start.Headers.Add(header, value);
        var complete = new HttpRequestMessage(HttpMethod.Post, "/authorize/complete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["session_id"] = id, ["activation_code"] = PiStub.ActivationCode })
        };
        complete.Headers.Add(header, value);

        Assert.Equal(HttpStatusCode.Forbidden, (await flow.Browser.SendAsync(start)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await flow.Browser.SendAsync(complete)).StatusCode);
        Assert.Equal(before, factory.Pi.ToPath("/api/device-activation/create-request").Count());
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/complete-request"));
    }

    [Fact]
    public async Task Same_origin_post_proceeds()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        var start = new HttpRequestMessage(HttpMethod.Post, "/authorize/start") { Content = new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!) };
        start.Headers.Add("Sec-Fetch-Site", "same-origin");
        start.Headers.Add("Origin", "https://MCP.test");

        var response = await flow.Browser.SendAsync(start);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<strong>claude.ai</strong>", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/authorize/start")]
    [InlineData("/authorize/complete")]
    public async Task Json_post_gets_400_not_500(string path)
    {
        using var factory = new WebFactory();
        var response = await new AuthorizeFlow(factory).Browser.PostAsync(path, JsonContent.Create(new { a = 1 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_state_is_invalid_request_before_any_pi_call_and_is_not_echoed()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        query["state"] = new string('s', AuthorizeRequest.MaxState + 1);

        var get = await flow.GetAuthorizeAsync(query);
        var post = await flow.Browser.PostAsync("/authorize/start", new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!));

        foreach (var r in new[] { get, post })
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            var back = QueryHelpers.ParseQuery(r.Headers.Location!.Query);
            Assert.Equal("invalid_request", back["error"]);
            Assert.False(back.ContainsKey("state"));
        }
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/create-request"));
    }

    [Fact]
    public async Task State_is_left_out_of_the_redirect_when_the_client_sent_none()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        query["state"] = null;
        var id = await flow.StartAsync(query);

        var response = await flow.CompleteAsync(id, PiStub.ActivationCode);

        var back = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.False(back.ContainsKey("state"));
        Assert.Equal("https://mcp.test", back["iss"]);
    }

    [Fact]
    public async Task Activation_page_names_the_redirect_host()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var response = await flow.Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(AuthorizeFlow.Query(await flow.RegisterAsync()).Where(kv => kv.Value is not null)!));
        Assert.Contains("<strong>claude.ai</strong>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cimd_client_consent_shows_its_identity_and_an_encoded_name()
    {
        using var factory = new WebFactory();
        const string url = "https://claude.ai/oauth/claude-code-client-metadata";
        factory.Pi.Fixed[url] = () => PiStub.Json(new
        {
            client_id = url,
            client_name = "<b>Evil</b> & \"co\"",
            redirect_uris = new[] { AuthorizeFlow.ClaudeCallback }
        });
        var flow = new AuthorizeFlow(factory);

        var html = await (await flow.GetAuthorizeAsync(AuthorizeFlow.Query(url))).Content.ReadAsStringAsync();

        Assert.Contains("App identity: <strong>claude.ai</strong>", html);
        Assert.Contains("It calls itself “&lt;b&gt;Evil&lt;/b&gt; &amp; &quot;co&quot;” (unverified).", html);
        Assert.DoesNotContain("<b>Evil", html);
    }

    [Fact]
    public async Task Oauth_headers_are_on_the_post_responses_too()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        var start = await flow.Browser.PostAsync("/authorize/start", new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!));
        var id = await flow.StartAsync(query);
        var complete = await flow.CompleteAsync(id, PiStub.ActivationCode);

        foreach (var r in new[] { start, complete })
        {
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
            Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("same-origin", r.Headers.GetValues("Referrer-Policy").Single());
        }
    }

    [Theory]
    [InlineData("2027-01-28T12:00:00", "2027-01-28T12:00:00Z")]
    [InlineData("2027-01-28T12:00:00Z", "2027-01-28T12:00:00Z")]
    [InlineData("2027-01-28T12:00:00+02:00", "2027-01-28T10:00:00Z")]
    [InlineData("2027-01-28T12:00:00-05:00", "2027-01-28T17:00:00Z")]
    public async Task Expiry_is_read_as_utc_or_converted_from_its_offset(string given, string expectedUtc)
    {
        using var factory = new WebFactory();
        factory.Pi.ActivationResult = (PiStub.TokenA, PiStub.UserA, given);
        var client = factory.Services.GetRequiredService<IPiActivationClient>();

        var result = await client.CompleteAsync("act-1", PiStub.ActivationCode, 129600, default);

        Assert.Equal(DateTimeOffset.Parse(expectedUtc), result.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, result.ExpiresAt!.Value.Offset);
    }
}
