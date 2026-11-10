using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class AuthorizeTests
{
    [Fact]
    public async Task Consent_page_names_the_redirect_host_and_starts_nothing_at_pi()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();

        var response = await flow.GetAuthorizeAsync(AuthorizeFlow.Query(clientId));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<strong>claude.ai</strong>", html);
        Assert.Contains("If someone sent you this link, close this page.", html);
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/create-request"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Loopback_client_is_named_as_an_app_on_this_computer_not_an_address()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync("http://127.0.0.1/callback");
        var query = AuthorizeFlow.Query(clientId, redirect: "http://127.0.0.1:33418/callback");

        var consent = await (await flow.GetAuthorizeAsync(query)).Content.ReadAsStringAsync();
        // An empty code re-renders the activation page without calling PI.
        var activation = await (await flow.CompleteAsync(await flow.StartAsync(query), "")).Content.ReadAsStringAsync();

        Assert.Contains("<strong>An app on this computer</strong> is asking", consent);
        Assert.Contains("Connecting <strong>An app on this computer</strong>", activation);
        Assert.DoesNotContain("<strong>127.0.0.1</strong>", consent + activation);
    }

    [Fact]
    public async Task Unregistered_redirect_gets_an_error_page_not_a_redirect()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync("http://127.0.0.1/callback");

        var response = await flow.GetAuthorizeAsync(AuthorizeFlow.Query(clientId, redirect: AuthorizeFlow.ClaudeCallback));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Unknown_client_gets_an_error_page_not_a_redirect()
    {
        using var factory = new WebFactory();
        var response = await new AuthorizeFlow(factory).GetAuthorizeAsync(AuthorizeFlow.Query("dcr_made_up"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("code_challenge_method", "plain", "invalid_request")]
    [InlineData("code_challenge", "short", "invalid_request")]
    [InlineData("response_type", "token", "unsupported_response_type")]
    [InlineData("scope", "admin", "invalid_scope")]
    [InlineData("resource", "https://other.test/mcp", "invalid_target")]
    public async Task Bad_parameters_go_back_to_the_client_with_state_and_iss(string key, string value, string error)
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        query[key] = value;

        var response = await flow.GetAuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var back = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(error, back["error"]);
        Assert.Equal("state-123", back["state"]);
        Assert.Equal("https://mcp.test", back["iss"]);
        Assert.StartsWith(AuthorizeFlow.ClaudeCallback, response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Start_creates_a_pi_activation_labelled_with_the_redirect_host_and_sets_the_cookie()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var response = await flow.Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(AuthorizeFlow.Query(await flow.RegisterAsync()).Where(kv => kv.Value is not null)!));

        var create = Assert.Single(factory.Pi.ToPath("/api/device-activation/create-request"));
        Assert.Contains("MCP connector (claude.ai)", create.Body);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-pi_auth=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://pi.test/device/activate/act-1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Correct_code_redirects_with_code_state_and_iss_and_asks_pi_for_a_90_day_token()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var back = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.False(string.IsNullOrEmpty(back["code"]));
        Assert.Equal("state-123", back["state"]);
        Assert.Equal("https://mcp.test", back["iss"]);
        Assert.DoesNotContain(PiStub.TokenA, response.Headers.Location.ToString());
        Assert.Contains("timeoutMinutes=129600", Assert.Single(factory.Pi.ToPath("/api/device-activation/complete-request")).Uri.Query);
    }

    [Fact]
    public async Task Wrong_codes_re_render_and_the_fifth_ends_the_session()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        for (var i = 1; i < 5; i++)
        {
            var retry = await flow.CompleteAsync(session, "WRONG");
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Contains("didn't work", await retry.Content.ReadAsStringAsync());
        }
        var last = await flow.CompleteAsync(session, "WRONG");
        Assert.Contains("Too many incorrect codes", await last.Content.ReadAsStringAsync());

        var after = await flow.CompleteAsync(session, PiStub.ActivationCode);
        Assert.NotEqual(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async Task Completion_from_another_browser_is_refused()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var other = new AuthorizeFlow(factory); // fresh cookie jar
        var response = await other.CompleteAsync(session, PiStub.ActivationCode);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/complete-request"));
    }

    [Fact]
    public async Task Double_submit_gives_exactly_one_redirect_and_a_plain_page_for_the_other()
    {
        var bothRead = new Rendezvous();
        using var factory = new WebFactory
        {
            ExtraServices = s => s.AddSingleton<ISessionStore>(new RendezvousSessionStore(new MemorySessionStore(), bothRead))
        };
        var releasePi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Pi.ActivationCompletionGate = releasePi.Task;
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        // Both posts read the session while it is still pending; the winner is then held inside PI,
        // so the other must answer on its own instead of waiting for the winner to finish.
        bothRead.Arm();
        var first = flow.CompleteAsync(session, PiStub.ActivationCode);
        var second = flow.CompleteAsync(session, PiStub.ActivationCode);
        var loser = await (await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(10)));
        releasePi.SetResult();
        var responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Redirect);
        Assert.Single(factory.Pi.ToPath("/api/device-activation/complete-request"));
    }

    [Fact]
    public async Task User_not_on_the_allowlist_gets_no_code()
    {
        using var factory = new WebFactory();
        factory.Pi.ActivationResult = (PiStub.StrangerToken, "0f0e2a4c-9999-4a4a-9b9b-cccccccccccc", "2027-01-28T12:00:00");
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("isn't open to your Project Insight account", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Activation_link_on_another_host_is_not_rendered()
    {
        using var factory = new WebFactory();
        factory.Pi.Fixed["https://pi.test/api/device-activation/create-request"] = () =>
            PiStub.Json(new { DeviceActivationId = "act-2", DeviceActivationRequestUrl = "https://evil.example/activate" });
        var flow = new AuthorizeFlow(factory);
        var response = await flow.Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(AuthorizeFlow.Query(await flow.RegisterAsync()).Where(kv => kv.Value is not null)!));

        Assert.DoesNotContain("evil.example", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Both_sign_in_pages_load_the_same_origin_script_that_disables_the_buttons_on_submit()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());

        var consent = await (await flow.GetAuthorizeAsync(query)).Content.ReadAsStringAsync();
        var activation = await (await flow.Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!))).Content.ReadAsStringAsync();
        var script = await flow.Browser.GetAsync("/oauth.js");

        Assert.Contains("<script src=\"/oauth.js\" defer></script>", consent);
        Assert.Contains("<script src=\"/oauth.js\" defer></script>", activation);
        Assert.DoesNotContain("onsubmit", consent + activation);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", script.Headers.GetValues("X-Content-Type-Options").Single());
        var body = await script.Content.ReadAsStringAsync();
        Assert.Contains("\"submit\"", body);
        Assert.Contains("button.disabled = true", body);
        Assert.Contains("Checking", body);
    }
}
