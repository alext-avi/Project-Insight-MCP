using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Pass-through bearer authentication: the bearer token is a PI token. PI says whose it is;
/// the allowlist says whether they may use this server.
/// </summary>
public sealed partial class PiBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    PiIdentityClient identity, IOptions<AuthOptions> auth, IOptions<WebOptions> web,
    PartitionedRateLimiter<HttpContext> unknownTokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PiBearer";
    private const string Outcome = "pi.auth.outcome";

    // RFC 6750 b64token. Anything else never reaches a header parser that could echo it in an exception.
    [GeneratedRegex(@"^[A-Za-z0-9\-._~+/]+=*$")]
    private static partial Regex Token68();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (token.Length is 0 or > 4096 || !Token68().IsMatch(token))
            return AuthenticateResult.Fail("Malformed bearer token.");

        if (!identity.IsCached(token))
        {
            using var lease = unknownTokens.AttemptAcquire(Context);
            if (!lease.IsAcquired)
            {
                Context.Items[Outcome] = "limited";
                return AuthenticateResult.Fail("Too many unrecognised tokens.");
            }
        }

        var who = await identity.ResolveAsync(token, Context.RequestAborted);
        switch (who.Status)
        {
            case PiIdentityStatus.Unavailable:
                Context.Items[Outcome] = "unavailable";
                return AuthenticateResult.Fail("Project Insight is unavailable.");
            case PiIdentityStatus.Rejected:
                return AuthenticateResult.Fail("Project Insight rejected the token.");
        }
        if (!auth.Value.PiUserIds.Contains(who.UserId!))
        {
            Context.Items[Outcome] = "forbidden";
            return AuthenticateResult.Fail("This PI user is not allowed on this server.");
        }

        // The MCP endpoint is stateless, so tools resolve services from this request's scope.
        var services = Context.RequestServices;
        services.GetRequiredService<ApiTokenAccessor>().ApiToken = token;
        services.GetRequiredService<PiCaller>().UserId = who.UserId;

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", who.UserId!)], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        switch (Context.Items[Outcome] as string)
        {
            case "unavailable":
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                Response.Headers.RetryAfter = "30";
                return Response.WriteAsync("Project Insight is not responding. Try again shortly.");
            case "limited":
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return Task.CompletedTask;
            case "forbidden":
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return Response.WriteAsync("This server isn't open to your Project Insight account.");
            default:
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{web.Value.ResourceMetadataUrl}\", scope=\"pi\"";
                return Task.CompletedTask;
        }
    }
}
