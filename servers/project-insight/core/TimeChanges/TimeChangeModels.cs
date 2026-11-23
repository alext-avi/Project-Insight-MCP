using System.ComponentModel;
using System.Text.Json.Serialization;

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
    public const string CreatePto = "create_pto";
    public const string UpdatePto = "update_pto";
    public const string DeletePto = "delete_pto";

    /// <summary>The one place that says whether an action works on a PTO entry rather than a time entry.</summary>
    public static bool IsPto(string? action) => action is CreatePto or UpdatePto or DeletePto;
}

/// <summary>What a draft row changes. A row stored before PTO existed has no kind, which means time.</summary>
public static class RowKind
{
    public const string Time = "time";
    public const string Pto = "pto";
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
    /// <summary>More than a month back or a week ahead: likely a wrong date or year.</summary>
    public const string FarDate = "far_date";
    /// <summary>A 7-day span of a person's timesheet period would total more than 40 hours (work and PTO together).</summary>
    public const string Over40HoursWeek = "over_40h_week";
    /// <summary>A day with PTO would total more than 8 hours (work and PTO together).</summary>
    public const string Over8HoursPtoDay = "over_8h_day";
    /// <summary>The PTO time code id isn't one this person has used.</summary>
    public const string UnknownTimeCode = "unknown_time_code";
    /// <summary>The PTO being changed or deleted was already approved.</summary>
    public const string PtoApproved = "pto_approved";
}

/// <summary>One requested change, as the agent sends it to plan_time_changes.</summary>
public sealed class ChangeInput
{
    [Description("create, update or delete for time entries; create_pto, update_pto or delete_pto for PTO.")]
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

    [Description("create/create_pto only: whose time (user id or name). Defaults to the signed-in user.")]
    public string? User { get; set; }

    [Description("update_pto/delete_pto: the PTO entry id (not the time entry id it creates).")]
    public string? PtoEntryId { get; set; }

    [Description("PTO only: the time code name, such as PTO. An exact name wins, else a unique partial match. Or give timeCodeId.")]
    public string? TimeCode { get; set; }

    [Description("PTO only: the time code id. Use instead of timeCode when known.")]
    public string? TimeCodeId { get; set; }
}

/// <summary>A time entry's reviewable fields. Hours only: never rates or monetary totals.</summary>
public sealed class EntryValues
{
    public required string TaskId { get; set; }
    public required string TaskName { get; set; }
    public required string ProjectId { get; set; }
    public required string ProjectName { get; set; }
    /// <summary>The project's colour in PI ("#rrggbb"), for the card. Not part of the entry's values.</summary>
    public string? ProjectColor { get; set; }
    public required DateOnly Date { get; set; }
    public required decimal Hours { get; set; }
    public bool Billable { get; set; }
    public string? Description { get; set; }
    /// <summary>PTO rows only; time rows leave these null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeCodeId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeCodeName { get; set; }

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
    /// <summary><see cref="RowKind"/>; null (drafts stored before PTO) means time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
    /// <summary>PTO rows: the PTO entry being updated or deleted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PtoEntryId { get; set; }
    /// <summary>PTO update/delete rows: the time entry PI keeps for that PTO, so the card can leave it out of the day's context.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedTimeEntryId { get; set; }
    /// <summary>True when the PTO being updated or deleted was already approved; otherwise null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PtoApproved { get; set; }
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
    public string? ProjectColor { get; set; }
    public required string TaskName { get; set; }
    public required decimal Hours { get; set; }
    public bool Billable { get; set; }
    public string? Description { get; set; }
    /// <summary>True for an existing PTO entry (shown as PTO · time code); null for time context.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPto { get; set; }
    /// <summary>PTO context only. Null when the person's PTO list couldn't be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeCodeName { get; set; }
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
    public UserAvatar? Avatar { get; set; }
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

/// <summary>How PI draws the person: their photo when they have one, else initials on their colours.</summary>
public sealed class UserAvatar
{
    public string? Initials { get; set; }
    public string? Background { get; set; }
    public string? Foreground { get; set; }
    /// <summary>A data: URI, so the card needs no network access to show it.</summary>
    public string? Photo { get; set; }
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
    /// <summary>Net hours the PTO rows add (or remove).</summary>
    public decimal PtoDelta { get; set; }
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
    /// <summary>
    /// Set when the undo in <see cref="RevertedBy"/> was itself reversed by this later commit (a redo),
    /// so this receipt's changes are in PI again.
    /// </summary>
    public string? RedoneBy { get; set; }
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
    /// <summary>Users whose PTO list couldn't be read at plan time (their draft has no PTO rows). Null when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PtoUnreadUsers { get; set; }
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
