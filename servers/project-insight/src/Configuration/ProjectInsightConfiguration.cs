using Shared.MCP.Configuration;

namespace ProjectInsight.Configuration;

/// <summary>
/// Configuration specific to Project Insight MCP server
/// </summary>
public class ProjectInsightConfiguration : BaseMcpConfiguration
{
    /// <summary>
    /// Default model properties to include in API responses
    /// </summary>
    public string? DefaultModelProperties { get; set; }

    /// <summary>
    /// Maximum number of results to return from searches
    /// </summary>
    public int MaxSearchResults { get; set; } = 100;

    /// <summary>
    /// Default filter for active projects only
    /// </summary>
    public bool DefaultActiveOnly { get; set; } = true;

    public override (bool IsValid, List<string> Errors) Validate()
    {
        var (baseValid, baseErrors) = base.Validate();
        var errors = baseErrors.ToList();

        if (MaxSearchResults <= 0)
            errors.Add("MaxSearchResults must be greater than 0");

        if (MaxSearchResults > 1000)
            errors.Add("MaxSearchResults should not exceed 1000 for performance reasons");

        return (errors.Count == 0, errors);
    }
}

/// <summary>
/// Factory for creating Project Insight configuration from environment variables
/// </summary>
public static class ProjectInsightConfigurationFactory
{
    public static ProjectInsightConfiguration CreateFromEnvironment()
    {
        var config = new ProjectInsightConfiguration
        {
            BaseUrl = Environment.GetEnvironmentVariable("MCP_BASE_URL") ?? "",
            ServerName = "ProjectInsight",
            Version = Environment.GetEnvironmentVariable("MCP_VERSION") ?? "1.0.0"
        };

        // Common properties from environment
        config.ApiToken = Environment.GetEnvironmentVariable("MCP_API_TOKEN");

        if (int.TryParse(Environment.GetEnvironmentVariable("MCP_TIMEOUT_SECONDS"), out var timeout))
            config.TimeoutSeconds = timeout;

        if (bool.TryParse(Environment.GetEnvironmentVariable("MCP_DEBUG_LOGGING"), out var debug))
            config.DebugLogging = debug;

        // Project Insight specific configuration
        config.DefaultModelProperties = Environment.GetEnvironmentVariable("PI_DEFAULT_MODEL_PROPERTIES");

        if (int.TryParse(Environment.GetEnvironmentVariable("PI_MAX_SEARCH_RESULTS"), out var maxResults))
            config.MaxSearchResults = maxResults;

        if (bool.TryParse(Environment.GetEnvironmentVariable("PI_DEFAULT_ACTIVE_ONLY"), out var activeOnly))
            config.DefaultActiveOnly = activeOnly;

        return config;
    }
}