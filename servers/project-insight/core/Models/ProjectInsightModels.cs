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
    public int? ProjectState { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
    
    [JsonPropertyName("isOpen")]
    public bool? IsOpen { get; set; }
    
    [JsonPropertyName("startDateTimeUTC")]
    public DateTime? StartDateTimeUTC { get; set; }
    
    [JsonPropertyName("endDateTimeUTC")]
    public DateTime? EndDateTimeUTC { get; set; }
    
    [JsonPropertyName("scheduleStartDate")]
    public DateTime? ScheduleStartDate { get; set; }
    
    [JsonPropertyName("workPercentComplete")]
    public decimal? WorkPercentComplete { get; set; }
    
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
    public decimal? WorkPercentComplete { get; set; }
    
    [JsonPropertyName("workHours")]
    public decimal? WorkHours { get; set; }
    
    [JsonPropertyName("workSeconds")]
    public decimal? WorkSeconds { get; set; }
    
    [JsonPropertyName("durationSeconds")]
    public decimal? DurationSeconds { get; set; }
    
    [JsonPropertyName("durationString")]
    public string? DurationString { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
    
    [JsonPropertyName("isOpen")]
    public bool? IsOpen { get; set; }
    
    [JsonPropertyName("isMilestone")]
    public bool? IsMilestone { get; set; }
    
    [JsonPropertyName("isSummaryTask")]
    public bool? IsSummaryTask { get; set; }
    
    [JsonPropertyName("itemNumber")]
    public int? ItemNumber { get; set; }
    
    [JsonPropertyName("summaryTask_Id")]
    public string? SummaryTaskId { get; set; }
    
    [JsonPropertyName("urlFull")]
    public string? UrlFull { get; set; }

    [JsonPropertyName("isTaskCaptureTimeBillableDefaultCheckedOn")]
    public bool? IsTaskCaptureTimeBillableDefaultCheckedOn { get; set; }

    [JsonPropertyName("isTaskCaptureTimeBillableResourceAllowedToToggle")]
    public bool? IsTaskCaptureTimeBillableResourceAllowedToToggle { get; set; }
}

/// <summary>
/// Response from task create/update operations
/// </summary>
public class TaskSaveResult
{
    [JsonPropertyName("savedId")]
    public string? SavedId { get; set; }
    
    [JsonPropertyName("savedIds")]
    public List<string>? SavedIds { get; set; }
    
    [JsonPropertyName("savedCount")]
    public int SavedCount { get; set; }
    
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("errors")]
    public List<ApiError>? Errors { get; set; }

    [JsonPropertyName("hasErrors")]
    public bool HasErrors { get; set; }

    [JsonPropertyName("hasWarnings")]
    public bool HasWarnings { get; set; }

    [JsonPropertyName("toastMessage")]
    public string? ToastMessage { get; set; }
}

/// <summary>
/// Error object returned by the Project Insight API
/// </summary>
public class ApiError
{
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("apiErrorType")]
    public int? ApiErrorType { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("property")]
    public string? Property { get; set; }

    [JsonPropertyName("additionalInfo")]
    public string? AdditionalInfo { get; set; }
}

/// <summary>
/// User model for Project Insight
/// </summary>
public class User
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }
    
    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }
    
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }
    
    [JsonPropertyName("email")]
    public string? Email { get; set; }
    
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
    
    [JsonPropertyName("mobilePhone")]
    public string? MobilePhone { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
    
    [JsonPropertyName("department_Id")]
    public string? DepartmentId { get; set; }
    
    [JsonPropertyName("userType_Id")]
    public string? UserTypeId { get; set; }
    
    [JsonPropertyName("companyDefault_Id")]
    public string? CompanyDefaultId { get; set; }
    
    [JsonPropertyName("customFieldValue_Id")]
    public string? CustomFieldValueId { get; set; }
    
    [JsonPropertyName("urlFull")]
    public string? UrlFull { get; set; }
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

/// <summary>
/// TimeSheet model for Project Insight
/// </summary>
public class TimeSheet
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("user_Id")]
    public string? UserId { get; set; }
    
    [JsonPropertyName("userApprover_Id")]
    public string? UserApproverId { get; set; }
    
    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }
    
    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }
    
    [JsonPropertyName("totalHours")]
    public decimal? TotalHours { get; set; }
    
    [JsonPropertyName("billableHours")]
    public decimal? BillableHours { get; set; }
    
    [JsonPropertyName("nonBillableHours")]
    public decimal? NonBillableHours { get; set; }
    
    [JsonPropertyName("isSubmitted")]
    public bool? IsSubmitted { get; set; }
    
    [JsonPropertyName("isApproved")]
    public bool? IsApproved { get; set; }
    
    [JsonPropertyName("isRejected")]
    public bool? IsRejected { get; set; }
    
    [JsonPropertyName("isLocked")]
    public bool? IsLocked { get; set; }
    
    [JsonPropertyName("isPending")]
    public bool? IsPending { get; set; }
    
    [JsonPropertyName("submittedDateTimeUTC")]
    public DateTime? SubmittedDateTimeUTC { get; set; }
    
    [JsonPropertyName("approvedDateTimeUTC")]
    public DateTime? ApprovedDateTimeUTC { get; set; }
    
    [JsonPropertyName("rejectedDateTimeUTC")]
    public DateTime? RejectedDateTimeUTC { get; set; }
    
    [JsonPropertyName("approvalStatus")]
    public int? ApprovalStatus { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("urlFull")]
    public string? UrlFull { get; set; }
    
    [JsonPropertyName("createdDateTimeUTC")]
    public DateTime? CreatedDateTimeUTC { get; set; }
    
    [JsonPropertyName("updatedDateTimeUTC")]
    public DateTime? UpdatedDateTimeUTC { get; set; }
}

/// <summary>
/// TimeSheet search request model
/// </summary>
public class TimeSheetSearchRequest
{
    public string? UserGuidList { get; set; }
    public string? UserApproverGuidList { get; set; }
    public string? UserPMApproverGuidList { get; set; }
    public string? UserTypeGuidList { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool? ExcludeUnsubmitted { get; set; }
    public bool? ExcludeApproved { get; set; }
    public bool? ExcludeRejected { get; set; }
    public bool? ExcludePendingApproval { get; set; }
    public bool? ExcludePreviouslyDownloaded { get; set; }
    public string? ModelProperties { get; set; }
}

/// <summary>
/// MyTimeCurrentStatus response model
/// </summary>
public class MyTimeCurrentStatus
{
    [JsonPropertyName("currentTimeSheet_Id")]
    public string? CurrentTimeSheetId { get; set; }
    
    [JsonPropertyName("currentTimeSheetName")]
    public string? CurrentTimeSheetName { get; set; }
    
    [JsonPropertyName("currentPeriodStartDate")]
    public DateTime? CurrentPeriodStartDate { get; set; }
    
    [JsonPropertyName("currentPeriodEndDate")]
    public DateTime? CurrentPeriodEndDate { get; set; }
    
    [JsonPropertyName("hoursWorkedThisPeriod")]
    public decimal? HoursWorkedThisPeriod { get; set; }
    
    [JsonPropertyName("hoursWorkedToday")]
    public decimal? HoursWorkedToday { get; set; }
    
    [JsonPropertyName("user_Id")]
    public string? UserId { get; set; }
}

/// <summary>
/// TimeEntry model for Project Insight - represents individual time entries within a timesheet
/// </summary>
public class TimeEntry
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("Date")]
    public DateTime? Date { get; set; }
    
    [JsonPropertyName("Description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("ActualHours")]
    public decimal? ActualHours { get; set; }
    
    [JsonPropertyName("ActualHoursFormattedString")]
    public string? ActualHoursFormatted { get; set; }
    
    [JsonPropertyName("ActualTimeString")]
    public string? ActualTimeString { get; set; }
    
    [JsonPropertyName("ActualTotal")]
    public decimal? ActualTotal { get; set; }
    
    [JsonPropertyName("BillableHours")]
    public decimal? BillableHours { get; set; }
    
    [JsonPropertyName("BillableHoursFormattedString")]
    public string? BillableHoursFormatted { get; set; }
    
    [JsonPropertyName("BillableTimeString")]
    public string? BillableTimeString { get; set; }
    
    [JsonPropertyName("BillableTotal")]
    public decimal? BillableTotal { get; set; }
    
    [JsonPropertyName("IsBillable")]
    public bool? IsBillable { get; set; }
    
    [JsonPropertyName("RateBill")]
    public decimal? RateBill { get; set; }
    
    [JsonPropertyName("RateBurden")]
    public decimal? RateBurden { get; set; }
    
    [JsonPropertyName("Project_Id")]
    public string? ProjectId { get; set; }
    
    [JsonPropertyName("Task_Id")]
    public string? TaskId { get; set; }
    
    [JsonPropertyName("User_Id")]
    public string? UserId { get; set; }
    
    [JsonPropertyName("TimeSheet_Id")]
    public string? TimeSheetId { get; set; }
    
    [JsonPropertyName("ExpenseCode_Id")]
    public string? ExpenseCodeId { get; set; }
    
    [JsonPropertyName("Issue_Id")]
    public string? IssueId { get; set; }
    
    [JsonPropertyName("Sprint_Id")]
    public string? SprintId { get; set; }
}

/// <summary>
/// TimeEntry search request model
/// </summary>
public class TimeEntrySearchRequest
{
    public string? ProjectGuidList { get; set; }
    public string? TaskGuidList { get; set; }
    public string? UserGuidList { get; set; }
    public string? TimeSheetGuidList { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool? IsBillable { get; set; }
    public string? ModelProperties { get; set; }
}

/// <summary>Id and name of a related record, as PI returns it for nested modelProperties.</summary>
public class NamedRef
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

/// <summary>
/// A time entry read together with its task and project names. Separate from <see cref="TimeEntry"/>
/// so existing tools' output is unchanged.
/// </summary>
public class TimeEntryWithNames : TimeEntry
{
    public const string ModelProperties =
        "Id,Date,ActualHours,IsBillable,Description,Task_Id,Project_Id,User_Id,TimeSheet_Id,Task,Project;Task:Id,Name;Project:Id,Name";

    [JsonPropertyName("Task")]
    public NamedRef? TaskRef { get; set; }

    [JsonPropertyName("Project")]
    public NamedRef? ProjectRef { get; set; }
}

/// <summary>
/// ProjectResource model - represents a user assigned to a project
/// </summary>
public class ProjectResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("project_Id")]
    public string? ProjectId { get; set; }
    
    [JsonPropertyName("user_Id")]
    public string? UserId { get; set; }
    
    [JsonPropertyName("user")]
    public User? User { get; set; }
    
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
    
    [JsonPropertyName("isProjectManager")]
    public bool? IsProjectManager { get; set; }
    
    [JsonPropertyName("isPrimaryProjectManager")]
    public bool? IsPrimaryProjectManager { get; set; }
    
    [JsonPropertyName("isProjectScheduler")]
    public bool? IsProjectScheduler { get; set; }
    
    [JsonPropertyName("percentAllocationDefault")]
    public decimal? PercentAllocationDefault { get; set; }
    
    [JsonPropertyName("projectPermissionLevel")]
    public int? ProjectPermissionLevel { get; set; }
    
    [JsonPropertyName("projectResourceRoleDescription")]
    public string? ProjectResourceRoleDescription { get; set; }
    
    [JsonPropertyName("actualHours")]
    public decimal? ActualHours { get; set; }
    
    [JsonPropertyName("workHours")]
    public decimal? WorkHours { get; set; }
    
    [JsonPropertyName("taskCount")]
    public int? TaskCount { get; set; }
    
    [JsonPropertyName("taskCountCompleted")]
    public int? TaskCountCompleted { get; set; }
    
    [JsonPropertyName("canViewFinancialData")]
    public bool? CanViewFinancialData { get; set; }
    
    [JsonPropertyName("canViewTimeEntryDetails")]
    public bool? CanViewTimeEntryDetails { get; set; }
    
    [JsonPropertyName("canViewExpenseEntryDetails")]
    public bool? CanViewExpenseEntryDetails { get; set; }
    
    [JsonPropertyName("resourceTypeDefault_Id")]
    public string? ResourceTypeDefaultId { get; set; }
    
    [JsonPropertyName("resourceTypeEffective_Id")]
    public string? ResourceTypeEffectiveId { get; set; }
    
    [JsonPropertyName("rateBill")]
    public decimal? RateBill { get; set; }
    
    [JsonPropertyName("rateBurden")]
    public decimal? RateBurden { get; set; }
}

/// <summary>
/// Resource Type model - categorizes resources (e.g., Senior Consultant, Project Manager)
/// </summary>
public class ResourceType
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}

/// <summary>
/// Company Contract (Rate Card) model - represents billing rate cards attached to projects
/// </summary>
public class CompanyContract
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("number")]
    public string? Number { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }

    [JsonPropertyName("company_Id")]
    public string? CompanyId { get; set; }

    [JsonPropertyName("contractStart")]
    public DateTime? ContractStart { get; set; }

    [JsonPropertyName("contractEnd")]
    public DateTime? ContractEnd { get; set; }

    [JsonPropertyName("rateBillDefault")]
    public decimal? RateBillDefault { get; set; }

    [JsonPropertyName("rateBurdenDefault")]
    public decimal? RateBurdenDefault { get; set; }

    [JsonPropertyName("rateCardRateContainer")]
    public RateCardRateContainer? RateCardRateContainer { get; set; }
}

/// <summary>
/// Container for the various rate tiers on a rate card
/// </summary>
public class RateCardRateContainer
{
    [JsonPropertyName("userRates")]
    public List<RateCardRate>? UserRates { get; set; }

    [JsonPropertyName("resourceTypeRates")]
    public List<RateCardRate>? ResourceTypeRates { get; set; }

    [JsonPropertyName("taskTypeRates")]
    public List<RateCardRate>? TaskTypeRates { get; set; }

    [JsonPropertyName("timeCodeRates")]
    public List<RateCardRate>? TimeCodeRates { get; set; }
}

/// <summary>
/// Individual rate entry within a rate card tier
/// </summary>
public class RateCardRate
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }

    [JsonPropertyName("rateBillableDefault")]
    public decimal? RateBillableDefault { get; set; }

    [JsonPropertyName("rateDefault")]
    public decimal? RateDefault { get; set; }

    [JsonPropertyName("rateInvoiceDefault")]
    public decimal? RateInvoiceDefault { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime? EffectiveDate { get; set; }

    [JsonPropertyName("expirationDate")]
    public DateTime? ExpirationDate { get; set; }
}

/// <summary>
/// TaskResource model - represents a user assigned to a task
/// </summary>
public class TaskResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("task_Id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("user_Id")]
    public string? UserId { get; set; }

    [JsonPropertyName("user")]
    public User? User { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }

    [JsonPropertyName("workHours")]
    public decimal? WorkHours { get; set; }

    [JsonPropertyName("percentAllocationDefault")]
    public decimal? PercentAllocationDefault { get; set; }
}

/// <summary>
/// User PTO (Paid Time Off) entry.
///
/// HOW PTO AND TIME ENTRIES WORK TOGETHER:
/// - Creating a PTO entry via POST /api/user-pto/pto-calendar automatically generates
///   a corresponding time entry (non-billable, no project/task). The PTO entry's
///   TimeEntry_Id links to this auto-generated time entry.
/// - Deleting a PTO entry automatically removes its linked time entry.
/// - PTO entries appear in time entry search results as entries with no projectId/taskId.
/// - The PTO API is the single source of truth — never create/delete PTO time entries directly.
///
/// ENTRY TYPES:
/// - Usage entries: PTOHours is set, PTOAdded is null — represents actual time off taken.
/// - Balance entries: PTOHours is null, PTOAdded is set — represents accruals or initial balances.
///
/// TIME CODES (PTO categories):
/// Time codes categorize PTO entries (e.g. Vacation, Personal Day, Floating Holiday).
/// They are instance-specific and discoverable by requesting modelProperties=TimeCode
/// on any PTO entry or list, which expands the full TimeCode object (Id, Name, IsActive).
/// Use list_user_pto to discover available time codes for a tenant.
/// </summary>
public class UserPto
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("User_Id")]
    public string? UserId { get; set; }

    [JsonPropertyName("Date")]
    public DateTime? Date { get; set; }

    [JsonPropertyName("PTOHours")]
    public decimal? PTOHours { get; set; }

    [JsonPropertyName("PTOAdded")]
    public decimal? PTOAdded { get; set; }

    [JsonPropertyName("PTOBalance")]
    public decimal? PTOBalance { get; set; }

    [JsonPropertyName("PTOBalanceFloatingHoliday")]
    public decimal? PTOBalanceFloatingHoliday { get; set; }

    [JsonPropertyName("PTOBalanceGeneral")]
    public decimal? PTOBalanceGeneral { get; set; }

    [JsonPropertyName("PTOBalancePersonalDay")]
    public decimal? PTOBalancePersonalDay { get; set; }

    [JsonPropertyName("TimeCode_Id")]
    public string? TimeCodeId { get; set; }

    [JsonPropertyName("TimeCode")]
    public TimeCode? TimeCode { get; set; }

    [JsonPropertyName("TimeEntry_Id")]
    public string? TimeEntryId { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("DateApproved")]
    public DateTime? DateApproved { get; set; }

    [JsonPropertyName("UserApproved_Id")]
    public string? UserApprovedId { get; set; }

    [JsonPropertyName("CreatedDateTimeUTC")]
    public DateTime? CreatedDateTimeUTC { get; set; }

    [JsonPropertyName("UpdatedDateTimeUTC")]
    public DateTime? UpdatedDateTimeUTC { get; set; }
}

/// <summary>
/// Time code — categorizes PTO entries (e.g. Vacation, Personal Day, Floating Holiday).
/// Discoverable via modelProperties=TimeCode on PTO entries.
/// </summary>
public class TimeCode
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("IsActive")]
    public bool? IsActive { get; set; }

    [JsonPropertyName("Code")]
    public string? Code { get; set; }

    [JsonPropertyName("IsTimeEntryCode")]
    public bool? IsTimeEntryCode { get; set; }

    [JsonPropertyName("IsExpenseEntryCode")]
    public bool? IsExpenseEntryCode { get; set; }
}