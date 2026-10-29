using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Service for handling Project Insight authentication
/// </summary>
public class ProjectInsightAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectInsightAuthService> _logger;
    private readonly string _baseUrl;
    private bool _isConfigured = false;

    public ProjectInsightAuthService(HttpClient httpClient, ILogger<ProjectInsightAuthService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["ProjectInsight:BaseUrl"] ?? "";

        // Only configure if base URL is provided and valid
        if (!string.IsNullOrEmpty(_baseUrl) && !_baseUrl.Contains("yourdomain") && Uri.TryCreate(_baseUrl, UriKind.Absolute, out _))
        {
            ConfigureHttpClientBase();
            _isConfigured = true;
            _logger.LogInformation("ProjectInsightAuthService initialized with base URL: {BaseUrl}", _baseUrl);
        }
        else
        {
            _logger.LogWarning("ProjectInsightAuthService created without valid configuration");
        }
    }

    /// <summary>
    /// Checks if the service is properly configured
    /// </summary>
    public bool IsConfigured => _isConfigured;

    private void EnsureConfigured()
    {
        if (!_isConfigured)
        {
            throw new InvalidOperationException("ProjectInsightAuthService is not properly configured. Ensure ProjectInsight:BaseUrl is set in your MCP client settings.");
        }
    }

    /// <summary>
    /// Configures the HTTP client base settings
    /// </summary>
    private void ConfigureHttpClientBase()
    {
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "ProjectInsight-MCP-Server/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Configure authentication for the current request
    /// </summary>
    private void ConfigureAuthentication(string apiToken)
    {
        // Remove any existing authorization headers
        _httpClient.DefaultRequestHeaders.Remove("Authorization");

        // Validate API token
        if (string.IsNullOrWhiteSpace(apiToken) ||
            apiToken == "YOUR_API_TOKEN_HERE" ||
            apiToken == "your-api-token-here" ||
            apiToken.Length < 10)
        {
            throw new ArgumentException("Valid ProjectInsight API token is required.");
        }

        // Add new authorization header
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
    }

    /// <summary>
    /// Validates the API token by making a test request
    /// </summary>
    public async Task<AuthValidationResult> ValidateAuthenticationAsync(string apiToken, CancellationToken cancellationToken = default)
    {
        try
        {
            // Configure authentication for this request
            ConfigureAuthentication(apiToken);

            _logger.LogInformation("Validating API authentication...");

            // Try multiple endpoints to validate authentication
            var endpoints = new[]
            {
                "/api/user/current",
                "/api/projects?$top=1",
                "/api/project/search?$top=1"
            };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var response = await _httpClient.GetAsync(endpoint, cancellationToken);

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        return new AuthValidationResult
                        {
                            IsValid = false,
                            ErrorMessage = "Authentication failed: Invalid or expired API token",
                            ErrorCode = "UNAUTHORIZED"
                        };
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return new AuthValidationResult
                        {
                            IsValid = false,
                            ErrorMessage = "Access forbidden: API token lacks sufficient permissions",
                            ErrorCode = "FORBIDDEN"
                        };
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("API authentication validated successfully using endpoint: {Endpoint}", endpoint);
                        return new AuthValidationResult
                        {
                            IsValid = true,
                            ValidatedEndpoint = endpoint
                        };
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("Failed to validate using endpoint {Endpoint}: {Error}", endpoint, ex.Message);
                    continue;
                }
            }

            return new AuthValidationResult
            {
                IsValid = false,
                ErrorMessage = "Unable to validate authentication with any endpoint",
                ErrorCode = "VALIDATION_FAILED"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating API authentication");
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Validation error: {ex.Message}",
                ErrorCode = "VALIDATION_ERROR"
            };
        }
    }

    /// <summary>
    /// Checks if an HTTP response indicates an authentication error
    /// </summary>
    public AuthErrorInfo CheckAuthenticationError(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return new AuthErrorInfo
            {
                IsAuthError = true,
                ErrorType = "UNAUTHORIZED",
                ErrorMessage = "Authentication failed: Invalid or expired API token",
                SuggestedAction = "Verify your API token is correct and has not expired"
            };
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new AuthErrorInfo
            {
                IsAuthError = true,
                ErrorType = "FORBIDDEN",
                ErrorMessage = "Access forbidden: Insufficient permissions",
                SuggestedAction = "Ensure your API token has the required permissions for this operation"
            };
        }

        return new AuthErrorInfo { IsAuthError = false };
    }

    /// <summary>
    /// Gets a masked version of the API token for logging
    /// </summary>
    public static string GetMaskedToken(string apiToken)
    {
        if (string.IsNullOrEmpty(apiToken) || apiToken.Length < 8)
            return "***INVALID***";

        return $"{apiToken[..4]}...{apiToken[^4..]}";
    }
}

/// <summary>
/// Result of authentication validation
/// </summary>
public class AuthValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ValidatedEndpoint { get; set; }
}

/// <summary>
/// Information about authentication errors
/// </summary>
public class AuthErrorInfo
{
    public bool IsAuthError { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SuggestedAction { get; set; }
}