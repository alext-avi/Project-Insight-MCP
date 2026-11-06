using System.Text.Json;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web.Hosting;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>Authorization code → the PI token (pass-through). No refresh tokens.</summary>
public static class TokenEndpoint
{
    public static void MapToken(this WebApplication app)
    {
        app.MapPost("/token", async (HttpRequest request, ICodeStore codes, IOptions<WebOptions> web, TimeProvider time,
            CancellationToken ct) =>
        {
            if (!FormInput.IsUrlEncoded(request))
                return Error("invalid_request", "Use application/x-www-form-urlencoded.");
            if (await FormInput.ReadAsync(request, ct) is not { } form)
                return Error("invalid_request", "The request body is malformed or too large.");
            if (form["grant_type"] != "authorization_code")
                return Error("unsupported_grant_type", "Only authorization_code is supported.");
            string code = form["code"].ToString(), verifier = form["code_verifier"].ToString(),
                   clientId = form["client_id"].ToString(), redirect = form["redirect_uri"].ToString(), resource = form["resource"].ToString();
            if (code.Length == 0 || verifier.Length == 0 || clientId.Length == 0 || redirect.Length == 0)
                return Error("invalid_request", "code, code_verifier, client_id and redirect_uri are required.");

            // Taken (deleted) before any check, so a wrong verifier burns the code and can't be retried.
            var stored = await codes.TryTakeAsync(CodeProtector.Hash(code), ct);
            var now = time.GetUtcNow();
            if (stored is null || stored.ExpiresAt <= now || CodeProtector.Open(code, stored.Payload) is not { } plain)
                return Error("invalid_grant", "The code is invalid, expired or already used.");
            var payload = JsonSerializer.Deserialize<CodePayload>(plain)!;
            if (payload.ClientId != clientId || payload.RedirectUri != redirect)
                return Error("invalid_grant", "The code was issued to another client or redirect URI.");
            // Single-resource server: /authorize only accepts this server's resource, so payload.Resource equals it by construction.
            if (resource.Length > 0 && !web.Value.IsResource(resource))
                return Error("invalid_target", "Unknown resource.");
            if (!Pkce.Verify(verifier, payload.CodeChallenge))
                return Error("invalid_grant", "PKCE verification failed.");

            var expiresIn = (long)Math.Max(60, (payload.ExpiresAt - now).TotalSeconds);
            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = payload.PiToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = expiresIn
            });
        }).WithOAuthHeaders().RequireRateLimiting(RateLimits.Token);
    }

    private static IResult Error(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: 400);
}
