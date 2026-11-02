using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

public static class AuthorizeEndpoints
{
    public const int MaxAttempts = 5;
    public const int MaxOutstandingSessions = 200;
    public const string CookieName = "__Host-pi_auth";
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(5);

    public static void MapAuthorize(this WebApplication app)
    {
        app.MapGet("/authorize", async (HttpRequest http, ClientResolver clients, RedirectPolicy policy, IOptions<WebOptions> web, CancellationToken ct) =>
        {
            var fields = http.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            var (request, failure) = await AuthorizeRequest.ValidateAsync(fields, clients, policy, web.Value, ct);
            return failure ?? AuthorizePages.Consent(request!, fields);
        }).WithOAuthHeaders();

        app.MapPost("/authorize/start", async (HttpContext http, ClientResolver clients, RedirectPolicy policy, IOptions<WebOptions> web,
            IPiActivationClient pi, ISessionStore sessions, IConfiguration config, TimeProvider time, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var fields = form.ToDictionary(f => f.Key, f => (string?)f.Value.ToString());
            var (request, failure) = await AuthorizeRequest.ValidateAsync(fields, clients, policy, web.Value, ct);
            if (failure is not null)
                return failure;

            var redirectHost = new Uri(request!.RedirectUri).Host;
            var activation = await pi.StartAsync($"MCP connector ({redirectHost})", ct);
            var piHost = new Uri(config["ProjectInsight:BaseUrl"]!).Host;
            if (activation is null || activation.ActivationUrl.Scheme != Uri.UriSchemeHttps || activation.ActivationUrl.Host != piHost)
                return AuthorizePages.Error(502, "Project Insight didn't start the sign-in. Try again in a moment.");

            var cookie = CodeProtector.NewCode();
            var now = time.GetUtcNow();
            var session = new SignInSession
            {
                Id = CodeProtector.NewCode(),
                ClientId = request.Client.ClientId,
                RedirectUri = request.RedirectUri,
                State = request.State,
                CodeChallenge = request.CodeChallenge,
                Resource = request.Resource,
                PiActivationId = activation.ActivationId,
                PiActivationUrl = activation.ActivationUrl.AbsoluteUri,
                CookieHash = TokenHash.Of(cookie),
                ExpiresAt = now + SessionLife
            };
            if (!await sessions.TryCreateAsync(session, MaxOutstandingSessions, now, ct))
                return AuthorizePages.Error(503, "Too many sign-ins in progress. Try again in a few minutes.");

            http.Response.Cookies.Append(CookieName, cookie, new CookieOptions
            {
                Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = SessionLife
            });
            return AuthorizePages.Activation(session.Id, activation.ActivationUrl, error: null);
        }).WithOAuthHeaders();

        app.MapPost("/authorize/complete", async (HttpContext http, ISessionStore sessions, ICodeStore codes, IPiActivationClient pi,
            IOptions<WebOptions> web, IOptions<AuthOptions> auth, TimeProvider time, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("ProjectInsightMCP.Web.Authorize");
            var form = await http.Request.ReadFormAsync(ct);
            var sessionId = form["session_id"].ToString();
            var code = form["activation_code"].ToString().Trim();
            var now = time.GetUtcNow();

            var session = sessionId.Length > 0 ? await sessions.GetAsync(sessionId, ct) : null;
            if (session is null || session.ExpiresAt <= now)
                return AuthorizePages.Error(400, "This sign-in has expired. Start again from Claude or ChatGPT.");
            var cookie = http.Request.Cookies[CookieName];
            if (cookie is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(TokenHash.Of(cookie)), Encoding.ASCII.GetBytes(session.CookieHash)))
                return AuthorizePages.Error(400, "This sign-in was started in a different browser. Start again from Claude or ChatGPT.");
            if (session.Status != SessionStatus.Pending)
                return AuthorizePages.Error(409, "This sign-in is already finished or in progress. Return to Claude or ChatGPT.");
            var activationUrl = new Uri(session.PiActivationUrl);
            if (code.Length is 0 or > 64)
                return AuthorizePages.Activation(session.Id, activationUrl, "Paste the activation code Project Insight shows.");

            // Claim the session before calling PI, so a double submit completes it exactly once.
            var claimed = session with { Status = SessionStatus.Completing };
            if (!await sessions.TryReplaceAsync(claimed, ct))
                return AuthorizePages.Error(409, "This sign-in is already finished or in progress. Return to Claude or ChatGPT.");
            claimed = await sessions.GetAsync(session.Id, ct) ?? claimed;

            var result = await pi.CompleteAsync(session.PiActivationId, code, auth.Value.PiTokenLifetimeDays * 24 * 60, ct);
            if (result.Outcome != PiActivationOutcome.Ok)
            {
                var attempts = claimed.Attempts + (result.Outcome == PiActivationOutcome.WrongCode ? 1 : 0);
                if (attempts >= MaxAttempts)
                {
                    await sessions.DeleteAsync(session.Id, ct);
                    logger.LogWarning("Sign-in {Session} ended after {Attempts} wrong activation codes", TokenHash.Of(session.Id)[..12], attempts);
                    return AuthorizePages.Error(400, "Too many incorrect codes. Start again from Claude or ChatGPT.");
                }
                await sessions.TryReplaceAsync(claimed with { Status = SessionStatus.Pending, Attempts = attempts }, ct);
                if (result.Outcome == PiActivationOutcome.WrongCode)
                    logger.LogWarning("Wrong activation code for sign-in {Session} (attempt {Attempt})", TokenHash.Of(session.Id)[..12], attempts);
                return AuthorizePages.Activation(session.Id, activationUrl, result.Outcome == PiActivationOutcome.WrongCode
                    ? "That code didn't work. Check it and try again."
                    : "Project Insight didn't respond. Try again in a moment.");
            }

            await sessions.DeleteAsync(session.Id, ct);
            if (!auth.Value.PiUserIds.Contains(result.UserId!))
                return AuthorizePages.Error(403, "This server isn't open to your Project Insight account.");

            var authCode = CodeProtector.NewCode();
            var payload = new CodePayload(result.ApiToken!,
                result.ExpiresAt ?? now.AddDays(auth.Value.PiTokenLifetimeDays),
                session.ClientId, session.RedirectUri, session.CodeChallenge, session.Resource, result.UserId!);
            await codes.SaveAsync(new StoredCode(CodeProtector.Hash(authCode),
                CodeProtector.Seal(authCode, JsonSerializer.SerializeToUtf8Bytes(payload)), now + CodeLife), ct);

            var back = new Dictionary<string, string?> { ["code"] = authCode };
            if (session.State is not null)
                back["state"] = session.State;
            back["iss"] = web.Value.Issuer;
            return Results.Redirect(QueryHelpers.AddQueryString(session.RedirectUri, back));
        }).WithOAuthHeaders();
    }
}
