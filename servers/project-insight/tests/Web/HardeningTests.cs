using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using ProjectInsightMCP.Web.Hosting;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class HardeningTests
{
    [Fact]
    public async Task Eleventh_registration_in_an_hour_is_429()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/register", new { redirect_uris = new[] { "http://127.0.0.1/callback" } })).StatusCode);

        var eleventh = await client.PostAsJsonAsync("/register", new { redirect_uris = new[] { "http://127.0.0.1/callback" } });
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        Assert.True(eleventh.Headers.RetryAfter is not null);
        Assert.Equal("Too many requests. Wait a minute and try again.", await eleventh.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", eleventh.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unknown_tokens_are_limited_before_they_reach_pi()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 31; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer random-token-{i}");
            last = await client.SendAsync(request);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null);
        Assert.Equal(30, factory.Pi.ToPath("/api/user/me").Count());
    }

    [Fact]
    public void Client_key_is_the_address_with_ipv6_grouped_by_64()
    {
        string Key(string? ip)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
            return RateLimits.ClientKey(ctx);
        }

        Assert.Equal("203.0.113.7", Key("203.0.113.7"));
        Assert.Equal("203.0.113.7", Key("::ffff:203.0.113.7"));
        Assert.Equal(Key("2001:db8:1:2::1"), Key("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));
        Assert.Equal("unknown", Key(null));
    }

    [Fact]
    public async Task Rate_limit_partition_is_the_right_most_forwarded_address()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        async Task<HttpStatusCode> Register(string forwardedFor)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/register")
            {
                Content = JsonContent.Create(new { redirect_uris = new[] { "http://127.0.0.1/callback" } })
            };
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            return (await client.SendAsync(request)).StatusCode;
        }

        // The left-most entry is client-supplied and varies; the right-most is what the front end saw.
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Created, await Register($"198.51.100.{i + 1}, 203.0.113.9"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await Register("198.51.100.200, 203.0.113.9"));
        Assert.Equal(HttpStatusCode.Created, await Register("198.51.100.1, 203.0.113.10"));
    }

    [Fact]
    public async Task Responses_carry_hsts()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().GetAsync("/healthz");
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Prune_removes_expired_sessions_and_old_drafts()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<TimeProvider>(time) };
        _ = factory.Client();
        var sessions = factory.Services.GetRequiredService<ISessionStore>();
        var drafts = factory.Services.GetRequiredService<IDraftStore>();
        var past = time.Now.AddHours(-1);
        await sessions.TryCreateAsync(new SignInSession
        {
            Id = "s-old", ClientId = "c", RedirectUri = "https://claude.ai/api/mcp/auth_callback", CodeChallenge = AuthorizeFlow.Challenge,
            PiActivationId = "a", PiActivationUrl = "https://pi.test/x", CookieHash = "h", ExpiresAt = past
        }, 200, 3, past.AddMinutes(-20), default);
        var old = Drafts.New(owner: PiStub.UserA, at: time.Now);
        await drafts.CreateAsync(old);
        time.Now = time.Now.AddDays(8);

        var prune = factory.Services.GetServices<IHostedService>().OfType<PruneService>().Single();
        await prune.PruneOnceAsync(default);

        Assert.Null(await sessions.GetAsync("s-old", default));
        Assert.Null(await drafts.GetAsync(PiStub.UserA, old.Id));
    }

    private static async Task SignInAsync(AuthorizeFlow flow, string clientId)
    {
        var code = await flow.CodeForAsync(clientId);
        var token = await flow.Browser.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = AuthorizeFlow.Verifier,
            ["client_id"] = clientId, ["redirect_uri"] = AuthorizeFlow.ClaudeCallback
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
    }

    [Fact]
    public async Task A_client_that_keeps_signing_in_survives_pruning_past_the_token_lifetime()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<TimeProvider>(time) };
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var clients = factory.Services.GetRequiredService<IClientStore>();
        var prune = factory.Services.GetServices<IHostedService>().OfType<PruneService>().Single();

        time.Now = time.Now.AddHours(1);
        await SignInAsync(flow, clientId);
        time.Now = time.Now.AddDays(89);
        await SignInAsync(flow, clientId);
        // Day 150: past 120 days from the first sign-in, so the client survives only because day 89 counted.
        time.Now = time.Now.AddDays(61);
        await prune.PruneOnceAsync(default);

        Assert.NotNull(await clients.GetAsync(clientId, default));

        // 90-day token + 30 days of grace after the last sign-in (day 89): gone after day 209.
        time.Now = time.Now.AddDays(60);
        await prune.PruneOnceAsync(default);
        Assert.Null(await clients.GetAsync(clientId, default));
    }

    [Fact]
    public async Task A_registration_nobody_signed_in_with_is_pruned_after_a_day_but_a_used_one_is_kept()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<TimeProvider>(time) };
        var flow = new AuthorizeFlow(factory);
        var never = await flow.RegisterAsync();
        var used = await flow.RegisterAsync();
        await SignInAsync(flow, used);
        var clients = factory.Services.GetRequiredService<IClientStore>();
        var prune = factory.Services.GetServices<IHostedService>().OfType<PruneService>().Single();

        time.Now = time.Now.AddHours(25);
        await prune.PruneOnceAsync(default);

        Assert.Null(await clients.GetAsync(never, default));
        Assert.NotNull(await clients.GetAsync(used, default));
    }

    [Fact]
    public async Task A_client_registered_under_a_day_ago_is_not_pruned()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<TimeProvider>(time) };
        var clientId = await new AuthorizeFlow(factory).RegisterAsync();
        var prune = factory.Services.GetServices<IHostedService>().OfType<PruneService>().Single();

        time.Now = time.Now.AddHours(23);
        await prune.PruneOnceAsync(default);

        Assert.NotNull(await factory.Services.GetRequiredService<IClientStore>().GetAsync(clientId, default));
    }
}
