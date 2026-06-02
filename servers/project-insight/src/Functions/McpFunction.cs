using System.Text;
using System.Text.Json;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shared.MCP;
using ProjectInsightMCP.Models;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Functions;

/// <summary>
/// Azure Function for handling MCP protocol messages
/// </summary>
public class McpFunction
{
    private readonly McpServerService _mcpServer;
    private readonly ProjectInsightAuthService _authService;
    private readonly ProjectInsightApiService _apiService;
    private readonly ApiTokenAccessor _tokenAccessor;
    private readonly KeyVaultTokenService _keyVaultTokenService;
    private readonly ILogger<McpFunction> _logger;

    /// <summary>
    /// Header name for the Project Insight API token
    /// Configure this in Copilot Studio as a custom header
    /// </summary>
    public const string ApiTokenHeader = "X-PI-Token";

    /// <summary>
    /// EasyAuth header set by Azure App Service Authentication
    /// </summary>
    public const string EntraOidHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    public McpFunction(McpServerService mcpServer, ProjectInsightAuthService authService,
                      ProjectInsightApiService apiService, ApiTokenAccessor tokenAccessor,
                      KeyVaultTokenService keyVaultTokenService,
                      ILogger<McpFunction> logger)
    {
        _mcpServer = mcpServer;
        _authService = authService;
        _apiService = apiService;
        _tokenAccessor = tokenAccessor;
        _keyVaultTokenService = keyVaultTokenService;
        _logger = logger;
    }

    /// <summary>
    /// Main MCP endpoint for handling all protocol messages
    /// </summary>
    [FunctionName("McpEndpoint")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "options", Route = "mcp")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        try
        {
            // Handle CORS preflight requests
            if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                return new OkObjectResult("")
                {
                    StatusCode = 200
                };
            }

            // Handle GET requests — Copilot probes this to discover auth requirements
            if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var hasAuth = req.Headers.ContainsKey("Authorization");
                if (!hasAuth)
                {
                    var scheme = req.Headers.TryGetValue("X-Forwarded-Proto", out var protoHeader) && protoHeader.Count > 0 ? protoHeader[0] : req.Scheme;
                    var host = req.Headers.TryGetValue("X-Forwarded-Host", out var hostHeader) && hostHeader.Count > 0 ? hostHeader[0] : req.Host.Value;
                    req.HttpContext.Response.Headers.Add("WWW-Authenticate",
                        $"Bearer resource_metadata=\"{scheme}://{host}/api/.well-known/oauth-protected-resource\"");
                    return new ObjectResult(new { error = "unauthorized", message = "Bearer token required" })
                    {
                        StatusCode = 401,
                        ContentTypes = { "application/json" }
                    };
                }
                // Authenticated GET — return server info
                return new OkObjectResult(new { status = "ok", server = "Project Insight MCP Server" });
            }

            _logger.LogInformation("Received MCP request from {Host}", req.Host);

            // Resolve PI API token — try Entra identity (Key Vault) first, then headers
            var entraOid = req.Headers.TryGetValue(EntraOidHeader, out var oidValues) && oidValues.Count > 0
                ? oidValues[0] : null;

            if (!string.IsNullOrEmpty(entraOid) && _keyVaultTokenService.IsConfigured)
            {
                // EasyAuth user — look up their PI token from Key Vault
                _tokenAccessor.EntraOid = entraOid;
                var userToken = await _keyVaultTokenService.GetTokenAsync(entraOid, cancellationToken);
                if (userToken != null)
                {
                    _tokenAccessor.ApiToken = userToken.ApiToken;
                    _logger.LogInformation("PI token resolved from Key Vault for Entra user {EntraOid}", entraOid);
                }
                else
                {
                    _logger.LogInformation("No PI token in Key Vault for Entra user {EntraOid} — account linking required", entraOid);
                }
            }
            else if (req.Headers.TryGetValue(ApiTokenHeader, out var tokenValues) && tokenValues.Count > 0)
            {
                _tokenAccessor.ApiToken = tokenValues[0];
                _logger.LogInformation("API token provided via {Header} header", ApiTokenHeader);
            }
            else if (req.Headers.TryGetValue("Authorization", out var authHeader) && authHeader.Count > 0)
            {
                var authValue = authHeader[0] ?? "";
                if (authValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    _tokenAccessor.ApiToken = authValue.Substring(7);
                    _logger.LogInformation("API token provided via Authorization Bearer header");
                }
            }
            else
            {
                _logger.LogWarning("No API token provided - no Entra identity, no {Header}, no Authorization Bearer", ApiTokenHeader);
            }

            // Require authentication for the MCP endpoint. Without this, an
            // unauthenticated client (e.g. Claude.ai's first POST) gets the
            // tool list and starts calling tools blind. With this 401, the
            // client follows the WWW-Authenticate header into our OAuth flow.
            //
            // Teams/Copilot users have an Entra OID via EasyAuth even when
            // their PI token isn't yet in Key Vault — they pass the gate so
            // they can call link_pi_account to bootstrap.
            if (string.IsNullOrEmpty(_tokenAccessor.ApiToken) && string.IsNullOrEmpty(entraOid))
            {
                var unauthScheme = req.Headers.TryGetValue("X-Forwarded-Proto", out var p) && p.Count > 0 ? p[0] : req.Scheme;
                var unauthHost = req.Headers.TryGetValue("X-Forwarded-Host", out var h) && h.Count > 0 ? h[0] : req.Host.Value;
                req.HttpContext.Response.Headers.Add("WWW-Authenticate",
                    $"Bearer resource_metadata=\"{unauthScheme}://{unauthHost}/api/.well-known/oauth-protected-resource\"");
                return new ObjectResult(new { error = "unauthorized", message = "Bearer token required" })
                {
                    StatusCode = 401,
                    ContentTypes = { "application/json" }
                };
            }

            // Set CORS headers for MCP clients
            req.HttpContext.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            req.HttpContext.Response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            req.HttpContext.Response.Headers.Add("Access-Control-Allow-Headers", $"Content-Type, Authorization, {ApiTokenHeader}");
            req.HttpContext.Response.Headers.Add("Content-Type", "application/json");
            req.HttpContext.Response.Headers.Add("Connection", "keep-alive");
            req.HttpContext.Response.Headers.Add("Keep-Alive", "timeout=30, max=100");

            // Read the request body
            string requestBody;
            using (var reader = new StreamReader(req.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrEmpty(requestBody))
            {
                _logger.LogWarning("Empty request body received");
                return new BadRequestObjectResult("Request body cannot be empty");
            }

            _logger.LogDebug("Request body: {RequestBody}", requestBody);

            // Parse the MCP message
            McpMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<McpMessage>(requestBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse JSON request");
                return new BadRequestObjectResult($"Invalid JSON: {ex.Message}");
            }

            if (message == null)
            {
                _logger.LogWarning("Null message after parsing");
                return new BadRequestObjectResult("Could not parse message");
            }

            // Handle the message based on method
            McpResponse response = message.Method switch
            {
                McpProtocol.Initialize => _mcpServer.HandleInitialize(message),
                McpProtocol.NotificationsInitialized => HandleNotificationInitialized(message),
                McpProtocol.ListTools => _mcpServer.HandleListTools(message),
                McpProtocol.CallTool => await _mcpServer.HandleToolCallAsync(message, cancellationToken),
                McpProtocol.Ping => HandlePing(message),
                _ => CreateErrorResponse(message.Id, -32601, $"Method not found: {message.Method}")
            };

            // Serialize and return the response
            var responseJson = JsonSerializer.Serialize(response, JsonOptions);
            _logger.LogDebug("Response: {Response}", responseJson);

            var result = new ContentResult
            {
                Content = responseJson,
                ContentType = "application/json",
                StatusCode = 200
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in MCP function");
            var errorResponse = CreateErrorResponse(null, -32603, "Internal server error");
            var errorJson = JsonSerializer.Serialize(errorResponse, JsonOptions);

            return new ContentResult
            {
                Content = errorJson,
                ContentType = "application/json",
                StatusCode = 500
            };
        }
    }

    /// <summary>
    /// Health check endpoint with authentication validation
    /// </summary>
    [FunctionName("Health")]
    public async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        _logger.LogInformation("Health check requested");

        bool? authValidated = null;
        string? authError = null;
        bool? apiTestPassed = null;
        string? apiError = null;
        int? projectCount = null;

        // Optionally validate authentication if requested
        if (req.Query.ContainsKey("validate-auth"))
        {
            var apiToken = req.Query["api-token"].ToString();
            if (string.IsNullOrEmpty(apiToken))
            {
                authValidated = false;
                authError = "API token is required when validate-auth is specified. Provide as query parameter: api-token=your-token";
            }
            else
            {
                try
                {
                    var authResult = await _authService.ValidateAuthenticationAsync(apiToken);
                    authValidated = authResult.IsValid;
                    if (!authResult.IsValid)
                    {
                        authError = authResult.ErrorMessage;
                    }
                }
                catch (Exception ex)
                {
                    authValidated = false;
                    authError = $"Validation error: {ex.Message}";
                    _logger.LogError(ex, "Error during authentication validation in health check");
                }
            }
        }

        // Optionally test API connectivity if requested (uses server-configured credentials)
        if (req.Query.ContainsKey("test-api"))
        {
            try
            {
                var searchRequest = new ProjectSearchRequest
                {
                    IsActive = true,
                    PageSize = 5
                };

                var result = await _apiService.SearchProjectsAsync(searchRequest);

                if (result.Success)
                {
                    apiTestPassed = true;
                    projectCount = result.Data?.Count ?? 0;
                    _logger.LogInformation("API test successful: found {Count} projects", projectCount);
                }
                else
                {
                    apiTestPassed = false;
                    apiError = $"API call failed: {result.Message}";
                    _logger.LogWarning("API test failed: {Error}", apiError);
                }
            }
            catch (Exception ex)
            {
                apiTestPassed = false;
                apiError = $"API test error: {ex.Message}";
                _logger.LogError(ex, "Error during API test in health check");
            }
        }

        // Build health response
        var healthData = new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "1.0.0",
            service = "ProjectInsight MCP Server",
            authentication = new
            {
                configured = "server-side",
                validated = authValidated,
                error = authError
            },
            api = new
            {
                tested = apiTestPassed,
                projectCount = projectCount,
                error = apiError
            }
        };

        return new OkObjectResult(healthData);
    }

    /// <summary>
    /// Get server information endpoint
    /// </summary>
    [FunctionName("ServerInfo")]
    public IActionResult ServerInfo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "info")] HttpRequest req)
    {
        _logger.LogInformation("Server info requested");

        var serverInfo = new McpServerInfo
        {
            Name = "Project Insight MCP Server",
            Version = "1.0.0"
        };

        var capabilities = new McpServerCapabilities
        {
            Tools = new { },
            Resources = new { }
        };

        return new OkObjectResult(new
        {
            server = serverInfo,
            capabilities,
            protocolVersion = "2024-11-05",
            supportedMethods = new[]
            {
                McpProtocol.Initialize,
                McpProtocol.ListTools,
                McpProtocol.CallTool,
                McpProtocol.Ping
            }
        });
    }

    /// <summary>
    /// Handle notifications/initialized - this is a notification so we return an empty response
    /// </summary>
    private McpResponse HandleNotificationInitialized(McpMessage message)
    {
        _logger.LogInformation("Client initialization completed - notifications/initialized received");

        // For notifications in MCP streaming HTTP, we still need to return a response
        // but with no id (null) to indicate it's a notification response
        return new McpResponse
        {
            Id = null, // No ID for notifications
            Result = new { } // Empty result object
        };
    }

    /// <summary>
    /// Handle ping requests
    /// </summary>
    private McpResponse HandlePing(McpMessage message)
    {
        _logger.LogInformation("Handling ping request");

        return new McpResponse
        {
            Id = message.Id,
            Result = new { pong = true, timestamp = DateTime.UtcNow }
        };
    }

    /// <summary>
    /// Create an error response
    /// </summary>
    private static McpResponse CreateErrorResponse(object? id, int code, string message)
    {
        return new McpResponse
        {
            Id = id,
            Error = new McpError
            {
                Code = code,
                Message = message
            }
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false // Compact output for network efficiency
    };
}