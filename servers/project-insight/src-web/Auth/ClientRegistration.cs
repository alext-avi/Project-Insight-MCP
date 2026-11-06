using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using ProjectInsightMCP.Web.Hosting;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>RFC 7591 dynamic registration for public clients whose redirects are on the allowlist.</summary>
public static class ClientRegistration
{
    public const int MaxClients = 500;

    public static void MapClientRegistration(this WebApplication app)
    {
        app.MapPost("/register", async (HttpRequest request, IClientStore store, RedirectPolicy policy, TimeProvider time, CancellationToken ct) =>
        {
            // Anonymous endpoint: never buffer more than a registration needs.
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 16 * 1024;

            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Error("invalid_client_metadata", "Body must be JSON.");
            }
            if (body.ValueKind != JsonValueKind.Object)
                return Error("invalid_client_metadata", "Body must be a JSON object.");

            if (body.TryGetProperty("token_endpoint_auth_method", out var method)
                && !(method.ValueKind == JsonValueKind.Null || (method.ValueKind == JsonValueKind.String && method.GetString() == "none")))
                return Error("invalid_client_metadata", "Only public clients (token_endpoint_auth_method none) are supported.");
            if (!body.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
                return Error("invalid_redirect_uri", "redirect_uris is required.");
            List<string?> redirects;
            string? name;
            try
            {
                redirects = uris.EnumerateArray().Select(u => u.ValueKind == JsonValueKind.String ? u.GetString() : null).ToList();
                name = body.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            }
            catch (InvalidOperationException)
            {
                // GetString throws on a lone surrogate escape.
                return Error("invalid_client_metadata", "Strings must be valid Unicode.");
            }
            if (redirects.Count is 0 or > 5 || redirects.Any(r => !policy.IsAllowed(r)))
                return Error("invalid_redirect_uri", "Every redirect URI must be an allowed callback.");
            if (name is { Length: > 100 })
                return Error("invalid_client_metadata", "client_name is too long.");

            var now = time.GetUtcNow();
            var client = new RegisteredClient("dcr_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)), name, redirects!, now);
            if (!await store.TryAddAsync(client, MaxClients, ct))
                return Results.Json(new { error = "temporarily_unavailable", error_description = "Too many registered clients." }, statusCode: 503);

            var response = new Dictionary<string, object?>
            {
                ["client_id"] = client.ClientId,
                ["client_id_issued_at"] = now.ToUnixTimeSeconds(),
                ["redirect_uris"] = client.RedirectUris,
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new[] { "authorization_code" },
                ["response_types"] = new[] { "code" }
            };
            if (name is not null) // clients' schemas reject a null here
                response["client_name"] = name;
            return Results.Json(response, statusCode: 201);
        }).WithOAuthHeaders().RequireRateLimiting(RateLimits.Register);
    }

    private static IResult Error(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: 400);
}
