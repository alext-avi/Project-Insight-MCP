using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Shared.MCP;

/// <summary>
/// Common utilities for MCP server implementations
/// </summary>
public static class McpUtilities
{
    /// <summary>
    /// Standard MCP error codes
    /// </summary>
    public static class ErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        public const int ServerError = -32000;
    }

    /// <summary>
    /// Creates a standardized MCP error response
    /// </summary>
    public static object CreateErrorResponse(string id, int code, string message, object? data = null)
    {
        var error = new
        {
            code,
            message,
            data
        };

        return new
        {
            jsonrpc = "2.0",
            id,
            error
        };
    }

    /// <summary>
    /// Creates a standardized MCP success response
    /// </summary>
    public static object CreateSuccessResponse(string id, object result)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            result
        };
    }

    /// <summary>
    /// Safely parses JSON with error handling
    /// </summary>
    public static T? SafeParseJson<T>(string json, ILogger logger) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse JSON: {Json}", json);
            return null;
        }
    }

    /// <summary>
    /// Validates required parameters for MCP tool calls
    /// </summary>
    public static (bool IsValid, string? Error) ValidateRequiredParameters(
        IDictionary<string, object?> parameters,
        params string[] requiredKeys)
    {
        var missing = requiredKeys.Where(key =>
            !parameters.ContainsKey(key) ||
            parameters[key] == null ||
            (parameters[key] is string str && string.IsNullOrWhiteSpace(str))
        ).ToList();

        if (missing.Any())
        {
            return (false, $"Missing required parameters: {string.Join(", ", missing)}");
        }

        return (true, null);
    }

    /// <summary>
    /// Converts parameters dictionary to specific type with validation
    /// </summary>
    public static T? ConvertParameters<T>(IDictionary<string, object?> parameters, ILogger logger) where T : class
    {
        try
        {
            var json = JsonSerializer.Serialize(parameters);
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to convert parameters to {Type}: {@Parameters}", typeof(T).Name, parameters);
            return null;
        }
    }

    /// <summary>
    /// Standard HTTP client configuration for API calls
    /// </summary>
    public static HttpClient ConfigureHttpClient(string baseUrl, string? apiToken = null)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.Add("User-Agent", "Project-Insight-MCP/1.0");

        if (!string.IsNullOrEmpty(apiToken))
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
        }

        return client;
    }

    /// <summary>
    /// Logs MCP tool execution with standard format
    /// </summary>
    public static void LogToolExecution(ILogger logger, string toolName, object? parameters = null, bool isSuccess = true, string? errorMessage = null)
    {
        if (isSuccess)
        {
            logger.LogInformation("MCP tool {ToolName} executed successfully with parameters: {@Parameters}", toolName, parameters);
        }
        else
        {
            logger.LogError("MCP tool {ToolName} failed with error: {Error}. Parameters: {@Parameters}", toolName, errorMessage, parameters);
        }
    }
}