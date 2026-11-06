using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Plays the browser: consent page, start, activation page, complete.</summary>
public sealed partial class AuthorizeFlow(WebFactory factory)
{
    public const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    public const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    public const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    public HttpClient Browser { get; } = factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = WebFactory.BaseAddress,
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>The __Host-pi_auth cookie value the last consent POST set, for tests that scan for it.</summary>
    public string? LastAuthCookieValue { get; private set; }

    public static Dictionary<string, string?> Query(string clientId, string redirect = ClaudeCallback, string? resource = "https://mcp.test/mcp") => new()
    {
        ["response_type"] = "code",
        ["client_id"] = clientId,
        ["redirect_uri"] = redirect,
        ["state"] = "state-123",
        ["code_challenge"] = Challenge,
        ["code_challenge_method"] = "S256",
        ["scope"] = "pi",
        ["resource"] = resource
    };

    public Task<HttpResponseMessage> GetAuthorizeAsync(Dictionary<string, string?> query)
        => Browser.GetAsync(QueryHelpers.AddQueryString("/authorize", query.Where(kv => kv.Value is not null)!));

    /// <summary>Posts the consent form and returns the activation page's session id.</summary>
    public async Task<string> StartAsync(Dictionary<string, string?> query)
    {
        var response = await Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!));
        var html = await response.Content.ReadAsStringAsync();
        LastAuthCookieValue = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("__Host-pi_auth=", StringComparison.Ordinal))?["__Host-pi_auth=".Length..]
            : null;
        // Fail here, not later: an error page would hand back "" and let a test pass for the wrong reason.
        var field = SessionField().Match(html);
        if (response.StatusCode != HttpStatusCode.OK || !field.Success)
            throw new InvalidOperationException($"Expected the activation page, got {(int)response.StatusCode}: {html}");
        return field.Groups[1].Value;
    }

    public Task<HttpResponseMessage> CompleteAsync(string sessionId, string code)
        => Browser.PostAsync("/authorize/complete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["session_id"] = sessionId,
            ["activation_code"] = code
        }));

    /// <summary>The whole browser side for a registered client; returns the code from the redirect.</summary>
    public async Task<string> CodeForAsync(string clientId, string redirect = ClaudeCallback)
    {
        var session = await StartAsync(Query(clientId, redirect));
        var response = await CompleteAsync(session, PiStub.ActivationCode);
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Expected a redirect, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"]!;
    }

    public async Task<string> RegisterAsync(string redirect = ClaudeCallback)
    {
        var response = await Browser.PostAsync("/register", JsonContent.Create(new { redirect_uris = new[] { redirect } }));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    [GeneratedRegex("name=\"session_id\" value=\"([^\"]+)\"")]
    private static partial Regex SessionField();
}
