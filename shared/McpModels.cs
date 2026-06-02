using System.Text.Json.Serialization;

namespace Shared.MCP;

/// <summary>
/// MCP Protocol message types
/// </summary>
public static class McpProtocol
{
    public const string JsonRpc = "2.0";

    // Message types
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string NotificationsInitialized = "notifications/initialized";
    public const string ListTools = "tools/list";
    public const string CallTool = "tools/call";
    public const string ListResources = "resources/list";
    public const string ReadResource = "resources/read";
    public const string Ping = "ping";
    public const string Notification = "notification";
}

/// <summary>
/// Base MCP message
/// </summary>
public class McpMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = McpProtocol.JsonRpc;

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>
/// MCP Response message
/// </summary>
public class McpResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = McpProtocol.JsonRpc;

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public McpError? Error { get; set; }
}

/// <summary>
/// MCP Error structure
/// </summary>
public class McpError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// MCP Tool definition
/// </summary>
public class McpTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; set; } = new object();
}

/// <summary>
/// MCP Tool call request
/// </summary>
public class McpToolCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public Dictionary<string, object> Arguments { get; set; } = new();
}

/// <summary>
/// MCP Tool result
/// </summary>
public class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

/// <summary>
/// MCP Content item
/// </summary>
public class McpContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// MCP Resource definition
/// </summary>
public class McpResource
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }
}

/// <summary>
/// MCP Initialize request
/// </summary>
public class McpInitializeRequest
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpClientCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("clientInfo")]
    public McpClientInfo ClientInfo { get; set; } = new();
}

/// <summary>
/// MCP Initialize result
/// </summary>
public class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();
}

/// <summary>
/// Client capabilities
/// </summary>
public class McpClientCapabilities
{
    [JsonPropertyName("roots")]
    public McpRootsCapability? Roots { get; set; }

    [JsonPropertyName("sampling")]
    public object? Sampling { get; set; }
}

/// <summary>
/// Server capabilities
/// </summary>
public class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public object? Tools { get; set; } = new { };

    [JsonPropertyName("resources")]
    public object? Resources { get; set; } = new { };

    [JsonPropertyName("prompts")]
    public object? Prompts { get; set; }

    [JsonPropertyName("logging")]
    public object? Logging { get; set; }
}

/// <summary>
/// Client information
/// </summary>
public class McpClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Server information
/// </summary>
public class McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "ProjectInsight MCP Server";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Roots capability
/// </summary>
public class McpRootsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}