using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Functions;

/// <summary>
/// Hosts the well-known OAuth discovery documents that Claude.ai (and any other
/// MCP client doing RFC 9728 / RFC 8414 discovery) reads to learn how to auth.
///
/// Lives in its own file so the MCP protocol handler stays a self-contained
/// agent vehicle, untouched by the OAuth bridge concern.
/// </summary>
public class OAuthMetadataFunction
{
    private readonly ILogger<OAuthMetadataFunction> _logger;

    public OAuthMetadataFunction(ILogger<OAuthMetadataFunction> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// RFC 9728 — Protected Resource Metadata.
    /// Tells the client which authorization server(s) issue tokens for this resource.
    /// We advertise OURSELVES (the OAuth bridge) so Claude follows our flow,
    /// which in turn delegates user sign-in to Entra via PI's hosted login.
    /// </summary>
    [FunctionName("OAuthProtectedResource")]
    public IActionResult OAuthProtectedResource(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/oauth-protected-resource")] HttpRequest req)
    {
        _logger.LogInformation("OAuth protected resource metadata requested");

        var baseUrl = GetBaseUrl(req);

        var metadata = new
        {
            resource = baseUrl,
            authorization_servers = new[] { baseUrl },
            bearer_methods_supported = new[] { "header" },
            resource_documentation = "https://yourdomain.projectinsight.net"
        };

        return new OkObjectResult(metadata);
    }

    /// <summary>
    /// RFC 8414 — Authorization Server Metadata.
    /// Tells the client where to send the user for /authorize and where to POST /token.
    /// </summary>
    [FunctionName("OAuthAuthorizationServer")]
    public IActionResult OAuthAuthorizationServer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/oauth-authorization-server")] HttpRequest req)
    {
        _logger.LogInformation("OAuth authorization server metadata requested");

        var baseUrl = GetBaseUrl(req);

        var metadata = new
        {
            issuer = baseUrl,
            authorization_endpoint = $"{baseUrl}/oauth/authorize",
            token_endpoint = $"{baseUrl}/oauth/token",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" }, // public client + PKCE
            scopes_supported = new[] { "mcp" }
        };

        return new OkObjectResult(metadata);
    }

    /// <summary>
    /// Resolve the externally-visible base URL of the API. Honors the
    /// X-Forwarded-* headers Azure App Service injects so we report
    /// https://...azurewebsites.net/api rather than the internal hostname.
    /// </summary>
    private static string GetBaseUrl(HttpRequest req)
    {
        var scheme = req.Headers.TryGetValue("X-Forwarded-Proto", out var protoHeader) && protoHeader.Count > 0
            ? protoHeader[0]
            : req.Scheme;
        var host = req.Headers.TryGetValue("X-Forwarded-Host", out var hostHeader) && hostHeader.Count > 0
            ? hostHeader[0]
            : req.Host.Value;

        return $"{scheme}://{host}/api";
    }
}
