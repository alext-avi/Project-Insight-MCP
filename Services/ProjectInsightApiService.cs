using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.Models;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Service for interacting with Project Insight API
/// </summary>
public class ProjectInsightApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectInsightApiService> _logger;
    private readonly string _baseUrl;
    private readonly string _apiToken;

    public ProjectInsightApiService(HttpClient httpClient, ILogger<ProjectInsightApiService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["ProjectInsight:BaseUrl"] ?? throw new ArgumentException("ProjectInsight:BaseUrl is required");
        _apiToken = configuration["ProjectInsight:ApiToken"] ?? throw new ArgumentException("ProjectInsight:ApiToken is required");

        // Validate token format
        if (string.IsNullOrWhiteSpace(_apiToken) || _apiToken == "YOUR_API_TOKEN_HERE")
        {
            throw new ArgumentException("Valid ProjectInsight API token is required");
        }

        // Configure HTTP client
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiToken}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Search for projects with optional filters
    /// </summary>
    public async Task<ApiListResponse<Project>> SearchProjectsAsync(ProjectSearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use POST instead of GET for project search
            var requestBody = new Dictionary<string, object>
            {
                { "modelProperties", request.ModelProperties ?? GetDefaultModelProperties() }
            };

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

            _logger.LogInformation("Searching projects with POST to /api/project/search");

            var response = await _httpClient.PostAsync("/api/project/search", httpContent, cancellationToken);

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

            var response = await _httpClient.GetAsync(url, cancellationToken);

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
    public async Task<ApiListResponse<ProjectTask>> GetProjectTasksAsync(string projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting tasks for project {ProjectId} with GET to /api/task/project/{ProjectId}", projectId, projectId);

            var response = await _httpClient.GetAsync($"/api/task/project/{projectId}", cancellationToken);

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

            var response = await _httpClient.PostAsync("/api/project", content, cancellationToken);

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

            var response = await _httpClient.PutAsync($"/api/project/{projectId}", content, cancellationToken);

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}