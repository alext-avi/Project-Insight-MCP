using ProjectInsightMCP.Models;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.TimeChanges;

public sealed record PiUser(string Id, string Name);
public sealed record PiNamed(string Id, string Name);
public sealed record PiTask(string Id, string Name, string ProjectId, string ProjectName, string? ProjectColor = null);
public sealed record PiEntry(string Id, string UserId, DateOnly Date, decimal Hours, bool Billable, string? Description,
    string TaskId, string TaskName, string ProjectId, string ProjectName, string? TimeSheetId, string? ProjectColor = null);
/// <summary>How PI draws a user: initials on their colours, or their photo (a site-relative path) when they have one.</summary>
public sealed record PiLook(string? Initials, string? Background, string? Foreground, string? PhotoUrl);
public sealed record PiTimeSheet(string Id, DateOnly Start, DateOnly End, bool Submitted, bool Approved, bool Locked, string? Url);
public sealed record EntryWrite(string UserId, string TaskId, DateOnly Date, decimal Hours, bool Billable, string? Description, string? TimeSheetId);
/// <summary>
/// A write's result. <paramref name="Uncertain"/> is true when PI gave no answer (timeout, dropped
/// connection), so the write may or may not have been applied.
/// </summary>
public sealed record PiWrite(bool Success, string? SavedId, string? Error, bool Uncertain = false);

/// <summary>The Project Insight operations time-change drafts need, and nothing else.</summary>
public interface ITimeEntryGateway
{
    Task<PiUser> GetCurrentUserAsync(CancellationToken ct);
    Task<PiUser?> GetUserAsync(string userId, CancellationToken ct);
    Task<IReadOnlyList<PiUser>> FindUsersAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<PiNamed>> ListOpenProjectsAsync(CancellationToken ct);
    Task<IReadOnlyList<PiNamed>> ListTasksAsync(string projectId, CancellationToken ct);
    Task<PiTask?> GetTaskAsync(string taskId, CancellationToken ct);
    Task<PiEntry?> GetEntryAsync(string timeEntryId, CancellationToken ct);
    Task<IReadOnlyList<PiEntry>> SearchEntriesAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct);
    Task<IReadOnlyList<PiTimeSheet>> SearchTimeSheetsAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct);
    Task<PiWrite> CreateEntryAsync(EntryWrite entry, CancellationToken ct);
    Task<PiWrite> UpdateEntryAsync(string timeEntryId, EntryWrite values, CancellationToken ct);
    Task<PiWrite> DeleteEntryAsync(string timeEntryId, CancellationToken ct);
    /// <summary>Avatars for the card, keyed by user id. Users PI doesn't return are left out.</summary>
    Task<IReadOnlyDictionary<string, PiLook>> GetUserLooksAsync(IReadOnlyCollection<string> userIds, CancellationToken ct);
    /// <summary>A user photo as a data: URI the card can show, or null.</summary>
    Task<string?> GetPhotoAsync(string photoUrl, CancellationToken ct);
}

public sealed class ProjectInsightTimeEntryGateway(ProjectInsightApiService api, ApiTokenAccessor token) : ITimeEntryGateway
{
    private readonly object _lock = new();
    private (string? Token, PiUser User)? _caller;

    public async Task<PiUser> GetCurrentUserAsync(CancellationToken ct)
    {
        if (!token.HasToken)
            // The stdio host starts sign-in before any time-change tool runs; this is the fallback.
            throw new TimeChangeException("not_signed_in",
                "You're not signed in to Project Insight. Call any other Project Insight tool to start sign-in, then try again.");
        var current = token.ApiToken;
        lock (_lock)
            if (_caller is { } cached && cached.Token == current)
                return cached.User;

        var response = await api.GetCurrentUserAsync("Id,Name", ct);
        if (!response.Success || response.Data?.Id is null)
            throw new TimeChangeException("not_signed_in",
                "Could not identify the signed-in Project Insight user. " + Describe(response.Message, response.Errors));

        var user = new PiUser(response.Data.Id, response.Data.Name ?? response.Data.Id);
        lock (_lock)
            _caller = (current, user);
        return user;
    }

    public async Task<PiUser?> GetUserAsync(string userId, CancellationToken ct)
    {
        var response = await api.GetUserAsync(userId, "Id,Name", ct);
        if (!response.Success)
            return MissingOrThrow<PiUser>(response.Message, response.Errors, "read the user");
        return response.Data?.Id is { } id ? new PiUser(id, response.Data.Name ?? id) : null;
    }

    public async Task<IReadOnlyList<PiUser>> FindUsersAsync(string name, CancellationToken ct)
    {
        var response = await api.ListAllUsersAsync(isActive: true, nameFilter: name, modelProperties: "Id,Name,FirstName,LastName,IsActive", cancellationToken: ct);
        if (!response.Success)
            throw Unavailable("find users", response.Message, response.Errors);
        return (response.Data ?? new()).Where(u => u.Id is not null)
            .Select(u => new PiUser(u.Id!, u.Name ?? u.Id!)).ToList();
    }

    public async Task<IReadOnlyList<PiNamed>> ListOpenProjectsAsync(CancellationToken ct)
    {
        var response = await api.SearchProjectsAsync(
            new ProjectSearchRequest { IsActive = true, IsOpen = true, ModelProperties = "Id,Name" }, ct);
        if (!response.Success)
            throw Unavailable("list projects", response.Message, response.Errors);
        return (response.Data ?? new()).Where(p => p.Id is not null)
            .Select(p => new PiNamed(p.Id!, p.Name ?? p.Id!)).ToList();
    }

    public async Task<IReadOnlyList<PiNamed>> ListTasksAsync(string projectId, CancellationToken ct)
    {
        var response = await api.GetProjectTasksAsync(projectId, "Id,Name", ct);
        if (!response.Success)
            throw Unavailable("list tasks", response.Message, response.Errors);
        return (response.Data ?? new()).Where(t => t.Id is not null)
            .Select(t => new PiNamed(t.Id!, t.Name ?? t.Id!)).ToList();
    }

    public async Task<PiTask?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        var task = await api.GetTaskAsync(taskId, "Id,Name,Project_Id", ct);
        if (!task.Success)
            return MissingOrThrow<PiTask>(task.Message, task.Errors, "read the task");
        if (task.Data?.Id is null || task.Data.ProjectId is null)
            return null;
        var project = await api.GetProjectAsync(task.Data.ProjectId, "Id,Name,Color", ct);
        if (!project.Success && !IsNotFound(project.Message, project.Errors))
            throw Unavailable("read the task's project", project.Message, project.Errors);
        return new PiTask(task.Data.Id, task.Data.Name ?? task.Data.Id, task.Data.ProjectId,
            project.Data?.Name ?? task.Data.ProjectId, Colour(project.Data?.Color));
    }

    public async Task<PiEntry?> GetEntryAsync(string timeEntryId, CancellationToken ct)
    {
        var response = await api.GetTimeEntryAsync(timeEntryId, ct);
        if (!response.Success)
            return MissingOrThrow<PiEntry>(response.Message, response.Errors, "read the time entry");
        return response.Data is { } e ? ToEntry(e) : null;
    }

    public async Task<IReadOnlyList<PiEntry>> SearchEntriesAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var response = await api.SearchTimeEntriesWithNamesAsync(
            userId, start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue), ct);
        if (!response.Success)
            throw new TimeChangeException("pi_unavailable",
                "Could not read time entries from Project Insight. " + Describe(response.Message, response.Errors));
        return (response.Data ?? new()).Select(ToEntry).OfType<PiEntry>().ToList();
    }

    public async Task<IReadOnlyList<PiTimeSheet>> SearchTimeSheetsAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var response = await api.SearchTimeSheetsAsync(new TimeSheetSearchRequest
        {
            UserGuidList = userId,
            StartDate = start.ToDateTime(TimeOnly.MinValue),
            EndDate = end.ToDateTime(TimeOnly.MinValue),
            ModelProperties = "Id,StartDate,EndDate,IsSubmitted,IsApproved,IsLocked,UrlFull"
        }, ct);
        if (!response.Success)
            throw Unavailable("read timesheets", response.Message, response.Errors);
        return (response.Data ?? new())
            .Where(s => s.Id is not null && s.StartDate.HasValue && s.EndDate.HasValue)
            .Select(s => new PiTimeSheet(s.Id!, DateOnly.FromDateTime(s.StartDate!.Value), DateOnly.FromDateTime(s.EndDate!.Value),
                s.IsSubmitted == true, s.IsApproved == true, s.IsLocked == true, s.UrlFull))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, PiLook>> GetUserLooksAsync(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return new Dictionary<string, PiLook>();
        var response = await api.GetUsersAsync(string.Join(",", userIds), "Id,Initials,ColorSettings,PhotoUrl", ct);
        if (!response.Success)
            throw Unavailable("read users' avatars", response.Message, response.Errors);
        return (response.Data ?? new()).Where(u => u.Id is not null).ToDictionary(u => u.Id!, u => new PiLook(
            string.IsNullOrWhiteSpace(u.Initials) ? null : u.Initials.Trim(),
            Colour(u.ColorSettings?.BackgroundColor), Colour(u.ColorSettings?.FontColor),
            string.IsNullOrWhiteSpace(u.PhotoUrl) ? null : u.PhotoUrl.Trim()));
    }

    /// <summary>Largest photo embedded in a draft. PI's are a few KB.</summary>
    public const int MaxPhotoBytes = 200 * 1024;

    public async Task<string?> GetPhotoAsync(string photoUrl, CancellationToken ct)
        => await api.GetSiteImageAsync(photoUrl, MaxPhotoBytes, ct) is { } image
            ? $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}"
            : null;

    /// <summary>A PI colour as "#rrggbb", or null for anything else (the card puts it in a style).</summary>
    public static string? Colour(string? value)
        => value is { } v && System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
            ? v.Trim().ToLowerInvariant()
            : null;

    public Task<PiWrite> CreateEntryAsync(EntryWrite entry, CancellationToken ct) => SaveAsync(null, entry, ct);

    public Task<PiWrite> UpdateEntryAsync(string timeEntryId, EntryWrite values, CancellationToken ct) => SaveAsync(timeEntryId, values, ct);

    public async Task<PiWrite> DeleteEntryAsync(string timeEntryId, CancellationToken ct)
    {
        var response = await api.DeleteTimeEntryAsync(timeEntryId, ct);
        return response.Success ? new PiWrite(true, null, null) : Failed(response.Message, response.Errors);
    }

    private async Task<PiWrite> SaveAsync(string? id, EntryWrite values, CancellationToken ct)
    {
        var entry = new TimeEntry
        {
            Id = id,
            UserId = values.UserId,
            TaskId = values.TaskId,
            Date = values.Date.ToDateTime(TimeOnly.MinValue),
            ActualHours = values.Hours,
            IsBillable = values.Billable,
            // Null is omitted on the wire, which PI reads as "unchanged"; an update that clears
            // the description has to send it empty.
            Description = id is null ? values.Description : values.Description ?? "",
            TimeSheetId = values.TimeSheetId
        };
        var response = await api.SaveTimeEntryAsync(entry, ct);
        return response.Success
            ? new PiWrite(true, response.Data?.SavedId ?? id, null)
            : Failed(response.Message, response.Errors);
    }

    // The API service turns any exception (timeout, dropped connection) into this message. Then PI gave
    // no answer, so the write may have been applied; "API returned <status>" or a success:false body is
    // a definite refusal.
    private const string NoAnswer = "Internal error occurred";

    private static PiWrite Failed(string? message, List<string>? errors)
        => new(false, null, Describe(message, errors), Uncertain: message == NoAnswer);

    private static PiEntry? ToEntry(TimeEntryWithNames e)
    {
        if (e.Id is null || e.UserId is null || e.Date is null || e.TaskId is null)
            return null;
        return new PiEntry(e.Id, e.UserId, DateOnly.FromDateTime(e.Date.Value), e.ActualHours ?? 0m, e.IsBillable == true,
            e.Description, e.TaskId, e.TaskRef?.Name ?? e.TaskId, e.ProjectId ?? e.ProjectRef?.Id ?? "",
            e.ProjectRef?.Name ?? e.ProjectId ?? "", e.TimeSheetId, Colour(e.ProjectRef?.Color));
    }

    private static TimeChangeException Unavailable(string what, string? message, List<string>? errors)
        => new("pi_unavailable", $"Could not {what} in Project Insight. " + Describe(message, errors));

    // The API service reports failures as "API returned NotFound" / "API call failed: BadRequest".
    // PI answering 404 (gone) or 400 (rejected id) means "no such record"; anything else is an outage.
    private static bool IsNotFound(string? message, List<string>? errors)
        => message is not null && (message.Contains("NotFound") || message.Contains("BadRequest"));

    private static T? MissingOrThrow<T>(string? message, List<string>? errors, string what) where T : class
        => IsNotFound(message, errors) ? null : throw Unavailable(what, message, errors);

    /// <summary>Longest PI error text passed on; PI can answer with a whole HTML error page.</summary>
    public const int MaxErrorLength = 300;

    private static string Describe(string? message, List<string>? errors)
        => Clip(string.Join(" ", new[] { message }.Concat(errors ?? new()).Where(s => !string.IsNullOrWhiteSpace(s))));

    public static string Clip(string text)
        => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength].TrimEnd() + "…";
}
