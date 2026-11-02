namespace ProjectInsightMCP.Web.Auth;

public static class OAuthHeaders
{
    // No form-action: it would block the redirect that follows the POST to /authorize/complete.
    private const string Csp = "default-src 'self'; frame-ancestors 'none'";

    public static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.ContentSecurityPolicy = Csp;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>Endpoint filter for /authorize, /authorize/*, /token and /register.</summary>
    public static RouteHandlerBuilder WithOAuthHeaders(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            Apply(context.HttpContext.Response);
            return await next(context);
        });
}
