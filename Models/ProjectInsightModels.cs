using System.Text.Json.Serialization;

namespace ProjectInsightMCP.Models;

/// <summary>
/// Core Project model based on Project Insight API specification
/// </summary>
public class Project
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("projectState")]
    public int ProjectState { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
    
    [JsonPropertyName("isOpen")]
    public bool IsOpen { get; set; }
    
    [JsonPropertyName("startDateTimeUTC")]
    public DateTime? StartDateTimeUTC { get; set; }
    
    [JsonPropertyName("endDateTimeUTC")]
    public DateTime? EndDateTimeUTC { get; set; }
    
    [JsonPropertyName("scheduleStartDate")]
    public DateTime? ScheduleStartDate { get; set; }
    
    [JsonPropertyName("workPercentComplete")]
    public decimal WorkPercentComplete { get; set; }
    
    [JsonPropertyName("actualHours")]
    public decimal? ActualHours { get; set; }
    
    [JsonPropertyName("actualTimeTotal")]
    public decimal? ActualTimeTotal { get; set; }
    
    [JsonPropertyName("actualExpenseTotal")]
    public decimal? ActualExpenseTotal { get; set; }
    
    [JsonPropertyName("targetWorkTotalCost")]
    public decimal? TargetWorkTotalCost { get; set; }
    
    [JsonPropertyName("primaryProjectManager_Id")]
    public string? PrimaryProjectManagerId { get; set; }
    
    [JsonPropertyName("projectStatus_Id")]
    public string? ProjectStatusId { get; set; }
    
    [JsonPropertyName("projectType_Id")]
    public string? ProjectTypeId { get; set; }
    
    [JsonPropertyName("department_Id")]
    public string? DepartmentId { get; set; }
    
    [JsonPropertyName("companyDefault_Id")]
    public string? CompanyDefaultId { get; set; }
    
    [JsonPropertyName("health_Id")]
    public string? HealthId { get; set; }
    
    [JsonPropertyName("urlFull")]
    public string? UrlFull { get; set; }
    
    [JsonPropertyName("itemNumber")]
    public int? ItemNumber { get; set; }
    
    [JsonPropertyName("createdDateTimeUTC")]
    public DateTime? CreatedDateTimeUTC { get; set; }
    
    [JsonPropertyName("updatedDateTimeUTC")]
    public DateTime? UpdatedDateTimeUTC { get; set; }
}

/// <summary>
/// Task model for Project Insight
/// </summary>
public class ProjectTask
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("project_Id")]
    public string? ProjectId { get; set; }
    
    [JsonPropertyName("startDateTimeUTC")]
    public DateTime? StartDateTimeUTC { get; set; }
    
    [JsonPropertyName("endDateTimeUTC")]
    public DateTime? EndDateTimeUTC { get; set; }
    
    [JsonPropertyName("workPercentComplete")]
    public decimal WorkPercentComplete { get; set; }
    
    [JsonPropertyName("workHours")]
    public decimal? WorkHours { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
    
    [JsonPropertyName("isMilestone")]
    public bool IsMilestone { get; set; }
    
    [JsonPropertyName("itemNumber")]
    public int? ItemNumber { get; set; }
}

/// <summary>
/// User model for Project Insight
/// </summary>
public class User
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }
    
    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }
    
    [JsonPropertyName("email")]
    public string? Email { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}

/// <summary>
/// Project Status model
/// </summary>
public class ProjectStatus
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

/// <summary>
/// Project Type model
/// </summary>
public class ProjectType
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// API Response wrapper for Project Insight API
/// </summary>
public class ApiResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
    
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    
    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }
}

/// <summary>
/// API List Response for paginated results
/// </summary>
public class ApiListResponse<T> : ApiResponse<List<T>>
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
    
    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }
    
    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
    
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

/// <summary>
/// Project search/filter request model
/// </summary>
public class ProjectSearchRequest
{
    public bool? IsActive { get; set; }
    public bool? IsOpen { get; set; }
    public string? ProjectManagerId { get; set; }
    public string? ProjectStatusId { get; set; }
    public string? ProjectTypeId { get; set; }
    public string? DepartmentId { get; set; }
    public string? CompanyId { get; set; }
    public DateTime? StartDateFrom { get; set; }
    public DateTime? StartDateTo { get; set; }
    public DateTime? EndDateFrom { get; set; }
    public DateTime? EndDateTo { get; set; }
    public string? ModelProperties { get; set; }
    public int? PageIndex { get; set; } = 0;
    public int? PageSize { get; set; } = 50;
}