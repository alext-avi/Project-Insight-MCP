using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Shared.MCP;
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
    private readonly DeviceActivationService? _deviceActivation;
    private readonly KeyVaultTokenService? _keyVaultTokenService;
    private readonly ApiTokenAccessor _apiTokenAccessor;

    public McpServerService(ProjectInsightApiService projectInsightApi, ILogger<McpServerService> logger, ApiTokenAccessor apiTokenAccessor, DeviceActivationService? deviceActivation = null, KeyVaultTokenService? keyVaultTokenService = null)
    {
        _projectInsightApi = projectInsightApi;
        _logger = logger;
        _apiTokenAccessor = apiTokenAccessor;
        _deviceActivation = deviceActivation;
        _keyVaultTokenService = keyVaultTokenService;
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
                    Version = "1.1.0",
                    Description = "Project management server for Project Insight. IMPORTANT: Use the 'fields' parameter to control response size and reduce token usage. Presets: 'minimal' (Id,Name), 'summary' (key fields), 'standard', 'full'. Default is 'summary'. Always use 'minimal' or 'summary' unless you specifically need more data."
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
    /// With no PI token, starts the sign-in flow that fits the host and returns what to tell the user;
    /// null when a token is present. Every tool that needs PI runs this first, so all of them guide
    /// sign-in the same way.
    /// </summary>
    public async Task<McpToolResult?> SignInIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_apiTokenAccessor.HasToken)
            return null;

        // Copilot context (Entra user, Key Vault available) → account linking
        if (_apiTokenAccessor.HasEntraIdentity && _keyVaultTokenService != null)
            return await LinkPiAccountAsync(cancellationToken);

        // Local stdio context → device activation
        if (_deviceActivation != null)
            return await TriggerAuthFlowAsync(cancellationToken);

        return new McpToolResult
        {
            Content = new List<McpContent> { new() { Type = "text", Text = "Error: No API token configured. Set ProjectInsight__ApiToken in your MCP server environment variables, or restart without it to use interactive authentication." } },
            IsError = true
        };
    }

    /// <summary>
    /// Execute a specific tool
    /// </summary>
    public async Task<McpToolResult> ExecuteToolAsync(McpToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("ExecuteToolAsync called for tool: {ToolName}", toolCall.Name);

            // Auth tools that work without a PI token
            if (toolCall.Name == "complete_authentication")
                return await CompleteAuthenticationAsync(toolCall.Arguments, cancellationToken);
            if (toolCall.Name == "link_pi_account")
                return await LinkPiAccountAsync(cancellationToken);
            if (toolCall.Name == "complete_pi_link")
                return await CompletePiLinkAsync(toolCall.Arguments, cancellationToken);

            // If no PI token, trigger the appropriate auth flow
            if (await SignInIfNeededAsync(cancellationToken) is { } signIn)
                return signIn;

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

                case "get_project_resources":
                    _logger.LogInformation("Executing get_project_resources");
                    return await GetProjectResourcesAsync(toolCall.Arguments, cancellationToken);

                case "add_project_resource":
                    _logger.LogInformation("Executing add_project_resource");
                    return await AddProjectResourceAsync(toolCall.Arguments, cancellationToken);

                case "update_project_resource":
                    _logger.LogInformation("Executing update_project_resource");
                    return await UpdateProjectResourceAsync(toolCall.Arguments, cancellationToken);

                case "remove_project_resource":
                    _logger.LogInformation("Executing remove_project_resource");
                    return await RemoveProjectResourceAsync(toolCall.Arguments, cancellationToken);

                case "add_task_resource":
                    _logger.LogInformation("Executing add_task_resource");
                    return await AddTaskResourceAsync(toolCall.Arguments, cancellationToken);

                case "remove_task_resource":
                    _logger.LogInformation("Executing remove_task_resource");
                    return await RemoveTaskResourceAsync(toolCall.Arguments, cancellationToken);

                case "get_task_resources":
                    _logger.LogInformation("Executing get_task_resources");
                    return await GetTaskResourcesAsync(toolCall.Arguments, cancellationToken);

                case "create_project":
                    _logger.LogInformation("Executing create_project");
                    return await CreateProjectAsync(toolCall.Arguments, cancellationToken);

                case "update_project":
                    _logger.LogInformation("Executing update_project");
                    return await UpdateProjectAsync(toolCall.Arguments, cancellationToken);

                case "get_task":
                    _logger.LogInformation("Executing get_task");
                    return await GetTaskAsync(toolCall.Arguments, cancellationToken);

                case "create_task":
                    _logger.LogInformation("Executing create_task");
                    return await CreateTaskAsync(toolCall.Arguments, cancellationToken);

                case "update_task":
                    _logger.LogInformation("Executing update_task");
                    return await UpdateTaskAsync(toolCall.Arguments, cancellationToken);

                // TimeSheet tools
                case "get_timesheet":
                    _logger.LogInformation("Executing get_timesheet");
                    return await GetTimeSheetAsync(toolCall.Arguments, cancellationToken);

                case "search_timesheets":
                    _logger.LogInformation("Executing search_timesheets");
                    return await SearchTimeSheetsAsync(toolCall.Arguments, cancellationToken);

                case "get_my_time_status":
                    _logger.LogInformation("Executing get_my_time_status");
                    return await GetMyTimeStatusAsync(cancellationToken);

                case "update_timesheet":
                    _logger.LogInformation("Executing update_timesheet");
                    return await UpdateTimeSheetAsync(toolCall.Arguments, cancellationToken);

                case "create_and_submit_timesheet":
                    _logger.LogInformation("Executing create_and_submit_timesheet");
                    return await CreateAndSubmitTimeSheetAsync(toolCall.Arguments, cancellationToken);

                case "submit_timesheet":
                    _logger.LogInformation("Executing submit_timesheet");
                    return await SubmitTimeSheetAsync(toolCall.Arguments, cancellationToken);

                case "approve_timesheet":
                    _logger.LogInformation("Executing approve_timesheet");
                    return await ApproveTimeSheetAsync(toolCall.Arguments, cancellationToken);

                case "reject_timesheet":
                    _logger.LogInformation("Executing reject_timesheet");
                    return await RejectTimeSheetAsync(toolCall.Arguments, cancellationToken);

                case "unsubmit_timesheet":
                    _logger.LogInformation("Executing unsubmit_timesheet");
                    return await UnsubmitTimeSheetAsync(toolCall.Arguments, cancellationToken);

                // Time Entry tools
                case "get_project_time_entries":
                    _logger.LogInformation("Executing get_project_time_entries");
                    return await GetProjectTimeEntriesAsync(toolCall.Arguments, cancellationToken);

                case "search_time_entries":
                    _logger.LogInformation("Executing search_time_entries");
                    return await SearchTimeEntriesAsync(toolCall.Arguments, cancellationToken);

                case "create_time_entry":
                    _logger.LogInformation("Executing create_time_entry");
                    return await CreateTimeEntryAsync(toolCall.Arguments, cancellationToken);

                case "delete_time_entry":
                    _logger.LogInformation("Executing delete_time_entry");
                    return await DeleteTimeEntryAsync(toolCall.Arguments, cancellationToken);

                case "update_time_entry":
                    _logger.LogInformation("Executing update_time_entry");
                    return await UpdateTimeEntryAsync(toolCall.Arguments, cancellationToken);

                // User tools
                case "get_current_user":
                    _logger.LogInformation("Executing get_current_user");
                    return await GetCurrentUserAsync(toolCall.Arguments, cancellationToken);

                case "get_user":
                    _logger.LogInformation("Executing get_user");
                    return await GetUserAsync(toolCall.Arguments, cancellationToken);

                case "get_users":
                    _logger.LogInformation("Executing get_users");
                    return await GetUsersAsync(toolCall.Arguments, cancellationToken);

                case "search_users":
                    _logger.LogInformation("Executing search_users");
                    return await SearchUsersAsync(toolCall.Arguments, cancellationToken);

                // Resource Type tools
                case "get_resource_types":
                    _logger.LogInformation("Executing get_resource_types");
                    return await GetResourceTypesAsync(toolCall.Arguments, cancellationToken);

                // Rate Card (Company Contract) tools
                case "get_project_rate_cards":
                    _logger.LogInformation("Executing get_project_rate_cards");
                    return await GetProjectRateCardsAsync(toolCall.Arguments, cancellationToken);

                case "get_rate_card":
                    _logger.LogInformation("Executing get_rate_card");
                    return await GetRateCardAsync(toolCall.Arguments, cancellationToken);

                case "save_rate_card_rate":
                    _logger.LogInformation("Executing save_rate_card_rate");
                    return await SaveRateCardRateAsync(toolCall.Arguments, cancellationToken);

                case "delete_rate_card_rate":
                    _logger.LogInformation("Executing delete_rate_card_rate");
                    return await DeleteRateCardRateAsync(toolCall.Arguments, cancellationToken);

                // PTO tools
                case "get_pto_entry":
                    _logger.LogInformation("Executing get_pto_entry");
                    return await GetPtoEntryAsync(toolCall.Arguments, cancellationToken);

                case "list_user_pto":
                    _logger.LogInformation("Executing list_user_pto");
                    return await ListUserPtoAsync(toolCall.Arguments, cancellationToken);

                case "create_pto_entry":
                    _logger.LogInformation("Executing create_pto_entry");
                    return await CreatePtoEntryAsync(toolCall.Arguments, cancellationToken);

                case "update_pto_entry":
                    _logger.LogInformation("Executing update_pto_entry");
                    return await UpdatePtoEntryAsync(toolCall.Arguments, cancellationToken);

                case "delete_pto_entry":
                    _logger.LogInformation("Executing delete_pto_entry");
                    return await DeletePtoEntryAsync(toolCall.Arguments, cancellationToken);

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
    /// Resolves field presets to actual model property strings.
    /// Supports: 'minimal', 'summary', 'standard', 'full', or comma-separated custom fields
    /// </summary>
    private static string ResolveFieldPreset(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return GetSummaryFields(); // Default to summary for token efficiency

        return fields.ToLowerInvariant().Trim() switch
        {
            "minimal" => "Id,Name",
            "summary" => GetSummaryFields(),
            "standard" => GetStandardFields(),
            "full" => GetFullFields(),
            _ => fields // Custom comma-separated fields passed through directly
        };
    }

    private static string GetSummaryFields() =>
        "Id,Name,IsActive,IsOpen,WorkPercentComplete,StartDateTimeUTC,EndDateTimeUTC,PrimaryProjectManager_Id,ProjectStatus_Id";

    /// <summary>
    /// Resolves task field presets to actual model property strings.
    /// </summary>
    private static string ResolveTaskFieldPreset(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return GetSummaryTaskFields(); // Default to summary for token efficiency

        return fields.ToLowerInvariant().Trim() switch
        {
            "minimal" => "Id,Name",
            "summary" => GetSummaryTaskFields(),
            "standard" => GetStandardTaskFields(),
            "full" => GetFullTaskFields(),
            _ => fields // Custom comma-separated fields passed through directly
        };
    }

    private static string GetSummaryTaskFields() =>
        "Id,Name,WorkPercentComplete,IsActive,StartDateTimeUTC,EndDateTimeUTC,Project_Id";

    private static string GetStandardFields() =>
        "Id,Name,Description,IsActive,IsOpen,WorkPercentComplete,StartDateTimeUTC,EndDateTimeUTC," +
        "ScheduleStartDate,UrlFull,PrimaryProjectManager_Id,ProjectStatus_Id,ProjectType_Id," +
        "Health_Id,CompanyDefault_Id,ItemNumber";

    private static string GetFullFields() =>
        "Id,Name,Description,IsActive,IsOpen,ProjectState,WorkPercentComplete,WorkSeconds,DurationSeconds," +
        "StartDateTimeUTC,EndDateTimeUTC,ScheduleStartDate,UrlFull,ItemNumber," +
        "PrimaryProjectManager_Id,ProjectStatus_Id,ProjectType_Id,ProjectPriority_Id," +
        "Health_Id,Health_Previous_Id,Department_Id,CompanyDefault_Id,Portfolio_Input_Id,Program_Input_Id," +
        "ProposalDefault_Id,CustomFieldValue_Id,WorkPercentCompleteType_Id," +
        "ActualHours,ActualTimeTotal,ActualExpenseTotal,TargetWorkTotalCost," +
        "EstimateAtCompletionToTargetTotal_Variance,EstimateAtCompletionToTargetTotalPercent_Variance," +
        "ProfitSalesOrderActual,CreatedDateTimeUTC,UpdatedDateTimeUTC";

    private static string GetStandardTaskFields() =>
        "Id,Name,Description,Project_Id,StartDateTimeUTC,EndDateTimeUTC," +
        "WorkPercentComplete,WorkHours,IsActive,IsMilestone,ItemNumber,UrlFull";

    private static string GetFullTaskFields() =>
        "Id,Name,Description,Project_Id,StartDateTimeUTC,EndDateTimeUTC," +
        "WorkPercentComplete,WorkHours,WorkSeconds,DurationSeconds,DurationString," +
        "IsActive,IsOpen,IsMilestone,IsSummaryTask,ItemNumber,SummaryTask_Id,UrlFull," +
        "CustomFieldValue_Id,WorkPercentCompleteType_Id,ExcludeDatesFromCalc,ExcludeWorkFromCalc";

    /// <summary>
    /// Search projects tool implementation
    /// </summary>
    private async Task<McpToolResult> SearchProjectsAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var request = new ProjectSearchRequest();

        // Parse optional filter arguments
        if (arguments.TryGetValue("isActive", out var isActive))
            request.IsActive = ExtractBoolParameter(isActive);

        if (arguments.TryGetValue("isOpen", out var isOpen))
            request.IsOpen = ExtractBoolParameter(isOpen);

        if (arguments.TryGetValue("projectManagerId", out var managerId))
            request.ProjectManagerId = ExtractStringParameter(arguments, "projectManagerId");

        if (arguments.TryGetValue("projectStatusId", out var statusId))
            request.ProjectStatusId = ExtractStringParameter(arguments, "projectStatusId");

        // Support both 'fields' (new) and 'modelProperties' (legacy) parameters
        var fields = ExtractStringParameter(arguments, "fields") 
                  ?? ExtractStringParameter(arguments, "modelProperties");
        request.ModelProperties = ResolveFieldPreset(fields);

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

        // Support both 'fields' (new) and 'modelProperties' (legacy) parameters
        var fields = ExtractStringParameter(arguments, "fields") 
                  ?? ExtractStringParameter(arguments, "modelProperties");
        var modelProperties = ResolveFieldPreset(fields);

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

        // Support 'fields' parameter for controlling response size
        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveTaskFieldPreset(fields);

        var response = await _projectInsightApi.GetProjectTasksAsync(projectId, modelProperties, cancellationToken);

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
    /// Get project resources tool implementation - returns contributors assigned to a project
    /// </summary>
    private async Task<McpToolResult> GetProjectResourcesAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
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

        // Optional filters
        bool? isActive = null;
        if (arguments.TryGetValue("isActive", out var isActiveObj))
        {
            isActive = ExtractBoolParameter(isActiveObj);
        }

        var fields = ExtractStringParameter(arguments, "fields");

        var response = await _projectInsightApi.GetProjectResourcesAsync(projectId, isActive, fields, cancellationToken);

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

        var resources = response.Data ?? new List<ProjectResource>();
        
        // Build a readable summary
        var summaryLines = new List<string>
        {
            $"Found {resources.Count} resource(s) assigned to project {projectId}:"
        };

        foreach (var resource in resources)
        {
            var userName = resource.User?.Name ?? resource.User?.Email ?? resource.UserId ?? "Unknown";
            var role = resource.IsPrimaryProjectManager == true ? "Primary PM" 
                     : resource.IsProjectManager == true ? "Project Manager" 
                     : resource.IsProjectScheduler == true ? "Scheduler"
                     : resource.ProjectResourceRoleDescription ?? "Team Member";
            var allocation = resource.PercentAllocationDefault.HasValue ? $"{resource.PercentAllocationDefault:0}%" : "N/A";
            var hours = resource.ActualHours.HasValue ? $"{resource.ActualHours:0.#}h" : "0h";
            var tasks = resource.TaskCount ?? 0;
            
            summaryLines.Add($"  - {userName} ({role}): {allocation} allocation, {hours} logged, {tasks} tasks");
        }

        var resourcesJson = JsonSerializer.Serialize(resources, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = string.Join("\n", summaryLines) },
                new() { Type = "text", Text = resourcesJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Build an agent-actionable error string from an API response.
    /// </summary>
    private static string FormatApiError(string action, string? message, List<string>? errors)
    {
        var detail = errors is { Count: > 0 } ? string.Join("; ", errors) : (message ?? "unknown error");
        var combined = $"{message} {detail}";

        string hint;
        if (combined.Contains("NotFound") || combined.Contains("404"))
            hint = "Not found - verify the IDs with search_projects / get_project_tasks / search_users. ";
        else if (combined.Contains("Forbidden") || combined.Contains("403") || combined.Contains("Unauthorized"))
            hint = "Insufficient permissions to modify resources on this project. ";
        else
            hint = "";

        return $"Failed to {action}. {hint}Details: {detail}";
    }

    /// <summary>
    /// Add a user as a resource to a project
    /// </summary>
    private async Task<McpToolResult> AddProjectResourceAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("projectId", out var projectIdObj) || string.IsNullOrEmpty(projectIdObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: projectId parameter is required" } },
                IsError = true
            };
        }
        if (!arguments.TryGetValue("userId", out var userIdObj) || string.IsNullOrEmpty(userIdObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: userId parameter is required" } },
                IsError = true
            };
        }

        var projectId = projectIdObj.ToString()!;
        var userId = userIdObj.ToString()!;

        // Step 1: ensure the user is a resource on the project (idempotent).
        var ensure = await _projectInsightApi.EnsureProjectResourceAsync(projectId, userId, cancellationToken);
        if (!ensure.Success || ensure.Errors?.Count > 0)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("assign user to project", ensure.Message, ensure.Errors) } },
                IsError = true
            };
        }

        var resourceId = ensure.Data?.SavedId;

        // Step 2: if any role/allocation params were supplied, apply them via a follow-up save.
        var wantsRoleOrAllocation =
            arguments.ContainsKey("isProjectManager")
            || arguments.ContainsKey("isProjectScheduler")
            || arguments.ContainsKey("percentAllocationDefault")
            || arguments.ContainsKey("resourceTypeDefaultId");

        var setParts = new List<string>();

        if (wantsRoleOrAllocation)
        {
            if (string.IsNullOrEmpty(resourceId))
            {
                // The user is on the project, but we don't have an id to attach the role/allocation to.
                // Report honestly instead of pretending the role was set.
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = $"User {userId} was added to the project, but the role/allocation was NOT set because the resource id could not be determined. Call get_project_resources for project {projectId}, then update_project_resource with that id." } },
                    IsError = true
                };
            }

            var resource = new ProjectResource { Id = resourceId, ProjectId = projectId, UserId = userId };

            if (arguments.TryGetValue("isProjectManager", out var isPm) && isPm is JsonElement isPmEl
                && (isPmEl.ValueKind == JsonValueKind.True || isPmEl.ValueKind == JsonValueKind.False))
            {
                resource.IsProjectManager = isPmEl.GetBoolean();
                if (resource.IsProjectManager == true) setParts.Add("Project Manager");
            }
            if (arguments.TryGetValue("isProjectScheduler", out var isSched) && isSched is JsonElement isSchedEl
                && (isSchedEl.ValueKind == JsonValueKind.True || isSchedEl.ValueKind == JsonValueKind.False))
            {
                resource.IsProjectScheduler = isSchedEl.GetBoolean();
                if (resource.IsProjectScheduler == true) setParts.Add("Scheduler");
            }
            if (arguments.TryGetValue("percentAllocationDefault", out var alloc) && alloc is JsonElement allocEl
                && allocEl.ValueKind == JsonValueKind.Number)
            {
                resource.PercentAllocationDefault = allocEl.GetDecimal();
                setParts.Add($"{resource.PercentAllocationDefault:0}% allocation");
            }
            if (arguments.TryGetValue("resourceTypeDefaultId", out var rtId) && rtId is JsonElement rtEl
                && rtEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rtEl.GetString()))
            {
                resource.ResourceTypeDefaultId = rtEl.GetString();
                setParts.Add($"resource type {rtEl.GetString()}");
            }

            var save = await _projectInsightApi.SaveProjectResourceAsync(resource, cancellationToken);
            if (!save.Success || save.Errors?.Count > 0)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = $"User {userId} was added to the project, but setting the role/allocation failed. {FormatApiError("update the project assignment", save.Message, save.Errors)}" } },
                    IsError = true
                };
            }

            resourceId = save.Data?.SavedId ?? resourceId;
        }

        var roleText = setParts.Count > 0 ? $" as {string.Join(", ", setParts)}" : "";
        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Assigned user {userId} to project {projectId}{roleText}. Resource assignment ID: {resourceId ?? "unknown"}" },
                new() { Type = "text", Text = JsonSerializer.Serialize(ensure.Data, JsonOptions) }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Update an existing project resource assignment
    /// </summary>
    private async Task<McpToolResult> UpdateProjectResourceAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("id", out var idObj) || string.IsNullOrEmpty(idObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: id parameter is required (project resource ID)" } },
                IsError = true
            };
        }

        var resource = new ProjectResource { Id = idObj.ToString() };

        if (arguments.TryGetValue("isProjectManager", out var isPm) && isPm is JsonElement isPmEl)
            resource.IsProjectManager = isPmEl.GetBoolean();
        if (arguments.TryGetValue("isProjectScheduler", out var isSched) && isSched is JsonElement isSchedEl)
            resource.IsProjectScheduler = isSchedEl.GetBoolean();
        if (arguments.TryGetValue("isActive", out var isActive) && isActive is JsonElement isActiveEl)
            resource.IsActive = isActiveEl.GetBoolean();
        if (arguments.TryGetValue("percentAllocationDefault", out var alloc) && alloc is JsonElement allocEl)
            resource.PercentAllocationDefault = allocEl.GetDecimal();
        if (arguments.TryGetValue("resourceTypeDefaultId", out var rtId))
            resource.ResourceTypeDefaultId = rtId.ToString();

        var response = await _projectInsightApi.SaveProjectResourceAsync(resource, cancellationToken);

        if (!response.Success || response.Errors?.Count > 0)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("update the project assignment", response.Message, response.Errors) } },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully updated project resource {resource.Id}" },
                new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Remove a user from a project (delete resource assignment)
    /// </summary>
    private async Task<McpToolResult> RemoveProjectResourceAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("id", out var idObj) || string.IsNullOrEmpty(idObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: id parameter is required (project resource ID)" } },
                IsError = true
            };
        }

        var resourceId = idObj.ToString()!;
        var response = await _projectInsightApi.DeleteProjectResourceAsync(resourceId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("unassign the user from the project", response.Message, response.Errors) } },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent> { new() { Type = "text", Text = $"Successfully removed project resource {resourceId}" } },
            IsError = false
        };
    }

    /// <summary>
    /// Add a user as a resource to a task
    /// </summary>
    private async Task<McpToolResult> AddTaskResourceAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("taskId", out var taskIdObj) || string.IsNullOrEmpty(taskIdObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: taskId parameter is required" } },
                IsError = true
            };
        }
        if (!arguments.TryGetValue("userId", out var userIdObj) || string.IsNullOrEmpty(userIdObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: userId parameter is required" } },
                IsError = true
            };
        }

        var taskId = taskIdObj.ToString()!;
        var userId = userIdObj.ToString()!;

        // Auto-ensure the user is on the task's project so the caller doesn't have to.
        var task = await _projectInsightApi.GetTaskAsync(taskId, "Project_Id", cancellationToken);
        var projectId = task.Data?.ProjectId;
        if (!task.Success || string.IsNullOrEmpty(projectId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("look up the task's project", task.Message, task.Errors) } },
                IsError = true
            };
        }

        var ensure = await _projectInsightApi.EnsureProjectResourceAsync(projectId, userId, cancellationToken);
        if (!ensure.Success || ensure.Errors?.Count > 0)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("add the user to the task's project", ensure.Message, ensure.Errors) } },
                IsError = true
            };
        }

        var resource = new TaskResource
        {
            TaskId = taskId,
            UserId = userId
        };

        if (arguments.TryGetValue("workHours", out var wh) && wh is JsonElement whEl && whEl.ValueKind == JsonValueKind.Number)
            resource.WorkHours = whEl.GetDecimal();
        if (arguments.TryGetValue("percentAllocationDefault", out var alloc) && alloc is JsonElement allocEl && allocEl.ValueKind == JsonValueKind.Number)
            resource.PercentAllocationDefault = allocEl.GetDecimal();

        var response = await _projectInsightApi.SaveTaskResourceAsync(resource, cancellationToken);

        if (!response.Success || response.Errors?.Count > 0)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("assign user to task", response.Message, response.Errors) } },
                IsError = true
            };
        }

        var savedId = response.Data?.SavedId ?? "unknown";
        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully added user {resource.UserId} to task {resource.TaskId}. Resource assignment ID: {savedId}" },
                new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Remove a user from a task (delete task resource assignment)
    /// </summary>
    private async Task<McpToolResult> RemoveTaskResourceAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var resourceId = ExtractStringParameter(arguments, "id");

        // If no assignment id was given, resolve it from taskId + userId so callers
        // don't need the opaque handle (which is only surfaced when the assignment is created).
        if (string.IsNullOrEmpty(resourceId))
        {
            var taskId = ExtractStringParameter(arguments, "taskId");
            var userId = ExtractStringParameter(arguments, "userId");
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(userId))
            {
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = "Error: provide either 'id' (the task resource assignment ID), or both 'taskId' and 'userId' so it can be resolved." } },
                    IsError = true
                };
            }

            var list = await _projectInsightApi.GetTaskResourcesAsync(taskId, null, null, cancellationToken);
            if (!list.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("look up the task's assignments", list.Message, list.Errors) } },
                    IsError = true
                };
            }

            var matches = (list.Data ?? new List<TaskResource>())
                .Where(r => r.UserId == userId && !string.IsNullOrEmpty(r.Id))
                .ToList();

            if (matches.Count == 0)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = $"User {userId} is not assigned to task {taskId} - nothing to remove. Use get_task_resources to see who is assigned." } },
                    IsError = true
                };
            }
            if (matches.Count > 1)
            {
                var ids = string.Join(", ", matches.Select(m => m.Id));
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = $"User {userId} has multiple assignments on task {taskId} (ids: {ids}). Call remove_task_resource again with a specific 'id'." } },
                    IsError = true
                };
            }

            resourceId = matches[0].Id!;
        }

        var response = await _projectInsightApi.DeleteTaskResourceAsync(resourceId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("unassign the user from the task", response.Message, response.Errors) } },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent> { new() { Type = "text", Text = $"Successfully removed task resource {resourceId}" } },
            IsError = false
        };
    }

    /// <summary>
    /// Get task resources tool implementation - lists users assigned to a task with their assignment ids
    /// </summary>
    private async Task<McpToolResult> GetTaskResourcesAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var taskId = ExtractStringParameter(arguments, "taskId");
        if (string.IsNullOrEmpty(taskId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: taskId parameter is required" } },
                IsError = true
            };
        }

        bool? isActive = null;
        if (arguments.TryGetValue("isActive", out var isActiveObj))
        {
            isActive = ExtractBoolParameter(isActiveObj);
        }

        var response = await _projectInsightApi.GetTaskResourcesAsync(taskId, isActive, null, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("list task resources", response.Message, response.Errors) } },
                IsError = true
            };
        }

        var resources = response.Data ?? new List<TaskResource>();

        var summaryLines = new List<string>
        {
            $"Found {resources.Count} user(s) assigned to task {taskId}:"
        };

        foreach (var resource in resources)
        {
            var userName = resource.User?.Name ?? resource.User?.Email ?? resource.UserId ?? "Unknown";
            var hours = resource.WorkHours.HasValue ? $"{resource.WorkHours:0.#}h planned" : "no planned hours";
            var allocation = resource.PercentAllocationDefault.HasValue ? $"{resource.PercentAllocationDefault:0}% allocation" : "N/A allocation";
            summaryLines.Add($"  - {userName}: {hours}, {allocation} (assignment id: {resource.Id})");
        }

        var resourcesJson = JsonSerializer.Serialize(resources, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = string.Join("\n", summaryLines) },
                new() { Type = "text", Text = resourcesJson }
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
    /// Get task tool implementation
    /// </summary>
    private async Task<McpToolResult> GetTaskAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var taskId = ExtractStringParameter(arguments, "taskId");
        if (string.IsNullOrEmpty(taskId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: taskId parameter is required" }
                },
                IsError = true
            };
        }

        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveTaskFieldPreset(fields);

        var response = await _projectInsightApi.GetTaskAsync(taskId, modelProperties, cancellationToken);

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

        var taskJson = JsonSerializer.Serialize(response.Data, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Task: {response.Data?.Name ?? "Unknown"}" },
                new() { Type = "text", Text = taskJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Create task tool implementation
    /// </summary>
    private async Task<McpToolResult> CreateTaskAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("task", out var taskObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: task parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var taskJson = JsonSerializer.Serialize(taskObj, JsonOptions);
            var task = JsonSerializer.Deserialize<ProjectTask>(taskJson, JsonOptions);

            if (task == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse task data" }
                    },
                    IsError = true
                };
            }

            if (string.IsNullOrEmpty(task.ProjectId))
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: project_Id is required to create a task" }
                    },
                    IsError = true
                };
            }

            var response = await _projectInsightApi.CreateTaskAsync(task, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" },
                        new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                    },
                    IsError = true
                };
            }

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Created task with ID: {response.Data?.SavedId}" },
                    new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
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
                    new() { Type = "text", Text = $"Error parsing task data: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Update task tool implementation
    /// </summary>
    private async Task<McpToolResult> UpdateTaskAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var taskId = ExtractStringParameter(arguments, "taskId");
        if (string.IsNullOrEmpty(taskId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: taskId parameter is required" }
                },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("task", out var taskObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: task parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var taskJson = JsonSerializer.Serialize(taskObj, JsonOptions);
            var task = JsonSerializer.Deserialize<ProjectTask>(taskJson, JsonOptions);

            if (task == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse task data" }
                    },
                    IsError = true
                };
            }

            // Ensure task ID is set from parameter
            task.Id = taskId;

            var response = await _projectInsightApi.UpdateTaskAsync(task, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" },
                        new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                    },
                    IsError = true
                };
            }

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Updated task: {taskId}" },
                    new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
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
                    new() { Type = "text", Text = $"Error parsing task data: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    #region TimeSheet Tool Implementations

    /// <summary>
    /// Get timesheet by ID
    /// </summary>
    private async Task<McpToolResult> GetTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeSheetId = ExtractStringParameter(arguments, "timeSheetId");
        if (string.IsNullOrEmpty(timeSheetId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheetId parameter is required" }
                },
                IsError = true
            };
        }

        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveTimeSheetFieldPreset(fields);

        var response = await _projectInsightApi.GetTimeSheetAsync(timeSheetId, modelProperties, cancellationToken);

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
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Search timesheets
    /// </summary>
    private async Task<McpToolResult> SearchTimeSheetsAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var request = new TimeSheetSearchRequest();

        if (arguments.TryGetValue("userId", out var userId))
            request.UserGuidList = ExtractStringParameter(arguments, "userId");
        if (arguments.TryGetValue("approverUserId", out var approver))
            request.UserApproverGuidList = ExtractStringParameter(arguments, "approverUserId");
        if (arguments.TryGetValue("startDate", out var startDate))
            request.StartDate = ExtractDateParameter(arguments, "startDate");
        if (arguments.TryGetValue("endDate", out var endDate))
            request.EndDate = ExtractDateParameter(arguments, "endDate");
        if (arguments.TryGetValue("excludeUnsubmitted", out var exUnsubmitted))
            request.ExcludeUnsubmitted = ExtractBoolParameter(exUnsubmitted);
        if (arguments.TryGetValue("excludeApproved", out var exApproved))
            request.ExcludeApproved = ExtractBoolParameter(exApproved);
        if (arguments.TryGetValue("excludeRejected", out var exRejected))
            request.ExcludeRejected = ExtractBoolParameter(exRejected);
        if (arguments.TryGetValue("excludePendingApproval", out var exPending))
            request.ExcludePendingApproval = ExtractBoolParameter(exPending);

        var fields = ExtractStringParameter(arguments, "fields");
        request.ModelProperties = ResolveTimeSheetFieldPreset(fields);

        var response = await _projectInsightApi.SearchTimeSheetsAsync(request, cancellationToken);

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
                new() { Type = "text", Text = $"Found {response.TotalCount} timesheet(s)" },
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Get current user's time status
    /// </summary>
    private async Task<McpToolResult> GetMyTimeStatusAsync(CancellationToken cancellationToken)
    {
        var response = await _projectInsightApi.GetMyTimeCurrentStatusAsync(cancellationToken);

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
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Update an existing timesheet
    /// </summary>
    private async Task<McpToolResult> UpdateTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeSheetId = ExtractStringParameter(arguments, "timeSheetId");
        if (string.IsNullOrEmpty(timeSheetId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheetId parameter is required" }
                },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("timeSheet", out var timeSheetObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheet parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var timeSheetJson = JsonSerializer.Serialize(timeSheetObj, JsonOptions);
            var timeSheet = JsonSerializer.Deserialize<TimeSheet>(timeSheetJson, JsonOptions);

            if (timeSheet == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse timesheet data" }
                    },
                    IsError = true
                };
            }

            // Ensure ID is set for update
            timeSheet.Id = timeSheetId;

            var response = await _projectInsightApi.SaveTimeSheetAsync(timeSheet, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" },
                        new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                    },
                    IsError = true
                };
            }

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Updated timesheet: {timeSheetId}" },
                    new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
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
                    new() { Type = "text", Text = $"Error parsing timesheet data: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Create a new timesheet and immediately submit it for approval
    /// </summary>
    private async Task<McpToolResult> CreateAndSubmitTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var startDate = ExtractStringParameter(arguments, "startDate");
        var endDate = ExtractStringParameter(arguments, "endDate");
        var userId = ExtractStringParameter(arguments, "userId");

        if (string.IsNullOrEmpty(startDate) || string.IsNullOrEmpty(endDate))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: startDate and endDate parameters are required" }
                },
                IsError = true
            };
        }

        if (!DateTime.TryParse(startDate, out var parsedStartDate) || !DateTime.TryParse(endDate, out var parsedEndDate))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: startDate and endDate must be valid dates (yyyy-MM-dd)" }
                },
                IsError = true
            };
        }

        // If no userId provided, get the current user's ID
        if (string.IsNullOrEmpty(userId))
        {
            var currentUser = await _projectInsightApi.GetCurrentUserAsync(null, cancellationToken);
            if (currentUser.Success && currentUser.Data != null)
            {
                userId = currentUser.Data.Id;
            }
            else
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: userId not provided and could not determine current user" }
                    },
                    IsError = true
                };
            }
        }

        var response = await _projectInsightApi.CreateAndSubmitTimeSheetAsync(userId!, parsedStartDate, parsedEndDate, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Created and submitted timesheet with ID: {response.Data?.SavedId}" },
                new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Submit a timesheet for approval
    /// </summary>
    private async Task<McpToolResult> SubmitTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeSheetId = ExtractStringParameter(arguments, "timeSheetId");
        if (string.IsNullOrEmpty(timeSheetId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheetId parameter is required" }
                },
                IsError = true
            };
        }

        var approverUserId = ExtractStringParameter(arguments, "approverUserId");
        var response = await _projectInsightApi.SubmitTimeSheetAsync(timeSheetId, approverUserId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully submitted timesheet {timeSheetId} for approval" }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Approve a timesheet
    /// </summary>
    private async Task<McpToolResult> ApproveTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeSheetId = ExtractStringParameter(arguments, "timeSheetId");
        if (string.IsNullOrEmpty(timeSheetId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheetId parameter is required" }
                },
                IsError = true
            };
        }

        var comments = ExtractStringParameter(arguments, "comments");
        var response = await _projectInsightApi.ApproveTimeSheetAsync(timeSheetId, comments, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully approved timesheet {timeSheetId}" }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Reject a timesheet
    /// </summary>
    private async Task<McpToolResult> RejectTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeSheetId = ExtractStringParameter(arguments, "timeSheetId");
        if (string.IsNullOrEmpty(timeSheetId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheetId parameter is required" }
                },
                IsError = true
            };
        }

        var comments = ExtractStringParameter(arguments, "comments");
        var response = await _projectInsightApi.RejectTimeSheetAsync(timeSheetId, comments, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully rejected timesheet {timeSheetId}" }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Unsubmit a timesheet - resets it from Submitted/Rejected/Pending back to Unsubmitted (draft) state.
    /// This is useful when a rejected or submitted timesheet is blocking new time entry creation for its date range.
    /// </summary>
    private async Task<McpToolResult> UnsubmitTimeSheetAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeSheetId = ExtractStringParameter(arguments, "timeSheetId");
        if (string.IsNullOrEmpty(timeSheetId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeSheetId parameter is required" }
                },
                IsError = true
            };
        }

        // Set ApprovalStatus to 0 (Unsubmitted) via the update endpoint
        var timeSheet = new TimeSheet
        {
            Id = timeSheetId,
            ApprovalStatus = 0
        };

        var response = await _projectInsightApi.SaveTimeSheetAsync(timeSheet, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully unsubmitted timesheet {timeSheetId}. It is now in draft/unsubmitted state and will no longer block time entry creation for its date range." }
            },
            IsError = false
        };
    }

    #endregion

    #region Time Entry Tool Implementations

    /// <summary>
    /// Get time entries for a project within a date range.
    /// 
    /// CRITICAL API BUG: The Project Insight API's projectGuidList and taskGuidList filters
    /// do NOT work correctly. They return time entries for users who have logged ANY time
    /// against the specified project/tasks, but includes ALL their entries across ALL projects.
    /// 
    /// WORKAROUND: We use the API filter to get a superset, then filter client-side by Project_Id.
    /// </summary>
    private async Task<McpToolResult> GetProjectTimeEntriesAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
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

        var startDate = ExtractDateParameter(arguments, "startDate");
        var endDate = ExtractDateParameter(arguments, "endDate");
        var userId = ExtractStringParameter(arguments, "userId");
        var taskId = ExtractStringParameter(arguments, "taskId");

        _logger.LogInformation("Fetching time entries for project {ProjectId} with date range {StartDate} to {EndDate}", 
            projectId, startDate?.ToString("yyyy-MM-dd") ?? "none", endDate?.ToString("yyyy-MM-dd") ?? "none");

        // Use the dedicated /api/time-entry/project/{id} endpoint which correctly returns
        // only entries belonging to the project (unlike /search which has broken filtering)
        var response = await _projectInsightApi.GetProjectTimeEntriesAsync(projectId, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        var entries = response.Data ?? new List<TimeEntry>();

        // Client-side date filtering (API endpoint doesn't support date params)
        if (startDate.HasValue)
            entries = entries.Where(e => e.Date >= startDate.Value).ToList();
        if (endDate.HasValue)
            entries = entries.Where(e => e.Date <= endDate.Value).ToList();

        // Client-side user filtering
        if (!string.IsNullOrEmpty(userId))
            entries = entries.Where(e => e.UserId?.Equals(userId, StringComparison.OrdinalIgnoreCase) == true).ToList();

        // Client-side task filtering
        if (!string.IsNullOrEmpty(taskId))
            entries = entries.Where(e => e.TaskId?.Equals(taskId, StringComparison.OrdinalIgnoreCase) == true).ToList();
        
        // Calculate summary statistics
        var totalHours = entries.Sum(e => e.ActualHours ?? 0);
        var billableHours = entries.Sum(e => e.BillableHours ?? 0);
        var billableTotal = entries.Sum(e => e.BillableTotal ?? 0);
        var uniqueUsers = entries.Select(e => e.UserId).Distinct().Count();

        var summary = $"Found {entries.Count} time entries for project.\n" +
                     $"Total Hours: {totalHours:F2}\n" +
                     $"Billable Hours: {billableHours:F2}\n" +
                     $"Billable Total: ${billableTotal:F2}\n" +
                     $"Unique Contributors: {uniqueUsers}";

        // Format entries for display - show key fields
        var formattedEntries = entries.Select(e => new
        {
            e.Id,
            e.Date,
            e.Description,
            Hours = e.ActualHours,
            BillableHours = e.BillableHours,
            IsBillable = e.IsBillable,
            UserId = e.UserId,
            TaskId = e.TaskId
        });

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = summary },
                new() { Type = "text", Text = JsonSerializer.Serialize(formattedEntries, JsonOptions) }
            },
            IsError = false
        };
    }

    private async Task<McpToolResult> SearchTimeEntriesAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var userId = ExtractStringParameter(arguments, "userId");
        var projectId = ExtractStringParameter(arguments, "projectId");
        var startDate = ExtractDateParameter(arguments, "startDate");
        var endDate = ExtractDateParameter(arguments, "endDate");
        var isBillableStr = ExtractStringParameter(arguments, "isBillable");
        var groupByProject = ExtractStringParameter(arguments, "groupByProject");

        bool? isBillable = null;
        if (!string.IsNullOrEmpty(isBillableStr))
            isBillable = isBillableStr.Equals("true", StringComparison.OrdinalIgnoreCase);

        bool shouldGroup = string.IsNullOrEmpty(groupByProject) || groupByProject.Equals("true", StringComparison.OrdinalIgnoreCase);

        var request = new TimeEntrySearchRequest
        {
            UserGuidList = userId,
            ProjectGuidList = projectId,
            StartDate = startDate,
            EndDate = endDate,
            IsBillable = isBillable
        };

        _logger.LogInformation("Searching time entries for user {UserId}, project {ProjectId}, date range {StartDate} to {EndDate}",
            userId ?? "any", projectId ?? "any",
            startDate?.ToString("yyyy-MM-dd") ?? "none", endDate?.ToString("yyyy-MM-dd") ?? "none");

        var response = await _projectInsightApi.SearchTimeEntriesAsync(request, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        var entries = response.Data ?? new List<TimeEntry>();

        // Calculate overall summary
        var totalHours = entries.Sum(e => e.ActualHours ?? 0);
        var billableHours = entries.Sum(e => e.BillableHours ?? 0);
        var billableTotal = entries.Sum(e => e.BillableTotal ?? 0);

        if (shouldGroup && entries.Count > 0)
        {
            // Group by project and provide per-project summary
            var projectGroups = entries
                .GroupBy(e => e.ProjectId ?? "unknown")
                .Select(g => new
                {
                    projectId = g.Key,
                    entryCount = g.Count(),
                    totalHours = g.Sum(e => e.ActualHours ?? 0),
                    billableHours = g.Sum(e => e.BillableHours ?? 0),
                    billableTotal = g.Sum(e => e.BillableTotal ?? 0),
                    firstDate = g.Min(e => e.Date),
                    lastDate = g.Max(e => e.Date)
                })
                .OrderByDescending(g => g.totalHours)
                .ToList();

            var summary = $"Found {entries.Count} time entries across {projectGroups.Count} projects.\n" +
                         $"Total Hours: {totalHours:F2}\n" +
                         $"Billable Hours: {billableHours:F2}\n" +
                         $"Billable Total: ${billableTotal:F2}";

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = summary },
                    new() { Type = "text", Text = JsonSerializer.Serialize(projectGroups, JsonOptions) }
                },
                IsError = false
            };
        }
        else
        {
            var summary = $"Found {entries.Count} time entries.\n" +
                         $"Total Hours: {totalHours:F2}\n" +
                         $"Billable Hours: {billableHours:F2}\n" +
                         $"Billable Total: ${billableTotal:F2}";

            var formattedEntries = entries.Select(e => new
            {
                e.Id,
                e.Date,
                e.Description,
                Hours = e.ActualHours,
                BillableHours = e.BillableHours,
                IsBillable = e.IsBillable,
                ProjectId = e.ProjectId,
                UserId = e.UserId,
                TaskId = e.TaskId
            });

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = summary },
                    new() { Type = "text", Text = JsonSerializer.Serialize(formattedEntries, JsonOptions) }
                },
                IsError = false
            };
        }
    }

    private async Task<McpToolResult> CreateTimeEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("timeEntry", out var timeEntryObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeEntry parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var timeEntryJson = JsonSerializer.Serialize(timeEntryObj, JsonOptions);
            var timeEntry = JsonSerializer.Deserialize<TimeEntry>(timeEntryJson, JsonOptions);

            if (timeEntry == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse time entry data" }
                    },
                    IsError = true
                };
            }

            // Ensure no ID for create operation
            timeEntry.Id = null;

            // Auto-populate User_Id from authenticated user if not provided
            if (string.IsNullOrEmpty(timeEntry.UserId))
            {
                var currentUser = await _projectInsightApi.GetCurrentUserAsync(null, cancellationToken);
                if (currentUser.Success && currentUser.Data != null)
                {
                    timeEntry.UserId = currentUser.Data.Id;
                }
                else
                {
                    return new McpToolResult
                    {
                        Content = new List<McpContent>
                        {
                            new() { Type = "text", Text = "Error: Could not determine current user. The API requires a User_Id for time entries." }
                        },
                        IsError = true
                    };
                }
            }

            // Auto-attach to existing timesheet if one covers the entry date.
            // The PI API blocks new entries for dates covered by a submitted/rejected/unsubmitted timesheet
            // unless the entry includes the TimeSheet_Id.
            if (string.IsNullOrEmpty(timeEntry.TimeSheetId) && timeEntry.Date.HasValue)
            {
                var searchRequest = new TimeSheetSearchRequest
                {
                    UserGuidList = timeEntry.UserId,
                    StartDate = timeEntry.Date.Value.Date,
                    EndDate = timeEntry.Date.Value.Date,
                    ModelProperties = "Id,StartDate,EndDate,Status"
                };
                var sheetsResponse = await _projectInsightApi.SearchTimeSheetsAsync(searchRequest, cancellationToken);
                if (sheetsResponse.Success && sheetsResponse.Data?.Count > 0)
                {
                    // Use the first matching timesheet that covers this date
                    var matchingSheet = sheetsResponse.Data.FirstOrDefault(ts =>
                        ts.StartDate.HasValue && ts.EndDate.HasValue &&
                        timeEntry.Date.Value.Date >= ts.StartDate.Value.Date &&
                        timeEntry.Date.Value.Date <= ts.EndDate.Value.Date);
                    if (matchingSheet != null)
                    {
                        timeEntry.TimeSheetId = matchingSheet.Id;
                        _logger.LogInformation("Auto-attached time entry to existing timesheet {TimeSheetId} ({Start} - {End})",
                            matchingSheet.Id, matchingSheet.StartDate, matchingSheet.EndDate);
                    }
                }
            }

            var response = await _projectInsightApi.SaveTimeEntryAsync(timeEntry, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" },
                        new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                    },
                    IsError = true
                };
            }

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Created time entry with ID: {response.Data?.SavedId}" },
                    new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
                },
                IsError = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating time entry");
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error creating time entry: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Update an existing time entry
    /// </summary>
    private async Task<McpToolResult> UpdateTimeEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeEntryId = ExtractStringParameter(arguments, "timeEntryId");
        if (string.IsNullOrEmpty(timeEntryId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeEntryId parameter is required" }
                },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("timeEntry", out var timeEntryObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeEntry parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var timeEntryJson = JsonSerializer.Serialize(timeEntryObj, JsonOptions);
            var timeEntry = JsonSerializer.Deserialize<TimeEntry>(timeEntryJson, JsonOptions);

            if (timeEntry == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse time entry data" }
                    },
                    IsError = true
                };
            }

            // Set the ID for update
            timeEntry.Id = timeEntryId;

            var response = await _projectInsightApi.SaveTimeEntryAsync(timeEntry, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" },
                        new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                    },
                    IsError = true
                };
            }

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Updated time entry: {timeEntryId}" },
                    new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
                },
                IsError = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating time entry");
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error updating time entry: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Delete a time entry by ID
    /// </summary>
    private async Task<McpToolResult> DeleteTimeEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var timeEntryId = ExtractStringParameter(arguments, "timeEntryId");
        if (string.IsNullOrEmpty(timeEntryId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: timeEntryId parameter is required" }
                },
                IsError = true
            };
        }

        var response = await _projectInsightApi.DeleteTimeEntryAsync(timeEntryId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully deleted time entry {timeEntryId}" }
            },
            IsError = false
        };
    }

    #endregion

    #region TimeSheet Helper Methods

    /// <summary>
    /// Resolves timesheet field presets to actual model property strings.
    /// </summary>
    private static string ResolveTimeSheetFieldPreset(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return GetSummaryTimeSheetFields(); // Default to summary for token efficiency

        return fields.ToLowerInvariant().Trim() switch
        {
            "minimal" => "Id,Name",
            "summary" => GetSummaryTimeSheetFields(),
            "standard" => GetStandardTimeSheetFields(),
            "full" => GetFullTimeSheetFields(),
            _ => fields
        };
    }

    private static string GetSummaryTimeSheetFields() =>
        "Id,Name,User_Id,StartDate,EndDate,TotalHours,IsSubmitted,IsApproved,IsRejected";

    private static string GetStandardTimeSheetFields() =>
        "Id,Name,User_Id,UserApprover_Id,StartDate,EndDate,TotalHours,BillableHours,NonBillableHours," +
        "IsSubmitted,IsApproved,IsRejected,IsLocked,UrlFull";

    private static string GetFullTimeSheetFields() =>
        "Id,Name,Description,User_Id,UserApprover_Id,StartDate,EndDate,TotalHours,BillableHours,NonBillableHours," +
        "IsSubmitted,IsApproved,IsRejected,IsLocked,IsPending,SubmittedDateTimeUTC,ApprovedDateTimeUTC," +
        "RejectedDateTimeUTC,Comments,UrlFull,CreatedDateTimeUTC,UpdatedDateTimeUTC";

    private static DateTime? ExtractDateParameter(Dictionary<string, object> arguments, string paramName)
    {
        var value = ExtractStringParameter(arguments, paramName);
        if (string.IsNullOrEmpty(value)) return null;
        return DateTime.TryParse(value, out var date) ? date : null;
    }

    #endregion

    #region User Tool Implementations

    /// <summary>
    /// Get current authenticated user
    /// </summary>
    private async Task<McpToolResult> GetCurrentUserAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveUserFieldPreset(fields);

        var response = await _projectInsightApi.GetCurrentUserAsync(modelProperties, cancellationToken);

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
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Get user by ID
    /// </summary>
    private async Task<McpToolResult> GetUserAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var userId = ExtractStringParameter(arguments, "userId");
        if (string.IsNullOrEmpty(userId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: userId parameter is required" }
                },
                IsError = true
            };
        }

        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveUserFieldPreset(fields);

        var response = await _projectInsightApi.GetUserAsync(userId, modelProperties, cancellationToken);

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
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Get multiple users by IDs
    /// </summary>
    private async Task<McpToolResult> GetUsersAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var ids = ExtractStringParameter(arguments, "ids");
        if (string.IsNullOrEmpty(ids))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: ids parameter is required (comma-separated user IDs)" }
                },
                IsError = true
            };
        }

        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveUserFieldPreset(fields);

        var response = await _projectInsightApi.GetUsersAsync(ids, modelProperties, cancellationToken);

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
                new() { Type = "text", Text = $"Found {response.TotalCount} user(s)" },
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Search/list all users - works around API limitation by collecting user IDs from projects and timesheets
    /// </summary>
    private async Task<McpToolResult> SearchUsersAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var nameFilter = ExtractStringParameter(arguments, "name");
        var isActiveParam = arguments.TryGetValue("isActive", out var isActiveVal) ? ExtractBoolParameter(isActiveVal) : null;
        var fields = ExtractStringParameter(arguments, "fields");
        var modelProperties = ResolveUserFieldPreset(fields);

        var response = await _projectInsightApi.ListAllUsersAsync(isActiveParam, nameFilter, modelProperties, cancellationToken);

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
                new() { Type = "text", Text = $"Found {response.TotalCount} user(s)" },
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Resolves user field presets to actual model property strings.
    /// </summary>
    private static string ResolveUserFieldPreset(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return GetSummaryUserFields(); // Default to summary for token efficiency

        return fields.ToLowerInvariant().Trim() switch
        {
            "minimal" => "Id,Name",
            "summary" => GetSummaryUserFields(),
            "standard" => GetStandardUserFields(),
            "full" => GetFullUserFields(),
            _ => fields
        };
    }

    private static string GetSummaryUserFields() =>
        "Id,Name,FirstName,LastName,Email,IsActive";

    private static string GetStandardUserFields() =>
        "Id,Name,FirstName,LastName,UserName,Email,IsActive,Title,Phone,Department_Id";

    private static string GetFullUserFields() =>
        "Id,Name,FirstName,LastName,UserName,Email,IsActive,Title,Phone,MobilePhone," +
        "Department_Id,UserType_Id,CompanyDefault_Id,CustomFieldValue_Id,UrlFull";

    #endregion

    #region Resource Type Tool Implementations

    /// <summary>
    /// Get resource types by IDs - resolves resource type GUIDs to names.
    /// Accepts a comma-separated list of IDs and fetches each one.
    /// </summary>
    private async Task<McpToolResult> GetResourceTypesAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var ids = ExtractStringParameter(arguments, "ids");
        if (string.IsNullOrEmpty(ids))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: ids parameter is required (comma-separated resource type IDs)" }
                },
                IsError = true
            };
        }

        var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<ResourceType>();
        var errors = new List<string>();

        // Fetch each resource type by ID
        foreach (var id in idList)
        {
            var response = await _projectInsightApi.GetResourceTypeAsync(id, cancellationToken);
            if (response.Success && response.Data != null)
            {
                results.Add(response.Data);
            }
            else
            {
                errors.Add($"Failed to resolve {id}: {response.Message}");
            }
        }

        var summaryLines = new List<string> { $"Resolved {results.Count}/{idList.Length} resource type(s):" };
        foreach (var rt in results)
        {
            summaryLines.Add($"  {rt.Id} = {rt.Name}");
        }
        if (errors.Count > 0)
        {
            summaryLines.Add($"\nErrors ({errors.Count}):");
            summaryLines.AddRange(errors.Select(e => $"  {e}"));
        }

        var resultJson = JsonSerializer.Serialize(results, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = string.Join("\n", summaryLines) },
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    #endregion

    #region Rate Card (Company Contract) Tool Implementations

    /// <summary>
    /// Get rate cards for a project - shows summary of each card and its rate tiers
    /// </summary>
    private async Task<McpToolResult> GetProjectRateCardsAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
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

        var response = await _projectInsightApi.GetCompanyContractsByProjectAsync(projectId, cancellationToken);

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

        var contracts = response.Data ?? new List<CompanyContract>();

        // Build readable summary
        var summaryLines = new List<string>
        {
            $"Found {contracts.Count} rate card(s) for project {projectId}:"
        };

        foreach (var contract in contracts)
        {
            summaryLines.Add($"\n--- {contract.Name ?? "Unnamed"} (ID: {contract.Id}) ---");
            summaryLines.Add($"  Number: {contract.Number ?? "N/A"} | Active: {contract.IsActive} | Default Bill Rate: {contract.RateBillDefault:C}");
            if (contract.ContractStart.HasValue || contract.ContractEnd.HasValue)
                summaryLines.Add($"  Period: {contract.ContractStart?.ToString("yyyy-MM-dd") ?? "N/A"} to {contract.ContractEnd?.ToString("yyyy-MM-dd") ?? "N/A"}");

            var container = contract.RateCardRateContainer;
            if (container != null)
            {
                if (container.UserRates?.Count > 0)
                    summaryLines.Add($"  User Rates: {container.UserRates.Count} entries");
                if (container.ResourceTypeRates?.Count > 0)
                    summaryLines.Add($"  Resource Type Rates: {container.ResourceTypeRates.Count} entries");
                if (container.TaskTypeRates?.Count > 0)
                    summaryLines.Add($"  Task Type Rates: {container.TaskTypeRates.Count} entries");
                if (container.TimeCodeRates?.Count > 0)
                    summaryLines.Add($"  Time Code Rates: {container.TimeCodeRates.Count} entries");
            }
        }

        var contractsJson = JsonSerializer.Serialize(contracts, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = string.Join("\n", summaryLines) },
                new() { Type = "text", Text = contractsJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Get a single rate card by ID with full detail
    /// </summary>
    private async Task<McpToolResult> GetRateCardAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var rateCardId = ExtractStringParameter(arguments, "rateCardId");
        if (string.IsNullOrEmpty(rateCardId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rateCardId parameter is required" }
                },
                IsError = true
            };
        }

        var response = await _projectInsightApi.GetCompanyContractAsync(rateCardId, cancellationToken);

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

        var contract = response.Data;
        var resultJson = JsonSerializer.Serialize(contract, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Rate Card: {contract?.Name ?? "Unknown"} (ID: {contract?.Id})" },
                new() { Type = "text", Text = resultJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Save (create or update) a rate on a rate card
    /// </summary>
    private async Task<McpToolResult> SaveRateCardRateAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var rateCardId = ExtractStringParameter(arguments, "rateCardId");
        if (string.IsNullOrEmpty(rateCardId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rateCardId parameter is required" }
                },
                IsError = true
            };
        }

        var rateType = ExtractStringParameter(arguments, "rateType");
        if (string.IsNullOrEmpty(rateType))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rateType parameter is required (default, user, resource-type, task-type, or time-code)" }
                },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("rate", out var rateObj))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rate parameter is required" }
                },
                IsError = true
            };
        }

        try
        {
            var rateJson = JsonSerializer.Serialize(rateObj, JsonOptions);
            var rate = JsonSerializer.Deserialize<RateCardRate>(rateJson, JsonOptions);

            if (rate == null)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "Error: Could not parse rate data" }
                    },
                    IsError = true
                };
            }

            var response = await _projectInsightApi.SaveCompanyContractRateAsync(rateType, rateCardId, rate, cancellationToken);

            if (!response.Success)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = $"Error: {response.Message}" },
                        new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                    },
                    IsError = true
                };
            }

            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Saved {rateType} rate on rate card {rateCardId}" },
                    new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
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
                    new() { Type = "text", Text = $"Error parsing rate data: {ex.Message}" }
                },
                IsError = true
            };
        }
    }

    /// <summary>
    /// Delete a rate from a rate card
    /// </summary>
    private async Task<McpToolResult> DeleteRateCardRateAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var rateCardId = ExtractStringParameter(arguments, "rateCardId");
        if (string.IsNullOrEmpty(rateCardId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rateCardId parameter is required" }
                },
                IsError = true
            };
        }

        var rateType = ExtractStringParameter(arguments, "rateType");
        if (string.IsNullOrEmpty(rateType))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rateType parameter is required (default, user, resource-type, task-type, or time-code)" }
                },
                IsError = true
            };
        }

        var rateId = ExtractStringParameter(arguments, "rateId");
        if (string.IsNullOrEmpty(rateId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: rateId parameter is required" }
                },
                IsError = true
            };
        }

        var response = await _projectInsightApi.DeleteCompanyContractRateAsync(rateType, rateCardId, rateId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully deleted {rateType} rate {rateId} from rate card {rateCardId}" }
            },
            IsError = false
        };
    }

    #endregion

    #region PTO Tool Implementations

    /// <summary>
    /// Get a single PTO entry by ID
    /// </summary>
    private async Task<McpToolResult> GetPtoEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var ptoEntryId = ExtractStringParameter(arguments, "ptoEntryId");
        if (string.IsNullOrEmpty(ptoEntryId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: ptoEntryId parameter is required" } },
                IsError = true
            };
        }

        // Request TimeCode expansion to get the name
        var response = await _projectInsightApi.GetUserPtoAsync(ptoEntryId,
            modelProperties: "Id,User_Id,Date,PTOHours,PTOAdded,PTOBalance,TimeCode_Id,TimeCode,TimeEntry_Id,Description,DateApproved,UserApproved_Id,CreatedDateTimeUTC,UpdatedDateTimeUTC;TimeCode:Id,Name,IsActive",
            cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        var ptoJson = JsonSerializer.Serialize(response.Data, JsonOptions);
        var entry = response.Data;
        var timeCodeLabel = entry?.TimeCode?.Name ?? entry?.TimeCodeId ?? "Unknown";
        var summary = entry?.PTOHours != null
            ? $"PTO entry: {entry.PTOHours}h {timeCodeLabel} on {entry.Date:yyyy-MM-dd} — {entry.Description ?? "(no description)"}"
            : $"PTO balance entry on {entry?.Date:yyyy-MM-dd} — added {entry?.PTOAdded}h";

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = summary },
                new() { Type = "text", Text = ptoJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// List PTO entries for a user with optional client-side date filtering
    /// </summary>
    private async Task<McpToolResult> ListUserPtoAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var userId = ExtractStringParameter(arguments, "userId");
        if (string.IsNullOrEmpty(userId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: userId parameter is required" } },
                IsError = true
            };
        }

        // Request TimeCode expansion so we can discover names dynamically
        var response = await _projectInsightApi.ListUserPtoAsync(userId,
            modelProperties: "Id,User_Id,Date,PTOHours,PTOAdded,PTOBalance,TimeCode_Id,TimeCode,TimeEntry_Id,Description,DateApproved,UserApproved_Id,CreatedDateTimeUTC,UpdatedDateTimeUTC;TimeCode:Id,Name,IsActive",
            cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        var entries = response.Data ?? new List<UserPto>();

        // Extract unique time codes for discovery (before filtering)
        var availableTimeCodes = entries
            .Where(e => e.TimeCode != null)
            .Select(e => e.TimeCode!)
            .GroupBy(tc => tc.Id)
            .Select(g => g.First())
            .OrderBy(tc => tc.Name)
            .ToList();

        // Client-side date filtering (the API returns ALL entries)
        var startDateStr = ExtractStringParameter(arguments, "startDate");
        var endDateStr = ExtractStringParameter(arguments, "endDate");
        var timeCodeFilter = ExtractStringParameter(arguments, "timeCodeId");

        if (DateTime.TryParse(startDateStr, out var startDate))
            entries = entries.Where(e => e.Date >= startDate).ToList();
        if (DateTime.TryParse(endDateStr, out var endDate))
            entries = entries.Where(e => e.Date <= endDate.Date.AddDays(1).AddTicks(-1)).ToList();
        if (!string.IsNullOrEmpty(timeCodeFilter))
            entries = entries.Where(e => string.Equals(e.TimeCodeId, timeCodeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        // Separate usage vs balance entries for summary
        var usageEntries = entries.Where(e => e.PTOHours.HasValue && e.PTOHours > 0).ToList();
        var balanceEntries = entries.Where(e => e.PTOAdded.HasValue).ToList();

        var totalPtoHours = usageEntries.Sum(e => e.PTOHours ?? 0);

        // Group usage by time code for summary, using TimeCode.Name
        var byTimeCode = usageEntries
            .GroupBy(e => e.TimeCodeId)
            .Select(g =>
            {
                var name = g.First().TimeCode?.Name ?? g.Key ?? "Unknown";
                return $"  {name}: {g.Sum(e => e.PTOHours ?? 0)}h ({g.Count()} entries)";
            })
            .ToList();

        var summaryLines = new List<string>
        {
            $"Found {entries.Count} PTO entries for user ({usageEntries.Count} usage, {balanceEntries.Count} balance)",
            $"Total PTO hours used: {totalPtoHours}h"
        };
        if (byTimeCode.Any())
        {
            summaryLines.Add("By category:");
            summaryLines.AddRange(byTimeCode);
        }
        if (availableTimeCodes.Any())
        {
            summaryLines.Add("Available PTO time codes (use these IDs for create_pto_entry):");
            summaryLines.AddRange(availableTimeCodes.Select(tc => $"  {tc.Name}: {tc.Id}"));
        }

        var ptoJson = JsonSerializer.Serialize(entries, JsonOptions);

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = string.Join("\n", summaryLines) },
                new() { Type = "text", Text = ptoJson }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Create a new PTO entry
    /// </summary>
    private async Task<McpToolResult> CreatePtoEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var userId = ExtractStringParameter(arguments, "userId");
        var dateStr = ExtractStringParameter(arguments, "date");
        var timeCodeId = ExtractStringParameter(arguments, "timeCodeId");
        var description = ExtractStringParameter(arguments, "description");

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(dateStr) || string.IsNullOrEmpty(timeCodeId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: userId, date, and timeCodeId are required" } },
                IsError = true
            };
        }

        if (!DateTime.TryParse(dateStr, out var date))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Error: Could not parse date '{dateStr}'. Use yyyy-MM-dd format." } },
                IsError = true
            };
        }

        decimal ptoHours = 0;
        if (arguments.TryGetValue("ptoHours", out var hoursObj))
        {
            ptoHours = hoursObj switch
            {
                JsonElement el when el.ValueKind == JsonValueKind.Number => el.GetDecimal(),
                double d => (decimal)d,
                int i => i,
                string s when decimal.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
        }

        if (ptoHours <= 0)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: ptoHours must be a positive number" } },
                IsError = true
            };
        }

        var ptoEntry = new UserPto
        {
            UserId = userId,
            Date = date,
            PTOHours = ptoHours,
            TimeCodeId = timeCodeId,
            Description = description
        };

        var response = await _projectInsightApi.SaveUserPtoAsync(ptoEntry, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Created PTO entry: {ptoHours}h on {date:yyyy-MM-dd} (ID: {response.Data?.SavedId}). A linked time entry was automatically created." },
                new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Update an existing PTO entry
    /// </summary>
    private async Task<McpToolResult> UpdatePtoEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var ptoEntryId = ExtractStringParameter(arguments, "ptoEntryId");
        if (string.IsNullOrEmpty(ptoEntryId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: ptoEntryId parameter is required" } },
                IsError = true
            };
        }

        // Fetch existing entry to merge updates
        var existing = await _projectInsightApi.GetUserPtoAsync(ptoEntryId, cancellationToken: cancellationToken);
        if (!existing.Success || existing.Data == null)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Error: Could not find PTO entry {ptoEntryId}" } },
                IsError = true
            };
        }

        var entry = existing.Data;
        entry.Id = ptoEntryId;

        // Apply updates
        var dateStr = ExtractStringParameter(arguments, "date");
        if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
            entry.Date = date;

        if (arguments.TryGetValue("ptoHours", out var hoursObj))
        {
            var hours = hoursObj switch
            {
                JsonElement el when el.ValueKind == JsonValueKind.Number => el.GetDecimal(),
                double d => (decimal)d,
                int i => i,
                string s when decimal.TryParse(s, out var parsed) => parsed,
                _ => entry.PTOHours ?? 0
            };
            if (hours <= 0)
            {
                return new McpToolResult
                {
                    Content = new List<McpContent> { new() { Type = "text", Text = "Error: ptoHours must be a positive number" } },
                    IsError = true
                };
            }
            entry.PTOHours = hours;
        }

        var newTimeCodeId = ExtractStringParameter(arguments, "timeCodeId");
        if (!string.IsNullOrEmpty(newTimeCodeId))
            entry.TimeCodeId = newTimeCodeId;

        if (arguments.ContainsKey("description"))
            entry.Description = ExtractStringParameter(arguments, "description");

        var response = await _projectInsightApi.SaveUserPtoAsync(entry, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Updated PTO entry {ptoEntryId}. The linked time entry was automatically updated." },
                new() { Type = "text", Text = JsonSerializer.Serialize(response.Data, JsonOptions) }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Delete a PTO entry (also auto-deletes linked time entry)
    /// </summary>
    private async Task<McpToolResult> DeletePtoEntryAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var ptoEntryId = ExtractStringParameter(arguments, "ptoEntryId");
        if (string.IsNullOrEmpty(ptoEntryId))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: ptoEntryId parameter is required" } },
                IsError = true
            };
        }

        var response = await _projectInsightApi.DeleteUserPtoAsync(ptoEntryId, cancellationToken);

        if (!response.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {response.Message}" },
                    new() { Type = "text", Text = string.Join(", ", response.Errors ?? new List<string>()) }
                },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Deleted PTO entry {ptoEntryId}. The linked time entry was also automatically removed." }
            },
            IsError = false
        };
    }

    #endregion

    #region Authentication Flow

    /// <summary>
    /// Trigger the device activation flow — opens browser and asks user for the code
    /// </summary>
    private async Task<McpToolResult> TriggerAuthFlowAsync(CancellationToken cancellationToken)
    {
        if (_deviceActivation == null)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: No API token configured. Set ProjectInsight__ApiToken in your MCP server environment variables." } },
                IsError = true
            };
        }

        // If we already have a pending activation, remind the user
        if (_deviceActivation.HasPendingActivation)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"I've already opened your browser to sign into Project Insight. Please complete the sign-in and tell me the activation code shown on the page. If you need a new link: {_deviceActivation.PendingActivationUrl}" }
                },
                IsError = true
            };
        }

        var result = await _deviceActivation.StartActivationAsync(cancellationToken);

        if (!result.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Failed to start authentication: {result.Error}" } },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"I've opened your browser to sign into Project Insight. Please complete the sign-in and tell me the activation code shown on the page.\n\nIf your browser didn't open, go to: {result.ActivationUrl}" }
            },
            IsError = true
        };
    }

    /// <summary>
    /// Complete authentication by exchanging the activation code for a token
    /// </summary>
    private async Task<McpToolResult> CompleteAuthenticationAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        if (_deviceActivation == null)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: Device activation is not available in this configuration." } },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("activationCode", out var codeObj) || string.IsNullOrEmpty(codeObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: activationCode parameter is required." } },
                IsError = true
            };
        }

        var code = codeObj.ToString()!.Trim();
        var result = await _deviceActivation.CompleteActivationAsync(code, cancellationToken);

        if (!result.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Authentication failed: {result.Error}" } },
                IsError = true
            };
        }

        // Update the token accessor so subsequent calls work immediately
        _apiTokenAccessor.ApiToken = result.ApiToken;

        var expiresStr = result.ExpiresUtc.HasValue ? result.ExpiresUtc.Value.ToString("yyyy-MM-dd") : "unknown";
        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Successfully authenticated with Project Insight! Your credentials have been saved and will expire on {expiresStr}. You can now use all Project Insight tools." }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Start PI account linking for a Copilot user (Entra-authenticated)
    /// </summary>
    private async Task<McpToolResult> LinkPiAccountAsync(CancellationToken cancellationToken)
    {
        if (_deviceActivation == null)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: Device activation is not available in this configuration." } },
                IsError = true
            };
        }

        if (_deviceActivation.HasPendingActivation)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"You've already started connecting Project Insight. Open this sign-in link and log in with your organization account:\n\n{_deviceActivation.PendingActivationUrl}\n\nAfter signing in, paste the activation code Project Insight shows you and I'll finish linking your account." }
                },
                IsError = false
            };
        }

        var result = await _deviceActivation.StartActivationAsync(cancellationToken);

        if (!result.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Failed to start account linking: {result.Error}" } },
                IsError = true
            };
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"To connect your Project Insight account, open this sign-in link and log in with your organization account:\n\n{result.ActivationUrl}\n\nAfter you sign in, Project Insight will show you an activation code. Paste that code back here and I'll finish linking your account." }
            },
            IsError = false
        };
    }

    /// <summary>
    /// Complete PI account linking — exchange activation code for token and store in Key Vault
    /// </summary>
    private async Task<McpToolResult> CompletePiLinkAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        // Local/stdio context (e.g. the .mcpb bundle in Claude Desktop/Cowork):
        // there is no Key Vault and no Entra identity. The model is told to call
        // complete_pi_link after link_pi_account, so honor that here by completing
        // against local credential storage — same as complete_authentication.
        // The Key Vault path below is reserved for the Copilot/Teams host.
        if (_keyVaultTokenService == null)
        {
            return await CompleteAuthenticationAsync(arguments, cancellationToken);
        }

        if (_deviceActivation == null)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: Account linking is not available in this configuration." } },
                IsError = true
            };
        }

        if (!_apiTokenAccessor.HasEntraIdentity)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: No Entra identity found. Account linking requires authentication through Microsoft 365." } },
                IsError = true
            };
        }

        if (!arguments.TryGetValue("activationCode", out var codeObj) || string.IsNullOrEmpty(codeObj?.ToString()))
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = "Error: activationCode parameter is required." } },
                IsError = true
            };
        }

        var code = codeObj.ToString()!.Trim();
        var result = await _deviceActivation.CompleteActivationAsync(code, cancellationToken);

        if (!result.Success)
        {
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Account linking failed: {result.Error}" } },
                IsError = true
            };
        }

        // Store in Key Vault keyed by Entra oid
        await _keyVaultTokenService.SaveTokenAsync(
            _apiTokenAccessor.EntraOid!,
            result.ApiToken!,
            piUserId: result.UserId,
            expiresUtc: result.ExpiresUtc,
            cancellationToken: cancellationToken);

        // Set token for immediate use
        _apiTokenAccessor.ApiToken = result.ApiToken;

        var expiresStr = result.ExpiresUtc.HasValue ? result.ExpiresUtc.Value.ToString("yyyy-MM-dd") : "unknown";
        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Your Project Insight account is now linked! Your connection will expire on {expiresStr}. You can now use all Project Insight tools." }
            },
            IsError = false
        };
    }

    #endregion

    /// <summary>
    /// Initialize available tools
    /// </summary>
    public static List<McpTool> InitializeTools()
    {
        return new List<McpTool>
        {
            // ============ AUTHENTICATION TOOLS ============
            new()
            {
                Name = "complete_authentication",
                Description = "Complete Project Insight authentication by providing the activation code shown after signing in via your browser. Use this after being prompted to authenticate.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        activationCode = new { type = "string", description = "The activation code displayed after signing into Project Insight in the browser" }
                    },
                    required = new[] { "activationCode" }
                }
            },
            new()
            {
                Name = "link_pi_account",
                Description = "Link your Project Insight account. Opens a sign-in page where you authenticate with your organization's SSO. After signing in, you'll receive an activation code to provide back.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            },
            new()
            {
                Name = "complete_pi_link",
                Description = "Complete Project Insight account linking by providing the activation code shown after signing in. Use this after link_pi_account.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        activationCode = new { type = "string", description = "The activation code displayed after signing into Project Insight" }
                    },
                    required = new[] { "activationCode" }
                }
            },
            // ============ PROJECT TOOLS ============
            new()
            {
                Name = "search_projects",
                Description = "Search for projects in Project Insight. IMPORTANT: Use 'fields' parameter to control response size - default is 'summary'. Use 'minimal' for lists, 'summary' for overview, 'standard'/'full' only when needed.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated fields. AVAILABLE PROJECT FIELDS: Id, Name, Description, ItemNumber, IsActive, IsOpen, IsBillable, WorkPercentComplete, StartDateTimeUTC, EndDateTimeUTC, ActualStartDateTimeUTC, ActualEndDateTimeUTC, BaselineStartDateTimeUTC, BaselineEndDateTimeUTC, PrimaryProjectManager_Id, ProjectStatus_Id, Priority_Id, Client_Id, WorkHours, ActualWorkHours, BudgetHours, RemainingWorkHours, UrlFull, CreatedByUser_Id, CreatedDateTimeUTC, ModifiedDateTimeUTC. Default: 'summary'" 
                        },
                        isActive = new { type = "boolean", description = "Filter by active status. true = only active, false = only inactive, omit = all" },
                        isOpen = new { type = "boolean", description = "Filter by open/closed status" },
                        projectManagerId = new { type = "string", description = "Filter by project manager's user ID (GUID)" },
                        projectStatusId = new { type = "string", description = "Filter by project status ID (GUID)" }
                    },
                    required = Array.Empty<string>()
                }
            },
            new()
            {
                Name = "get_project",
                Description = "Get project details by ID. Use 'fields' parameter to control response size - default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID)" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE: Id, Name, Description, ItemNumber, IsActive, IsOpen, WorkPercentComplete, StartDateTimeUTC, EndDateTimeUTC, PrimaryProjectManager_Id, ProjectStatus_Id, WorkHours, ActualWorkHours, UrlFull. Default: 'summary'" 
                        }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "get_project_tasks",
                Description = "Get tasks for a project. Use 'fields' parameter to control response size - default is 'summary'. For large projects, use 'minimal' first.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID)" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE TASK FIELDS: Id, Name, Description, ItemNumber, Project_Id, Parent_Id, IsActive, IsMilestone, IsSummary, WorkPercentComplete, StartDateTimeUTC, EndDateTimeUTC, ActualStartDateTimeUTC, ActualEndDateTimeUTC, WorkHours, ActualWorkHours, RemainingWorkHours, AssignedTo_Id, TaskStatus_Id, Priority_Id, UrlFull, IsTaskCaptureTimeBillableDefaultCheckedOn, IsTaskCaptureTimeBillableResourceAllowedToToggle, BillableRate, BillableHours, BillableTotal, BillableTimeTotal. Default: 'summary'" 
                        }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "get_project_resources",
                Description = "List the users assigned to a project (team members, project managers, schedulers) with their roles, allocation %, hours, and task counts. Use this to see who is staffed on a project, or to get a resource assignment id for update_project_resource / remove_project_resource.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID)" },
                        isActive = new { type = "boolean", description = "Filter active assignments only" },
                        fields = new { 
                            type = "string", 
                            description = "CUSTOM: comma-separated. AVAILABLE RESOURCE FIELDS: Id, ProjectId, UserId, User (nested: Id,Name,Email,FirstName,LastName), IsActive, IsProjectManager, IsPrimaryProjectManager, IsProjectScheduler, PercentAllocationDefault, ProjectResourceRoleDescription, ActualHours, TaskCount, OpenTaskCount, TaskBehindScheduleCount" 
                        }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "add_project_resource",
                Description = "Assign (staff / allocate) a user to a project - as a team member, project manager, or scheduler, with an optional default allocation %. This is THE tool for putting a person on a project. Idempotent and safe to re-run. Pass projectId + userId (GUIDs); if you only have names, resolve them with search_projects / search_users first.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID) to add the user to" },
                        userId = new { type = "string", description = "The user ID (GUID) to assign to the project" },
                        isProjectManager = new { type = "boolean", description = "Whether the user is a project manager on this project" },
                        isProjectScheduler = new { type = "boolean", description = "Whether the user is a project scheduler" },
                        percentAllocationDefault = new { type = "number", description = "Default allocation percentage (0-100)" },
                        resourceTypeDefaultId = new { type = "string", description = "Resource type classification ID (GUID). Use get_resource_types to resolve." }
                    },
                    required = new[] { "projectId", "userId" }
                }
            },
            new()
            {
                Name = "update_project_resource",
                Description = "Change an existing project assignment: promote to/from project manager or scheduler, adjust allocation %, activate/deactivate, or set resource type. Needs the resource assignment id from get_project_resources.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "The project resource assignment ID (GUID) - get this from get_project_resources" },
                        isProjectManager = new { type = "boolean", description = "Update PM status" },
                        isProjectScheduler = new { type = "boolean", description = "Update scheduler status" },
                        isActive = new { type = "boolean", description = "Activate or deactivate the assignment" },
                        percentAllocationDefault = new { type = "number", description = "Update allocation percentage (0-100)" },
                        resourceTypeDefaultId = new { type = "string", description = "Update resource type classification ID (GUID)" }
                    },
                    required = new[] { "id" }
                }
            },
            new()
            {
                Name = "remove_project_resource",
                Description = "Unassign a user from a project (delete the assignment). Needs the resource assignment id from get_project_resources.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "The project resource assignment ID (GUID) to remove - get this from get_project_resources" }
                    },
                    required = new[] { "id" }
                }
            },
            new()
            {
                Name = "add_task_resource",
                Description = "Assign (staff) a user to a specific task, with optional planned work hours and allocation %. If the user isn't on the project yet, they are added to the project automatically. Pass taskId + userId (GUIDs); use get_project_tasks / search_users to resolve them.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        taskId = new { type = "string", description = "The task ID (GUID) to assign the user to" },
                        userId = new { type = "string", description = "The user ID (GUID) to assign to the task" },
                        workHours = new { type = "number", description = "Planned work hours for this assignment" },
                        percentAllocationDefault = new { type = "number", description = "Allocation percentage (0-100)" }
                    },
                    required = new[] { "taskId", "userId" }
                }
            },
            new()
            {
                Name = "remove_task_resource",
                Description = "Unassign a user from a task. Preferred: pass taskId + userId and it resolves the assignment automatically. Alternatively pass the task resource assignment id directly (from get_task_resources). Use get_task_resources to see who is on a task.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        taskId = new { type = "string", description = "The task ID (GUID) the user is assigned to (use with userId; no need to know the assignment id)" },
                        userId = new { type = "string", description = "The user ID (GUID) to unassign from the task (use with taskId)" },
                        id = new { type = "string", description = "The task resource assignment ID (GUID) to remove directly. Optional if taskId + userId are given; get it from get_task_resources." }
                    }
                }
            },
            new()
            {
                Name = "get_task_resources",
                Description = "List the users assigned to a specific task (with planned hours, allocation %, and each assignment's id). Use this to see who is staffed on a task, or to get the assignment id needed by remove_task_resource.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        taskId = new { type = "string", description = "The task ID (GUID) to list assigned users for" },
                        isActive = new { type = "boolean", description = "Optional: only return active assignments" }
                    },
                    required = new[] { "taskId" }
                }
            },
            // ============ TASK TOOLS ============
            new()
            {
                Name = "get_task",
                Description = "Get task details by ID. Use 'fields' parameter to control response size - default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        taskId = new { type = "string", description = "The task ID (GUID)" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE TASK FIELDS: Id, Name, Description, ItemNumber, Project_Id, Parent_Id, IsActive, IsMilestone, IsSummary, WorkPercentComplete, StartDateTimeUTC, EndDateTimeUTC, ActualStartDateTimeUTC, ActualEndDateTimeUTC, WorkHours, ActualWorkHours, RemainingWorkHours, AssignedTo_Id, TaskStatus_Id, Priority_Id, UrlFull, IsTaskCaptureTimeBillableDefaultCheckedOn, IsTaskCaptureTimeBillableResourceAllowedToToggle, BillableRate, BillableHours, BillableTotal, BillableTimeTotal. Default: 'summary'" 
                        }
                    },
                    required = new[] { "taskId" }
                }
            },
            new()
            {
                Name = "create_project",
                Description = "Create a new project in Project Insight. Returns the created project with its assigned ID.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        project = new
                        {
                            type = "object",
                            description = "The project object to create",
                            properties = new
                            {
                                name = new { type = "string", description = "Project name (required)" },
                                description = new { type = "string", description = "Project description" },
                                isActive = new { type = "boolean", description = "Whether project is active (default: true)" }
                            },
                            required = new[] { "name" }
                        }
                    },
                    required = new[] { "project" }
                }
            },
            new()
            {
                Name = "update_project",
                Description = "Update an existing project's properties. Only the specified fields will be modified; other fields remain unchanged.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID) to update" },
                        project = new
                        {
                            type = "object",
                            description = "Project properties to update. Only include fields you want to change.",
                            properties = new
                            {
                                name = new { type = "string", description = "Project name" },
                                description = new { type = "string", description = "Project description" },
                                isActive = new { type = "boolean", description = "Whether project is active" },
                                isOpen = new { type = "boolean", description = "Whether project is open" }
                            }
                        }
                    },
                    required = new[] { "projectId", "project" }
                }
            },
            new()
            {
                Name = "create_task",
                Description = "Create a new task within a project. The task will be added to the specified project's task list.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        task = new
                        {
                            type = "object",
                            description = "The task object to create",
                            properties = new
                            {
                                name = new { type = "string", description = "Task name (required)" },
                                project_Id = new { type = "string", description = "Parent project ID (required, GUID)" },
                                description = new { type = "string", description = "Task description" },
                                startDateTimeUTC = new { type = "string", description = "Start date/time in ISO 8601 format (e.g., '2026-02-15T09:00:00Z')" },
                                endDateTimeUTC = new { type = "string", description = "End date/time in ISO 8601 format" },
                                workPercentComplete = new { type = "number", description = "Completion percentage (0-100)" },
                                isMilestone = new { type = "boolean", description = "Whether this is a milestone (zero-duration marker)" }
                            },
                            required = new[] { "name", "project_Id" }
                        }
                    },
                    required = new[] { "task" }
                }
            },
            new()
            {
                Name = "update_task",
                Description = "Update an existing task's properties. Only the specified fields will be modified.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        taskId = new { type = "string", description = "The task ID (GUID) to update" },
                        task = new
                        {
                            type = "object",
                            description = "Task properties to update. Only include fields you want to change.",
                            properties = new
                            {
                                name = new { type = "string", description = "Task name" },
                                description = new { type = "string", description = "Task description" },
                                workPercentComplete = new { type = "number", description = "Completion percentage (0-100)" },
                                startDateTimeUTC = new { type = "string", description = "Start date/time in ISO 8601 format" },
                                endDateTimeUTC = new { type = "string", description = "End date/time in ISO 8601 format" },
                                isActive = new { type = "boolean", description = "Whether task is active" },
                                isTaskCaptureTimeBillableDefaultCheckedOn = new { type = "boolean", description = "Whether time entries default to billable when logging time against this task" },
                                isTaskCaptureTimeBillableResourceAllowedToToggle = new { type = "boolean", description = "Whether resources can toggle the billable flag on time entries for this task" }
                            }
                        }
                    },
                    required = new[] { "taskId", "task" }
                }
            },
            // ============ TIMESHEET TOOLS ============
            new()
            {
                Name = "get_timesheet",
                Description = "Get timesheet by ID. Use 'fields' parameter to control response size - default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeSheetId = new { type = "string", description = "The timesheet ID (GUID)" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE TIMESHEET FIELDS: Id, Name, User_Id, StartDate, EndDate, TotalHours, TotalBillableHours, TotalNonBillableHours, IsSubmitted, IsApproved, IsRejected, SubmittedDateTimeUTC, ApprovedDateTimeUTC, RejectedDateTimeUTC, ApprovedBy_Id, TimeSheetEntries (nested). Default: 'summary'" 
                        }
                    },
                    required = new[] { "timeSheetId" }
                }
            },
            new()
            {
                Name = "search_timesheets",
                Description = "Search timesheets by user, approver, date range, or status. Use 'fields' to control response size. Default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE: Id, Name, User_Id, StartDate, EndDate, TotalHours, TotalBillableHours, IsSubmitted, IsApproved, IsRejected, SubmittedDateTimeUTC, ApprovedDateTimeUTC. Default: 'summary'" 
                        },
                        userId = new { type = "string", description = "Filter by timesheet owner's user ID (GUID)" },
                        approverUserId = new { type = "string", description = "Filter by approver's user ID (GUID)" },
                        startDate = new { type = "string", description = "Filter timesheets starting on/after this date (yyyy-MM-dd)" },
                        endDate = new { type = "string", description = "Filter timesheets ending on/before this date (yyyy-MM-dd)" },
                        excludeUnsubmitted = new { type = "boolean", description = "Exclude unsubmitted timesheets" },
                        excludeApproved = new { type = "boolean", description = "Exclude approved timesheets" },
                        excludeRejected = new { type = "boolean", description = "Exclude rejected timesheets" },
                        excludePendingApproval = new { type = "boolean", description = "Exclude pending-approval timesheets" }
                    },
                    required = Array.Empty<string>()
                }
            },
            new()
            {
                Name = "get_my_time_status",
                Description = "Get the current authenticated user's time tracking status. Returns current timesheet info, hours worked today, and hours worked this period. Useful for quick status checks.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                }
            },
            new()
            {
                Name = "update_timesheet",
                Description = "Update an existing timesheet's properties. Only the specified fields will be modified. Cannot update submitted/approved timesheets.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeSheetId = new { type = "string", description = "The timesheet ID (GUID) to update" },
                        timeSheet = new
                        {
                            type = "object",
                            description = "Timesheet properties to update. Only include fields you want to change.",
                            properties = new
                            {
                                name = new { type = "string", description = "Timesheet name" },
                                description = new { type = "string", description = "Timesheet description or notes" }
                            }
                        }
                    },
                    required = new[] { "timeSheetId", "timeSheet" }
                }
            },
            new()
            {
                Name = "submit_timesheet",
                Description = "Submit a timesheet for manager approval. Once submitted, the timesheet enters the approval workflow and cannot be edited until approved or rejected.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeSheetId = new { type = "string", description = "The timesheet ID (GUID) to submit" },
                        approverUserId = new { type = "string", description = "Optional: Override the default approver with a specific user ID (GUID)" }
                    },
                    required = new[] { "timeSheetId" }
                }
            },
            new()
            {
                Name = "create_and_submit_timesheet",
                Description = "Create a new timesheet for a date range and immediately submit it for approval in one step. This is the standard workflow for submitting a weekly or bi-weekly timesheet. If no userId is provided, defaults to the current authenticated user.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        startDate = new { type = "string", description = "Period start date (format: yyyy-MM-dd)" },
                        endDate = new { type = "string", description = "Period end date (format: yyyy-MM-dd)" },
                        userId = new { type = "string", description = "Optional: User ID (GUID) to create the timesheet for. Defaults to the current authenticated user." }
                    },
                    required = new[] { "startDate", "endDate" }
                }
            },
            new()
            {
                Name = "approve_timesheet",
                Description = "Approve a submitted timesheet. Requires manager/approver permissions. The timesheet will be marked as approved and locked.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeSheetId = new { type = "string", description = "The timesheet ID (GUID) to approve" },
                        comments = new { type = "string", description = "Optional comments to include with the approval" }
                    },
                    required = new[] { "timeSheetId" }
                }
            },
            new()
            {
                Name = "reject_timesheet",
                Description = "Reject a submitted timesheet and return it to the user for corrections. Requires manager/approver permissions. Include comments explaining what needs to be fixed.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeSheetId = new { type = "string", description = "The timesheet ID (GUID) to reject" },
                        comments = new { type = "string", description = "Reason for rejection - explain what needs to be corrected before resubmission" }
                    },
                    required = new[] { "timeSheetId" }
                }
            },
            new()
            {
                Name = "unsubmit_timesheet",
                Description = "Reset a timesheet back to Unsubmitted (draft) state. Use this to unblock time entry creation when a Rejected or Submitted timesheet is preventing new entries for its date range. The PI API blocks new time entries for dates covered by a submitted/rejected timesheet — this tool resolves that by reverting the timesheet to draft. After unsubmitting, time entries can be freely created for those dates again.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeSheetId = new { type = "string", description = "The timesheet ID (GUID) to unsubmit" }
                    },
                    required = new[] { "timeSheetId" }
                }
            },
            // ============ TIME ENTRY TOOLS ============
            new()
            {
                Name = "get_project_time_entries",
                Description = "Get detailed time entries logged against a specific project. Returns individual time entries with date, description, hours, billable status, billable amount, user ID, and task ID. Use this to analyze how time was spent on a project, audit billing, or see who worked on what. IMPORTANT: Always specify a date range (startDate/endDate) to limit results - without dates, returns ALL project time entries which can be very large. Supports filtering by user and task. Output includes summary stats: total entries, total hours, billable hours, and billable amount.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID) to get time entries for - REQUIRED" },
                        startDate = new { type = "string", description = "Start date filter (format: yyyy-MM-dd). STRONGLY RECOMMENDED to limit results. Entries on or after this date are included." },
                        endDate = new { type = "string", description = "End date filter (format: yyyy-MM-dd). STRONGLY RECOMMENDED to limit results. Entries on or before this date are included." },
                        userId = new { type = "string", description = "Filter to a specific user's entries only (GUID). Useful for seeing one person's time on a project." },
                        taskId = new { type = "string", description = "Filter to a specific task's entries only (GUID). Useful for seeing time logged against a particular task." }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "search_time_entries",
                Description = "Search time entries across all projects with flexible filters. Returns entries grouped by project (default) with per-project summary stats, or raw entries if groupByProject=false. Use this to find all time a specific user has logged, analyze billable vs non-billable time, or audit time entries across the organization. Much more efficient than querying individual projects when you need cross-project data.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        userId = new { type = "string", description = "Filter by user ID (GUID). Returns only this user's time entries across all projects." },
                        projectId = new { type = "string", description = "Filter by project ID (GUID). Can be combined with userId to see a user's time on a specific project." },
                        startDate = new { type = "string", description = "Start date filter (format: yyyy-MM-dd). STRONGLY RECOMMENDED to limit results." },
                        endDate = new { type = "string", description = "End date filter (format: yyyy-MM-dd). STRONGLY RECOMMENDED to limit results." },
                        isBillable = new { type = "string", description = "Filter by billable status: 'true' for billable only, 'false' for non-billable only. Omit for all entries." },
                        groupByProject = new { type = "string", description = "Set to 'false' to return individual entries instead of per-project summaries. Default is 'true' which groups entries by project and returns summary stats (hours, billable hours, billable amount) per project. Use 'true' for overview reports, 'false' for detailed entry-level data." }
                    },
                    required = Array.Empty<string>()
                }
            },
            new()
            {
                Name = "create_time_entry",
                Annotations = McpToolAnnotations.Destructive,
                Description = "Create a new time entry against a project task. Requires a task ID, date, hours, and optionally a description and billable flag. The time entry will be created for the authenticated user. NOTE: If you get an error about a submitted timesheet blocking the entry, use search_timesheets to find the blocking timesheet for that date range, then use unsubmit_timesheet to reset it to draft before retrying.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeEntry = new
                        {
                            type = "object",
                            description = "The time entry object to create",
                            properties = new
                            {
                                task_Id = new { type = "string", description = "The task ID (GUID) to log time against (required)" },
                                date = new { type = "string", description = "Date of the time entry (format: yyyy-MM-dd) (required)" },
                                actualHours = new { type = "number", description = "Number of hours worked (required, in 0.25 increments)" },
                                description = new { type = "string", description = "Description of work performed" },
                                isBillable = new { type = "boolean", description = "Whether this time is billable (default: false)" }
                            },
                            required = new[] { "task_Id", "date", "actualHours" }
                        }
                    },
                    required = new[] { "timeEntry" }
                }
            },
            new()
            {
                Name = "delete_time_entry",
                Annotations = McpToolAnnotations.Destructive,
                Description = "Delete a time entry by ID. This permanently removes the time entry. Use search_time_entries or get_project_time_entries to find entry IDs.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeEntryId = new { type = "string", description = "The time entry ID (GUID) to delete" }
                    },
                    required = new[] { "timeEntryId" }
                }
            },
            new()
            {
                Name = "update_time_entry",
                Annotations = McpToolAnnotations.Destructive,
                Description = "Update an existing time entry. Only the specified fields will be modified. Use search_time_entries or get_project_time_entries to find entry IDs.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        timeEntryId = new { type = "string", description = "The time entry ID (GUID) to update" },
                        timeEntry = new
                        {
                            type = "object",
                            description = "Time entry properties to update. Only include fields you want to change.",
                            properties = new
                            {
                                task_Id = new { type = "string", description = "The task ID (GUID) to log time against" },
                                date = new { type = "string", description = "Date of the time entry (format: yyyy-MM-dd)" },
                                actualHours = new { type = "number", description = "Number of hours worked (in 0.25 increments)" },
                                description = new { type = "string", description = "Description of work performed" },
                                isBillable = new { type = "boolean", description = "Whether this time is billable" }
                            }
                        }
                    },
                    required = new[] { "timeEntryId", "timeEntry" }
                }
            },
            // ============ USER TOOLS ============
            new()
            {
                Name = "get_current_user",
                Description = "Get authenticated user's profile. Use 'fields' to control response size - default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        fields = new { 
                            type = "string", 
                            description = "PRESETS: 'minimal' (Id,Name), 'summary' (default - contact info), 'standard', 'full'." 
                        }
                    },
                    required = Array.Empty<string>()
                }
            },
            new()
            {
                Name = "get_user",
                Description = "Get user profile by ID. Use 'fields' to control response size - default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        userId = new { type = "string", description = "The user ID (GUID)" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE USER FIELDS: Id, Name, FirstName, LastName, Email, IsActive, Title, Department, PhoneNumber, MobileNumber, EmployeeNumber, HireDate, Address, City, State, PostalCode, Country, TimeZone_Id, Manager_Id, UrlFull. Default: 'summary'" 
                        }
                    },
                    required = new[] { "userId" }
                }
            },
            new()
            {
                Name = "get_users",
                Description = "Get multiple users by IDs in one request. More efficient than multiple get_user calls. Use 'fields' to control response size.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "string", description = "Comma-separated user IDs (GUIDs)" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE: Id, Name, FirstName, LastName, Email, IsActive, Title, Department, Manager_Id. Default: 'summary'" 
                        }
                    },
                    required = new[] { "ids" }
                }
            },
            new()
            {
                Name = "search_users",
                Description = "Search users by name or list all. Use 'fields' to control response size - default is 'summary'.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Filter by name (partial match in first/last/full name/email)" },
                        isActive = new { type = "boolean", description = "Filter by active status" },
                        fields = new { 
                            type = "string", 
                            description = "Controls response fields. PRESETS: 'minimal'|'summary'|'standard'|'full'. CUSTOM: comma-separated. AVAILABLE USER FIELDS: Id, Name, FirstName, LastName, Email, IsActive, Title, Department, PhoneNumber, Manager_Id, HireDate. Default: 'summary'" 
                        }
                    },
                    required = Array.Empty<string>()
                }
            },
            // ============ RESOURCE TYPE TOOLS ============
            new()
            {
                Name = "get_resource_types",
                Description = "Resolve resource type IDs to names (e.g., 'Senior Consultant', 'Project Manager'). Use this to look up the objectId values from rate card rates or resourceTypeDefault_Id from project resources. Accepts multiple IDs at once.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "string", description = "Comma-separated resource type IDs (GUIDs) to resolve" }
                    },
                    required = new[] { "ids" }
                }
            },
            // ============ RATE CARD TOOLS ============
            new()
            {
                Name = "get_project_rate_cards",
                Description = "Get rate cards (company contracts) for a project. Returns billing rate cards with their nested rate tiers (user rates, resource type rates, task type rates, time code rates). Use this to see what billing rates apply to a project.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectId = new { type = "string", description = "The project ID (GUID)" }
                    },
                    required = new[] { "projectId" }
                }
            },
            new()
            {
                Name = "get_rate_card",
                Description = "Get a single rate card (company contract) by ID with full detail including all rate tiers and amounts.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        rateCardId = new { type = "string", description = "The rate card / company contract ID (GUID)" }
                    },
                    required = new[] { "rateCardId" }
                }
            },
            new()
            {
                Name = "save_rate_card_rate",
                Description = "Create or update a rate on a rate card. Specify the rate type tier and the rate details. To update an existing rate, include its 'id' in the rate object.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        rateCardId = new { type = "string", description = "The rate card / company contract ID (GUID)" },
                        rateType = new { type = "string", description = "Rate tier type: 'default', 'user', 'resource-type', 'task-type', or 'time-code'" },
                        rate = new
                        {
                            type = "object",
                            description = "The rate data to save",
                            properties = new
                            {
                                id = new { type = "string", description = "Rate ID (GUID) - include for updates, omit for new rates" },
                                objectId = new { type = "string", description = "ID of the associated object (user ID, resource type ID, task type ID, or time code ID depending on rateType)" },
                                rateBillableDefault = new { type = "number", description = "Default billable rate amount" },
                                rateDefault = new { type = "number", description = "Default rate amount" },
                                rateInvoiceDefault = new { type = "number", description = "Default invoice rate amount" },
                                effectiveDate = new { type = "string", description = "Rate effective date (yyyy-MM-dd)" },
                                expirationDate = new { type = "string", description = "Rate expiration date (yyyy-MM-dd)" }
                            }
                        }
                    },
                    required = new[] { "rateCardId", "rateType", "rate" }
                }
            },
            new()
            {
                Name = "delete_rate_card_rate",
                Description = "Delete a rate from a rate card. Permanently removes the specified rate entry from the rate card tier.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        rateCardId = new { type = "string", description = "The rate card / company contract ID (GUID)" },
                        rateType = new { type = "string", description = "Rate tier type: 'default', 'user', 'resource-type', 'task-type', or 'time-code'" },
                        rateId = new { type = "string", description = "The ID of the rate entry to delete (GUID)" }
                    },
                    required = new[] { "rateCardId", "rateType", "rateId" }
                }
            },
            // ============ PTO (PAID TIME OFF) TOOLS ============
            // HOW PTO AND TIME ENTRIES WORK TOGETHER:
            // PTO entries are managed through a separate API (/api/user-pto) but are tightly coupled
            // with regular time entries. When you create a PTO entry, the system automatically generates
            // a corresponding non-billable time entry with no project or task. The PTO entry's TimeEntry_Id
            // field links to this auto-generated time entry. Deleting a PTO entry also removes its linked
            // time entry. PTO time entries appear in search_time_entries results as entries with no
            // projectId/taskId — use list_user_pto for the authoritative PTO view.
            //
            // There are two types of PTO entries:
            //   - Usage entries (PTOHours set): actual time off taken
            //   - Balance entries (PTOAdded set): accruals or initial balance adjustments
            //
            // Time codes categorize PTO (e.g. Vacation, Personal Day, Floating Holiday).
            // They are instance-specific — use list_user_pto to discover available codes and their IDs.
            new()
            {
                Name = "get_pto_entry",
                Description = "Get a single PTO (Paid Time Off) entry by its ID. Returns the full PTO record including hours, date, time code category (with name), description, and the linked time entry ID. NOTE: PTO entries are separate from regular time entries — each PTO entry auto-generates a linked non-billable time entry (visible in search_time_entries with no projectId/taskId). Use this tool to inspect a specific PTO record; use list_user_pto to see all PTO for a user.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ptoEntryId = new { type = "string", description = "The PTO entry ID (GUID). This is NOT a time entry ID — it's the PTO-specific record ID." }
                    },
                    required = new[] { "ptoEntryId" }
                }
            },
            new()
            {
                Name = "list_user_pto",
                Description = "List all PTO (Paid Time Off) entries for a user. Returns the complete PTO history including both usage entries (time off taken, PTOHours set) and balance entries (accruals, PTOAdded set). Also returns the available PTO time codes (categories like Vacation, Personal Day, etc.) with their IDs and names — use these IDs when creating new PTO entries. NOTE: The API returns ALL entries with no server-side date filtering — use startDate/endDate to filter client-side. Each usage entry has a TimeEntry_Id linking to its auto-generated time entry.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        userId = new { type = "string", description = "The user ID (GUID) to list PTO entries for" },
                        startDate = new { type = "string", description = "Optional: client-side filter — only return entries on or after this date (yyyy-MM-dd)" },
                        endDate = new { type = "string", description = "Optional: client-side filter — only return entries on or before this date (yyyy-MM-dd)" },
                        timeCodeId = new { type = "string", description = "Optional: filter by time code ID (GUID) to show only one PTO category" }
                    },
                    required = new[] { "userId" }
                }
            },
            new()
            {
                Name = "create_pto_entry",
                Description = "Create a new PTO (Paid Time Off) entry. This records time off for a user on a specific date. IMPORTANT: Creating a PTO entry automatically generates a linked non-billable time entry (no project/task) — do NOT also create a time entry manually. Required fields: userId, date, ptoHours (must be > 0), and timeCodeId. To discover available timeCodeId values, call list_user_pto first — the response includes the available time codes with their names and IDs.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        userId = new { type = "string", description = "The user ID (GUID) to record PTO for" },
                        date = new { type = "string", description = "The date of the PTO (format: yyyy-MM-dd)" },
                        ptoHours = new { type = "number", description = "Hours of PTO (must be > 0, supports decimals e.g. 3.25)" },
                        timeCodeId = new { type = "string", description = "Time code category ID (GUID). Call list_user_pto to discover available time codes and their IDs for this tenant." },
                        description = new { type = "string", description = "Optional description (e.g. 'Vacation day', 'Doctor appointment')" }
                    },
                    required = new[] { "userId", "date", "ptoHours", "timeCodeId" }
                }
            },
            new()
            {
                Name = "update_pto_entry",
                Description = "Update an existing PTO entry. Provide the ptoEntryId of the entry to update along with any fields to change. The linked time entry is automatically updated to match. Only include fields you want to change.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ptoEntryId = new { type = "string", description = "The PTO entry ID (GUID) to update" },
                        date = new { type = "string", description = "New date (format: yyyy-MM-dd)" },
                        ptoHours = new { type = "number", description = "New PTO hours (must be > 0)" },
                        timeCodeId = new { type = "string", description = "New time code category ID (GUID). Call list_user_pto to see available codes." },
                        description = new { type = "string", description = "New description" }
                    },
                    required = new[] { "ptoEntryId" }
                }
            },
            new()
            {
                Name = "delete_pto_entry",
                Description = "Delete a PTO entry. IMPORTANT: This also automatically deletes the linked time entry — do NOT separately delete the time entry. Use get_pto_entry or list_user_pto to find the PTO entry ID.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ptoEntryId = new { type = "string", description = "The PTO entry ID (GUID) to delete. This is NOT a time entry ID." }
                    },
                    required = new[] { "ptoEntryId" }
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

    /// <summary>
    /// Extract boolean parameter from arguments, handling JsonElement conversion
    /// </summary>
    private static bool? ExtractBoolParameter(object? paramValue)
    {
        if (paramValue == null)
            return null;

        return paramValue switch
        {
            bool b => b,
            JsonElement element when element.ValueKind == JsonValueKind.True => true,
            JsonElement element when element.ValueKind == JsonValueKind.False => false,
            string str when bool.TryParse(str, out var result) => result,
            _ => null
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}