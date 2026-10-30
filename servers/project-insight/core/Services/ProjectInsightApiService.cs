using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.Models;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Service for interacting with Project Insight API
/// Per-request authentication is provided via the X-PI-Token header
/// </summary>
public class ProjectInsightApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectInsightApiService> _logger;
    private readonly ApiTokenAccessor _tokenAccessor;
    private readonly string _baseUrl;

    public ProjectInsightApiService(HttpClient httpClient, ILogger<ProjectInsightApiService> logger, 
        IConfiguration configuration, ApiTokenAccessor tokenAccessor)
    {
        _httpClient = httpClient;
        _logger = logger;
        _tokenAccessor = tokenAccessor;
        _baseUrl = configuration["ProjectInsight:BaseUrl"] ?? "";

        // Configure base settings if base URL is provided
        if (!string.IsNullOrEmpty(_baseUrl) && !_baseUrl.Contains("yourdomain"))
        {
            _httpClient.BaseAddress = new Uri(_baseUrl);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ProjectInsight-MCP-Server/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            _logger.LogInformation("ProjectInsightApiService initialized with base URL: {BaseUrl}", _baseUrl);
        }
        else
        {
            _logger.LogWarning("ProjectInsightApiService created without valid base URL configuration");
        }
    }

    /// <summary>
    /// Creates an HttpRequestMessage with the per-request API token
    /// </summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        
        if (_tokenAccessor.HasToken)
        {
            request.Headers.Add("Authorization", $"Bearer {_tokenAccessor.ApiToken}");
        }
        
        return request;
    }

    /// <summary>
    /// Creates an HttpRequestMessage with JSON body and the per-request API token
    /// </summary>
    private HttpRequestMessage CreateRequest<T>(HttpMethod method, string requestUri, T body)
    {
        var request = CreateRequest(method, requestUri);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrEmpty(_baseUrl) || _baseUrl.Contains("yourdomain"))
        {
            throw new InvalidOperationException("ProjectInsightApiService is not properly configured. Ensure ProjectInsight:BaseUrl is set.");
        }
        
        if (!_tokenAccessor.HasToken)
        {
            throw new InvalidOperationException("No API token provided. Include your Project Insight API token in the X-PI-Token header.");
        }
    }

    /// <summary>
    /// Send a GET request with the per-request API token
    /// </summary>
    private async Task<HttpResponseMessage> GetWithTokenAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, requestUri);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Send a POST request with the per-request API token
    /// </summary>
    private async Task<HttpResponseMessage> PostWithTokenAsync(string requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, requestUri);
        if (content != null)
        {
            request.Content = content;
        }
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Send a PUT request with the per-request API token
    /// </summary>
    private async Task<HttpResponseMessage> PutWithTokenAsync(string requestUri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Put, requestUri);
        if (content != null)
        {
            request.Content = content;
        }
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Send a DELETE request with the per-request API token
    /// </summary>
    private async Task<HttpResponseMessage> DeleteWithTokenAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, requestUri);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Search for projects with optional filters
    /// </summary>
    public async Task<ApiListResponse<Project>> SearchProjectsAsync(ProjectSearchRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            // Use POST for project search with filters in body, modelProperties as query param
            var requestBody = new Dictionary<string, object>();

            // Add filtering parameters if specified
            if (request.IsActive.HasValue)
                requestBody["isActive"] = request.IsActive.Value;

            if (request.IsOpen.HasValue)
                requestBody["isOpen"] = request.IsOpen.Value;

            if (!string.IsNullOrEmpty(request.ProjectManagerId))
                requestBody["primaryProjectManager_Id"] = request.ProjectManagerId;

            if (!string.IsNullOrEmpty(request.ProjectStatusId))
                requestBody["projectStatus_Id"] = request.ProjectStatusId;

            if (!string.IsNullOrEmpty(request.ProjectTypeId))
                requestBody["projectType_Id"] = request.ProjectTypeId;

            if (!string.IsNullOrEmpty(request.DepartmentId))
                requestBody["department_Id"] = request.DepartmentId;

            if (!string.IsNullOrEmpty(request.CompanyId))
                requestBody["companyDefault_Id"] = request.CompanyId;

            if (request.StartDateFrom.HasValue)
                requestBody["startDateFrom"] = request.StartDateFrom.Value.ToString("yyyy-MM-dd");

            if (request.StartDateTo.HasValue)
                requestBody["startDateTo"] = request.StartDateTo.Value.ToString("yyyy-MM-dd");

            if (request.EndDateFrom.HasValue)
                requestBody["endDateFrom"] = request.EndDateFrom.Value.ToString("yyyy-MM-dd");

            if (request.EndDateTo.HasValue)
                requestBody["endDateTo"] = request.EndDateTo.Value.ToString("yyyy-MM-dd");

            var json = JsonSerializer.Serialize(requestBody, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            // modelProperties must be passed as query string parameter (not in body)
            var modelProps = request.ModelProperties ?? GetDefaultModelProperties();
            var url = $"/api/project/search?modelProperties={Uri.EscapeDataString(modelProps)}";

            _logger.LogInformation("Searching projects with POST to {Url}", url);

            var response = await PostWithTokenAsync(url, httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Project search failed with status {StatusCode}: {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);

                return new ApiListResponse<Project>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { response.ReasonPhrase ?? "Unknown error" }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            // Project Insight API typically returns arrays directly or wrapped objects
            // We'll handle both cases
            if (responseContent.TrimStart().StartsWith("["))
            {
                // Direct array response
                var projects = JsonSerializer.Deserialize<List<Project>>(responseContent, JsonOptions);
                return new ApiListResponse<Project>
                {
                    Success = true,
                    Data = projects ?? new List<Project>(),
                    TotalCount = projects?.Count ?? 0
                };
            }
            else
            {
                // Try to deserialize as wrapped response
                var wrappedResponse = JsonSerializer.Deserialize<ApiListResponse<Project>>(responseContent, JsonOptions);
                if (wrappedResponse != null)
                {
                    wrappedResponse.Success = true;
                    return wrappedResponse;
                }

                // Fallback: try to extract data property
                using var doc = JsonDocument.Parse(responseContent);
                if (doc.RootElement.TryGetProperty("data", out var dataElement) ||
                    doc.RootElement.TryGetProperty("Data", out dataElement))
                {
                    var projects = JsonSerializer.Deserialize<List<Project>>(dataElement.GetRawText(), JsonOptions);
                    return new ApiListResponse<Project>
                    {
                        Success = true,
                        Data = projects ?? new List<Project>(),
                        TotalCount = projects?.Count ?? 0
                    };
                }

                throw new JsonException("Unable to parse response format");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching projects");
            return new ApiListResponse<Project>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get project by ID
    /// </summary>
    public async Task<ApiResponse<Project>> GetProjectAsync(string projectId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/project/{projectId}";
            if (!string.IsNullOrEmpty(modelProperties))
            {
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";
            }

            _logger.LogInformation("Getting project {ProjectId} with URL: {Url}", projectId, url);

            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Get project failed with status {StatusCode}: {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);

                return new ApiResponse<Project>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { response.ReasonPhrase ?? "Unknown error" }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var project = JsonSerializer.Deserialize<Project>(content, JsonOptions);

            return new ApiResponse<Project>
            {
                Success = true,
                Data = project
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting project {ProjectId}", projectId);
            return new ApiResponse<Project>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get tasks for a project
    /// </summary>
    public async Task<ApiListResponse<ProjectTask>> GetProjectTasksAsync(string projectId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Build query string with modelProperties if provided
            var queryString = string.IsNullOrEmpty(modelProperties) 
                ? "" 
                : $"?modelProperties={Uri.EscapeDataString(modelProperties)}";
            
            _logger.LogInformation("Getting tasks for project {ProjectId} with GET to /api/task/project/{ProjectId}{QueryString}", 
                projectId, projectId, queryString);

            var response = await GetWithTokenAsync($"/api/task/project/{projectId}{queryString}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Task search failed with status {StatusCode}: {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);

                return new ApiListResponse<ProjectTask>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { response.ReasonPhrase ?? "Unknown error" }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Task API response content: {Content}", content);

            // Handle different response formats like in SearchProjectsAsync
            if (content.TrimStart().StartsWith("["))
            {
                // Direct array response
                var tasks = JsonSerializer.Deserialize<List<ProjectTask>>(content, JsonOptions);
                return new ApiListResponse<ProjectTask>
                {
                    Success = true,
                    Data = tasks ?? new List<ProjectTask>(),
                    TotalCount = tasks?.Count ?? 0
                };
            }
            else
            {
                // Try to deserialize as wrapped response
                try
                {
                    var wrappedResponse = JsonSerializer.Deserialize<ApiListResponse<ProjectTask>>(content, JsonOptions);
                    if (wrappedResponse != null)
                    {
                        wrappedResponse.Success = true;
                        return wrappedResponse;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Failed to deserialize as wrapped response: {Error}", ex.Message);
                }

                // Fallback: try to extract data property
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("data", out var dataElement) ||
                    doc.RootElement.TryGetProperty("Data", out dataElement))
                {
                    var tasks = JsonSerializer.Deserialize<List<ProjectTask>>(dataElement.GetRawText(), JsonOptions);
                    return new ApiListResponse<ProjectTask>
                    {
                        Success = true,
                        Data = tasks ?? new List<ProjectTask>(),
                        TotalCount = tasks?.Count ?? 0
                    };
                }

                // If all else fails, return empty result
                _logger.LogWarning("Unable to parse task response format. Content: {Content}", content);
                return new ApiListResponse<ProjectTask>
                {
                    Success = true,
                    Data = new List<ProjectTask>(),
                    TotalCount = 0
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tasks for project {ProjectId}", projectId);
            return new ApiListResponse<ProjectTask>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Create a new project
    /// </summary>
    public async Task<ApiResponse<Project>> CreateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(project, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Creating new project: {ProjectName}", project.Name);

            var response = await PostWithTokenAsync("/api/project", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Create project failed with status {StatusCode}: {Content}",
                    response.StatusCode, errorContent);

                return new ApiResponse<Project>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var createdProject = JsonSerializer.Deserialize<Project>(responseContent, JsonOptions);

            return new ApiResponse<Project>
            {
                Success = true,
                Data = createdProject
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating project");
            return new ApiResponse<Project>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Update an existing project
    /// </summary>
    public async Task<ApiResponse<Project>> UpdateProjectAsync(string projectId, Project project, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(project, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Updating project {ProjectId}", projectId);

            var response = await PutWithTokenAsync($"/api/project/{projectId}", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Update project failed with status {StatusCode}: {Content}",
                    response.StatusCode, errorContent);

                return new ApiResponse<Project>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var updatedProject = JsonSerializer.Deserialize<Project>(responseContent, JsonOptions);

            return new ApiResponse<Project>
            {
                Success = true,
                Data = updatedProject
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating project {ProjectId}", projectId);
            return new ApiResponse<Project>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get a single task by ID
    /// </summary>
    public async Task<ApiResponse<ProjectTask>> GetTaskAsync(string taskId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var queryString = string.IsNullOrEmpty(modelProperties) 
                ? "" 
                : $"?modelProperties={Uri.EscapeDataString(modelProperties)}";
            
            _logger.LogInformation("Getting task {TaskId} with GET to /api/task/{TaskId}{QueryString}", 
                taskId, taskId, queryString);

            var response = await GetWithTokenAsync($"/api/task/{taskId}{queryString}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Get task failed with status {StatusCode}: {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);

                return new ApiResponse<ProjectTask>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { response.ReasonPhrase ?? "Unknown error" }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var task = JsonSerializer.Deserialize<ProjectTask>(content, JsonOptions);

            return new ApiResponse<ProjectTask>
            {
                Success = true,
                Data = task
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting task {TaskId}", taskId);
            return new ApiResponse<ProjectTask>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Create a new task (POST to /api/task without Id)
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> CreateTaskAsync(ProjectTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure no Id is set for create
            task.Id = null;
            
            var json = JsonSerializer.Serialize(task, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Creating new task: {TaskName} for project {ProjectId}", task.Name, task.ProjectId);

            var response = await PostWithTokenAsync("/api/task", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Create task failed with status {StatusCode}: {Content}",
                    response.StatusCode, errorContent);

                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(responseContent, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? false,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating task");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Update an existing task (POST to /api/task with Id in body)
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> UpdateTaskAsync(ProjectTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(task.Id))
            {
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = "Task Id is required for update",
                    Errors = new List<string> { "Task Id is required" }
                };
            }
            
            var json = JsonSerializer.Serialize(task, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Updating task {TaskId}: {TaskName}", task.Id, task.Name);

            var response = await PostWithTokenAsync("/api/task", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Update task failed with status {StatusCode}: {Content}",
                    response.StatusCode, errorContent);

                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API call failed: {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(responseContent, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? false,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating task {TaskId}", task.Id);
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    private static string BuildQueryString(ProjectSearchRequest request)
    {
        var parameters = new List<string>();

        if (request.IsActive.HasValue)
            parameters.Add($"isActive={request.IsActive.Value.ToString().ToLower()}");

        if (request.IsOpen.HasValue)
            parameters.Add($"isOpen={request.IsOpen.Value.ToString().ToLower()}");

        if (!string.IsNullOrEmpty(request.ProjectManagerId))
            parameters.Add($"primaryProjectManager_Id={Uri.EscapeDataString(request.ProjectManagerId)}");

        if (!string.IsNullOrEmpty(request.ProjectStatusId))
            parameters.Add($"projectStatus_Id={Uri.EscapeDataString(request.ProjectStatusId)}");

        if (!string.IsNullOrEmpty(request.ProjectTypeId))
            parameters.Add($"projectType_Id={Uri.EscapeDataString(request.ProjectTypeId)}");

        if (!string.IsNullOrEmpty(request.DepartmentId))
            parameters.Add($"department_Id={Uri.EscapeDataString(request.DepartmentId)}");

        if (!string.IsNullOrEmpty(request.CompanyId))
            parameters.Add($"companyDefault_Id={Uri.EscapeDataString(request.CompanyId)}");

        if (request.StartDateFrom.HasValue)
            parameters.Add($"startDateFrom={request.StartDateFrom.Value:yyyy-MM-dd}");

        if (request.StartDateTo.HasValue)
            parameters.Add($"startDateTo={request.StartDateTo.Value:yyyy-MM-dd}");

        if (request.EndDateFrom.HasValue)
            parameters.Add($"endDateFrom={request.EndDateFrom.Value:yyyy-MM-dd}");

        if (request.EndDateTo.HasValue)
            parameters.Add($"endDateTo={request.EndDateTo.Value:yyyy-MM-dd}");

        if (!string.IsNullOrEmpty(request.ModelProperties))
            parameters.Add($"modelProperties={Uri.EscapeDataString(request.ModelProperties)}");

        if (request.PageIndex.HasValue && request.PageIndex.Value > 0)
            parameters.Add($"pageIndex={request.PageIndex.Value}");

        if (request.PageSize.HasValue && request.PageSize.Value != 50)
            parameters.Add($"pageSize={request.PageSize.Value}");

        return parameters.Any() ? "?" + string.Join("&", parameters) : string.Empty;
    }

    #region Project Resource Operations

    /// <summary>
    /// Get all resources (contributors) assigned to a project
    /// </summary>
    public async Task<ApiListResponse<ProjectResource>> GetProjectResourcesAsync(string projectId, bool? isActive = null, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            // Build request body with Project_Id filter
            var requestBody = new Dictionary<string, object>
            {
                ["project_Id"] = projectId
            };

            if (isActive.HasValue)
                requestBody["isActive"] = isActive.Value;

            // Build query string for modelProperties
            // Request user details along with resource assignment info
            var properties = modelProperties ?? "Id,User_Id,User,IsActive,IsProjectManager,IsPrimaryProjectManager,IsProjectScheduler,PercentAllocationDefault,ProjectResourceRoleDescription,ActualHours,TaskCount";
            var queryString = $"?modelProperties={Uri.EscapeDataString(properties)};User:Id,Name,FirstName,LastName,Email,Title";

            _logger.LogInformation("Getting resources for project {ProjectId}", projectId);

            var json = JsonSerializer.Serialize(requestBody, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Try kebab-case first, then camelCase endpoint
            var response = await PostWithTokenAsync($"/api/project-resource/search{queryString}", content, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Recreate content for retry (HttpContent can only be sent once)
                content = new StringContent(json, Encoding.UTF8, "application/json");
                _logger.LogInformation("Trying alternate endpoint /api/projectresource/search");
                response = await PostWithTokenAsync($"/api/projectresource/search{queryString}", content, cancellationToken);
            }
            
            // If still not found, try getting resources from project endpoint with ProjectResources property
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Trying to get resources via project endpoint with ProjectResources property");
                var projectUrl = $"/api/project/{projectId}?modelProperties=Id,Name,ProjectResources;ProjectResource:Id,User_Id,User,IsActive,IsProjectManager,IsPrimaryProjectManager,IsProjectScheduler,PercentAllocationDefault,ProjectResourceRoleDescription,ActualHours,TaskCount;User:Id,Name,FirstName,LastName,Email,Title";
                var projectResponse = await GetWithTokenAsync(projectUrl, cancellationToken);
                
                if (projectResponse.IsSuccessStatusCode)
                {
                    var projectContent = await projectResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(projectContent);
                    
                    // Try to extract ProjectResources from the project response
                    if (doc.RootElement.TryGetProperty("projectResources", out var resourcesElement) ||
                        doc.RootElement.TryGetProperty("ProjectResources", out resourcesElement))
                    {
                        var resources = JsonSerializer.Deserialize<List<ProjectResource>>(resourcesElement.GetRawText(), JsonOptions);
                        
                        // Apply isActive filter if specified
                        if (isActive.HasValue && resources != null)
                        {
                            resources = resources.Where(r => r.IsActive == isActive.Value).ToList();
                        }
                        
                        return new ApiListResponse<ProjectResource>
                        {
                            Success = true,
                            Data = resources ?? new List<ProjectResource>(),
                            TotalCount = resources?.Count ?? 0
                        };
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get project resources: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<ProjectResource>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Project resources response: {Content}", responseContent);

            // Handle different response formats
            if (responseContent.TrimStart().StartsWith("["))
            {
                // Direct array response
                var resources = JsonSerializer.Deserialize<List<ProjectResource>>(responseContent, JsonOptions);
                return new ApiListResponse<ProjectResource>
                {
                    Success = true,
                    Data = resources ?? new List<ProjectResource>(),
                    TotalCount = resources?.Count ?? 0
                };
            }
            else
            {
                // Try to deserialize as wrapped response
                try
                {
                    var wrappedResponse = JsonSerializer.Deserialize<ApiListResponse<ProjectResource>>(responseContent, JsonOptions);
                    if (wrappedResponse != null)
                    {
                        wrappedResponse.Success = true;
                        return wrappedResponse;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Failed to deserialize as wrapped response: {Error}", ex.Message);
                }

                // Fallback: try to extract data property
                using var doc = JsonDocument.Parse(responseContent);
                if (doc.RootElement.TryGetProperty("data", out var dataElement) ||
                    doc.RootElement.TryGetProperty("Data", out dataElement))
                {
                    var resources = JsonSerializer.Deserialize<List<ProjectResource>>(dataElement.GetRawText(), JsonOptions);
                    return new ApiListResponse<ProjectResource>
                    {
                        Success = true,
                        Data = resources ?? new List<ProjectResource>(),
                        TotalCount = resources?.Count ?? 0
                    };
                }

                // If all else fails, return empty result
                _logger.LogWarning("Unable to parse project resources response format");
                return new ApiListResponse<ProjectResource>
                {
                    Success = true,
                    Data = new List<ProjectResource>(),
                    TotalCount = 0
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resources for project {ProjectId}", projectId);
            return new ApiListResponse<ProjectResource>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Ensure a user is a resource on a project (idempotent add)
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> EnsureProjectResourceAsync(string projectId, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/project/ensure-user-is-project-resource/{Uri.EscapeDataString(projectId)}?userId={Uri.EscapeDataString(userId)}";
            _logger.LogInformation("Ensuring user {UserId} is a resource on project {ProjectId}", userId, projectId);

            var response = await PostWithTokenAsync(url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to ensure project resource: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? true,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring project resource");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Save (create or update) a project resource assignment
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> SaveProjectResourceAsync(ProjectResource resource, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(resource, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Saving project resource: {ResourceId} for project {ProjectId}, user {UserId}",
                resource.Id ?? "new", resource.ProjectId, resource.UserId);

            var response = await PostWithTokenAsync("/api/project-resource", httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to save project resource: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? true,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving project resource");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Delete a project resource assignment by ID
    /// </summary>
    public async Task<ApiResponse<object>> DeleteProjectResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/project-resource/{Uri.EscapeDataString(resourceId)}";
            _logger.LogInformation("Deleting project resource: {ResourceId}", resourceId);

            var response = await DeleteWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete project resource: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting project resource {ResourceId}", resourceId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    #region Task Resource Operations

    /// <summary>
    /// List the resources (users) assigned to a specific task, including each
    /// assignment's Id (needed to remove/update it). Mirrors GetProjectResourcesAsync:
    /// tries /api/task-resource/search, then /api/taskresource/search, then the task's
    /// TaskResources child collection.
    /// </summary>
    public async Task<ApiListResponse<TaskResource>> GetTaskResourcesAsync(string taskId, bool? isActive = null, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var requestBody = new Dictionary<string, object>
            {
                ["task_Id"] = taskId
            };
            if (isActive.HasValue)
                requestBody["isActive"] = isActive.Value;

            var properties = modelProperties ?? "Id,Task_Id,User_Id,User,IsActive,WorkHours,PercentAllocationDefault";
            var queryString = $"?modelProperties={Uri.EscapeDataString(properties)};User:Id,Name,FirstName,LastName,Email,Title";

            _logger.LogInformation("Getting resources for task {TaskId}", taskId);

            var json = JsonSerializer.Serialize(requestBody, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await PostWithTokenAsync($"/api/task-resource/search{queryString}", content, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                content = new StringContent(json, Encoding.UTF8, "application/json");
                _logger.LogInformation("Trying alternate endpoint /api/taskresource/search");
                response = await PostWithTokenAsync($"/api/taskresource/search{queryString}", content, cancellationToken);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Trying to get resources via task endpoint with TaskResources property");
                var taskUrl = $"/api/task/{taskId}?modelProperties=Id,Name,TaskResources;TaskResource:Id,Task_Id,User_Id,User,IsActive,WorkHours,PercentAllocationDefault;User:Id,Name,FirstName,LastName,Email,Title";
                var taskResponse = await GetWithTokenAsync(taskUrl, cancellationToken);

                if (taskResponse.IsSuccessStatusCode)
                {
                    var taskContent = await taskResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(taskContent);

                    if (doc.RootElement.TryGetProperty("taskResources", out var resourcesElement) ||
                        doc.RootElement.TryGetProperty("TaskResources", out resourcesElement))
                    {
                        var resources = JsonSerializer.Deserialize<List<TaskResource>>(resourcesElement.GetRawText(), JsonOptions);

                        if (isActive.HasValue && resources != null)
                        {
                            resources = resources.Where(r => r.IsActive == isActive.Value).ToList();
                        }

                        return new ApiListResponse<TaskResource>
                        {
                            Success = true,
                            Data = resources ?? new List<TaskResource>(),
                            TotalCount = resources?.Count ?? 0
                        };
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get task resources: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<TaskResource>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (responseContent.TrimStart().StartsWith("["))
            {
                var resources = JsonSerializer.Deserialize<List<TaskResource>>(responseContent, JsonOptions);
                return new ApiListResponse<TaskResource>
                {
                    Success = true,
                    Data = resources ?? new List<TaskResource>(),
                    TotalCount = resources?.Count ?? 0
                };
            }
            else
            {
                try
                {
                    var wrappedResponse = JsonSerializer.Deserialize<ApiListResponse<TaskResource>>(responseContent, JsonOptions);
                    if (wrappedResponse != null)
                    {
                        wrappedResponse.Success = true;
                        return wrappedResponse;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Failed to deserialize as wrapped response: {Error}", ex.Message);
                }

                using var doc = JsonDocument.Parse(responseContent);
                if (doc.RootElement.TryGetProperty("data", out var dataElement) ||
                    doc.RootElement.TryGetProperty("Data", out dataElement))
                {
                    var resources = JsonSerializer.Deserialize<List<TaskResource>>(dataElement.GetRawText(), JsonOptions);
                    return new ApiListResponse<TaskResource>
                    {
                        Success = true,
                        Data = resources ?? new List<TaskResource>(),
                        TotalCount = resources?.Count ?? 0
                    };
                }

                _logger.LogWarning("Unable to parse task resources response format");
                return new ApiListResponse<TaskResource>
                {
                    Success = true,
                    Data = new List<TaskResource>(),
                    TotalCount = 0
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resources for task {TaskId}", taskId);
            return new ApiListResponse<TaskResource>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Save (create or update) a task resource assignment
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> SaveTaskResourceAsync(TaskResource resource, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(resource, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Saving task resource: {ResourceId} for task {TaskId}, user {UserId}",
                resource.Id ?? "new", resource.TaskId, resource.UserId);

            var response = await PostWithTokenAsync("/api/task-resource", httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to save task resource: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? true,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving task resource");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Delete a task resource assignment by ID
    /// </summary>
    public async Task<ApiResponse<object>> DeleteTaskResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/task-resource/{Uri.EscapeDataString(resourceId)}";
            _logger.LogInformation("Deleting task resource: {ResourceId}", resourceId);

            var response = await DeleteWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete task resource: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting task resource {ResourceId}", resourceId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    #region TimeSheet Operations

    /// <summary>
    /// Get a single timesheet by ID
    /// </summary>
    public async Task<ApiResponse<TimeSheet>> GetTimeSheetAsync(string timeSheetId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-sheet/{timeSheetId}";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Getting timesheet {TimeSheetId}", timeSheetId);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get timesheet: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TimeSheet>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var timeSheet = JsonSerializer.Deserialize<TimeSheet>(content, JsonOptions);

            return new ApiResponse<TimeSheet>
            {
                Success = true,
                Data = timeSheet
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting timesheet {TimeSheetId}", timeSheetId);
            return new ApiResponse<TimeSheet>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Search timesheets with filters
    /// </summary>
    public async Task<ApiListResponse<TimeSheet>> SearchTimeSheetsAsync(TimeSheetSearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var queryParams = new List<string>();

            if (!string.IsNullOrEmpty(request.UserGuidList))
                queryParams.Add($"userGuidList={Uri.EscapeDataString(request.UserGuidList)}");
            if (!string.IsNullOrEmpty(request.UserApproverGuidList))
                queryParams.Add($"userApproverGuidList={Uri.EscapeDataString(request.UserApproverGuidList)}");
            if (!string.IsNullOrEmpty(request.UserPMApproverGuidList))
                queryParams.Add($"userPMApproverGuidList={Uri.EscapeDataString(request.UserPMApproverGuidList)}");
            if (!string.IsNullOrEmpty(request.UserTypeGuidList))
                queryParams.Add($"userTypeGuidList={Uri.EscapeDataString(request.UserTypeGuidList)}");
            if (request.StartDate.HasValue)
                queryParams.Add($"startDate={request.StartDate.Value:yyyy-MM-dd}");
            if (request.EndDate.HasValue)
                queryParams.Add($"endDate={request.EndDate.Value:yyyy-MM-dd}");
            if (request.ExcludeUnsubmitted.HasValue)
                queryParams.Add($"excludeUnsubmitted={request.ExcludeUnsubmitted.Value.ToString().ToLower()}");
            if (request.ExcludeApproved.HasValue)
                queryParams.Add($"excludeApproved={request.ExcludeApproved.Value.ToString().ToLower()}");
            if (request.ExcludeRejected.HasValue)
                queryParams.Add($"excludeRejected={request.ExcludeRejected.Value.ToString().ToLower()}");
            if (request.ExcludePendingApproval.HasValue)
                queryParams.Add($"excludePendingApproval={request.ExcludePendingApproval.Value.ToString().ToLower()}");
            if (request.ExcludePreviouslyDownloaded.HasValue)
                queryParams.Add($"excludePreviouslyDownloaded={request.ExcludePreviouslyDownloaded.Value.ToString().ToLower()}");
            if (!string.IsNullOrEmpty(request.ModelProperties))
                queryParams.Add($"modelProperties={Uri.EscapeDataString(request.ModelProperties)}");

            var url = "/api/time-sheet/search" + (queryParams.Any() ? "?" + string.Join("&", queryParams) : "");

            _logger.LogInformation("Searching timesheets: {Url}", url);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to search timesheets: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<TimeSheet>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var timeSheets = JsonSerializer.Deserialize<List<TimeSheet>>(content, JsonOptions) ?? new List<TimeSheet>();

            return new ApiListResponse<TimeSheet>
            {
                Success = true,
                Data = timeSheets,
                TotalCount = timeSheets.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching timesheets");
            return new ApiListResponse<TimeSheet>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get current user's time status
    /// </summary>
    public async Task<ApiResponse<MyTimeCurrentStatus>> GetMyTimeCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/time-sheet/my-time-current-status";

            _logger.LogInformation("Getting my time current status");
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get my time status: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<MyTimeCurrentStatus>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = JsonSerializer.Deserialize<MyTimeCurrentStatus>(content, JsonOptions);

            return new ApiResponse<MyTimeCurrentStatus>
            {
                Success = true,
                Data = status
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting my time current status");
            return new ApiResponse<MyTimeCurrentStatus>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Create or update a timesheet (upsert - if Id is provided, updates; otherwise creates)
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> SaveTimeSheetAsync(TimeSheet timeSheet, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/time-sheet";
            var json = JsonSerializer.Serialize(timeSheet, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Saving timesheet: {TimeSheetId}", timeSheet.Id ?? "new");
            var response = await PostWithTokenAsync(url, httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to save timesheet: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? false,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving timesheet");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Create a new timesheet and immediately submit it for approval in a single operation.
    /// Uses the /api/time-sheet/create-and-submit endpoint with query parameters.
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> CreateAndSubmitTimeSheetAsync(string userId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-sheet/create-and-submit?startDate={startDate:yyyy-MM-dd}&endDate={endDate:yyyy-MM-dd}&userId={Uri.EscapeDataString(userId)}";

            _logger.LogInformation("Creating and submitting timesheet for user {UserId}, {StartDate} - {EndDate}", userId, startDate, endDate);
            var response = await PostWithTokenAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to create-and-submit timesheet: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? false,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating and submitting timesheet");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Submit a timesheet for approval
    /// </summary>
    public async Task<ApiResponse<object>> SubmitTimeSheetAsync(string timeSheetId, string? approverUserId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-sheet/{timeSheetId}/submit";
            if (!string.IsNullOrEmpty(approverUserId))
                url += $"?approverUserId={Uri.EscapeDataString(approverUserId)}";

            _logger.LogInformation("Submitting timesheet {TimeSheetId}", timeSheetId);
            var response = await PostWithTokenAsync(url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to submit timesheet: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting timesheet {TimeSheetId}", timeSheetId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Approve a timesheet
    /// </summary>
    public async Task<ApiResponse<object>> ApproveTimeSheetAsync(string timeSheetId, string? comments = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-sheet/{timeSheetId}/approve";
            if (!string.IsNullOrEmpty(comments))
                url += $"?comments={Uri.EscapeDataString(comments)}";

            _logger.LogInformation("Approving timesheet {TimeSheetId}", timeSheetId);
            var response = await PostWithTokenAsync(url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to approve timesheet: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving timesheet {TimeSheetId}", timeSheetId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Reject a timesheet
    /// </summary>
    public async Task<ApiResponse<object>> RejectTimeSheetAsync(string timeSheetId, string? comments = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-sheet/{timeSheetId}/reject";
            if (!string.IsNullOrEmpty(comments))
                url += $"?comments={Uri.EscapeDataString(comments)}";

            _logger.LogInformation("Rejecting timesheet {TimeSheetId}", timeSheetId);
            var response = await PostWithTokenAsync(url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to reject timesheet: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting timesheet {TimeSheetId}", timeSheetId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    #region TimeEntry Operations

    /// <summary>
    /// Search time entries with filters (project, task, user, date range).
    /// Uses GET with query parameters. Per PI API docs, parameter names are:
    /// userList, projectList, taskSpecialtyList, startDate, endDate, etc.
    /// </summary>
    public async Task<ApiListResponse<TimeEntry>> SearchTimeEntriesAsync(TimeEntrySearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var queryParams = new List<string>();

            // Per PI API docs: correct parameter names are userList, projectList (not userGuidList, projectGuidList)
            if (!string.IsNullOrEmpty(request.ProjectGuidList))
                queryParams.Add($"projectList={Uri.EscapeDataString(request.ProjectGuidList)}");
            if (!string.IsNullOrEmpty(request.TaskGuidList))
                queryParams.Add($"taskGuidList={Uri.EscapeDataString(request.TaskGuidList)}");
            if (!string.IsNullOrEmpty(request.UserGuidList))
                queryParams.Add($"userList={Uri.EscapeDataString(request.UserGuidList)}");
            if (!string.IsNullOrEmpty(request.TimeSheetGuidList))
                queryParams.Add($"timeSheetGuidList={Uri.EscapeDataString(request.TimeSheetGuidList)}");
            if (request.StartDate.HasValue)
                queryParams.Add($"startDate={request.StartDate.Value:yyyy-MM-dd}");
            if (request.EndDate.HasValue)
                queryParams.Add($"endDate={request.EndDate.Value:yyyy-MM-dd}");
            if (request.IsBillable.HasValue)
                queryParams.Add($"isBillable={request.IsBillable.Value.ToString().ToLower()}");
            if (!string.IsNullOrEmpty(request.ModelProperties))
                queryParams.Add($"modelProperties={Uri.EscapeDataString(request.ModelProperties)}");

            var url = "/api/time-entry/search" + (queryParams.Any() ? "?" + string.Join("&", queryParams) : "");

            _logger.LogInformation("Searching time entries: {Url}", url);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to search time entries: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<TimeEntry>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var timeEntries = JsonSerializer.Deserialize<List<TimeEntry>>(content, JsonOptions) ?? new List<TimeEntry>();

            return new ApiListResponse<TimeEntry>
            {
                Success = true,
                Data = timeEntries,
                TotalCount = timeEntries.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching time entries");
            return new ApiListResponse<TimeEntry>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get all time entries for a specific project using the dedicated project endpoint.
    /// This endpoint correctly returns only entries belonging to the project (unlike /search).
    /// Note: This endpoint does not support date filtering - filter client-side if needed.
    /// </summary>
    public async Task<ApiListResponse<TimeEntry>> GetProjectTimeEntriesAsync(string projectId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-entry/project/{Uri.EscapeDataString(projectId)}";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Getting time entries for project {ProjectId} using /api/time-entry/project endpoint", projectId);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get project time entries: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<TimeEntry>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var timeEntries = JsonSerializer.Deserialize<List<TimeEntry>>(content, JsonOptions) ?? new List<TimeEntry>();

            return new ApiListResponse<TimeEntry>
            {
                Success = true,
                Data = timeEntries,
                TotalCount = timeEntries.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting project time entries for {ProjectId}", projectId);
            return new ApiListResponse<TimeEntry>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Create or update a time entry (POST to /api/time-entry).
    /// If Id is null, creates a new entry. If Id is set, updates existing.
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> SaveTimeEntryAsync(TimeEntry timeEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/time-entry";
            var json = JsonSerializer.Serialize(timeEntry, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Saving time entry: {TimeEntryId} for task {TaskId} on {Date}",
                timeEntry.Id ?? "new", timeEntry.TaskId, timeEntry.Date);
            var response = await PostWithTokenAsync(url, httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to save time entry: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? true,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving time entry");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Delete a time entry by ID
    /// </summary>
    public async Task<ApiResponse<object>> DeleteTimeEntryAsync(string timeEntryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-entry/{Uri.EscapeDataString(timeEntryId)}";
            _logger.LogInformation("Deleting time entry: {TimeEntryId}", timeEntryId);

            var response = await DeleteWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete time entry: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting time entry {TimeEntryId}", timeEntryId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Search one user's time entries in a date range, with task and project names.
    /// Requests only the fields time-change drafts use (no rates or totals).
    /// </summary>
    public async Task<ApiListResponse<TimeEntryWithNames>> SearchTimeEntriesWithNamesAsync(
        string userId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/time-entry/search" +
                $"?userList={Uri.EscapeDataString(userId)}" +
                $"&startDate={startDate:yyyy-MM-dd}&endDate={endDate:yyyy-MM-dd}" +
                $"&modelProperties={Uri.EscapeDataString(TimeEntryWithNames.ModelProperties)}";
            var response = await GetWithTokenAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to search time entries with names: {StatusCode} - {Error}", response.StatusCode, content);
                return new ApiListResponse<TimeEntryWithNames>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { content }
                };
            }

            var entries = JsonSerializer.Deserialize<List<TimeEntryWithNames>>(content, JsonOptions) ?? new();
            return new ApiListResponse<TimeEntryWithNames> { Success = true, Data = entries, TotalCount = entries.Count };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching time entries with names");
            return new ApiListResponse<TimeEntryWithNames>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>Get one time entry by id, with task and project names.</summary>
    public async Task<ApiResponse<TimeEntryWithNames>> GetTimeEntryAsync(string timeEntryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/time-entry/{Uri.EscapeDataString(timeEntryId)}" +
                $"?modelProperties={Uri.EscapeDataString(TimeEntryWithNames.ModelProperties)}";
            var response = await GetWithTokenAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ApiResponse<TimeEntryWithNames>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { content }
                };
            }

            return new ApiResponse<TimeEntryWithNames>
            {
                Success = true,
                Data = JsonSerializer.Deserialize<TimeEntryWithNames>(content, JsonOptions)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting time entry {TimeEntryId}", timeEntryId);
            return new ApiResponse<TimeEntryWithNames>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    #region User Operations

    /// <summary>
    /// Get current authenticated user
    /// </summary>
    public async Task<ApiResponse<User>> GetCurrentUserAsync(string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/user/me";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Getting current user");
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get current user: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<User>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var user = JsonSerializer.Deserialize<User>(content, JsonOptions);

            return new ApiResponse<User>
            {
                Success = true,
                Data = user
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current user");
            return new ApiResponse<User>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get a user by ID
    /// </summary>
    public async Task<ApiResponse<User>> GetUserAsync(string userId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/user/{userId}";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Getting user {UserId}", userId);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get user: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<User>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var user = JsonSerializer.Deserialize<User>(content, JsonOptions);

            return new ApiResponse<User>
            {
                Success = true,
                Data = user
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user {UserId}", userId);
            return new ApiResponse<User>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get multiple users by IDs
    /// </summary>
    public async Task<ApiListResponse<User>> GetUsersAsync(string ids, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/user/list?ids={Uri.EscapeDataString(ids)}";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"&modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Getting users by IDs");
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get users: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<User>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var users = JsonSerializer.Deserialize<List<User>>(content, JsonOptions) ?? new List<User>();

            return new ApiListResponse<User>
            {
                Success = true,
                Data = users,
                TotalCount = users.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting users by IDs");
            return new ApiListResponse<User>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// List all users by collecting user IDs from projects and timesheets, then resolving them.
    /// This is a workaround since the Project Insight API doesn't have a direct user search endpoint.
    /// </summary>
    public async Task<ApiListResponse<User>> ListAllUsersAsync(bool? isActive = null, string? nameFilter = null, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var userIds = new HashSet<string>();

            // 1. Collect user IDs from projects (project managers)
            _logger.LogInformation("Collecting user IDs from projects...");
            var projectsResponse = await GetWithTokenAsync("/api/project/search?modelProperties=Id,PrimaryProjectManager_Id", cancellationToken);
            if (projectsResponse.IsSuccessStatusCode)
            {
                var projectsContent = await projectsResponse.Content.ReadAsStringAsync(cancellationToken);
                var projects = JsonSerializer.Deserialize<List<JsonElement>>(projectsContent, JsonOptions) ?? new List<JsonElement>();
                foreach (var project in projects)
                {
                    if (project.TryGetProperty("PrimaryProjectManager_Id", out var pmId) && pmId.ValueKind == JsonValueKind.String)
                    {
                        var id = pmId.GetString();
                        if (!string.IsNullOrEmpty(id))
                            userIds.Add(id);
                    }
                }
            }

            // 2. Collect user IDs from timesheets
            _logger.LogInformation("Collecting user IDs from timesheets...");
            var timesheetsResponse = await GetWithTokenAsync("/api/time-sheet/search?modelProperties=Id,User_Id,UserApprover_Id", cancellationToken);
            if (timesheetsResponse.IsSuccessStatusCode)
            {
                var timesheetsContent = await timesheetsResponse.Content.ReadAsStringAsync(cancellationToken);
                var timesheets = JsonSerializer.Deserialize<List<JsonElement>>(timesheetsContent, JsonOptions) ?? new List<JsonElement>();
                foreach (var timesheet in timesheets)
                {
                    if (timesheet.TryGetProperty("User_Id", out var userId) && userId.ValueKind == JsonValueKind.String)
                    {
                        var id = userId.GetString();
                        if (!string.IsNullOrEmpty(id))
                            userIds.Add(id);
                    }
                    if (timesheet.TryGetProperty("UserApprover_Id", out var approverId) && approverId.ValueKind == JsonValueKind.String)
                    {
                        var id = approverId.GetString();
                        if (!string.IsNullOrEmpty(id))
                            userIds.Add(id);
                    }
                }
            }

            _logger.LogInformation("Found {Count} unique user IDs", userIds.Count);

            if (userIds.Count == 0)
            {
                return new ApiListResponse<User>
                {
                    Success = true,
                    Data = new List<User>(),
                    TotalCount = 0,
                    Message = "No users found in projects or timesheets"
                };
            }

            // 3. Resolve user IDs to user objects
            var resolvedModelProperties = modelProperties ?? "Id,Name,FirstName,LastName,Email,IsActive";
            var url = $"/api/user/list?ids={Uri.EscapeDataString(string.Join(",", userIds))}&modelProperties={Uri.EscapeDataString(resolvedModelProperties)}";
            
            var response = await GetWithTokenAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to resolve users: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<User>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var users = JsonSerializer.Deserialize<List<User>>(content, JsonOptions) ?? new List<User>();

            // 4. Apply filters
            if (isActive.HasValue)
            {
                users = users.Where(u => u.IsActive == isActive.Value).ToList();
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                var filter = nameFilter.ToLowerInvariant();
                users = users.Where(u => 
                    (u.Name?.ToLowerInvariant().Contains(filter) ?? false) ||
                    (u.FirstName?.ToLowerInvariant().Contains(filter) ?? false) ||
                    (u.LastName?.ToLowerInvariant().Contains(filter) ?? false) ||
                    (u.Email?.ToLowerInvariant().Contains(filter) ?? false)
                ).ToList();
            }

            return new ApiListResponse<User>
            {
                Success = true,
                Data = users,
                TotalCount = users.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing all users");
            return new ApiListResponse<User>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    private static string GetDefaultModelProperties()
    {
        // Return all the key properties we saw in the API response
        return "Name,Id,CompanyDefault_Id,CustomFieldValue_Id,Department_Id,DurationSeconds," +
               "DurationString,EndDateTimeUTC,EstimateAtCompletionToTargetTotal_Variance," +
               "EstimateAtCompletionToTargetTotalPercent_Variance,Health_Id,Health_Previous_Id," +
               "IsActive,IsOpen,Portfolio_Input_Id,PrimaryProjectManager_Id,ProfitSalesOrderActual," +
               "Program_Input_Id,ProjectPriority_Id,ProjectState,ProjectStatus_Id,ProjectType_Id," +
               "ProposalDefault_Id,ScheduleStartDate,StartDateTimeUTC,UrlFull,WorkPercentComplete," +
               "WorkPercentCompleteType_Id,WorkSeconds";
    }

    private static string GetDefaultTaskModelProperties()
    {
        // Return task-specific properties based on what we saw in the API response
        return "Id,Name,Description,Project_Id,StartDateTimeUTC,EndDateTimeUTC," +
               "WorkPercentComplete,WorkHours,IsActive,IsMilestone,ItemNumber";
    }

    #region Resource Type Operations

    /// <summary>
    /// Get a single resource type by ID
    /// </summary>
    public async Task<ApiResponse<ResourceType>> GetResourceTypeAsync(string resourceTypeId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var url = $"/api/resource-type/{Uri.EscapeDataString(resourceTypeId)}";
            _logger.LogInformation("Getting resource type {ResourceTypeId}", resourceTypeId);

            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get resource type: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<ResourceType>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var resourceType = JsonSerializer.Deserialize<ResourceType>(content, JsonOptions);

            return new ApiResponse<ResourceType>
            {
                Success = true,
                Data = resourceType
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting resource type {ResourceTypeId}", resourceTypeId);
            return new ApiResponse<ResourceType>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    #region Company Contract (Rate Card) Operations

    /// <summary>
    /// Get all company contracts (rate cards) for a project
    /// </summary>
    public async Task<ApiListResponse<CompanyContract>> GetCompanyContractsByProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var url = $"/api/company-contract/list-by-project/{Uri.EscapeDataString(projectId)}";
            _logger.LogInformation("Getting company contracts for project {ProjectId}", projectId);

            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get company contracts: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiListResponse<CompanyContract>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (content.TrimStart().StartsWith("["))
            {
                var contracts = JsonSerializer.Deserialize<List<CompanyContract>>(content, JsonOptions);
                return new ApiListResponse<CompanyContract>
                {
                    Success = true,
                    Data = contracts ?? new List<CompanyContract>(),
                    TotalCount = contracts?.Count ?? 0
                };
            }
            else
            {
                var contract = JsonSerializer.Deserialize<CompanyContract>(content, JsonOptions);
                return new ApiListResponse<CompanyContract>
                {
                    Success = true,
                    Data = contract != null ? new List<CompanyContract> { contract } : new List<CompanyContract>(),
                    TotalCount = contract != null ? 1 : 0
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting company contracts for project {ProjectId}", projectId);
            return new ApiListResponse<CompanyContract>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Get a single company contract (rate card) by ID
    /// </summary>
    public async Task<ApiResponse<CompanyContract>> GetCompanyContractAsync(string contractId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var url = $"/api/company-contract/{Uri.EscapeDataString(contractId)}";
            _logger.LogInformation("Getting company contract {ContractId}", contractId);

            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get company contract: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<CompanyContract>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var contract = JsonSerializer.Deserialize<CompanyContract>(content, JsonOptions);

            return new ApiResponse<CompanyContract>
            {
                Success = true,
                Data = contract
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting company contract {ContractId}", contractId);
            return new ApiResponse<CompanyContract>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Save (create or update) a rate on a company contract rate card.
    /// rateType must be one of: default, user, resource-type, task-type, time-code
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> SaveCompanyContractRateAsync(string rateType, string contractId, RateCardRate rate, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var url = $"/api/company-contract/{Uri.EscapeDataString(rateType)}-rate/{Uri.EscapeDataString(contractId)}";
            var json = JsonSerializer.Serialize(rate, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Saving {RateType} rate on contract {ContractId}", rateType, contractId);
            var response = await PostWithTokenAsync(url, httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to save contract rate: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? true,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving {RateType} rate on contract {ContractId}", rateType, contractId);
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Delete a rate from a company contract rate card.
    /// rateType must be one of: default, user, resource-type, task-type, time-code
    /// </summary>
    public async Task<ApiResponse<object>> DeleteCompanyContractRateAsync(string rateType, string contractId, string rateId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var url = $"/api/company-contract/{Uri.EscapeDataString(rateType)}-rate/{Uri.EscapeDataString(contractId)}/{Uri.EscapeDataString(rateId)}";
            _logger.LogInformation("Deleting {RateType} rate {RateId} from contract {ContractId}", rateType, rateId, contractId);

            var response = await DeleteWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete contract rate: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting {RateType} rate {RateId} from contract {ContractId}", rateType, rateId, contractId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    #region PTO Operations

    /// <summary>
    /// Get a single PTO entry by its ID.
    /// GET /api/user-pto/{ptoEntryId}
    /// </summary>
    public async Task<ApiResponse<UserPto>> GetUserPtoAsync(string ptoEntryId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/user-pto/{Uri.EscapeDataString(ptoEntryId)}";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Getting PTO entry {PtoEntryId}", ptoEntryId);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to get PTO entry: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<UserPto>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var pto = JsonSerializer.Deserialize<UserPto>(content, JsonOptions);

            return new ApiResponse<UserPto>
            {
                Success = true,
                Data = pto
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PTO entry {PtoEntryId}", ptoEntryId);
            return new ApiResponse<UserPto>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// List all PTO entries for a user. Returns ALL entries (no server-side date filtering).
    /// GET /api/user-pto/list-by-user/{userId}
    /// </summary>
    public async Task<ApiResponse<List<UserPto>>> ListUserPtoAsync(string userId, string? modelProperties = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/user-pto/list-by-user/{Uri.EscapeDataString(userId)}";
            if (!string.IsNullOrEmpty(modelProperties))
                url += $"?modelProperties={Uri.EscapeDataString(modelProperties)}";

            _logger.LogInformation("Listing PTO entries for user {UserId}", userId);
            var response = await GetWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to list PTO entries: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<List<UserPto>>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var ptoList = JsonSerializer.Deserialize<List<UserPto>>(content, JsonOptions);

            return new ApiResponse<List<UserPto>>
            {
                Success = true,
                Data = ptoList ?? new List<UserPto>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing PTO entries for user {UserId}", userId);
            return new ApiResponse<List<UserPto>>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Create or update a PTO entry (POST to /api/user-pto/pto-calendar).
    /// This also auto-creates/updates a linked time entry (non-billable, no project/task).
    /// If Id matches an existing entry, updates it. Otherwise creates new.
    /// </summary>
    public async Task<ApiResponse<TaskSaveResult>> SaveUserPtoAsync(UserPto ptoEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/user-pto/pto-calendar";

            // The PI API expects Date in M/d/yyyy format for this endpoint
            var payload = new Dictionary<string, object?>();
            if (!string.IsNullOrEmpty(ptoEntry.Id))
                payload["Id"] = ptoEntry.Id;
            if (!string.IsNullOrEmpty(ptoEntry.UserId))
                payload["User_Id"] = ptoEntry.UserId;
            if (ptoEntry.Date.HasValue)
                payload["Date"] = ptoEntry.Date.Value.ToString("M/d/yyyy");
            if (ptoEntry.PTOHours.HasValue)
                payload["PTOHours"] = ptoEntry.PTOHours.Value;
            if (!string.IsNullOrEmpty(ptoEntry.TimeCodeId))
                payload["TimeCode_Id"] = ptoEntry.TimeCodeId;
            if (ptoEntry.Description != null)
                payload["Description"] = ptoEntry.Description;

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Saving PTO entry for user {UserId} on {Date}",
                ptoEntry.UserId, ptoEntry.Date);
            var response = await PostWithTokenAsync(url, httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to save PTO entry: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<TaskSaveResult>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TaskSaveResult>(content, JsonOptions);

            return new ApiResponse<TaskSaveResult>
            {
                Success = result?.Success ?? true,
                Data = result,
                Errors = ExtractErrorMessages(result?.Errors)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving PTO entry");
            return new ApiResponse<TaskSaveResult>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Delete a PTO entry by ID. Also auto-deletes the linked time entry.
    /// DELETE /api/user-pto/pto-calendar/{ptoEntryId}
    /// </summary>
    public async Task<ApiResponse<object>> DeleteUserPtoAsync(string ptoEntryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/user-pto/pto-calendar/{Uri.EscapeDataString(ptoEntryId)}";
            _logger.LogInformation("Deleting PTO entry: {PtoEntryId}", ptoEntryId);

            var response = await DeleteWithTokenAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete PTO entry: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new ApiResponse<object>
                {
                    Success = false,
                    Message = $"API returned {response.StatusCode}",
                    Errors = new List<string> { errorContent }
                };
            }

            return new ApiResponse<object> { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting PTO entry {PtoEntryId}", ptoEntryId);
            return new ApiResponse<object>
            {
                Success = false,
                Message = "Internal error occurred",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    #endregion

    /// <summary>
    /// Extracts error messages from API error objects into a flat string list.
    /// </summary>
    private static List<string>? ExtractErrorMessages(List<ApiError>? errors)
    {
        if (errors == null || errors.Count == 0) return null;
        return errors
            .Select(e => e.ErrorMessage ?? "Unknown error")
            .ToList();
    }

    /// <summary>Image types a user photo may have. SVG is left out: it can carry script.</summary>
    private static readonly HashSet<string> PhotoTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/gif", "image/webp" };

    /// <summary>
    /// Fetch an image PI serves from its own site, such as a user's PhotoUrl ("/Sites/Files/…").
    /// Only site-relative paths are fetched, so a stored URL can't send the request (or the token)
    /// anywhere else. Returns null when it's missing, not a photo type, or larger than maxBytes.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetSiteImageAsync(string path, int maxBytes, CancellationToken cancellationToken = default)
    {
        // Only PI's own uploaded files: no query, fragment, escapes or path tricks that could reach another page.
        if (!path.StartsWith("/Sites/Files/", StringComparison.Ordinal) || path.IndexOfAny(['?', '#', '%', '\\']) >= 0
            || path.Contains("..") || path.Contains("//"))
            return null;
        try
        {
            using var request = CreateRequest(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("image/*");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || type is null || !PhotoTypes.Contains(type)
                || response.Content.Headers.ContentLength > maxBytes)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), type.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not fetch site image");
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}