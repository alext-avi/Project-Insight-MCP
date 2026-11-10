using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web.Hosting;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

public static class AuthorizeEndpoints
{
    public const int MaxAttempts = 5;
    public const int MaxOutstandingSessions = 200;
    /// <summary>Per client network (IPv4 address or IPv6 /64), so a few addresses can't take the whole global cap.</summary>
    public const int MaxSessionsPerClient = 3;
    public const string CookieName = "__Host-pi_auth";
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FullRetryAfter = TimeSpan.FromMinutes(5);

    public static void MapAuthorize(this WebApplication app)
    {
        app.MapGet("/authorize", async (HttpRequest http, ClientResolver clients, RedirectPolicy policy, IOptions<WebOptions> web, CancellationToken ct) =>
        {
            var fields = http.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            var (request, failure) = await AuthorizeRequest.ValidateAsync(fields, clients, policy, web.Value, ct);
            return failure ?? AuthorizePages.Consent(request!, fields);
        }).WithOAuthHeaders().RequireRateLimiting(RateLimits.Authorize);

        app.MapPost("/authorize/start", async (HttpContext http, ClientResolver clients, RedirectPolicy policy, IOptions<WebOptions> web,
            IPiActivationClient pi, ISessionStore sessions, IClientStore registered, IConfiguration config, TimeProvider time, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("ProjectInsightMCP.Web.Authorize");
            if (CrossSiteOrNotForm(http, web.Value) is { } refused)
                return refused;
            if (await FormInput.ReadAsync(http.Request, ct) is not { } form)
                return AuthorizePages.Error(400, "This request was malformed or too large.");
            var fields = form.ToDictionary(f => f.Key, f => (string?)f.Value.ToString());
            var (request, failure) = await AuthorizeRequest.ValidateAsync(fields, clients, policy, web.Value, ct);
            if (failure is not null)
                return failure;

            // A browser starting again gives up the sign-in it started before: the new cookie replaces the one that
            // sign-in is bound to, so it could never be finished, yet would hold one of its network's slots. A client
            // that drops cookies supersedes nothing, and the per-network cap still counts each of its starts.
            if (http.Request.Cookies[CookieName] is { } previous && CodeProtector.IsWellFormed(previous))
                await DeleteByCookieQuietlyAsync(sessions, TokenHash.Of(previous), logger);

            // The slot is reserved before PI is asked, so a start the caps refuse never opens a PI activation.
            var cookie = CodeProtector.NewCode();
            var now = time.GetUtcNow();
            var session = new SignInSession
            {
                Id = CodeProtector.NewCode(),
                ClientId = request!.Client.ClientId,
                RedirectUri = request.RedirectUri,
                State = request.State,
                CodeChallenge = request.CodeChallenge,
                Resource = request.Resource,
                PiActivationId = "",
                PiActivationUrl = "",
                CookieHash = TokenHash.Of(cookie),
                ExpiresAt = now + SessionLife,
                Status = SessionStatus.Starting,
                // The rate limiter's partition, so one network can't hold more than its share of the global cap.
                ClientKeyHash = NetworkKey.Of(RateLimits.ClientKey(http))
            };
            // Not tied to the request: a client that aborts after the store accepted the reservation would otherwise
            // leave it held, unreleased, for the session's whole life. From here the finally below releases it.
            switch (await sessions.TryCreateAsync(session, MaxOutstandingSessions, MaxSessionsPerClient, now, CancellationToken.None))
            {
                case SessionCreate.ClientFull:
                    // Another sign-in from this network ends within the session life at most.
                    http.Response.Headers.RetryAfter = ((int)SessionLife.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    return AuthorizePages.Error(429, "Too many sign-ins are in progress from your network. Finish one in the browser that started it, or try again in 15 minutes.");
                case SessionCreate.Full:
                    http.Response.Headers.RetryAfter = ((int)FullRetryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    return AuthorizePages.Error(503, "Too many sign-ins in progress. Try again in a few minutes.");
            }

            // A registration with a sign-in under way is the newest, not the oldest, when the cap evicts or the prune
            // runs, so it is still there when /token marks it used. Never fails the start.
            if (!request.Client.IsMetadataDocument)
            {
                try
                {
                    await registered.TouchAsync(request.Client.ClientId, now, signedIn: false, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not record a sign-in start for a registered client: {Error}", ex.GetType().Name);
                }
            }

            var started = false;
            try
            {
                var redirectHost = new Uri(request.RedirectUri).Host;
                var activation = await pi.StartAsync($"MCP connector ({redirectHost})", ct);
                var piHost = new Uri(config["ProjectInsight:BaseUrl"]!).Host;
                if (activation is null || activation.ActivationUrl.Scheme != Uri.UriSchemeHttps || activation.ActivationUrl.Host != piHost)
                    return AuthorizePages.Error(502, "Project Insight didn't start the sign-in. Try again in a moment.");

                // The re-read supplies the store's Version; nobody else knows the id yet, so the replace only fails if the store does.
                if (await sessions.GetAsync(session.Id, ct) is not { } reserved
                    || !await sessions.TryReplaceAsync(reserved with
                    {
                        PiActivationId = activation.ActivationId,
                        PiActivationUrl = activation.ActivationUrl.AbsoluteUri,
                        Status = SessionStatus.Pending
                    }, ct))
                    return AuthorizePages.Error(503, "Something went wrong starting the sign-in. Try again in a moment.");
                // A client already gone never gets the cookie, so this sign-in could never be finished: release it.
                ct.ThrowIfCancellationRequested();
                started = true;

                http.Response.Cookies.Append(CookieName, cookie, new CookieOptions
                {
                    Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = SessionLife
                });
                return AuthorizePages.Activation(session.Id, activation.ActivationUrl, new Uri(request.RedirectUri), error: null);
            }
            finally
            {
                // A start that didn't finish gives its slot back.
                if (!started)
                    await DeleteQuietlyAsync(sessions, session.Id, logger);
            }
        }).WithOAuthHeaders().RequireRateLimiting(RateLimits.Authorize);

        app.MapPost("/authorize/complete", async (HttpContext http, ISessionStore sessions, ICodeStore codes, IPiActivationClient pi,
            IOptions<WebOptions> web, IOptions<AuthOptions> auth, TimeProvider time, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("ProjectInsightMCP.Web.Authorize");
            if (CrossSiteOrNotForm(http, web.Value) is { } refused)
                return refused;
            if (await FormInput.ReadAsync(http.Request, ct) is not { } form)
                return AuthorizePages.Error(400, "This request was malformed or too large.");
            var sessionId = form["session_id"].ToString();
            var code = form["activation_code"].ToString().Trim();
            var now = time.GetUtcNow();

            // Only the shape /authorize/start issues reaches the store: anything else would make a bad table key.
            var session = CodeProtector.IsWellFormed(sessionId) ? await sessions.GetAsync(sessionId, ct) : null;
            if (session is null || session.ExpiresAt <= now)
                return AuthorizePages.Error(400, "This sign-in has expired. Start again from Claude or ChatGPT.");
            var cookie = http.Request.Cookies[CookieName];
            if (cookie is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(TokenHash.Of(cookie)), Encoding.ASCII.GetBytes(session.CookieHash)))
                return AuthorizePages.Error(400, "This sign-in was started in a different browser. Start again from Claude or ChatGPT.");
            if (session.Status != SessionStatus.Pending)
                return AuthorizePages.Error(409, "This sign-in is already finished or in progress. Start again from Claude or ChatGPT.");
            var activationUrl = new Uri(session.PiActivationUrl);
            if (code.Length is 0 or > 64)
                return AuthorizePages.Activation(session.Id, activationUrl, new Uri(session.RedirectUri), "Paste the activation code Project Insight shows.");

            // Claim the session before calling PI, so a double submit completes it exactly once. Not tied to the
            // request: an aborted POST must not leave the claim undecided while the work below carries on.
            var claimed = session with { Status = SessionStatus.Completing };
            if (!await sessions.TryReplaceAsync(claimed, CancellationToken.None))
                return AuthorizePages.Error(409, "This sign-in is already finished or in progress. Start again from Claude or ChatGPT.");

            // Past the claim nothing may depend on the request staying open: a double-click aborts the first
            // POST, and PI may already have minted a token. The client's own timeout still bounds the call.
            try
            {
                // The re-read supplies the store's new Version for the later replace.
                claimed = await sessions.GetAsync(session.Id, CancellationToken.None) ?? claimed;

                var result = await pi.CompleteAsync(session.PiActivationId, code, auth.Value.PiTokenLifetimeDays * 24 * 60, CancellationToken.None);
                if (result.Outcome != PiActivationOutcome.Ok)
                {
                    var attempts = claimed.Attempts + (result.Outcome == PiActivationOutcome.WrongCode ? 1 : 0);
                    if (attempts >= MaxAttempts)
                    {
                        await sessions.DeleteAsync(session.Id, CancellationToken.None);
                        logger.LogWarning("Sign-in {Session} ended after {Attempts} wrong activation codes", TokenHash.Of(session.Id)[..12], attempts);
                        return AuthorizePages.Error(400, "Too many incorrect codes. Start again from Claude or ChatGPT.");
                    }
                    await sessions.TryReplaceAsync(claimed with { Status = SessionStatus.Pending, Attempts = attempts }, CancellationToken.None);
                    if (result.Outcome == PiActivationOutcome.WrongCode)
                        logger.LogWarning("Wrong activation code for sign-in {Session} (attempt {Attempt})", TokenHash.Of(session.Id)[..12], attempts);
                    return AuthorizePages.Activation(session.Id, activationUrl, new Uri(session.RedirectUri), result.Outcome == PiActivationOutcome.WrongCode
                        ? "That code didn't work. Check it and try again."
                        : "Project Insight didn't respond. Try again in a moment.");
                }

                await sessions.DeleteAsync(session.Id, CancellationToken.None);
                if (!auth.Value.PiUserIds.Contains(result.UserId!))
                    return AuthorizePages.Error(403, "This server isn't open to your Project Insight account.");

                var authCode = CodeProtector.NewCode();
                var payload = new CodePayload(result.ApiToken!,
                    result.ExpiresAt ?? now.AddDays(auth.Value.PiTokenLifetimeDays),
                    session.ClientId, session.RedirectUri, session.CodeChallenge, session.Resource, result.UserId!);
                await codes.SaveAsync(new StoredCode(CodeProtector.Hash(authCode),
                    CodeProtector.Seal(authCode, JsonSerializer.SerializeToUtf8Bytes(payload)), now + CodeLife), CancellationToken.None);

                var back = new Dictionary<string, string?> { ["code"] = authCode };
                if (session.State is not null)
                    back["state"] = session.State;
                back["iss"] = web.Value.Issuer;
                return Results.Redirect(QueryHelpers.AddQueryString(session.RedirectUri, back));
            }
            catch (Exception ex)
            {
                logger.LogWarning("Sign-in {Session} failed after the claim: {Error}", TokenHash.Of(session.Id)[..12], ex.GetType().Name);
                await RestorePendingAsync(sessions, session.Id, logger);
                return AuthorizePages.Error(503, "Something went wrong finishing the sign-in. Start again from Claude or ChatGPT.");
            }
        }).WithOAuthHeaders().RequireRateLimiting(RateLimits.Complete);
    }

    /// <summary>Best effort: puts a claimed session back to Pending without counting an attempt.</summary>
    private static async Task RestorePendingAsync(ISessionStore sessions, string id, ILogger logger)
    {
        try
        {
            if (await sessions.GetAsync(id, CancellationToken.None) is { Status: SessionStatus.Completing } current)
                await sessions.TryReplaceAsync(current with { Status = SessionStatus.Pending }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not restore sign-in {Session}: {Error}", TokenHash.Of(id)[..12], ex.GetType().Name);
        }
    }

    private static async Task DeleteByCookieQuietlyAsync(ISessionStore sessions, string cookieHash, ILogger logger)
    {
        try
        {
            await sessions.DeleteByCookieAsync(cookieHash, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // It expires with the session life anyway; the new start goes ahead.
            logger.LogWarning("Could not release a browser's earlier sign-in: {Error}", ex.GetType().Name);
        }
    }

    private static async Task DeleteQuietlyAsync(ISessionStore sessions, string id, ILogger logger)
    {
        try
        {
            await sessions.DeleteAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // It expires with the session life anyway.
            logger.LogWarning("Could not release sign-in {Session}: {Error}", TokenHash.Of(id)[..12], ex.GetType().Name);
        }
    }

    /// <summary>
    /// A browser POST from another site must not skip the consent page, and these endpoints read forms only.
    /// Requests carrying neither Sec-Fetch-Site nor Origin (non-browser clients) pass.
    /// </summary>
    private static IResult? CrossSiteOrNotForm(HttpContext http, WebOptions web)
    {
        var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin")
            return AuthorizePages.Error(403, "This request came from another site. Start again from Claude or ChatGPT.");
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !SameOrigin(origin, web.Issuer))
            return AuthorizePages.Error(403, "This request came from another site. Start again from Claude or ChatGPT.");
        if (!FormInput.IsUrlEncoded(http.Request))
            return AuthorizePages.Error(400, "This request must be a form post.");
        return null;
    }

    private static bool SameOrigin(string origin, string issuer)
        => Uri.TryCreate(origin, UriKind.Absolute, out var a) && Uri.TryCreate(issuer, UriKind.Absolute, out var b)
           && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
