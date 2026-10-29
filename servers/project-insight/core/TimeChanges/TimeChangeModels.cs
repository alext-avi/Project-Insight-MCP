using System.ComponentModel;

namespace ProjectInsightMCP.TimeChanges;

public static class DraftStatus
{
    public const string Draft = "draft";
    public const string Committing = "committing";
    public const string Committed = "committed";
    public const string Stale = "stale";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public static class ChangeAction
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
}

public static class RowOutcome
{
    public const string Applied = "applied";
    public const string Reversed = "reversed";
    public const string Rejected = "rejected";
    public const string NotAttempted = "not_attempted";
    public const string ReverseFailed = "reverse_failed";
    /// <summary>PI didn't answer the write (timeout, dropped connection), so it may have been applied.</summary>
    public const string Unknown = "unknown";
}

public static class CommitChannel
{
    public const string Agent = "agent";
    public const string ConfirmedInChat = "confirmed_in_chat";
}

public static class WarningCode
{
    public const string TimesheetSubmitted = "timesheet_submitted";
    public const string TimesheetApproved = "timesheet_approved";
    public const string TimesheetLocked = "timesheet_locked";
    public const string Over12Hours = "over_12h";
    public const string NotQuarterHour = "not_quarter_hour";
    public const string LikelyDuplicate = "likely_duplicate";
    public const string OtherPerson = "other_person";
}

/// <summary>One requested change, as the agent sends it to plan_time_changes.</summary>
public sealed class ChangeInput
{
    [Description("create, update or delete.")]
    public string Action { get; set; } = "";

    [Description("update/delete: the time entry id (GUID).")]
    public string? TimeEntryId { get; set; }

    [Description("create: required. update: new date. Format yyyy-MM-dd.")]
    public string? Date { get; set; }

    [Description("create: required. update: new hours. Use 0.25 increments.")]
    public decimal? Hours { get; set; }

    [Description("Task id (GUID). Or give project and task names instead.")]
    public string? TaskId { get; set; }

    [Description("Project name, used with task when taskId is not known.")]
    public string? Project { get; set; }

    [Description("Task name within project, used when taskId is not known.")]
    public string? Task { get; set; }

    [Description("Work description. update: an empty string clears it.")]
    public string? Description { get; set; }

    [Description("Whether the time is billable. create: defaults to false.")]
    public bool? Billable { get; set; }

    [Description("create only: whose time (user id or name). Defaults to the signed-in user.")]
    public string? User { get; set; }
}

/// <summary>A time entry's reviewable fields. Hours only: never rates or monetary totals.</summary>
public sealed class EntryValues
{
    public required string TaskId { get; set; }
    public required string TaskName { get; set; }
    public required string ProjectId { get; set; }
    public required string ProjectName { get; set; }
    public required DateOnly Date { get; set; }
    public required decimal Hours { get; set; }
    public bool Billable { get; set; }
    public string? Description { get; set; }

    public EntryValues Clone() => (EntryValues)MemberwiseClone();
}

public sealed class DraftWarning
{
    public required string Code { get; set; }
    public required string Message { get; set; }
}

public sealed class DraftRow
{
    public required int Index { get; set; }
    public required string Action { get; set; }
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public string? TimeEntryId { get; set; }
    /// <summary>Id PI gave a created entry, or the new id of a deleted entry recreated during rollback.</summary>
    public string? SavedId { get; set; }
    public EntryValues? Before { get; set; }
    public EntryValues? After { get; set; }
    public List<DraftWarning> Warnings { get; set; } = new();
    public string? Outcome { get; set; }
    public string? Error { get; set; }
}

/// <summary>An existing entry the draft doesn't touch, shown greyed for context.</summary>
public sealed class ContextEntry
{
    public required string TimeEntryId { get; set; }
    public required string ProjectName { get; set; }
    public required string TaskName { get; set; }
    public required decimal Hours { get; set; }
    public bool Billable { get; set; }
    public string? Description { get; set; }
}

public sealed class DayView
{
    public required DateOnly Date { get; set; }
    public decimal TotalBefore { get; set; }
    public decimal TotalAfter { get; set; }
    public bool HasChanges { get; set; }
    public List<int> RowIndexes { get; set; } = new();
    public List<ContextEntry> Context { get; set; } = new();
}

public sealed class WeekView
{
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public bool IsCaller { get; set; }
    public required DateOnly Start { get; set; }
    public required DateOnly End { get; set; }
    public string? TimesheetId { get; set; }
    public string? TimesheetUrl { get; set; }
    public bool Submitted { get; set; }
    public bool Approved { get; set; }
    public bool Locked { get; set; }
    public decimal TotalBefore { get; set; }
    public decimal TotalAfter { get; set; }
    public List<DayView> Days { get; set; } = new();
}

public sealed class DraftTotals
{
    public decimal NetHours { get; set; }
    public decimal BillableDelta { get; set; }
    public decimal NonBillableDelta { get; set; }
    public int Changes { get; set; }
    public int Days { get; set; }
    public int People { get; set; }
    public int Warnings { get; set; }
}

/// <summary>A draft, and after commit its receipt (same record and id).</summary>
public sealed class Draft
{
    public required string Id { get; set; }
    public required string Status { get; set; }
    public required string Intent { get; set; }
    public required string ProposedBy { get; set; }
    public required string ProposedByName { get; set; }
    public required DateTimeOffset ProposedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
    /// <summary>Set by the store on every write; drives pruning.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
    public string? Channel { get; set; }
    public string? UndoOf { get; set; }
    /// <summary>When this draft is an undo, the commit time of the receipt it reverses.</summary>
    public DateTimeOffset? UndoOfCommittedAt { get; set; }
    public string? RevertedBy { get; set; }
    public required string ConfirmTokenHash { get; set; }
    public bool ConfirmTokenUsed { get; set; }
    public List<DraftRow> Rows { get; set; } = new();
    public List<WeekView> Weeks { get; set; } = new();
    /// <summary>Key: <see cref="Weeks.DayKey"/>. Only days the draft changes.</summary>
    public Dictionary<string, string> DayFingerprints { get; set; } = new();
    public DraftTotals Totals { get; set; } = new();
    public string? Error { get; set; }
    /// <summary>
    /// True when a failed commit may have left changes in PI that weren't reversed: a write PI didn't
    /// answer, or a commit interrupted midway.
    /// </summary>
    public bool OutcomeUnknown { get; set; }
    public List<string> StaleDays { get; set; } = new();
}

/// <summary>Why a change couldn't be planned, with candidates when a name matched several records.</summary>
public sealed class Clarification
{
    public required int ChangeIndex { get; set; }
    public required string Field { get; set; }
    public required string Query { get; set; }
    public required string Message { get; set; }
    public List<Candidate> Candidates { get; set; } = new();
}

public sealed class Candidate
{
    public required string Id { get; set; }
    public required string Name { get; set; }
}
