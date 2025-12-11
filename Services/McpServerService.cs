using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.MCP;
using ProjectInsightMCP.Models;

namespace ProjectInsightMCP.Services;

/// <summary>
/// MCP Server implementation for Project Insight integration
/// </summary>
public class McpServerService
{
    private readonly ProjectInsightApiService _projectInsightApi;
    private readonly ILogger<McpServerService> _logger;
    private readonly List<McpTool> _tools;

    public McpServerService(ProjectInsightApiService projectInsightApi, ILogger<McpServerService> logger)
    {
        _projectInsightApi = projectInsightApi;
        _logger = logger;
        _tools = InitializeTools();
    }

    /// <summary>
    /// Handle MCP initialize request
    /// </summary>
    public McpResponse HandleInitialize(McpMessage request)
    {
        try
        {
            _logger.LogInformation("Handling MCP initialize request");

            var result = new McpInitializeResult
            {
                ProtocolVersion = "2025-03-26",
                Capabilities = new McpServerCapabilities
                {
                    Tools = new { },
                    Resources = new { }
                },
                ServerInfo = new McpServerInfo
                {
                    Name = "Project Insight MCP Server",
                    Version = "1.0.0"
                }
            };

            return new McpResponse
            {
                Id = request.Id,
                Result = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling initialize request");
            return CreateErrorResponse(request.Id, -32603, "Internal error", ex.Message);
        }
    }

    /// <summary>
    /// Handle tools/list request
    /// </summary>
    public McpResponse HandleListTools(McpMessage request)
    {
        try
        {
            _logger.LogInformation("Handling list tools request");

            var result = new { tools = _tools };

            return new McpResponse
            {
                Id = request.Id,
                Result = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing tools");
            return CreateErrorResponse(request.Id, -32603, "Internal error", ex.Message);
        }
    }

    /// <summary>
    /// Handle tools/call request
    /// </summary>
    public async Task<McpResponse> HandleToolCallAsync(McpMessage request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("HandleToolCallAsync called with request ID: {RequestId}", request.Id);

            if (request.Params is not JsonElement paramsElement)
            {
                _logger.LogWarning("Invalid params - not a JsonElement");
                return CreateErrorResponse(request.Id, -32602, "Invalid params", "Parameters must be provided");
            }

            _logger.LogInformation("Parsing tool call from params: {Params}", paramsElement.GetRawText());
            var toolCall = JsonSerializer.Deserialize<McpToolCall>(paramsElement.GetRawText());
            if (toolCall == null)
            {
                _logger.LogWarning("Could not deserialize tool call from params");
                return CreateErrorResponse(request.Id, -32602, "Invalid params", "Could not parse tool call");
            }

            _logger.LogInformation("Handling tool call: {ToolName} with arguments: {Arguments}",
                toolCall.Name, JsonSerializer.Serialize(toolCall.Arguments));

            var result = await ExecuteToolAsync(toolCall, cancellationToken);
            _logger.LogInformation("Tool execution completed. IsError: {IsError}", result.IsError);

            return new McpResponse
            {
                Id = request.Id,
                Result = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tool call. Exception type: {ExceptionType}, Message: {Message}, StackTrace: {StackTrace}",
                ex.GetType().Name, ex.Message, ex.StackTrace);
            return CreateErrorResponse(request.Id, -32603, "Internal error", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute a specific tool
    /// </summary>
    private async Task<McpToolResult> ExecuteToolAsync(McpToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("ExecuteToolAsync called for tool: {ToolName}", toolCall.Name);

            switch (toolCall.Name)
            {
                case "search_projects":
                    _logger.LogInformation("Executing search_projects");
                    return await SearchProjectsAsync(toolCall.Arguments, cancellationToken);

                case "get_project":
                    _logger.LogInformation("Executing get_project");
                    return await GetProjectAsync(toolCall.Arguments, cancellationToken);

                case "get_project_tasks":
                    _logger.LogInformation("Executing get_project_tasks");
                    return await GetProjectTasksAsync(toolCall.Arguments, cancellationToken);

                case "create_project":
                    _logger.LogInformation("Executing create_project");
                    return await CreateProjectAsync(toolCall.Arguments, cancellationToken);

                case "update_project":
                    _logger.LogInformation("Executing update_project");
                    return await UpdateProjectAsync(toolCall.Arguments, cancellationToken);

                default:
                    _logger.LogWarning("Unknown tool: {ToolName}", toolCall.Name);
                    return new McpToolResult
                    {
                        Content = new List<McpContent>
                        {
                            new() { Type = "text", Text = $"Unknown tool: {toolCall.Name}" }
                        },
                        IsError = true
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing tool {ToolName}. Exception type: {ExceptionType}, Message: {Message}, StackTrace: {StackTrace}",
                toolCall.Name, ex.GetType().Name, ex.Message, ex.StackTrace);
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error executing tool: {ex.GetType().Name}: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Search projects tool implementation
    /// </summary>
    private async Task<McpToolResult> SearchProjectsAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var request = new ProjectSearchRequest();

        // Parse arguments
        if (arguments.TryGetValue("isActive", out var isActive) && isActive is bool activeValue)
            request.IsActive = activeValue;

        if (arguments.TryGetValue("isOpen", out var isOpen) && isOpen is bool openValue)
            request.IsOpen = openValue;

        if (arguments.TryGetValue("projectManagerId", out var managerId) && managerId is string managerIdStr)
            request.ProjectManagerId = managerIdStr;

        if (arguments.TryGetValue("projectStatusId", out var statusId) && statusId is string statusIdStr)
            request.ProjectStatusId = statusIdStr;

        if (arguments.TryGetValue("modelProperties", out var modelProps) && modelProps is string modelPropsStr)
            request.ModelProperties = modelPropsStr;

        var response = await _projectInsightApi.SearchProjectsAsync(request, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" }
                },
                IsError = true
            };
        }

        var projectsJson = JsonSerializer.Serialize(response.Data, JsonOptions);
        var summary = $"Found {response.Data?.Count ?? 0} projects";

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = summary },
                new() { Type = "text", Text = projectsJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Get project tool implementation
    /// </summary>
    private async Task<McpToolResult> GetProjectAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var projectId = ExtractStringParameter(arguments, "projectId");
        if (string.IsNullOrEmpty(projectId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: projectId parameter is required" }
                },
                IsError = true
            };
        }

        var modelProperties = ExtractStringParameter(arguments, "modelProperties");

        var response = await _projectInsightApi.GetProjectAsync(projectId, modelProperties, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" }
                },
                IsError = true
            };
        }

        var projectJson = JsonSerializer.Serialize(response.Data, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Retrieved project: {response.Data?.Name ?? "Unknown"}" },
                new() { Type = "text", Text = projectJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Get project tasks tool implementation
    /// </summary>
    private async Task<McpToolResult> GetProjectTasksAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var projectId = ExtractStringParameter(arguments, "projectId");
        if (string.IsNullOrEmpty(projectId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: projectId parameter is required" }
                },
                IsError = true
            };
        }

        var response = await _projectInsightApi.GetProjectTasksAsync(projectId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" }
                },
                IsError = true
            };
        }

        var tasksJson = JsonSerializer.Serialize(response.Data, JsonOptions);
        var summary = $"Found {response.Data?.Count ?? 0} tasks for project {projectId}";

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = summary },
                new() { Type = "text", Text = tasksJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Create project tool implementation
    /// </summary>
    private async Task<McpToolResult> CreateProjectAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("project", out var projectObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: project parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var projectJson = JsonSerializer.Serialize(projectObj, JsonOptions);
            var project = JsonSerializer.Deserialize<Project>(projectJson, JsonOptions);

            if (project == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse project data" }
                    },
                    IsError = true
                };
            }

            var response = await _projectInsightApi.CreateProjectAsync(project, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" }
                    },
                    IsError = true
                };
            }

            var resultJson = JsonSerializer.Serialize(response.Data, JsonOptions);

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Created project: {response.Data?.Name ?? "Unknown"}" },
                    new() { Type = "text", Text = resultJson }
                },
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error parsing project data: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Update project tool implementation
    /// </summary>
    private async Task<McpToolResult> UpdateProjectAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var projectId = ExtractStringParameter(arguments, "projectId");
        if (string.IsNullOrEmpty(projectId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: projectId parameter is required" }
                },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("project", out var projectObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: project parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var projectJson = JsonSerializer.Serialize(projectObj, JsonOptions);
            var project = JsonSerializer.Deserialize<Project>(projectJson, JsonOptions);

            if (project == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse project data" }
                    },
                    IsError = true
                };
            }

            var response = await _projectInsightApi.UpdateProjectAsync(projectId, project, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" }
                    },
                    IsError = true
                };
            }

            var resultJson = JsonSerializer.Serialize(response.Data, JsonOptions);

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Updated project: {response.Data?.Name ?? "Unknown"}" },
                    new() { Type = "text", Text = resultJson }
                },
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error parsing project data: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Initialize available tools
    /// </summary>
    private static List<McpTool> InitializeTools()
    {
        return new List<McpTool>
        {
            new()
            {
                Name = "search_projects",
                Description = "Search for projects in Project Insight with optional filters",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        isActive = new { type = "boolean", description = "Filter by active status" },
                        isOpen = new { type = "boolean", description = "Filter by open status" },
                        projectManagerId = new { type = "string", description = "Filter by project manager ID" },
                        projectStatusId = new { type = "string", description = "Filter by project status ID" },
                        modelProperties = new { type = "string", description = "Specify which properties to include" }
                    }
                }
            },
            new()
            {
                Name = "get_project",
                Description = "Get detailed information about a specific project",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID" },
                        modelProperties = new { type = "string", description = "Specify which properties to include" }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "get_project_tasks",
                Description = "Get all tasks for a specific project",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID" }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "create_project",
                Description = "Create a new project in Project Insight",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        project = new
                        {
                            type = "object",
                            description = "The project object with name, description, and other properties"
                        }
                    },
                    required = new[] { "project" }
                }
            },
            new()
            {
                Name = "update_project",
                Description = "Update an existing project in Project Insight",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID" },
                        project = new
                        {
                            type = "object",
                            description = "The project object with updated properties"
                        }
                    },
                    required = new[] { "projectId", "project" }
                }
            }
        };
    }

    /// <summary>
    /// Create an error response
    /// </summary>
    private static McpResponse CreateErrorResponse(object? id, int code, string message, string? data = null)
    {
        return new McpResponse
        {
            Id = id,
            Error = new McpError
            {
                Code = code,
                Message = message,
                Data = data
            }
        };
    }

    /// <summary>
    /// Extract string parameter from arguments, handling JsonElement conversion
    /// </summary>
    private static string? ExtractStringParameter(Dictionary<string, object> arguments, string paramName)
    {
        if (!arguments.TryGetValue(paramName, out var paramValue))
            return null;

        return paramValue switch
        {
            string str => str,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.ToString(),
            _ => paramValue?.ToString()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}