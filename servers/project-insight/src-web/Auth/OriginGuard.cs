using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// MCP transports must refuse a present-but-unknown Origin (DNS rebinding). Connector backends send
/// none, so the allowlist is empty by default.
/// </summary>
public sealed class OriginGuard(RequestDelegate next, IOptions<AuthOptions> auth)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp")
            && context.Request.Headers.Origin is { Count: > 0 } origin
            && !auth.Value.Origins.Contains(origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(context);
    }
}
