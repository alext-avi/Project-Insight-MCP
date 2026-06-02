namespace Shared.MCP.Configuration;

/// <summary>
/// Base configuration for MCP servers
/// </summary>
public abstract class BaseMcpConfiguration
{
    /// <summary>
    /// The base URL for the API this MCP server connects to
    /// </summary>
    public required string BaseUrl { get; set; }

    /// <summary>
    /// API token for authentication (optional)
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// Server name for identification
    /// </summary>
    public required string ServerName { get; set; }

    /// <summary>
    /// Server version
    /// </summary>
    public required string Version { get; set; }

    /// <summary>
    /// Timeout for HTTP requests in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to enable debug logging
    /// </summary>
    public bool DebugLogging { get; set; } = false;

    /// <summary>
    /// Custom headers to include in API requests
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; set; } = new();

    /// <summary>
    /// Validates the configuration
    /// </summary>
    public virtual (bool IsValid, List<string> Errors) Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BaseUrl))
            errors.Add("BaseUrl is required");

        if (string.IsNullOrWhiteSpace(ServerName))
            errors.Add("ServerName is required");

        if (string.IsNullOrWhiteSpace(Version))
            errors.Add("Version is required");

        if (TimeoutSeconds <= 0)
            errors.Add("TimeoutSeconds must be greater than 0");

        if (!string.IsNullOrWhiteSpace(BaseUrl) && !Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
            errors.Add("BaseUrl must be a valid URL");

        return (errors.Count == 0, errors);
    }
}



/// <summary>
/// Factory for creating base configurations from environment variables
/// Specific server implementations should extend this in their own factories
/// </summary>
public static class ConfigurationFactory
{
    public static T CreateFromEnvironment<T>() where T : BaseMcpConfiguration, new()
    {
        var config = new T();

        // Common properties from environment
        config.BaseUrl = Environment.GetEnvironmentVariable("MCP_BASE_URL") ?? "";
        config.ApiToken = Environment.GetEnvironmentVariable("MCP_API_TOKEN");
        config.ServerName = typeof(T).Name.Replace("Configuration", "");
        config.Version = Environment.GetEnvironmentVariable("MCP_VERSION") ?? "1.0.0";

        if (int.TryParse(Environment.GetEnvironmentVariable("MCP_TIMEOUT_SECONDS"), out var timeout))
            config.TimeoutSeconds = timeout;

        if (bool.TryParse(Environment.GetEnvironmentVariable("MCP_DEBUG_LOGGING"), out var debug))
            config.DebugLogging = debug;

        // Additional type-specific configuration can be added by derived factories

        return config;
    }
}