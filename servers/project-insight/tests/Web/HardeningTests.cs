using System.Net;
using System.Net.Http.Json;
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
    }

    [Fact]
    public async Task Unknown_tokens_are_limited_before_they_reach_pi()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer random-token-{i}");
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(30, factory.Pi.ToPath("/api/user/me").Count());
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
        }, 200, past.AddMinutes(-20), default);
        var old = Drafts.New(owner: PiStub.UserA, at: time.Now);
        await drafts.CreateAsync(old);
        time.Now = time.Now.AddDays(8);

        var prune = factory.Services.GetServices<IHostedService>().OfType<PruneService>().Single();
        // Drafts are pruned on every 12th run, and the host's own first run may have used the first.
        for (var i = 0; i < 12; i++)
            await prune.PruneOnceAsync(default);

        Assert.Null(await sessions.GetAsync("s-old", default));
        Assert.Null(await drafts.GetAsync(PiStub.UserA, old.Id));
    }
}
