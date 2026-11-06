using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>RFC 9728 protected-resource metadata and RFC 8414 authorization-server metadata.</summary>
public static class OAuthMetadata
{
    public static void MapOAuthMetadata(this WebApplication app)
    {
        IResult Resource(IOptions<WebOptions> web) => Results.Json(new JsonObject
        {
            ["resource"] = web.Value.Resource,
            ["authorization_servers"] = new JsonArray(web.Value.Issuer),
            ["scopes_supported"] = new JsonArray("pi"),
            ["bearer_methods_supported"] = new JsonArray("header")
        });

        app.MapGet("/.well-known/oauth-protected-resource/mcp", Resource);
        app.MapGet("/.well-known/oauth-protected-resource", Resource);

        app.MapGet("/.well-known/oauth-authorization-server", (IOptions<WebOptions> web) =>
        {
            var issuer = web.Value.Issuer;
            return Results.Json(new JsonObject
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + "/authorize",
                ["token_endpoint"] = issuer + "/token",
                ["registration_endpoint"] = issuer + "/register",
                ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["client_id_metadata_document_supported"] = true,
                ["authorization_response_iss_parameter_supported"] = true,
                // offline_access is deliberately absent: pass-through issues no refresh tokens.
                ["scopes_supported"] = new JsonArray("pi")
            });
        });
    }
}
