using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>
/// IHttpClientFactory pools each client's primary handler across requests, and so across users: a cookie jar
/// on it would hand one user's PI cookies to the next caller.
/// </summary>
public class PiCookieTests
{
    public static readonly TheoryData<string> PiClients = new()
    {
        PiIdentityClient.HttpClientName,
        PiActivationClient.HttpClientName,
        nameof(ProjectInsightApiService) // the typed client's name
    };

    [Theory]
    [MemberData(nameof(PiClients))]
    public void Pi_clients_keep_no_cookie_jar(string clientName)
    {
        using var factory = new WebFactory();
        using var configured = ConfiguredHandler.Of<HttpClientHandler>(factory, clientName);

        Assert.NotNull(configured);
        Assert.False(configured!.UseCookies);
    }

    [Theory]
    [MemberData(nameof(PiClients))]
    public async Task A_cookie_pi_sets_for_one_user_is_not_sent_with_the_next_users_request(string clientName)
    {
        using var factory = new WebFactory();
        using var handler = ConfiguredHandler.Of<HttpClientHandler>(factory, clientName)!;
        var cookiesSeen = new List<string>();
        await using var pi = await StartFakePiAsync(cookiesSeen);
        // One handler instance for both calls, as the factory's pool would give within a handler lifetime.
        using var client = new HttpClient(handler, disposeHandler: false);

        using var a = new HttpRequestMessage(HttpMethod.Get, pi.Url + "api/user/me");
        a.Headers.Authorization = new("Bearer", PiStub.TokenA);
        (await client.SendAsync(a)).EnsureSuccessStatusCode();
        using var b = new HttpRequestMessage(HttpMethod.Get, pi.Url + "api/user/me");
        b.Headers.Authorization = new("Bearer", PiStub.TokenB);
        (await client.SendAsync(b)).EnsureSuccessStatusCode();

        Assert.Equal(["", ""], cookiesSeen);
    }

    /// <summary>A real HTTP server (the stub handler would bypass the cookie jar) that sets a session cookie on every answer.</summary>
    private static async Task<FakePi> StartFakePiAsync(List<string> cookiesSeen)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/api/user/me", (HttpContext http) =>
        {
            lock (cookiesSeen)
                cookiesSeen.Add(http.Request.Headers.Cookie.ToString());
            http.Response.Headers.SetCookie = "PISESSION=session-of-" + http.Request.Headers.Authorization.ToString()["Bearer ".Length..] + "; Path=/";
            return Results.Json(new { id = PiStub.UserA });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakePi(app, address.TrimEnd('/') + "/");
    }

    private sealed record FakePi(WebApplication App, string Url) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
