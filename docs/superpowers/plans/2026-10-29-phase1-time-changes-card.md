# Phase 1: Reviewable Time-Entry Changes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agents draft time-entry changes, the user reviews them in an MCP Apps card in the context of each affected week (or as plain text), and confirms, cancels or undoes them. Commits are all-or-nothing and refused when PI has changed in the meantime.

**Architecture:** The domain logic lives in Core under `TimeChanges/` and has no SDK dependency, so the net8.0 Functions host keeps building:
- a narrow `ITimeEntryGateway` over `ProjectInsightApiService`
- `FileDraftStore`
- `TimeChangeResolver`, `TimeChangePlanner`, `TimeChangeCommitter`
- the `TimeChangeService` facade
- `TimeChangeText`
- the embedded card HTML

The stdio host registers six SDK-native tools and the card resource (`TimeChangeTools.cs`, using `ModelContextProtocol.Extensions.Apps`). It also moves the one-at-a-time gate into a call-tool filter, so it covers both tool styles. The existing 46 tools stay on the Phase 0 adapter. The SDK lists both sets and dispatches each call by name; this was verified in a spike.

**Tech Stack:** .NET 10 (stdio host, tests), Core `net8.0;net10.0`, ModelContextProtocol 2.3.0 and ModelContextProtocol.Extensions.Apps 2.3.0 (experimental, `MCPEXP003`), xUnit 2.9, vanilla HTML/CSS/JS for the card.

**Spec:** `docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md`

**One deviation from the spec's code layout:** the spec put `TimeChangeTools.cs` in Core. Here it lives in `src-stdio/`, because referencing the SDK from Core would bring Microsoft.Extensions 10.x packages into the in-process Functions host. Everything else the spec lists under `core/TimeChanges/` stays there. Task 9 updates the spec's layout section.

## Global Constraints

- The three existing tools `create_time_entry`, `update_time_entry` and `delete_time_entry` stay. Their only change is `annotations: {destructiveHint: true}`.
- Drafts, receipts, the card and text output hold hours, project, task, description and billable flag only. They must never contain `RateBill`, `RateBurden`, `BillableTotal` or `ActualTotal`.
- Drafts live at `~/.projectinsight/drafts/<piUserId>/<id>.json`, with file mode 0600 and directory mode 0700. Writes go to a temp file and are then renamed.
- A draft expires at `proposedAt + 24h`. Undo is available for 24h after commit. Records are pruned 7 days after their last change. A draft left in `committing` for more than 10 minutes is reported as `failed` with outcome unknown.
- At most 100 changes per draft.
- Only the proposer (the caller's PI user id) can read, commit, confirm, cancel or undo a draft. The store's owner directory enforces this: another user's draft is simply "not found".
- The confirm token is 128 random bits. It is returned only in the plan or undo result's `_meta.confirmToken`, stored as a SHA-256 hash, and is single-use.
- Card resource URI: `ui://project-insight/time-changes.html`, MIME `text/html;profile=mcp-app`. All six tools link to it. The card makes no network requests and builds the DOM with `createElement` and text nodes only; `innerHTML` is never used.
- Tool visibility:
  - `plan_time_changes`, `commit_time_changes`: model
  - `confirm_time_changes`: app
  - `cancel_time_changes`, `undo_time_changes`, `get_time_changes`: model and app
- Tool annotations: plan, undo and get are `readOnlyHint` (they write no PI data); commit and confirm are `destructiveHint`.
- Release version 2.1.0, in the stdio csproj and the mcpb manifest.
- No `Co-Authored-By` trailer in commits. Never commit secrets; the repo is public.

## Review Focus

1. **PI returns dates as date-times** (e.g. `"2026-10-27T00:00:00"`). An entry must land on that calendar day, with no time-zone shift. Pinned in Task 1 (gateway mapping test).
2. **Fractional hours from PI arrive as floating-point JSON** (`1.5`, `0.1`). Totals and quarter-hour checks must use `decimal`, so 0.1 + 0.2 is exactly 0.3 and 1.25 isn't flagged. Pinned in Task 5.
3. **Two changes naming the same `timeEntryId`** (e.g. an update and a delete). Expected: the plan is refused with a clear message, and no draft is created. Pinned in Task 5.
4. **A corrupt or hand-edited draft file, or an id containing path characters** (`../x`). Expected: "not found", with no crash and no access outside the owner's directory. Pinned in Task 3.
5. **An update that moves an entry to another day or week.** Both days get fingerprints, both weeks are shown, and day totals move correctly. Pinned in Task 5.

---

## File Structure

```
servers/project-insight/
  core/
    ProjectInsightMCP.Core.csproj                 + EmbeddedResource for the card
    Models/ProjectInsightModels.cs                + NamedRef, TimeEntryWithNames
    Services/ProjectInsightApiService.cs          + SearchTimeEntriesWithNamesAsync, GetTimeEntryAsync
    Services/McpServerService.cs                  + destructiveHint on 3 time-entry tools
    Shared/McpModels.cs                           + McpToolAnnotations, McpTool.Annotations
    TimeChanges/
      TimeChangeModels.cs        status/action constants, ChangeInput, Draft, rows, views
      TimeChangeException.cs     coded domain error
      TimeEntryGateway.cs        ITimeEntryGateway + Pi* records + ProjectInsightTimeEntryGateway
      Weeks.cs                   week/day helpers, DayKey, labels, covering timesheet
      Fingerprint.cs             day fingerprint hash
      DraftIds.cs                draft ids, confirm tokens, token hashing
      Drafts/IDraftStore.cs
      Drafts/FileDraftStore.cs
      TimeChangeResolver.cs      user / project / task resolution, NameMatch
      TimeChangePlanner.cs       builds drafts (and undo drafts); writes nothing to PI
      TimeChangeCommitter.cs     commit, stale check, apply + reverse, cancel, load/normalise
      TimeChangeService.cs       facade used by the tools; DraftView, TimeChangesView, TimeChangeResponse
      TimeChangeText.cs          model-facing text for every outcome
      Card/time-changes.html     the MCP Apps card (embedded resource)
      Card/TimeChangeCard.cs     loads the embedded HTML
  src-stdio/
    ProjectInsightMCP.Stdio.csproj                + ModelContextProtocol.Extensions.Apps, NoWarn MCPEXP003, Version 2.1.0
    ProjectInsightServer.cs                       register services, tools, resource, gate filter
    TimeChangeTools.cs                            six SDK tools + card resource
  tests/
    TimeChanges/FakeGateway.cs, ManualTimeProvider.cs, Drafts.cs (test builders)
    TimeChanges/TimeEntryGatewayTests.cs
    TimeChanges/FingerprintTests.cs
    TimeChanges/FileDraftStoreTests.cs
    TimeChanges/TimeChangeResolverTests.cs
    TimeChanges/TimeChangePlannerTests.cs
    TimeChanges/TimeChangeCommitterTests.cs
    TimeChanges/TimeChangeServiceTests.cs
    TimeChanges/TimeChangeCardTests.cs
    TimeChanges/JsonSchemaCheck.cs
    TimeChangeToolsTests.cs      over-the-wire contract tests (McpClient)
    Snapshots/tools-list.json    regenerated (destructiveHint)
    Snapshots/time-change-tools.json  new
```

All commands run from `servers/project-insight`. Tests run with
`dotnet test tests --filter "FullyQualifiedName~<Class>"`. `$SCRATCH` means the session scratchpad
directory: throwaway output goes there, never into the repo.

---

### Task 1: PI access for time changes (gateway)

**Files:**
- Modify: `core/Models/ProjectInsightModels.cs` (append after `TimeEntrySearchRequest`)
- Modify: `core/Services/ProjectInsightApiService.cs` (add two methods at the end of `#region Time Entry`, just before `#endregion` that precedes `#region User Operations`)
- Create: `core/TimeChanges/TimeChangeException.cs`
- Create: `core/TimeChanges/TimeEntryGateway.cs`
- Create: `tests/TimeChanges/TimeEntryGatewayTests.cs`

**Interfaces:**
- Produces:
  - `record PiUser(string Id, string Name)`
  - `record PiNamed(string Id, string Name)`
  - `record PiTask(string Id, string Name, string ProjectId, string ProjectName)`
  - `record PiEntry(string Id, string UserId, DateOnly Date, decimal Hours, bool Billable, string? Description, string TaskId, string TaskName, string ProjectId, string ProjectName, string? TimeSheetId)`
  - `record PiTimeSheet(string Id, DateOnly Start, DateOnly End, bool Submitted, bool Approved, bool Locked, string? Url)`
  - `record EntryWrite(string UserId, string TaskId, DateOnly Date, decimal Hours, bool Billable, string? Description, string? TimeSheetId)`
  - `record PiWrite(bool Success, string? SavedId, string? Error)`
  - `interface ITimeEntryGateway`, with the methods shown in Step 3
  - `class ProjectInsightTimeEntryGateway(ProjectInsightApiService api, ApiTokenAccessor token) : ITimeEntryGateway`
  - `class TimeChangeException(string code, string message) : Exception`, exposing `Code`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/TimeEntryGatewayTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.TimeChanges;
using Xunit;
using static ProjectInsightMCP.Tests.McpServerTestHarness;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class TimeEntryGatewayTests
{
    private static (ProjectInsightTimeEntryGateway gw, StubHttpMessageHandler pi) Create(params HttpResponseMessage[] responses)
    {
        var pi = new StubHttpMessageHandler(responses);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectInsight:BaseUrl"] = "https://pi.test" })
            .Build();
        var token = new ApiTokenAccessor { ApiToken = "t" };
        var api = new ProjectInsightApiService(new HttpClient(pi), NullLogger<ProjectInsightApiService>.Instance, config, token);
        return (new ProjectInsightTimeEntryGateway(api, token), pi);
    }

    private const string EntryJson = """
        {"Id":"te-1","Date":"2026-10-27T00:00:00","ActualHours":1.5,"IsBillable":true,"Description":"API work",
         "Task_Id":"t-1","Project_Id":"p-1","User_Id":"u-1","TimeSheet_Id":null,
         "Task":{"Id":"t-1","Name":"Development"},"Project":{"Id":"p-1","Name":"Acme Portal"},
         "RateBill":150.0,"BillableTotal":225.0}
        """;

    [Fact]
    public async Task Search_requests_names_and_maps_dates_without_shifting_days()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, $"[{EntryJson}]"));

        var entries = await gw.SearchEntriesAsync("u-1", new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), default);

        var e = Assert.Single(entries);
        Assert.Equal(new PiEntry("te-1", "u-1", new DateOnly(2026, 10, 27), 1.5m, true, "API work",
            "t-1", "Development", "p-1", "Acme Portal", null), e);
        var url = Uri.UnescapeDataString(Assert.Single(pi.Requests).Uri.PathAndQuery);
        Assert.StartsWith("/api/time-entry/search?", url);
        Assert.Contains("userList=u-1", url);
        Assert.Contains("startDate=2026-10-26", url);
        Assert.Contains("endDate=2026-11-01", url);
        Assert.Contains("Task;Task:Id,Name;Project:Id,Name", url);
        Assert.DoesNotContain("RateBill", url);
    }

    [Fact]
    public async Task Get_entry_reads_one_entry_by_id_and_returns_null_when_missing()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, EntryJson), Json(HttpStatusCode.NotFound, "{}"));

        var found = await gw.GetEntryAsync("te-1", default);
        var missing = await gw.GetEntryAsync("te-9", default);

        Assert.Equal("Development", found!.TaskName);
        Assert.Null(missing);
        Assert.StartsWith("/api/time-entry/te-1?modelProperties=", pi.Requests[0].Uri.PathAndQuery);
    }

    [Fact]
    public async Task Create_posts_the_entry_and_returns_the_saved_id()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """{"SavedId":"te-new","Success":true}"""));

        var result = await gw.CreateEntryAsync(
            new EntryWrite("u-1", "t-1", new DateOnly(2026, 10, 27), 4m, true, "Sync", "ts-1"), default);

        Assert.Equal(new PiWrite(true, "te-new", null), result);
        var body = JsonDocument.Parse(Assert.Single(pi.Requests).Body).RootElement;
        Assert.Equal("u-1", body.GetProperty("User_Id").GetString());
        Assert.Equal("t-1", body.GetProperty("Task_Id").GetString());
        Assert.Equal(4m, body.GetProperty("ActualHours").GetDecimal());
        Assert.True(body.GetProperty("IsBillable").GetBoolean());
        Assert.Equal("ts-1", body.GetProperty("TimeSheet_Id").GetString());
        Assert.StartsWith("2026-10-27", body.GetProperty("Date").GetString());
        Assert.False(body.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task Update_sends_every_field_and_clears_a_null_description()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """{"SavedId":"te-1","Success":true}"""));

        await gw.UpdateEntryAsync("te-1", new EntryWrite("u-1", "t-2", new DateOnly(2026, 10, 28), 2m, false, null, null), default);

        var body = JsonDocument.Parse(Assert.Single(pi.Requests).Body).RootElement;
        Assert.Equal("te-1", body.GetProperty("Id").GetString());
        Assert.Equal("t-2", body.GetProperty("Task_Id").GetString());
        Assert.Equal("", body.GetProperty("Description").GetString());
        Assert.False(body.GetProperty("IsBillable").GetBoolean());
    }

    [Fact]
    public async Task Failed_write_reports_pi_error_text()
    {
        var (gw, _) = Create(Json(HttpStatusCode.BadRequest, "Task is closed to time entry."));

        var result = await gw.DeleteEntryAsync("te-1", default);

        Assert.False(result.Success);
        Assert.Contains("Task is closed to time entry.", result.Error);
    }

    [Fact]
    public async Task Current_user_is_cached_per_token()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """{"Id":"u-1","Name":"Alex Example"}"""));

        var a = await gw.GetCurrentUserAsync(default);
        var b = await gw.GetCurrentUserAsync(default);

        Assert.Equal(new PiUser("u-1", "Alex Example"), a);
        Assert.Same(a, b);
        Assert.Single(pi.Requests);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeEntryGatewayTests"`
Expected: build FAILS — `ProjectInsightTimeEntryGateway`, `PiEntry`, `EntryWrite` etc. do not exist.

- [ ] **Step 3: Implement**

Append to `core/Models/ProjectInsightModels.cs`, after `TimeEntrySearchRequest`:

```csharp
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
```

Add to `core/Services/ProjectInsightApiService.cs`, after `DeleteTimeEntryAsync` and inside the same region:

```csharp
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
```

`core/TimeChanges/TimeChangeException.cs`:

```csharp
namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// A time-change request that can't proceed (bad input, unknown draft, wrong token…).
/// The message is written for the agent and the user; <see cref="Code"/> is for tests and the card.
/// </summary>
public sealed class TimeChangeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
```

`core/TimeChanges/TimeEntryGateway.cs`:

```csharp
using ProjectInsightMCP.Models;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.TimeChanges;

public sealed record PiUser(string Id, string Name);
public sealed record PiNamed(string Id, string Name);
public sealed record PiTask(string Id, string Name, string ProjectId, string ProjectName);
public sealed record PiEntry(string Id, string UserId, DateOnly Date, decimal Hours, bool Billable, string? Description,
    string TaskId, string TaskName, string ProjectId, string ProjectName, string? TimeSheetId);
public sealed record PiTimeSheet(string Id, DateOnly Start, DateOnly End, bool Submitted, bool Approved, bool Locked, string? Url);
public sealed record EntryWrite(string UserId, string TaskId, DateOnly Date, decimal Hours, bool Billable, string? Description, string? TimeSheetId);
public sealed record PiWrite(bool Success, string? SavedId, string? Error);

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
}

public sealed class ProjectInsightTimeEntryGateway(ProjectInsightApiService api, ApiTokenAccessor token) : ITimeEntryGateway
{
    private readonly object _lock = new();
    private (string? Token, PiUser User)? _caller;

    public async Task<PiUser> GetCurrentUserAsync(CancellationToken ct)
    {
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
        return response.Success && response.Data?.Id is { } id ? new PiUser(id, response.Data.Name ?? id) : null;
    }

    public async Task<IReadOnlyList<PiUser>> FindUsersAsync(string name, CancellationToken ct)
    {
        var response = await api.ListAllUsersAsync(isActive: true, nameFilter: name, modelProperties: "Id,Name", cancellationToken: ct);
        return (response.Data ?? new()).Where(u => u.Id is not null)
            .Select(u => new PiUser(u.Id!, u.Name ?? u.Id!)).ToList();
    }

    public async Task<IReadOnlyList<PiNamed>> ListOpenProjectsAsync(CancellationToken ct)
    {
        var response = await api.SearchProjectsAsync(
            new ProjectSearchRequest { IsActive = true, IsOpen = true, ModelProperties = "Id,Name" }, ct);
        return (response.Data ?? new()).Where(p => p.Id is not null)
            .Select(p => new PiNamed(p.Id!, p.Name ?? p.Id!)).ToList();
    }

    public async Task<IReadOnlyList<PiNamed>> ListTasksAsync(string projectId, CancellationToken ct)
    {
        var response = await api.GetProjectTasksAsync(projectId, "Id,Name", ct);
        return (response.Data ?? new()).Where(t => t.Id is not null)
            .Select(t => new PiNamed(t.Id!, t.Name ?? t.Id!)).ToList();
    }

    public async Task<PiTask?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        var task = await api.GetTaskAsync(taskId, "Id,Name,Project_Id", ct);
        if (!task.Success || task.Data?.Id is null || task.Data.ProjectId is null)
            return null;
        var project = await api.GetProjectAsync(task.Data.ProjectId, "Id,Name", ct);
        return new PiTask(task.Data.Id, task.Data.Name ?? task.Data.Id, task.Data.ProjectId,
            project.Data?.Name ?? task.Data.ProjectId);
    }

    public async Task<PiEntry?> GetEntryAsync(string timeEntryId, CancellationToken ct)
    {
        var response = await api.GetTimeEntryAsync(timeEntryId, ct);
        return response.Success && response.Data is { } e ? ToEntry(e) : null;
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
        return (response.Data ?? new())
            .Where(s => s.Id is not null && s.StartDate.HasValue && s.EndDate.HasValue)
            .Select(s => new PiTimeSheet(s.Id!, DateOnly.FromDateTime(s.StartDate!.Value), DateOnly.FromDateTime(s.EndDate!.Value),
                s.IsSubmitted == true, s.IsApproved == true, s.IsLocked == true, s.UrlFull))
            .ToList();
    }

    public Task<PiWrite> CreateEntryAsync(EntryWrite entry, CancellationToken ct) => SaveAsync(null, entry, ct);

    public Task<PiWrite> UpdateEntryAsync(string timeEntryId, EntryWrite values, CancellationToken ct) => SaveAsync(timeEntryId, values, ct);

    public async Task<PiWrite> DeleteEntryAsync(string timeEntryId, CancellationToken ct)
    {
        var response = await api.DeleteTimeEntryAsync(timeEntryId, ct);
        return response.Success ? new PiWrite(true, null, null) : new PiWrite(false, null, Describe(response.Message, response.Errors));
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
            : new PiWrite(false, null, Describe(response.Message, response.Errors));
    }

    private static PiEntry? ToEntry(TimeEntryWithNames e)
    {
        if (e.Id is null || e.UserId is null || e.Date is null || e.TaskId is null)
            return null;
        return new PiEntry(e.Id, e.UserId, DateOnly.FromDateTime(e.Date.Value), e.ActualHours ?? 0m, e.IsBillable == true,
            e.Description, e.TaskId, e.TaskRef?.Name ?? e.TaskId, e.ProjectId ?? e.ProjectRef?.Id ?? "",
            e.ProjectRef?.Name ?? e.ProjectId ?? "", e.TimeSheetId);
    }

    private static string Describe(string? message, List<string>? errors)
        => string.Join(" ", new[] { message }.Concat(errors ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)));
}
```

`DateOnly.FromDateTime` takes the calendar date as PI sent it, with no time-zone conversion. `"2026-10-27T00:00:00"` deserializes as `DateTimeKind.Unspecified`, so it lands on the 27th (Review Focus 1).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeEntryGatewayTests"`
Expected: PASS (6 tests). Then run `dotnet test tests`: all existing tests pass, so nothing else changed.

- [ ] **Step 5: Commit**

```bash
git add core/Models/ProjectInsightModels.cs core/Services/ProjectInsightApiService.cs core/TimeChanges tests/TimeChanges/TimeEntryGatewayTests.cs
git commit -m "feat(time-changes): PI gateway for time-change drafts"
```

---

### Task 2: Draft model, week helpers, fingerprints and ids

**Files:**
- Create: `core/TimeChanges/TimeChangeModels.cs`
- Create: `core/TimeChanges/Weeks.cs`
- Create: `core/TimeChanges/Fingerprint.cs`
- Create: `core/TimeChanges/DraftIds.cs`
- Create: `tests/TimeChanges/FingerprintTests.cs`

**Interfaces:**
- Consumes: `PiEntry` and `PiTimeSheet` (Task 1).
- Produces:
  - constants `DraftStatus`, `ChangeAction`, `RowOutcome`, `CommitChannel`, `WarningCode`
  - classes `ChangeInput`, `EntryValues`, `DraftWarning`, `DraftRow`, `ContextEntry`, `DayView`, `WeekView`, `DraftTotals`, `Draft`, `Clarification`, `Candidate`
  - `Weeks.MondayOf(DateOnly)`, `Weeks.SundayOf(DateOnly)`, `Weeks.DayKey(string userId, DateOnly date)`, `Weeks.Covering(IEnumerable<PiTimeSheet>, DateOnly)`, `Weeks.DayLabel(string personName, DateOnly date)`
  - `Fingerprint.Compute(IEnumerable<PiEntry> dayEntries, PiTimeSheet? sheet) : string`
  - `DraftIds.NewDraftId()`, `DraftIds.NewConfirmToken()`, `DraftIds.Hash(string)`, `DraftIds.TokenMatches(string token, string hash)`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/FingerprintTests.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class FingerprintTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);

    private static PiEntry Entry(string id, decimal hours = 1m, string task = "t-1", bool billable = true, string? desc = "x")
        => new(id, "u-1", Day, hours, billable, desc, task, "Task", "p-1", "Project", null);

    [Fact]
    public void Same_entries_in_any_order_give_the_same_fingerprint()
        => Assert.Equal(
            Fingerprint.Compute([Entry("a"), Entry("b")], null),
            Fingerprint.Compute([Entry("b"), Entry("a")], null));

    [Fact]
    public void Equal_hours_written_differently_give_the_same_fingerprint()
        => Assert.Equal(Fingerprint.Compute([Entry("a", 1.5m)], null), Fingerprint.Compute([Entry("a", 1.50m)], null));

    [Theory]
    [InlineData("hours")]
    [InlineData("task")]
    [InlineData("billable")]
    [InlineData("description")]
    [InlineData("added")]
    [InlineData("removed")]
    public void Any_change_to_the_day_changes_the_fingerprint(string change)
    {
        var before = Fingerprint.Compute([Entry("a"), Entry("b")], null);
        PiEntry[] after = change switch
        {
            "hours" => [Entry("a", 2m), Entry("b")],
            "task" => [Entry("a", task: "t-2"), Entry("b")],
            "billable" => [Entry("a", billable: false), Entry("b")],
            "description" => [Entry("a", desc: "y"), Entry("b")],
            "added" => [Entry("a"), Entry("b"), Entry("c")],
            _ => [Entry("a")]
        };
        Assert.NotEqual(before, Fingerprint.Compute(after, null));
    }

    [Fact]
    public void Timesheet_status_is_part_of_the_fingerprint()
    {
        var open = new PiTimeSheet("ts", Day, Day, false, false, false, null);
        Assert.NotEqual(Fingerprint.Compute([], open), Fingerprint.Compute([], open with { Submitted = true }));
        Assert.NotEqual(Fingerprint.Compute([], open), Fingerprint.Compute([], open with { Approved = true }));
        Assert.NotEqual(Fingerprint.Compute([], open), Fingerprint.Compute([], open with { Locked = true }));
        Assert.Equal(Fingerprint.Compute([], null), Fingerprint.Compute([], open));
    }

    [Fact]
    public void Week_helpers_use_monday_to_sunday_and_prefer_a_covering_timesheet()
    {
        Assert.Equal(new DateOnly(2026, 10, 26), Weeks.MondayOf(Day));
        Assert.Equal(new DateOnly(2026, 11, 1), Weeks.SundayOf(Day));
        Assert.Equal(new DateOnly(2026, 10, 26), Weeks.MondayOf(new DateOnly(2026, 11, 1)));
        var sheet = new PiTimeSheet("ts", new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 30), false, false, false, null);
        Assert.Same(sheet, Weeks.Covering([sheet], Day));
        Assert.Null(Weeks.Covering([sheet], new DateOnly(2026, 10, 31)));
        Assert.Equal("u-1|2026-10-27", Weeks.DayKey("u-1", Day));
        Assert.Equal("Jane Doe · Tue 27 Oct", Weeks.DayLabel("Jane Doe", Day));
    }

    [Fact]
    public void Draft_ids_and_tokens_are_random_and_tokens_verify_against_their_hash()
    {
        var id = DraftIds.NewDraftId();
        Assert.Matches("^d_[a-z2-9]{10}$", id);
        Assert.NotEqual(id, DraftIds.NewDraftId());

        var token = DraftIds.NewConfirmToken();
        Assert.Matches("^[0-9a-f]{32}$", token);
        var hash = DraftIds.Hash(token);
        Assert.True(DraftIds.TokenMatches(token, hash));
        Assert.False(DraftIds.TokenMatches(DraftIds.NewConfirmToken(), hash));
        Assert.False(DraftIds.TokenMatches("", hash));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~FingerprintTests"`
Expected: build FAILS — `Fingerprint`, `Weeks`, `DraftIds` do not exist.

- [ ] **Step 3: Implement**

`core/TimeChanges/TimeChangeModels.cs`:

```csharp
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
    public string? RevertedBy { get; set; }
    public required string ConfirmTokenHash { get; set; }
    public bool ConfirmTokenUsed { get; set; }
    public List<DraftRow> Rows { get; set; } = new();
    public List<WeekView> Weeks { get; set; } = new();
    /// <summary>Key: <see cref="Weeks.DayKey"/>. Only days the draft changes.</summary>
    public Dictionary<string, string> DayFingerprints { get; set; } = new();
    public DraftTotals Totals { get; set; } = new();
    public string? Error { get; set; }
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
```

`core/TimeChanges/Weeks.cs`:

```csharp
using System.Globalization;

namespace ProjectInsightMCP.TimeChanges;

public static class Weeks
{
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly SundayOf(DateOnly date) => MondayOf(date).AddDays(6);

    public static string DayKey(string userId, DateOnly date) => $"{userId}|{date:yyyy-MM-dd}";

    public static PiTimeSheet? Covering(IEnumerable<PiTimeSheet> sheets, DateOnly date)
        => sheets.FirstOrDefault(s => s.Start <= date && date <= s.End);

    public static string DayLabel(string personName, DateOnly date)
        => $"{personName} · {date.ToString("ddd d MMM", CultureInfo.InvariantCulture)}";

    public static IEnumerable<DateOnly> Range(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
            yield return d;
    }
}
```

`core/TimeChanges/Fingerprint.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// PI time entries have no "last updated" field, so staleness is detected by hashing everything
/// a draft depends on for one person-day: the day's entries plus the timesheet's status flags.
/// </summary>
public static class Fingerprint
{
    public static string Compute(IEnumerable<PiEntry> dayEntries, PiTimeSheet? sheet)
    {
        var sb = new StringBuilder();
        foreach (var e in dayEntries.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            sb.Append(e.Id).Append('|')
              .Append(e.TaskId).Append('|')
              .Append(e.Hours.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
              .Append(e.Billable ? '1' : '0').Append('|')
              .Append(e.Description ?? "").Append('\n');
        }
        sb.Append("ts|")
          .Append(sheet?.Submitted == true ? '1' : '0')
          .Append(sheet?.Approved == true ? '1' : '0')
          .Append(sheet?.Locked == true ? '1' : '0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
```

`core/TimeChanges/DraftIds.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.TimeChanges;

public static class DraftIds
{
    // No 0/1/o/l, so ids read back unambiguously.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string NewDraftId() => "d_" + RandomNumberGenerator.GetString(Alphabet, 10);

    public static string NewConfirmToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenMatches(string token, string hash)
        => !string.IsNullOrEmpty(token) &&
           CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));
}
```

`RandomNumberGenerator.GetString(ReadOnlySpan<char>, int)` exists on .NET 8+.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~FingerprintTests"`
Expected: PASS (11 test cases).

- [ ] **Step 5: Commit**

```bash
git add core/TimeChanges tests/TimeChanges/FingerprintTests.cs
git commit -m "feat(time-changes): draft model, week helpers, fingerprints and ids"
```

---

### Task 3: File draft store

**Files:**
- Create: `core/TimeChanges/Drafts/IDraftStore.cs`
- Create: `core/TimeChanges/Drafts/FileDraftStore.cs`
- Create: `tests/TimeChanges/ManualTimeProvider.cs`
- Create: `tests/TimeChanges/Drafts.cs` (test builder)
- Create: `tests/TimeChanges/FileDraftStoreTests.cs`

**Interfaces:**
- Consumes: `Draft` and `DraftStatus` (Task 2).
- Produces:
  - `interface IDraftStore` with `GetAsync(string ownerId, string id, CancellationToken ct = default) : Task<Draft?>`, `CreateAsync(Draft draft, CancellationToken ct = default) : Task`, `TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default) : Task<Draft?>` and `PruneAsync(string ownerId, CancellationToken ct = default) : Task`
  - `class FileDraftStore(string root, TimeProvider time) : IDraftStore`, with `static string DefaultRoot`
  - test helpers `ManualTimeProvider` (settable `Now`) and `Drafts.New(string owner = "u-1", string? id = null, string status = "draft", DateTimeOffset? at = null) : Draft`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/ManualTimeProvider.cs`:

```csharp
namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
```

`tests/TimeChanges/Drafts.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Tests.TimeChanges;

public static class Drafts
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    public static Draft New(string owner = "u-1", string? id = null, string status = DraftStatus.Draft, DateTimeOffset? at = null)
    {
        var when = at ?? T0;
        return new Draft
        {
            Id = id ?? DraftIds.NewDraftId(),
            Status = status,
            Intent = "Test draft",
            ProposedBy = owner,
            ProposedByName = "Alex Example",
            ProposedAt = when,
            ExpiresAt = when.AddHours(24),
            StatusChangedAt = when,
            ConfirmTokenHash = DraftIds.Hash("token")
        };
    }
}
```

`tests/TimeChanges/FileDraftStoreTests.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class FileDraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-drafts-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Drafts.T0);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FileDraftStore Store() => new(_root, _time);

    [Fact]
    public async Task Created_draft_reads_back_and_is_private_to_its_owner()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);

        var read = await Store().GetAsync("u-1", draft.Id);
        Assert.Equal(draft.Intent, read!.Intent);
        Assert.Equal(Drafts.T0, read.UpdatedAt);
        Assert.Null(await Store().GetAsync("u-2", draft.Id));

        if (!OperatingSystem.IsWindows())
        {
            var file = Path.Combine(_root, "u-1", draft.Id + ".json");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(_root, "u-1")));
        }
    }

    [Theory]
    [InlineData("../u-2/d_abc")]
    [InlineData("d_ABC/../../x")]
    [InlineData("")]
    public async Task Ids_with_path_characters_are_not_found(string id)
        => Assert.Null(await Store().GetAsync("u-1", id));

    [Fact]
    public async Task Corrupt_file_reads_as_not_found()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        await File.WriteAllTextAsync(Path.Combine(_root, "u-1", draft.Id + ".json"), "{ not json");

        Assert.Null(await Store().GetAsync("u-1", draft.Id));
    }

    [Fact]
    public async Task Transition_applies_only_from_the_expected_status()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        _time.Now = Drafts.T0.AddMinutes(5);

        Assert.Null(await Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Committed, d => d.Status = DraftStatus.Cancelled));
        var moved = await Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Cancelled);

        Assert.Equal(DraftStatus.Cancelled, moved!.Status);
        Assert.Equal(_time.Now, moved.UpdatedAt);
        Assert.Equal(DraftStatus.Cancelled, (await Store().GetAsync("u-1", draft.Id))!.Status);
    }

    [Fact]
    public async Task Concurrent_claims_from_separate_store_instances_have_exactly_one_winner()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing))));

        Assert.Single(claims, c => c is not null);
    }

    [Fact]
    public async Task Creating_the_same_id_twice_fails()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CreateAsync(draft));
    }

    [Fact]
    public async Task Prune_removes_records_untouched_for_seven_days()
    {
        var old = Drafts.New();
        var recent = Drafts.New();
        await Store().CreateAsync(old);
        _time.Now = Drafts.T0.AddDays(6);
        await Store().CreateAsync(recent);

        _time.Now = Drafts.T0.AddDays(7).AddMinutes(1);
        await Store().PruneAsync("u-1");

        Assert.Null(await Store().GetAsync("u-1", old.Id));
        Assert.NotNull(await Store().GetAsync("u-1", recent.Id));
        Assert.False(File.Exists(Path.Combine(_root, "u-1", old.Id + ".lock")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~FileDraftStoreTests"`
Expected: build FAILS — `FileDraftStore` does not exist.

- [ ] **Step 3: Implement**

`core/TimeChanges/Drafts/IDraftStore.cs`:

```csharp
namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// Drafts and receipts, partitioned by owner (the proposer's PI user id). A draft is only ever
/// visible to its owner, which is how "only the proposer may act on it" is enforced.
/// </summary>
public interface IDraftStore
{
    Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default);

    /// <summary>Stores a new draft under <see cref="Draft.ProposedBy"/>. Throws if the id exists.</summary>
    Task CreateAsync(Draft draft, CancellationToken ct = default);

    /// <summary>
    /// Under an exclusive lock: re-reads the draft and, only if its status is
    /// <paramref name="expectedStatus"/>, applies <paramref name="update"/> and saves it.
    /// Returns the saved draft, or null if it is missing or in another status.
    /// </summary>
    Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default);

    /// <summary>Deletes the owner's records untouched for 7 days.</summary>
    Task PruneAsync(string ownerId, CancellationToken ct = default);
}
```

`core/TimeChanges/Drafts/FileDraftStore.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// One JSON file per record at &lt;root&gt;/&lt;ownerId&gt;/&lt;id&gt;.json, readable only by the
/// current OS user. Writes go to a temp file and are renamed into place. Status changes hold an
/// exclusive lock file, so two server processes (two Claude windows) can't both commit a draft.
/// </summary>
public sealed partial class FileDraftStore(string root, TimeProvider time) : IDraftStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".projectinsight", "drafts");

    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [GeneratedRegex("^d_[a-z0-9]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex OwnerPattern();

    public async Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default)
    {
        var path = PathFor(ownerId, id);
        return path is null ? null : await ReadAsync(path, ct);
    }

    public async Task CreateAsync(Draft draft, CancellationToken ct = default)
    {
        var path = PathFor(draft.ProposedBy, draft.Id)
            ?? throw new ArgumentException($"Invalid draft id or owner: {draft.Id}");
        EnsureDirectory(Path.GetDirectoryName(path)!);
        using (await LockAsync(path, ct))
        {
            if (File.Exists(path))
                throw new InvalidOperationException($"Draft {draft.Id} already exists.");
            draft.UpdatedAt = time.GetUtcNow();
            await WriteAsync(path, draft, ct);
        }
    }

    public async Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default)
    {
        var path = PathFor(ownerId, id);
        if (path is null || !File.Exists(path))
            return null;
        using (await LockAsync(path, ct))
        {
            var draft = await ReadAsync(path, ct);
            if (draft is null || draft.Status != expectedStatus)
                return null;
            update(draft);
            draft.UpdatedAt = time.GetUtcNow();
            await WriteAsync(path, draft, ct);
            return draft;
        }
    }

    public async Task PruneAsync(string ownerId, CancellationToken ct = default)
    {
        if (!OwnerPattern().IsMatch(ownerId))
            return;
        var dir = Path.Combine(root, ownerId);
        if (!Directory.Exists(dir))
            return;
        var cutoff = time.GetUtcNow() - Keep;
        foreach (var file in Directory.EnumerateFiles(dir, "d_*.json"))
        {
            var draft = await ReadAsync(file, ct);
            if (draft is null || draft.UpdatedAt >= cutoff)
                continue;
            File.Delete(file);
            File.Delete(Path.ChangeExtension(file, ".lock"));
        }
    }

    private string? PathFor(string ownerId, string id)
        => OwnerPattern().IsMatch(ownerId) && IdPattern().IsMatch(id) ? Path.Combine(root, ownerId, id + ".json") : null;

    private static async Task<Draft?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<Draft>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(string path, Draft draft, CancellationToken ct)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(stream, draft, Json, ct);
        }
        File.Move(temp, path, overwrite: true);
    }

    private void EnsureDirectory(string dir)
    {
        foreach (var d in new[] { root, dir })
        {
            Directory.CreateDirectory(d);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // FileShare.None takes an OS-level exclusive lock (flock on Unix), honoured across processes
    // and across FileStreams in one process. The lock file is left in place: deleting it on close
    // would let two waiters lock different inodes.
    private static async Task<IDisposable> LockAsync(string path, CancellationToken ct)
    {
        var lockPath = Path.ChangeExtension(path, ".lock");
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, ct);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~FileDraftStoreTests"`
Expected: PASS (9 test cases). Run it twice more to check the concurrency test is stable.

- [ ] **Step 5: Commit**

```bash
git add core/TimeChanges/Drafts tests/TimeChanges
git commit -m "feat(time-changes): file draft store with atomic writes and locked transitions"
```

---

### Task 4: Name resolution (users, projects, tasks)

**Files:**
- Create: `tests/TimeChanges/FakeGateway.cs`
- Create: `core/TimeChanges/TimeChangeResolver.cs`
- Create: `tests/TimeChanges/TimeChangeResolverTests.cs`

**Interfaces:**
- Consumes: `ITimeEntryGateway`, `PiUser`, `PiNamed`, `PiTask` (Task 1); `Clarification`, `Candidate` (Task 2).
- Produces:
  - `NameMatch.Find<T>(IEnumerable<T> items, Func<T,string> name, string query) : NameMatch.Result<T>`, where `Result<T>(T? Match, IReadOnlyList<T> Candidates)`
  - `class TimeChangeResolver(ITimeEntryGateway pi)` with `UserAsync(int index, string? user, PiUser caller, CancellationToken ct) : Task<Resolved<PiUser>>`, `UserByIdAsync(string userId, CancellationToken ct) : Task<PiUser?>` and `TaskAsync(int index, string? taskId, string? project, string? task, CancellationToken ct) : Task<Resolved<PiTask>>`
  - `record TimeChangeResolver.Resolved<T>(T? Value, Clarification? Clarification)`
  - test fake `FakeGateway`. Later tasks rely on its members exactly as written below.

- [ ] **Step 1: Write the fake and the failing tests**

`tests/TimeChanges/FakeGateway.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Tests.TimeChanges;

/// <summary>In-memory Project Insight for time-change tests.</summary>
public sealed class FakeGateway : ITimeEntryGateway
{
    public PiUser Caller { get; set; } = new("u-1", "Alex Example");

    public List<PiUser> Users { get; } = [new("u-1", "Alex Example"), new("u-2", "Jane Doe")];

    public List<PiNamed> Projects { get; } =
        [new("p-1", "Acme Portal"), new("p-2", "Acme Support"), new("p-3", "Internal")];

    public Dictionary<string, List<PiNamed>> Tasks { get; } = new()
    {
        ["p-1"] = [new("t-1", "Development"), new("t-2", "Client calls"), new("t-3", "Design review")],
        ["p-2"] = [new("t-4", "Development")],
        ["p-3"] = [new("t-5", "Staff meeting"), new("t-6", "Email & admin")]
    };

    public List<PiEntry> Entries { get; } = new();

    /// <summary>Timesheets per user id.</summary>
    public Dictionary<string, List<PiTimeSheet>> Sheets { get; } = new();

    /// <summary>Successful writes, in order: "create:te-100", "update:te-1", "delete:te-4".</summary>
    public List<string> Writes { get; } = new();

    /// <summary>Given (operation, entry id or null for create), returns an error to fail that write.</summary>
    public Func<string, string?, string?>? FailWrite { get; set; }

    public TimeSpan Delay { get; set; }
    public int MaxConcurrent => _max;
    private int _inFlight, _max, _next = 100;

    public PiEntry Add(string id, string userId, DateOnly date, decimal hours, string taskId = "t-1",
        bool billable = true, string? description = null)
    {
        var task = GetTask(taskId)!;
        var entry = new PiEntry(id, userId, date, hours, billable, description, task.Id, task.Name, task.ProjectId, task.ProjectName, null);
        Entries.Add(entry);
        return entry;
    }

    public async Task<PiUser> GetCurrentUserAsync(CancellationToken ct) { await Enter(); return Caller; }

    public async Task<PiUser?> GetUserAsync(string userId, CancellationToken ct)
    { await Enter(); return Users.FirstOrDefault(u => u.Id == userId); }

    public async Task<IReadOnlyList<PiUser>> FindUsersAsync(string name, CancellationToken ct)
    { await Enter(); return Users.Where(u => u.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList(); }

    public async Task<IReadOnlyList<PiNamed>> ListOpenProjectsAsync(CancellationToken ct) { await Enter(); return Projects.ToList(); }

    public async Task<IReadOnlyList<PiNamed>> ListTasksAsync(string projectId, CancellationToken ct)
    { await Enter(); return Tasks.GetValueOrDefault(projectId)?.ToList() ?? []; }

    public async Task<PiTask?> GetTaskAsync(string taskId, CancellationToken ct) { await Enter(); return GetTask(taskId); }

    public async Task<PiEntry?> GetEntryAsync(string timeEntryId, CancellationToken ct)
    { await Enter(); return Entries.FirstOrDefault(e => e.Id == timeEntryId); }

    public async Task<IReadOnlyList<PiEntry>> SearchEntriesAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct)
    { await Enter(); return Entries.Where(e => e.UserId == userId && e.Date >= start && e.Date <= end).ToList(); }

    public async Task<IReadOnlyList<PiTimeSheet>> SearchTimeSheetsAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        await Enter();
        return (Sheets.GetValueOrDefault(userId) ?? []).Where(s => s.Start <= end && s.End >= start).ToList();
    }

    public async Task<PiWrite> CreateEntryAsync(EntryWrite w, CancellationToken ct)
    {
        await Enter();
        if (FailWrite?.Invoke("create", null) is { } error) return new PiWrite(false, null, error);
        var id = $"te-{_next++}";
        var task = GetTask(w.TaskId)!;
        Entries.Add(new PiEntry(id, w.UserId, w.Date, w.Hours, w.Billable, w.Description, task.Id, task.Name, task.ProjectId, task.ProjectName, w.TimeSheetId));
        Writes.Add($"create:{id}");
        return new PiWrite(true, id, null);
    }

    public async Task<PiWrite> UpdateEntryAsync(string timeEntryId, EntryWrite w, CancellationToken ct)
    {
        await Enter();
        if (FailWrite?.Invoke("update", timeEntryId) is { } error) return new PiWrite(false, null, error);
        var i = Entries.FindIndex(e => e.Id == timeEntryId);
        if (i < 0) return new PiWrite(false, null, "Not found");
        var task = GetTask(w.TaskId)!;
        Entries[i] = Entries[i] with
        {
            Date = w.Date, Hours = w.Hours, Billable = w.Billable, Description = w.Description,
            TaskId = task.Id, TaskName = task.Name, ProjectId = task.ProjectId, ProjectName = task.ProjectName
        };
        Writes.Add($"update:{timeEntryId}");
        return new PiWrite(true, timeEntryId, null);
    }

    public async Task<PiWrite> DeleteEntryAsync(string timeEntryId, CancellationToken ct)
    {
        await Enter();
        if (FailWrite?.Invoke("delete", timeEntryId) is { } error) return new PiWrite(false, null, error);
        if (Entries.RemoveAll(e => e.Id == timeEntryId) == 0) return new PiWrite(false, null, "Not found");
        Writes.Add($"delete:{timeEntryId}");
        return new PiWrite(true, null, null);
    }

    private PiTask? GetTask(string taskId)
    {
        foreach (var (projectId, tasks) in Tasks)
            if (tasks.FirstOrDefault(t => t.Id == taskId) is { } task)
                return new PiTask(task.Id, task.Name, projectId, Projects.First(p => p.Id == projectId).Name);
        return null;
    }

    private async Task Enter()
    {
        var n = Interlocked.Increment(ref _inFlight);
        int seen;
        while (n > (seen = _max))
            Interlocked.CompareExchange(ref _max, n, seen);
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
```

`tests/TimeChanges/TimeChangeResolverTests.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class TimeChangeResolverTests
{
    private readonly FakeGateway _pi = new();
    private TimeChangeResolver Resolver() => new(_pi);

    [Fact]
    public async Task No_user_means_the_caller()
    {
        var r = await Resolver().UserAsync(0, null, _pi.Caller, default);
        Assert.Same(_pi.Caller, r.Value);
    }

    [Theory]
    [InlineData("jane")]
    [InlineData("Jane Doe")]
    [InlineData("u-2")]
    public async Task User_resolves_by_unique_name_or_id(string query)
        => Assert.Equal("u-2", (await Resolver().UserAsync(0, query, _pi.Caller, default)).Value!.Id);

    [Fact]
    public async Task Ambiguous_user_returns_candidates()
    {
        _pi.Users.Add(new PiUser("u-3", "Jane Smith"));
        var r = await Resolver().UserAsync(2, "Jane", _pi.Caller, default);

        Assert.Null(r.Value);
        Assert.Equal(2, r.Clarification!.ChangeIndex);
        Assert.Equal("user", r.Clarification.Field);
        Assert.Equal(["u-2", "u-3"], r.Clarification.Candidates.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Task_resolves_from_project_and_task_names_ignoring_case()
    {
        var r = await Resolver().TaskAsync(0, null, "acme portal", "development", default);
        Assert.Equal(new PiTask("t-1", "Development", "p-1", "Acme Portal"), r.Value);
    }

    [Fact]
    public async Task Exact_name_wins_over_partial_matches()
    {
        _pi.Projects.Add(new PiNamed("p-4", "Internal Tools"));
        var r = await Resolver().TaskAsync(0, null, "Internal", "Staff meeting", default);
        Assert.Equal("p-3", r.Value!.ProjectId);
    }

    [Fact]
    public async Task Ambiguous_project_lists_the_matching_projects()
    {
        var r = await Resolver().TaskAsync(1, null, "Acme", "Development", default);

        Assert.Null(r.Value);
        Assert.Equal("project", r.Clarification!.Field);
        Assert.Equal(["Acme Portal", "Acme Support"], r.Clarification.Candidates.Select(c => c.Name));
    }

    [Fact]
    public async Task Unknown_task_and_missing_names_are_clarifications()
    {
        var unknown = await Resolver().TaskAsync(0, null, "Internal", "Gardening", default);
        var missing = await Resolver().TaskAsync(0, null, "Internal", null, default);
        var badId = await Resolver().TaskAsync(0, "t-404", null, null, default);

        Assert.Contains("No task in Internal matches", unknown.Clarification!.Message);
        Assert.Contains("Give taskId, or both project and task names", missing.Clarification!.Message);
        Assert.Contains("No task with id t-404", badId.Clarification!.Message);
    }

    [Fact]
    public async Task Task_id_resolves_with_its_project_name()
        => Assert.Equal("Internal", (await Resolver().TaskAsync(0, "t-5", null, null, default)).Value!.ProjectName);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeResolverTests"`
Expected: build FAILS — `TimeChangeResolver` does not exist.

- [ ] **Step 3: Implement**

`core/TimeChanges/TimeChangeResolver.cs`:

```csharp
namespace ProjectInsightMCP.TimeChanges;

public static class NameMatch
{
    public sealed record Result<T>(T? Match, IReadOnlyList<T> Candidates) where T : class;

    /// <summary>
    /// A unique case-insensitive exact name wins; otherwise a unique partial match; otherwise no
    /// match, with up to five candidates (shortest names first).
    /// </summary>
    public static Result<T> Find<T>(IEnumerable<T> items, Func<T, string> name, string query) where T : class
    {
        var q = query.Trim();
        var list = items.ToList();
        var exact = list.Where(i => string.Equals(name(i).Trim(), q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return new(exact[0], exact);
        if (exact.Count > 1)
            return new(null, exact.Take(5).ToList());

        var partial = list.Where(i => name(i).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => name(i).Length).ThenBy(name, StringComparer.OrdinalIgnoreCase).ToList();
        return partial.Count == 1 ? new(partial[0], partial) : new(null, partial.Take(5).ToList());
    }
}

/// <summary>Turns the names an agent gives into PI ids. One instance per plan (it caches lookups).</summary>
public sealed class TimeChangeResolver(ITimeEntryGateway pi)
{
    public sealed record Resolved<T>(T? Value, Clarification? Clarification) where T : class;

    private IReadOnlyList<PiNamed>? _projects;
    private readonly Dictionary<string, IReadOnlyList<PiNamed>> _tasks = new();
    private readonly Dictionary<string, PiUser?> _users = new();

    public async Task<Resolved<PiUser>> UserAsync(int index, string? user, PiUser caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user))
            return new(caller, null);
        var query = user.Trim();
        if (query == caller.Id)
            return new(caller, null);
        if (await UserByIdAsync(query, ct) is { } byId)
            return new(byId, null);

        var match = NameMatch.Find(await pi.FindUsersAsync(query, ct), u => u.Name, query);
        if (match.Match is not null)
            return new(match.Match, null);
        return new(null, Clarify(index, "user", query,
            match.Candidates.Count == 0
                ? $"No active user matches \"{query}\"."
                : $"{match.Candidates.Count} users match \"{query}\". Use one of their ids.",
            match.Candidates.Select(u => new Candidate { Id = u.Id, Name = u.Name })));
    }

    public async Task<PiUser?> UserByIdAsync(string userId, CancellationToken ct)
    {
        if (!_users.TryGetValue(userId, out var user))
            _users[userId] = user = await pi.GetUserAsync(userId, ct);
        return user;
    }

    public async Task<Resolved<PiTask>> TaskAsync(int index, string? taskId, string? project, string? task, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var byId = await pi.GetTaskAsync(taskId.Trim(), ct);
            return byId is not null
                ? new(byId, null)
                : new(null, Clarify(index, "taskId", taskId, $"No task with id {taskId}."));
        }
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(task))
            return new(null, Clarify(index, "task", task ?? project ?? "", "Give taskId, or both project and task names."));

        _projects ??= await pi.ListOpenProjectsAsync(ct);
        var p = NameMatch.Find(_projects, x => x.Name, project);
        if (p.Match is null)
            return new(null, Clarify(index, "project", project,
                p.Candidates.Count == 0
                    ? $"No open project matches \"{project}\"."
                    : $"{p.Candidates.Count} projects match \"{project}\". Use the exact name or a taskId.",
                p.Candidates.Select(c => new Candidate { Id = c.Id, Name = c.Name })));

        if (!_tasks.TryGetValue(p.Match.Id, out var tasks))
            _tasks[p.Match.Id] = tasks = await pi.ListTasksAsync(p.Match.Id, ct);
        var t = NameMatch.Find(tasks, x => x.Name, task);
        if (t.Match is null)
            return new(null, Clarify(index, "task", task,
                t.Candidates.Count == 0
                    ? $"No task in {p.Match.Name} matches \"{task}\"."
                    : $"{t.Candidates.Count} tasks in {p.Match.Name} match \"{task}\". Use the exact name or a taskId.",
                t.Candidates.Select(c => new Candidate { Id = c.Id, Name = c.Name })));

        return new(new PiTask(t.Match.Id, t.Match.Name, p.Match.Id, p.Match.Name), null);
    }

    private static Clarification Clarify(int index, string field, string query, string message, IEnumerable<Candidate>? candidates = null)
        => new() { ChangeIndex = index, Field = field, Query = query, Message = message, Candidates = candidates?.ToList() ?? new() };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeResolverTests"`
Expected: PASS (10 test cases).

- [ ] **Step 5: Commit**

```bash
git add core/TimeChanges/TimeChangeResolver.cs tests/TimeChanges/FakeGateway.cs tests/TimeChanges/TimeChangeResolverTests.cs
git commit -m "feat(time-changes): resolve users, projects and tasks by name"
```

---

### Task 5: Planner (drafts and undo drafts; writes nothing to PI)

**Files:**
- Create: `core/TimeChanges/TimeChangePlanner.cs`
- Create: `tests/TimeChanges/TimeChangePlannerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces:
  - `class PlanResult { Draft? Draft; string? ConfirmToken; List<Clarification> Clarifications }`
  - `class TimeChangePlanner(ITimeEntryGateway pi, IDraftStore store, TimeProvider time)`, with `const int MaxChanges = 100`, `static readonly TimeSpan Lifetime` (24h), `PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct) : Task<PlanResult>` and `PlanUndoAsync(Draft receipt, CancellationToken ct) : Task<PlanResult>`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/TimeChangePlannerTests.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TimeChangePlannerTests : IDisposable
{
    internal static readonly DateOnly Mon = new(2026, 10, 26), Tue = new(2026, 10, 27), Wed = new(2026, 10, 28);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Drafts.T0);
    private readonly FakeGateway _pi = Seeded();
    private readonly FileDraftStore _store;
    private readonly TimeChangePlanner _planner;

    public TimeChangePlannerTests()
    {
        _store = new FileDraftStore(_root, _time);
        _planner = new TimeChangePlanner(_pi, _store, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Alex's week of 26 Oct: Mon 6h, Tue 2.5h, Wed 8h.</summary>
    internal static FakeGateway Seeded()
    {
        var pi = new FakeGateway();
        pi.Add("te-1", "u-1", Mon, 1.0m, "t-2");
        pi.Add("te-2", "u-1", Mon, 5.0m, "t-1");
        pi.Add("te-3", "u-1", Tue, 0.5m, "t-6", billable: false);
        pi.Add("te-4", "u-1", Tue, 2.0m, "t-1", description: "API integration");
        pi.Add("te-5", "u-1", Wed, 8.0m, "t-1");
        return pi;
    }

    internal static List<ChangeInput> TypicalChanges() =>
    [
        new() { Action = "create", Date = "2026-10-27", Hours = 4m, Project = "Acme Portal", Task = "Development", Billable = true, Description = "Order sync" },
        new() { Action = "update", TimeEntryId = "te-1", Hours = 1.5m },
        new() { Action = "delete", TimeEntryId = "te-4" }
    ];

    [Fact]
    public async Task Plan_stores_a_draft_and_writes_nothing_to_project_insight()
    {
        var result = await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default);

        var draft = result.Draft!;
        Assert.Empty(_pi.Writes);
        Assert.Equal(DraftStatus.Draft, draft.Status);
        Assert.Equal(Drafts.T0.AddHours(24), draft.ExpiresAt);
        Assert.True(DraftIds.TokenMatches(result.ConfirmToken!, draft.ConfirmTokenHash));
        Assert.Equal(["create", "update", "delete"], draft.Rows.Select(r => r.Action));
        Assert.Equal(1.0m, draft.Rows[1].Before!.Hours);
        Assert.Equal(1.5m, draft.Rows[1].After!.Hours);
        Assert.Equal("Client calls", draft.Rows[1].After!.TaskName);
        Assert.NotNull(await _store.GetAsync("u-1", draft.Id));
    }

    [Fact]
    public async Task Week_view_shows_every_weekday_with_context_and_totals()
    {
        var draft = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;

        var week = Assert.Single(draft.Weeks);
        Assert.True(week.IsCaller);
        Assert.Equal((Mon, new DateOnly(2026, 11, 1)), (week.Start, week.End));
        Assert.Equal([Mon, Tue, Wed, Mon.AddDays(3), Mon.AddDays(4)], week.Days.Select(d => d.Date)); // no empty weekend
        Assert.Equal((16.5m, 19.0m), (week.TotalBefore, week.TotalAfter));

        var tue = week.Days[1];
        Assert.True(tue.HasChanges);
        Assert.Equal((2.5m, 4.5m), (tue.TotalBefore, tue.TotalAfter));
        Assert.Equal(["te-3"], tue.Context.Select(c => c.TimeEntryId)); // te-4 is being deleted, so it's a row
        Assert.Equal([0, 2], tue.RowIndexes);

        var wed = week.Days[2];
        Assert.False(wed.HasChanges);
        Assert.Equal((8m, 8m), (wed.TotalBefore, wed.TotalAfter));

        Assert.Equal(["u-1|2026-10-26", "u-1|2026-10-27"], draft.DayFingerprints.Keys.Order());
        Assert.Equal((3, 2, 1), (draft.Totals.Changes, draft.Totals.Days, draft.Totals.People));
        Assert.Equal(2.5m, draft.Totals.NetHours);
    }

    [Fact]
    public async Task Totals_and_quarter_hour_checks_use_exact_decimals()
    {
        _pi.Add("te-6", "u-1", Wed.AddDays(1), 0.1m);
        _pi.Add("te-7", "u-1", Wed.AddDays(1), 0.2m);
        var draft = (await _planner.PlanAsync("Odd hours",
        [
            new() { Action = "create", Date = "2026-10-29", Hours = 0.1m, TaskId = "t-5" },
            new() { Action = "create", Date = "2026-10-29", Hours = 1.25m, TaskId = "t-6" }
        ], default)).Draft!;

        var thu = draft.Weeks[0].Days.Single(d => d.Date == Wed.AddDays(1));
        Assert.Equal(0.3m, thu.TotalBefore);
        Assert.Equal(1.65m, thu.TotalAfter);
        Assert.Contains(draft.Rows[0].Warnings, w => w.Code == WarningCode.NotQuarterHour);
        Assert.DoesNotContain(draft.Rows[1].Warnings, w => w.Code == WarningCode.NotQuarterHour);
        Assert.Equal(1.35m, draft.Totals.NetHours);
    }

    [Fact]
    public async Task Ambiguous_names_return_candidates_and_create_no_draft()
    {
        var result = await _planner.PlanAsync("Log",
            [new() { Action = "create", Date = "2026-10-27", Hours = 1m, Project = "Acme", Task = "Development" }], default);

        Assert.Null(result.Draft);
        var c = Assert.Single(result.Clarifications);
        Assert.Equal(["Acme Portal", "Acme Support"], c.Candidates.Select(x => x.Name));
        Assert.False(Directory.Exists(Path.Combine(_root, "u-1")) && Directory.EnumerateFiles(Path.Combine(_root, "u-1"), "*.json").Any());
    }

    [Fact]
    public async Task The_same_entry_in_two_changes_is_refused()
    {
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanAsync("Fix",
            [new() { Action = "update", TimeEntryId = "te-1", Hours = 2m }, new() { Action = "delete", TimeEntryId = "te-1" }], default));

        Assert.Equal("invalid_change", ex.Code);
        Assert.Contains("te-1 appears in more than one change", ex.Message);
    }

    [Fact]
    public async Task Moving_an_entry_to_another_week_shows_both_weeks()
    {
        var draft = (await _planner.PlanAsync("Move",
            [new() { Action = "update", TimeEntryId = "te-5", Date = "2026-11-03" }], default)).Draft!;

        Assert.Equal([Mon, new DateOnly(2026, 11, 2)], draft.Weeks.Select(w => w.Start));
        Assert.Equal((8m, 0m), Day(draft, Wed));
        Assert.Equal((0m, 8m), Day(draft, new DateOnly(2026, 11, 3)));
        Assert.Equal(["u-1|2026-10-28", "u-1|2026-11-03"], draft.DayFingerprints.Keys.Order());
        Assert.Equal(0m, draft.Totals.NetHours);

        static (decimal, decimal) Day(Draft d, DateOnly date)
        {
            var day = d.Weeks.SelectMany(w => w.Days).Single(x => x.Date == date);
            return (day.TotalBefore, day.TotalAfter);
        }
    }

    [Fact]
    public async Task Rows_carry_warnings()
    {
        _pi.Sheets["u-2"] = [new PiTimeSheet("ts-2", Mon, new DateOnly(2026, 11, 1), true, false, false, "https://pi.test/ts-2")];
        var draft = (await _planner.PlanAsync("Mixed",
        [
            new() { Action = "create", Date = "2026-10-28", Hours = 6m, TaskId = "t-1" },
            new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" },
            new() { Action = "create", Date = "2026-10-27", Hours = 3m, TaskId = "t-4", User = "Jane" }
        ], default)).Draft!;

        Assert.Contains(draft.Rows[0].Warnings, w => w.Code == WarningCode.Over12Hours && w.Message.Contains("14h"));
        Assert.Contains(draft.Rows[1].Warnings, w => w.Code == WarningCode.LikelyDuplicate);
        Assert.Equal([WarningCode.TimesheetSubmitted, WarningCode.OtherPerson], draft.Rows[2].Warnings.Select(w => w.Code));
        var jane = draft.Weeks.Single(w => w.UserId == "u-2");
        Assert.False(jane.IsCaller);
        Assert.Equal(("ts-2", "https://pi.test/ts-2"), (jane.TimesheetId, jane.TimesheetUrl));
        Assert.Equal(4, draft.Totals.Warnings);
    }

    [Theory]
    [InlineData("""{"action":"create","date":"27/10/2026","hours":1,"taskId":"t-1"}""", "date must be yyyy-MM-dd")]
    [InlineData("""{"action":"create","date":"2026-10-27","hours":0,"taskId":"t-1"}""", "hours must be more than 0")]
    [InlineData("""{"action":"create","date":"2026-10-27","hours":25,"taskId":"t-1"}""", "at most 24")]
    [InlineData("""{"action":"create","hours":1,"taskId":"t-1"}""", "date must be yyyy-MM-dd")]
    [InlineData("""{"action":"move","timeEntryId":"te-1"}""", "action must be create, update or delete")]
    [InlineData("""{"action":"update","timeEntryId":"te-1","hours":1}""", "changes nothing")]
    [InlineData("""{"action":"update","timeEntryId":"te-1","user":"Jane"}""", "can't change whose time")]
    [InlineData("""{"action":"delete"}""", "needs timeEntryId")]
    public async Task Invalid_changes_are_refused_with_a_reason(string json, string reason)
    {
        var change = System.Text.Json.JsonSerializer.Deserialize<ChangeInput>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanAsync("x", [change], default));
        Assert.Contains(reason, ex.Message);
        Assert.StartsWith("Change 1:", ex.Message);
    }

    [Fact]
    public async Task More_than_a_hundred_changes_is_refused()
    {
        var changes = Enumerable.Range(0, 101).Select(_ => new ChangeInput { Action = "delete", TimeEntryId = "te-1" }).ToList();
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanAsync("x", changes, default));
        Assert.Equal("too_many_changes", ex.Code);
    }

    [Fact]
    public async Task Unknown_entry_is_a_clarification()
    {
        var result = await _planner.PlanAsync("x", [new() { Action = "delete", TimeEntryId = "te-404" }], default);
        Assert.Equal("timeEntryId", Assert.Single(result.Clarifications).Field);
    }

    [Fact]
    public async Task Undo_draft_reverses_each_action()
    {
        // A receipt as the committer leaves it: te-100 was created, te-1 updated 1.0 → 1.5, te-4 deleted.
        var planned = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;
        _pi.Add("te-100", "u-1", Tue, 4m, "t-1", description: "Order sync");
        _pi.Entries[_pi.Entries.FindIndex(e => e.Id == "te-1")] = _pi.Entries.First(e => e.Id == "te-1") with { Hours = 1.5m };
        _pi.Entries.RemoveAll(e => e.Id == "te-4");
        planned.Status = DraftStatus.Committed;
        planned.Rows[0].SavedId = "te-100";

        var undo = (await _planner.PlanUndoAsync(planned, default)).Draft!;

        Assert.Equal(planned.Id, undo.UndoOf);
        Assert.Equal("Undo: Log Tuesday", undo.Intent);
        Assert.Equal(["delete", "update", "create"], undo.Rows.Select(r => r.Action));
        Assert.Equal("te-100", undo.Rows[0].TimeEntryId);
        Assert.Equal(1.0m, undo.Rows[1].After!.Hours);
        Assert.Equal(("t-1", 2.0m, "API integration"), (undo.Rows[2].After!.TaskId, undo.Rows[2].After!.Hours, undo.Rows[2].After!.Description));
        Assert.Equal(0m, undo.Totals.NetHours + planned.Totals.NetHours);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangePlannerTests"`
Expected: build FAILS — `TimeChangePlanner` does not exist.

- [ ] **Step 3: Implement**

`core/TimeChanges/TimeChangePlanner.cs`:

```csharp
using System.Globalization;

namespace ProjectInsightMCP.TimeChanges;

public sealed class PlanResult
{
    public Draft? Draft { get; init; }
    public string? ConfirmToken { get; init; }
    public List<Clarification> Clarifications { get; init; } = new();
}

/// <summary>
/// Builds drafts: resolves names, reads each affected person-week once, and works out context,
/// totals, warnings and day fingerprints. Reads from PI; never writes to it.
/// </summary>
public sealed class TimeChangePlanner(ITimeEntryGateway pi, IDraftStore store, TimeProvider time)
{
    public const int MaxChanges = 100;
    public const int MaxIntentLength = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Task<PlanResult> PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct)
        => PlanCoreAsync(intent, changes, undoOf: null, ct);

    /// <summary>A new draft that reverses a committed receipt's rows.</summary>
    public Task<PlanResult> PlanUndoAsync(Draft receipt, CancellationToken ct)
    {
        var changes = receipt.Rows.Select(r => r.Action switch
        {
            ChangeAction.Create => new ChangeInput { Action = ChangeAction.Delete, TimeEntryId = r.SavedId },
            ChangeAction.Update => new ChangeInput
            {
                Action = ChangeAction.Update, TimeEntryId = r.TimeEntryId, TaskId = r.Before!.TaskId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Billable = r.Before.Billable, Description = r.Before.Description ?? ""
            },
            _ => new ChangeInput
            {
                Action = ChangeAction.Create, User = r.UserId, TaskId = r.Before!.TaskId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Billable = r.Before.Billable, Description = r.Before.Description
            }
        }).ToList();
        return PlanCoreAsync($"Undo: {receipt.Intent}", changes, receipt.Id, ct);
    }

    private sealed class WeekData
    {
        public required PiUser User { get; init; }
        public required DateOnly Start { get; init; }
        public required DateOnly End { get; init; }
        public PiTimeSheet? Sheet { get; init; }
        public required IReadOnlyList<PiEntry> Entries { get; init; }
        public bool Contains(string userId, DateOnly date) => User.Id == userId && Start <= date && date <= End;
    }

    private async Task<PlanResult> PlanCoreAsync(string intent, IReadOnlyList<ChangeInput>? changes, string? undoOf, CancellationToken ct)
    {
        intent = (intent ?? "").Trim();
        if (intent.Length == 0)
            throw new TimeChangeException("invalid_change", "Give a one-line intent saying what these changes are for.");
        if (intent.Length > MaxIntentLength)
            intent = intent[..MaxIntentLength];
        if (changes is null || changes.Count == 0)
            throw new TimeChangeException("invalid_change", "Give at least one change.");
        if (changes.Count > MaxChanges)
            throw new TimeChangeException("too_many_changes",
                $"A draft holds at most {MaxChanges} changes; this one has {changes.Count}. Split it into several drafts.");

        var caller = await pi.GetCurrentUserAsync(ct);
        var resolver = new TimeChangeResolver(pi);
        var clarifications = new List<Clarification>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<DraftRow>();
        for (var i = 0; i < changes.Count; i++)
        {
            if (await BuildRowAsync(i, changes[i], caller, resolver, clarifications, seen, isUndo: undoOf is not null, ct) is { } row)
                rows.Add(row);
        }

        if (clarifications.Count > 0)
            return new PlanResult { Clarifications = clarifications };
        if (rows.Count == 0)
            throw new TimeChangeException("nothing_to_change", "None of these changes would change anything.");
        for (var i = 0; i < rows.Count; i++)
            rows[i].Index = i;

        var weeks = await LoadWeeksAsync(rows, ct);
        var now = time.GetUtcNow();
        var token = DraftIds.NewConfirmToken();
        var draft = new Draft
        {
            Id = DraftIds.NewDraftId(),
            Status = DraftStatus.Draft,
            Intent = intent,
            ProposedBy = caller.Id,
            ProposedByName = caller.Name,
            ProposedAt = now,
            ExpiresAt = now + Lifetime,
            StatusChangedAt = now,
            UndoOf = undoOf,
            ConfirmTokenHash = DraftIds.Hash(token),
            Rows = rows
        };
        BuildViews(draft, weeks, caller);

        await store.CreateAsync(draft, ct);
        try
        {
            await store.PruneAsync(caller.Id, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping; never fail a plan over it.
        }
        return new PlanResult { Draft = draft, ConfirmToken = token };
    }

    private async Task<DraftRow?> BuildRowAsync(int i, ChangeInput c, PiUser caller, TimeChangeResolver resolver,
        List<Clarification> clarifications, HashSet<string> seen, bool isUndo, CancellationToken ct)
    {
        var n = i + 1;
        var action = (c.Action ?? "").Trim().ToLowerInvariant();
        if (action == ChangeAction.Create)
        {
            var date = ParseDate(n, c.Date);
            var hours = ParseHours(n, c.Hours);
            var user = await resolver.UserAsync(i, c.User, caller, ct);
            var task = await resolver.TaskAsync(i, c.TaskId, c.Project, c.Task, ct);
            if (user.Clarification is { } uc) clarifications.Add(uc);
            if (task.Clarification is { } tc) clarifications.Add(tc);
            if (user.Value is null || task.Value is null)
                return null;
            return new DraftRow
            {
                Index = i, Action = action, UserId = user.Value.Id, UserName = user.Value.Name,
                After = new EntryValues
                {
                    TaskId = task.Value.Id, TaskName = task.Value.Name, ProjectId = task.Value.ProjectId,
                    ProjectName = task.Value.ProjectName, Date = date, Hours = hours,
                    Billable = c.Billable ?? false, Description = Blank(c.Description)
                }
            };
        }

        if (action is not (ChangeAction.Update or ChangeAction.Delete))
            throw Invalid(n, "action must be create, update or delete.");
        if (string.IsNullOrWhiteSpace(c.TimeEntryId))
            throw Invalid(n, $"{action} needs timeEntryId.");
        var id = c.TimeEntryId.Trim();
        if (!seen.Add(id))
            throw Invalid(n, $"time entry {id} appears in more than one change. Combine them into one change.");

        var entry = await pi.GetEntryAsync(id, ct);
        if (entry is null)
        {
            clarifications.Add(new Clarification { ChangeIndex = i, Field = "timeEntryId", Query = id, Message = $"No time entry with id {id}." });
            return null;
        }
        var owner = entry.UserId == caller.Id
            ? caller
            : await resolver.UserByIdAsync(entry.UserId, ct) ?? new PiUser(entry.UserId, entry.UserId);
        var before = new EntryValues
        {
            TaskId = entry.TaskId, TaskName = entry.TaskName, ProjectId = entry.ProjectId, ProjectName = entry.ProjectName,
            Date = entry.Date, Hours = entry.Hours, Billable = entry.Billable, Description = Blank(entry.Description)
        };
        if (action == ChangeAction.Delete)
            return new DraftRow { Index = i, Action = action, UserId = owner.Id, UserName = owner.Name, TimeEntryId = id, Before = before };

        if (!string.IsNullOrWhiteSpace(c.User) && !isUndo)
            throw Invalid(n, "an update can't change whose time it is. Delete the entry and create a new one for the other person.");
        var after = before.Clone();
        if (c.Date is not null) after.Date = ParseDate(n, c.Date);
        if (c.Hours is not null) after.Hours = ParseHours(n, c.Hours);
        if (c.Billable is not null) after.Billable = c.Billable.Value;
        if (c.Description is not null) after.Description = Blank(c.Description);
        if (c.TaskId is not null || c.Project is not null || c.Task is not null)
        {
            var task = await resolver.TaskAsync(i, c.TaskId, c.Project, c.Task, ct);
            if (task.Clarification is { } tc)
            {
                clarifications.Add(tc);
                return null;
            }
            after.TaskId = task.Value!.Id;
            after.TaskName = task.Value.Name;
            after.ProjectId = task.Value.ProjectId;
            after.ProjectName = task.Value.ProjectName;
        }
        if (Same(before, after))
        {
            if (isUndo)
                return null; // already back to how it was
            throw Invalid(n, $"this update changes nothing on time entry {id}.");
        }
        return new DraftRow { Index = i, Action = action, UserId = owner.Id, UserName = owner.Name, TimeEntryId = id, Before = before, After = after };
    }

    private async Task<List<WeekData>> LoadWeeksAsync(List<DraftRow> rows, CancellationToken ct)
    {
        var weeks = new List<WeekData>();
        var touched = rows
            .SelectMany(r => new[] { r.Before?.Date, r.After?.Date }.OfType<DateOnly>().Select(d => (r.UserId, r.UserName, Date: d)))
            .Distinct()
            .OrderBy(t => t.Date);
        foreach (var (userId, userName, date) in touched)
        {
            if (weeks.Any(w => w.Contains(userId, date)))
                continue;
            var sheet = Weeks.Covering(await pi.SearchTimeSheetsAsync(userId, date, date, ct), date);
            var start = sheet?.Start ?? Weeks.MondayOf(date);
            var end = sheet?.End ?? Weeks.SundayOf(date);
            weeks.Add(new WeekData
            {
                User = new PiUser(userId, userName), Start = start, End = end, Sheet = sheet,
                Entries = await pi.SearchEntriesAsync(userId, start, end, ct)
            });
        }
        return weeks;
    }

    private static void BuildViews(Draft draft, List<WeekData> weeks, PiUser caller)
    {
        var rows = draft.Rows;
        var touchedIds = rows.Select(r => r.TimeEntryId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var w in weeks.OrderBy(w => w.User.Id == caller.Id ? 0 : 1).ThenBy(w => w.User.Name).ThenBy(w => w.Start))
        {
            var view = new WeekView
            {
                UserId = w.User.Id, UserName = w.User.Name, IsCaller = w.User.Id == caller.Id, Start = w.Start, End = w.End,
                TimesheetId = w.Sheet?.Id, TimesheetUrl = w.Sheet?.Url,
                Submitted = w.Sheet?.Submitted == true, Approved = w.Sheet?.Approved == true, Locked = w.Sheet?.Locked == true
            };
            foreach (var date in Weeks.Range(w.Start, w.End))
            {
                var existing = w.Entries.Where(e => e.Date == date).ToList();
                var here = rows.Where(r => r.UserId == w.User.Id && (r.Before?.Date == date || r.After?.Date == date)).ToList();
                var before = existing.Sum(e => e.Hours);
                var after = before
                    - here.Where(r => r.Before?.Date == date).Sum(r => r.Before!.Hours)
                    + here.Where(r => r.After?.Date == date).Sum(r => r.After!.Hours);
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if (here.Count == 0 && weekend && before == 0)
                    continue;

                view.Days.Add(new DayView
                {
                    Date = date, TotalBefore = before, TotalAfter = after, HasChanges = here.Count > 0,
                    RowIndexes = here.Select(r => r.Index).ToList(),
                    Context = existing.Where(e => !touchedIds.Contains(e.Id)).Select(e => new ContextEntry
                    {
                        TimeEntryId = e.Id, ProjectName = e.ProjectName, TaskName = e.TaskName,
                        Hours = e.Hours, Billable = e.Billable, Description = e.Description
                    }).ToList()
                });
                if (here.Count > 0)
                    draft.DayFingerprints[Weeks.DayKey(w.User.Id, date)] = Fingerprint.Compute(existing, w.Sheet);
            }
            view.TotalBefore = view.Days.Sum(d => d.TotalBefore);
            view.TotalAfter = view.Days.Sum(d => d.TotalAfter);
            draft.Weeks.Add(view);
        }

        AddWarnings(draft, weeks, caller);

        var befores = rows.Select(r => r.Before).OfType<EntryValues>().ToList();
        var afters = rows.Select(r => r.After).OfType<EntryValues>().ToList();
        draft.Totals = new DraftTotals
        {
            NetHours = afters.Sum(a => a.Hours) - befores.Sum(b => b.Hours),
            BillableDelta = afters.Where(a => a.Billable).Sum(a => a.Hours) - befores.Where(b => b.Billable).Sum(b => b.Hours),
            NonBillableDelta = afters.Where(a => !a.Billable).Sum(a => a.Hours) - befores.Where(b => !b.Billable).Sum(b => b.Hours),
            Changes = rows.Count,
            Days = draft.DayFingerprints.Count,
            People = rows.Select(r => r.UserId).Distinct().Count(),
            Warnings = rows.Sum(r => r.Warnings.Count)
        };
    }

    private static void AddWarnings(Draft draft, List<WeekData> weeks, PiUser caller)
    {
        foreach (var row in draft.Rows)
        {
            var date = (row.After ?? row.Before)!.Date;
            var week = weeks.First(w => w.Contains(row.UserId, date));
            var day = draft.Weeks.First(v => v.UserId == row.UserId && v.Start == week.Start).Days.First(d => d.Date == date);

            if (week.Sheet?.Locked == true) Warn(row, WarningCode.TimesheetLocked, "Timesheet is locked");
            else if (week.Sheet?.Approved == true) Warn(row, WarningCode.TimesheetApproved, "Timesheet already approved");
            else if (week.Sheet?.Submitted == true) Warn(row, WarningCode.TimesheetSubmitted, "Timesheet already submitted");

            if (row.After is { } after)
            {
                if (day.TotalAfter > 12m)
                    Warn(row, WarningCode.Over12Hours, $"Day goes over 12h ({Hours(day.TotalAfter)}h)");
                if (after.Hours * 4 != decimal.Truncate(after.Hours * 4))
                    Warn(row, WarningCode.NotQuarterHour, $"Not a whole quarter-hour ({Hours(after.Hours)}h)");
            }
            if (row.Action == ChangeAction.Create && IsLikelyDuplicate(row, draft.Rows, week.Entries))
                Warn(row, WarningCode.LikelyDuplicate, "Looks like a duplicate of an entry already on this day");
            if (row.UserId != caller.Id)
                Warn(row, WarningCode.OtherPerson, $"Changes {row.UserName}'s time");
        }
    }

    private static bool IsLikelyDuplicate(DraftRow row, List<DraftRow> rows, IReadOnlyList<PiEntry> weekEntries)
    {
        var a = row.After!;
        var deleted = rows.Where(r => r.Action == ChangeAction.Delete).Select(r => r.TimeEntryId).ToHashSet();
        var matchesExisting = weekEntries.Any(e =>
            e.Date == a.Date && e.TaskId == a.TaskId && !deleted.Contains(e.Id) &&
            (e.Hours == a.Hours || SameText(e.Description, a.Description)));
        var matchesEarlierRow = rows.Any(r =>
            r.Index < row.Index && r.Action == ChangeAction.Create && r.UserId == row.UserId &&
            r.After!.Date == a.Date && r.After.TaskId == a.TaskId && r.After.Hours == a.Hours);
        return matchesExisting || matchesEarlierRow;
    }

    private static void Warn(DraftRow row, string code, string message) => row.Warnings.Add(new DraftWarning { Code = code, Message = message });

    private static bool SameText(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Same(EntryValues a, EntryValues b)
        => a.TaskId == b.TaskId && a.Date == b.Date && a.Hours == b.Hours && a.Billable == b.Billable &&
           (a.Description ?? "") == (b.Description ?? "");

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string Hours(decimal h) => h.ToString("0.##", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(int n, string? value)
        => DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw Invalid(n, "date must be yyyy-MM-dd.");

    private static decimal ParseHours(int n, decimal? value)
        => value is > 0m and <= 24m ? value.Value : throw Invalid(n, "hours must be more than 0 and at most 24.");

    private static TimeChangeException Invalid(int n, string message) => new("invalid_change", $"Change {n}: {message}");
}
```

`Rows_carry_warnings` checks the duplicate rule. Row 1 creates 2h on t-1 on Tue, and te-4 (2h, t-1, Tue) isn't deleted in that draft, so it gets `likely_duplicate`. Row 0 (6h on Wed) takes Wed from 8h to 14h, so it gets `over_12h` with "14h".

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangePlannerTests"`
Expected: PASS (18 test cases).

- [ ] **Step 5: Commit**

```bash
git add core/TimeChanges/TimeChangePlanner.cs tests/TimeChanges/TimeChangePlannerTests.cs
git commit -m "feat(time-changes): planner builds drafts with week context, warnings and fingerprints"
```

---

### Task 6: Committer (all-or-nothing, stale check, reversal, cancel, expiry)

**Files:**
- Create: `core/TimeChanges/TimeChangeCommitter.cs`
- Create: `tests/TimeChanges/TimeChangeCommitterTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5. The tests use `TimeChangePlannerTests.Seeded()` and `TimeChangePlannerTests.TypicalChanges()`.
- Produces:
  - `class TimeChangeCommitter(ITimeEntryGateway pi, IDraftStore store, TimeProvider time)`, with `static readonly TimeSpan StuckAfter` (10 min)
  - `LoadAsync(PiUser caller, string id, CancellationToken ct) : Task<Draft>`: throws `TimeChangeException("not_found")`, and normalises expired or stuck drafts
  - `CommitAsync(PiUser caller, string draftId, string channel, string? confirmToken, CancellationToken ct) : Task<Draft>`: throws `TimeChangeException("bad_confirm_token")`
  - `CancelAsync(PiUser caller, string draftId, CancellationToken ct) : Task<Draft>`
  - `static IEnumerable<string> ChangedDayLabels(Draft d)`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/TimeChangeCommitterTests.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using Xunit;
using static ProjectInsightMCP.Tests.TimeChanges.TimeChangePlannerTests;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TimeChangeCommitterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-commit-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Drafts.T0);
    private readonly FakeGateway _pi = Seeded();
    private readonly FileDraftStore _store;
    private readonly TimeChangePlanner _planner;
    private readonly TimeChangeCommitter _committer;

    public TimeChangeCommitterTests()
    {
        _store = new FileDraftStore(_root, _time);
        _planner = new TimeChangePlanner(_pi, _store, _time);
        _committer = new TimeChangeCommitter(_pi, _store, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PiUser Alex => _pi.Caller;

    private async Task<(Draft Draft, string Token)> Plan(List<ChangeInput>? changes = null)
    {
        var result = await _planner.PlanAsync("Log Tuesday", changes ?? TypicalChanges(), default);
        return (result.Draft!, result.ConfirmToken!);
    }

    private Task<Draft> Commit(string id) => _committer.CommitAsync(Alex, id, CommitChannel.Agent, null, default);

    private PiEntry? Entry(string id) => _pi.Entries.FirstOrDefault(e => e.Id == id);

    [Fact]
    public async Task Commit_applies_every_row_in_order_and_keeps_a_receipt()
    {
        var (draft, _) = await Plan();

        var receipt = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, receipt.Status);
        Assert.Equal((CommitChannel.Agent, Drafts.T0), (receipt.Channel, receipt.CommittedAt));
        Assert.Equal(["create:te-100", "update:te-1", "delete:te-4"], _pi.Writes);
        Assert.All(receipt.Rows, r => Assert.Equal(RowOutcome.Applied, r.Outcome));
        Assert.Equal("te-100", receipt.Rows[0].SavedId);
        Assert.Equal(1.5m, Entry("te-1")!.Hours);
        Assert.Null(Entry("te-4"));
        Assert.Equal(DraftStatus.Committed, (await _store.GetAsync("u-1", draft.Id))!.Status);
    }

    [Fact]
    public async Task A_rejected_change_reverses_the_earlier_ones_last_first()
    {
        var (draft, _) = await Plan();
        _pi.FailWrite = (op, id) => op == "delete" && id == "te-4" ? "Task is closed to time entry." : null;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.Contains("change 3 of 3", result.Error);
        Assert.Contains("Task is closed to time entry.", result.Error);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal(["create:te-100", "update:te-1", "update:te-1", "delete:te-100"], _pi.Writes);
        foreach (var (key, fingerprint) in draft.DayFingerprints)
        {
            var date = DateOnly.Parse(key.Split('|')[1]);
            Assert.Equal(fingerprint, Fingerprint.Compute(_pi.Entries.Where(e => e.UserId == "u-1" && e.Date == date), null));
        }
    }

    [Fact]
    public async Task A_failed_reversal_is_reported_on_its_row()
    {
        var (draft, _) = await Plan();
        _pi.FailWrite = (op, _) => op == "delete" ? "Locked" : null;

        var result = await Commit(draft.Id);

        Assert.Equal([RowOutcome.ReverseFailed, RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal("Locked", result.Rows[0].Error);
    }

    [Fact]
    public async Task A_reversed_delete_recreates_the_entry()
    {
        var (draft, _) = await Plan([new() { Action = "delete", TimeEntryId = "te-4" }, new() { Action = "update", TimeEntryId = "te-1", Hours = 3m }]);
        _pi.FailWrite = (op, _) => op == "update" ? "Nope" : null;

        var result = await Commit(draft.Id);

        Assert.Equal(RowOutcome.Reversed, result.Rows[0].Outcome);
        Assert.Equal("te-100", result.Rows[0].SavedId);
        var recreated = Entry("te-100")!;
        Assert.Equal((TimeChangePlannerTests.Tue, 2.0m, "t-1", "API integration"), (recreated.Date, recreated.Hours, recreated.TaskId, recreated.Description));
    }

    [Fact]
    public async Task A_change_in_pi_since_planning_makes_the_draft_stale_and_writes_nothing()
    {
        var (draft, _) = await Plan();
        _pi.Add("te-9", "u-1", TimeChangePlannerTests.Tue, 1m, "t-3");

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Stale, result.Status);
        Assert.Equal(["You · Tue 27 Oct"], result.StaleDays);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task A_change_on_an_untouched_day_does_not_make_the_draft_stale()
    {
        var (draft, _) = await Plan();
        var wed = _pi.Entries.FindIndex(e => e.Id == "te-5");
        _pi.Entries[wed] = _pi.Entries[wed] with { Hours = 7m };

        Assert.Equal(DraftStatus.Committed, (await Commit(draft.Id)).Status);
    }

    [Fact]
    public async Task Submitting_the_timesheet_after_planning_makes_the_draft_stale()
    {
        var (draft, _) = await Plan();
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-1", TimeChangePlannerTests.Mon, new DateOnly(2026, 11, 1), true, false, false, null)];

        var result = await Commit(draft.Id);

        Assert.Equal(["You · Mon 26 Oct", "You · Tue 27 Oct"], result.StaleDays);
    }

    [Fact]
    public async Task An_expired_draft_cannot_be_committed()
    {
        var (draft, _) = await Plan();
        _time.Now = Drafts.T0.AddHours(24).AddMinutes(1);

        Assert.Equal(DraftStatus.Expired, (await Commit(draft.Id)).Status);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task Confirm_needs_the_drafts_token()
    {
        var (draft, token) = await Plan();

        var bad = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, DraftIds.NewConfirmToken(), default));
        var none = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, null, default));
        var receipt = await _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, token, default);

        Assert.Equal(("bad_confirm_token", "bad_confirm_token"), (bad.Code, none.Code));
        Assert.Equal((DraftStatus.Committed, CommitChannel.ConfirmedInChat, true), (receipt.Status, receipt.Channel, receipt.ConfirmTokenUsed));
    }

    [Fact]
    public async Task Committing_twice_applies_once()
    {
        var (draft, _) = await Plan();
        await Commit(draft.Id);
        var again = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, again.Status);
        Assert.Equal(3, _pi.Writes.Count);
    }

    [Fact]
    public async Task Another_user_cannot_see_or_act_on_the_draft()
    {
        var (draft, _) = await Plan();
        var jane = new PiUser("u-2", "Jane Doe");

        foreach (var attempt in new Func<Task>[]
        {
            () => _committer.LoadAsync(jane, draft.Id, default),
            () => _committer.CommitAsync(jane, draft.Id, CommitChannel.Agent, null, default),
            () => _committer.CancelAsync(jane, draft.Id, default)
        })
            Assert.Equal("not_found", (await Assert.ThrowsAsync<TimeChangeException>(attempt)).Code);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task A_commit_interrupted_midway_is_reported_as_failed_after_ten_minutes()
    {
        var (draft, _) = await Plan();
        await _store.TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing);

        _time.Now = Drafts.T0.AddMinutes(5);
        Assert.Equal(DraftStatus.Committing, (await _committer.LoadAsync(Alex, draft.Id, default)).Status);

        _time.Now = Drafts.T0.AddMinutes(11);
        var loaded = await _committer.LoadAsync(Alex, draft.Id, default);
        Assert.Equal(DraftStatus.Failed, loaded.Status);
        Assert.Contains("interrupted", loaded.Error);
        Assert.Contains("You · Mon 26 Oct", loaded.Error);
    }

    [Fact]
    public async Task Cancel_stops_a_draft_but_not_a_receipt()
    {
        var (draft, _) = await Plan();
        Assert.Equal(DraftStatus.Cancelled, (await _committer.CancelAsync(Alex, draft.Id, default)).Status);
        Assert.Equal(DraftStatus.Cancelled, (await Commit(draft.Id)).Status);
        Assert.Empty(_pi.Writes);

        var (second, _) = await Plan([new() { Action = "update", TimeEntryId = "te-5", Hours = 7m }]);
        await Commit(second.Id);
        Assert.Equal(DraftStatus.Committed, (await _committer.CancelAsync(Alex, second.Id, default)).Status);
    }

    [Fact]
    public async Task Committing_an_undo_restores_pi_and_marks_the_receipt_reverted()
    {
        var (draft, _) = await Plan();
        var receipt = await Commit(draft.Id);

        var undo = (await _planner.PlanUndoAsync(receipt, default)).Draft!;
        Assert.Equal(DraftStatus.Committed, (await Commit(undo.Id)).Status);

        Assert.Equal(undo.Id, (await _store.GetAsync("u-1", receipt.Id))!.RevertedBy);
        Assert.Null(Entry("te-100"));
        Assert.Equal(1.0m, Entry("te-1")!.Hours);
        Assert.Contains(_pi.Entries, e => e.Date == TimeChangePlannerTests.Tue && e.TaskId == "t-1" && e.Hours == 2.0m && e.Description == "API integration");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeCommitterTests"`
Expected: build FAILS — `TimeChangeCommitter` does not exist.

- [ ] **Step 3: Implement**

`core/TimeChanges/TimeChangeCommitter.cs`:

```csharp
using System.Globalization;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// Commits, cancels and loads drafts. PI has no transactions, so a commit checks that nothing
/// changed since planning, applies rows in order, and on the first failure reverses what it
/// applied, last first. Every status change goes through the store's locked transition.
/// </summary>
public sealed class TimeChangeCommitter(ITimeEntryGateway pi, IDraftStore store, TimeProvider time)
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Loads the caller's draft, first marking it expired, or failed if a commit was interrupted.</summary>
    public async Task<Draft> LoadAsync(PiUser caller, string id, CancellationToken ct)
    {
        var draft = await store.GetAsync(caller.Id, (id ?? "").Trim(), ct)
            ?? throw new TimeChangeException("not_found", $"No draft or receipt \"{id}\" for {caller.Name}.");
        var now = time.GetUtcNow();
        if (draft.Status == DraftStatus.Draft && now > draft.ExpiresAt)
            return await store.TryTransitionAsync(caller.Id, draft.Id, DraftStatus.Draft,
                d => SetStatus(d, DraftStatus.Expired, now), ct) ?? await LoadAsync(caller, draft.Id, ct);
        if (draft.Status == DraftStatus.Committing && now - draft.StatusChangedAt > StuckAfter)
            return await store.TryTransitionAsync(caller.Id, draft.Id, DraftStatus.Committing, d =>
            {
                SetStatus(d, DraftStatus.Failed, now);
                d.Error = "The commit was interrupted, so the outcome is unknown. Check these days in Project Insight: "
                          + string.Join(", ", ChangedDayLabels(d)) + ".";
            }, ct) ?? await LoadAsync(caller, draft.Id, ct);
        return draft;
    }

    public async Task<Draft> CommitAsync(PiUser caller, string draftId, string channel, string? confirmToken, CancellationToken ct)
    {
        var draft = await LoadAsync(caller, draftId, ct);
        if (draft.Status != DraftStatus.Draft)
            return draft; // committed, stale, cancelled…: report it as it is
        if (channel == CommitChannel.ConfirmedInChat &&
            (draft.ConfirmTokenUsed || confirmToken is null || !DraftIds.TokenMatches(confirmToken, draft.ConfirmTokenHash)))
            throw new TimeChangeException("bad_confirm_token",
                "That confirmation isn't valid for this draft. Confirm from the draft's own card, or ask the agent to commit it.");

        var now = time.GetUtcNow();
        var claimed = await store.TryTransitionAsync(caller.Id, draft.Id, DraftStatus.Draft, d =>
        {
            SetStatus(d, DraftStatus.Committing, now);
            d.Channel = channel;
            if (channel == CommitChannel.ConfirmedInChat)
                d.ConfirmTokenUsed = true;
        }, ct);
        if (claimed is null)
            return await LoadAsync(caller, draft.Id, ct); // another process claimed it first

        var stale = await FindStaleDaysAsync(claimed, ct);
        if (stale.Count > 0)
            return await FinishAsync(caller, claimed, d => { d.Status = DraftStatus.Stale; d.StaleDays = stale; }, ct);

        var applied = new List<DraftRow>();
        for (var i = 0; i < claimed.Rows.Count; i++)
        {
            var row = claimed.Rows[i];
            var result = await SafeAsync(() => ApplyAsync(claimed, row, ct));
            if (result.Success)
            {
                row.Outcome = RowOutcome.Applied;
                if (row.Action == ChangeAction.Create)
                    row.SavedId = result.SavedId;
                applied.Add(row);
                continue;
            }

            row.Outcome = RowOutcome.Rejected;
            row.Error = result.Error;
            foreach (var later in claimed.Rows.Skip(i + 1))
                later.Outcome = RowOutcome.NotAttempted;
            for (var j = applied.Count - 1; j >= 0; j--)
            {
                var done = applied[j];
                var reversed = await SafeAsync(() => ReverseAsync(claimed, done, ct));
                done.Outcome = reversed.Success ? RowOutcome.Reversed : RowOutcome.ReverseFailed;
                if (!reversed.Success)
                    done.Error = reversed.Error;
                else if (done.Action == ChangeAction.Delete)
                    done.SavedId = reversed.SavedId; // recreated under a new id
            }
            var error = $"Project Insight rejected change {i + 1} of {claimed.Rows.Count}: {result.Error}";
            return await FinishAsync(caller, claimed, d => { d.Status = DraftStatus.Failed; d.Error = error; }, ct);
        }

        var committed = await FinishAsync(caller, claimed, d =>
        {
            d.Status = DraftStatus.Committed;
            d.CommittedAt = time.GetUtcNow();
        }, ct);
        if (committed.UndoOf is { } receiptId)
            await store.TryTransitionAsync(caller.Id, receiptId, DraftStatus.Committed, r => r.RevertedBy = committed.Id, ct);
        return committed;
    }

    public async Task<Draft> CancelAsync(PiUser caller, string draftId, CancellationToken ct)
    {
        var draft = await LoadAsync(caller, draftId, ct);
        if (draft.Status is not (DraftStatus.Draft or DraftStatus.Stale))
            return draft;
        var now = time.GetUtcNow();
        return await store.TryTransitionAsync(caller.Id, draft.Id, draft.Status, d => SetStatus(d, DraftStatus.Cancelled, now), ct)
            ?? await LoadAsync(caller, draft.Id, ct);
    }

    /// <summary>"You · Tue 27 Oct" for each day the draft changes, in date order.</summary>
    public static IEnumerable<string> ChangedDayLabels(Draft d)
        => d.DayFingerprints.Keys.Order(StringComparer.Ordinal).Select(key =>
        {
            var bar = key.LastIndexOf('|');
            var userId = key[..bar];
            var date = DateOnly.ParseExact(key[(bar + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var week = d.Weeks.FirstOrDefault(w => w.UserId == userId);
            return Weeks.DayLabel(week is null ? userId : week.IsCaller ? "You" : week.UserName, date);
        });

    private async Task<List<string>> FindStaleDaysAsync(Draft draft, CancellationToken ct)
    {
        var stale = new List<string>();
        foreach (var week in draft.Weeks)
        {
            var days = draft.DayFingerprints.Keys
                .Where(k => k.StartsWith(week.UserId + "|", StringComparison.Ordinal))
                .Select(k => (Key: k, Date: DateOnly.ParseExact(k[(week.UserId.Length + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(k => week.Start <= k.Date && k.Date <= week.End)
                .OrderBy(k => k.Date)
                .ToList();
            if (days.Count == 0)
                continue;
            var entries = await pi.SearchEntriesAsync(week.UserId, week.Start, week.End, ct);
            var sheets = await pi.SearchTimeSheetsAsync(week.UserId, week.Start, week.End, ct);
            foreach (var (key, date) in days)
            {
                var now = Fingerprint.Compute(entries.Where(e => e.Date == date), Weeks.Covering(sheets, date));
                if (now != draft.DayFingerprints[key])
                    stale.Add(Weeks.DayLabel(week.IsCaller ? "You" : week.UserName, date));
            }
        }
        return stale;
    }

    private Task<PiWrite> ApplyAsync(Draft draft, DraftRow row, CancellationToken ct) => row.Action switch
    {
        ChangeAction.Create => pi.CreateEntryAsync(Write(draft, row, row.After!), ct),
        ChangeAction.Update => pi.UpdateEntryAsync(row.TimeEntryId!, Write(draft, row, row.After!), ct),
        _ => pi.DeleteEntryAsync(row.TimeEntryId!, ct)
    };

    private Task<PiWrite> ReverseAsync(Draft draft, DraftRow row, CancellationToken ct) => row.Action switch
    {
        ChangeAction.Create => pi.DeleteEntryAsync(row.SavedId!, ct),
        ChangeAction.Update => pi.UpdateEntryAsync(row.TimeEntryId!, Write(draft, row, row.Before!), ct),
        _ => pi.CreateEntryAsync(Write(draft, row, row.Before!), ct)
    };

    private static EntryWrite Write(Draft draft, DraftRow row, EntryValues v)
        => new(row.UserId, v.TaskId, v.Date, v.Hours, v.Billable, v.Description,
            draft.Weeks.FirstOrDefault(w => w.UserId == row.UserId && w.Start <= v.Date && v.Date <= w.End)?.TimesheetId);

    private static async Task<PiWrite> SafeAsync(Func<Task<PiWrite>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PiWrite(false, null, ex.Message);
        }
    }

    private async Task<Draft> FinishAsync(PiUser caller, Draft working, Action<Draft> outcome, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await store.TryTransitionAsync(caller.Id, working.Id, DraftStatus.Committing, d =>
        {
            d.Rows = working.Rows;
            outcome(d);
            d.StatusChangedAt = now;
        }, ct) ?? throw new InvalidOperationException($"Draft {working.Id} left 'committing' while it was being committed.");
    }

    private static void SetStatus(Draft d, string status, DateTimeOffset now)
    {
        d.Status = status;
        d.StatusChangedAt = now;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeCommitterTests"`
Expected: PASS (14 tests).

- [ ] **Step 5: Commit**

```bash
git add core/TimeChanges/TimeChangeCommitter.cs tests/TimeChanges/TimeChangeCommitterTests.cs
git commit -m "feat(time-changes): all-or-nothing commit with stale check, reversal and cancel"
```

---

### Task 7: Service facade, views and model-facing text

**Files:**
- Create: `core/TimeChanges/TimeChangeService.cs`
- Create: `core/TimeChanges/TimeChangeText.cs`
- Create: `tests/TimeChanges/TimeChangeServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1–6.
- Produces:
  - `class TimeChangesView { required string Kind ("draft"|"clarification"|"error"); string? Message; DraftView? Draft; List<Clarification>? Clarifications }`. This is the single `structuredContent` and `outputSchema` type for all six tools.
  - `class DraftView`, with `static DraftView From(Draft d, DateTimeOffset now)`
  - `class TimeChangeResponse { required TimeChangesView View; required string Text; bool IsError; string? ConfirmToken }`
  - `class TimeChangeService(ITimeEntryGateway pi, TimeChangePlanner planner, TimeChangeCommitter committer, TimeProvider time)`, with `static readonly TimeSpan UndoWindow` (24h) and `PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct)`, `CommitAsync(string draftId, CancellationToken ct)`, `ConfirmAsync(string draftId, string confirmToken, CancellationToken ct)`, `CancelAsync(string draftId, CancellationToken ct)`, `UndoAsync(string receiptId, CancellationToken ct)` and `GetAsync(string id, CancellationToken ct)`, all returning `Task<TimeChangeResponse>`
  - `static class TimeChangeText`, with `For(TimeChangesView view) : string`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/TimeChangeServiceTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectInsightMCP.TimeChanges;
using Xunit;
using static ProjectInsightMCP.Tests.TimeChanges.TimeChangePlannerTests;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TimeChangeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-svc-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Drafts.T0);
    private readonly FakeGateway _pi = Seeded();
    private readonly TimeChangeService _service;

    public TimeChangeServiceTests()
    {
        var store = new FileDraftStore(_root, _time);
        _service = new TimeChangeService(_pi, new TimeChangePlanner(_pi, store, _time), new TimeChangeCommitter(_pi, store, _time), _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Plan_response_carries_the_draft_the_token_and_readable_text()
    {
        var r = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);

        Assert.False(r.IsError);
        Assert.Equal("draft", r.View.Kind);
        Assert.Equal(DraftStatus.Draft, r.View.Draft!.Status);
        Assert.NotNull(r.ConfirmToken);
        Assert.Contains("Nothing has changed yet", r.Text);
        Assert.Contains("1. create · You · Tue 27 Oct · Acme Portal › Development · +4h (billable) — \"Order sync\"", r.Text);
        Assert.Contains("2. update · You · Mon 26 Oct · Acme Portal › Client calls · 1h → 1.5h", r.Text);
        Assert.Contains("3. delete · You · Tue 27 Oct · Acme Portal › Development · −2h", r.Text);
        Assert.DoesNotContain(r.ConfirmToken!, r.Text);
    }

    [Fact]
    public async Task Clarification_is_not_an_error_and_lists_candidates()
    {
        var r = await _service.PlanAsync("Log",
            [new() { Action = "create", Date = "2026-10-27", Hours = 1m, Project = "Acme", Task = "Development" }], default);

        Assert.False(r.IsError);
        Assert.Equal("clarification", r.View.Kind);
        Assert.Null(r.ConfirmToken);
        Assert.Contains("No draft was created", r.Text);
        Assert.Contains("Acme Portal (p-1); Acme Support (p-2)", r.Text);
    }

    [Fact]
    public async Task Invalid_input_is_an_error_response()
    {
        var r = await _service.PlanAsync("", TypicalChanges(), default);

        Assert.True(r.IsError);
        Assert.Equal("error", r.View.Kind);
        Assert.Contains("intent", r.Text);
    }

    [Fact]
    public async Task Commit_text_offers_undo_until_the_window_closes()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var r = await _service.CommitAsync(plan.View.Draft!.Id, default);

        Assert.Equal(Drafts.T0.AddHours(24), r.View.Draft!.UndoUntil);
        Assert.Contains("Undo is available until 2026-10-28 09:00 UTC", r.Text);

        _time.Now = Drafts.T0.AddHours(25);
        Assert.Null((await _service.GetAsync(plan.View.Draft.Id, default)).View.Draft!.UndoUntil);
    }

    [Fact]
    public async Task Undo_drafts_the_reverse_and_can_only_happen_once()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var receiptId = plan.View.Draft!.Id;
        await _service.CommitAsync(receiptId, default);

        var undo = await _service.UndoAsync(receiptId, default);
        Assert.Equal(receiptId, undo.View.Draft!.UndoOf);
        Assert.NotNull(undo.ConfirmToken);
        Assert.StartsWith($"Undo draft (reverses {receiptId})", undo.Text);

        var confirmed = await _service.ConfirmAsync(undo.View.Draft.Id, undo.ConfirmToken!, default);
        Assert.Equal(DraftStatus.Committed, confirmed.View.Draft!.Status);
        Assert.Contains("It has since been undone", (await _service.GetAsync(receiptId, default)).Text);

        var again = await _service.UndoAsync(receiptId, default);
        Assert.True(again.IsError);
        Assert.Contains("already undone", again.Text);
    }

    [Fact]
    public async Task Undo_after_24_hours_is_refused()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        await _service.CommitAsync(plan.View.Draft!.Id, default);
        _time.Now = Drafts.T0.AddHours(24).AddMinutes(1);

        var r = await _service.UndoAsync(plan.View.Draft.Id, default);

        Assert.True(r.IsError);
        Assert.Contains("24 hours", r.Text);
    }

    [Fact]
    public async Task Unknown_ids_are_errors_and_get_never_returns_a_token()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);

        Assert.True((await _service.GetAsync("d_nope", default)).IsError);
        Assert.Null((await _service.GetAsync(plan.View.Draft!.Id, default)).ConfirmToken);
    }

    [Fact]
    public async Task Responses_never_contain_rates_or_money()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var commit = await _service.CommitAsync(plan.View.Draft!.Id, default);

        foreach (var r in new[] { plan, commit })
        {
            var json = JsonSerializer.Serialize(r.View, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + r.Text;
            foreach (var word in new[] { "rateBill", "rateBurden", "billableTotal", "actualTotal", "RateBill", "BillableTotal" })
                Assert.DoesNotContain(word, json);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeServiceTests"`
Expected: build FAILS — `TimeChangeService` does not exist.

- [ ] **Step 3: Implement**

`core/TimeChanges/TimeChangeService.cs`:

```csharp
namespace ProjectInsightMCP.TimeChanges;

/// <summary>What every time-change tool returns as structuredContent (one schema for all six).</summary>
public sealed class TimeChangesView
{
    /// <summary>"draft" (a draft or receipt is in Draft), "clarification" or "error".</summary>
    public required string Kind { get; init; }
    public string? Message { get; init; }
    public DraftView? Draft { get; init; }
    public List<Clarification>? Clarifications { get; init; }
}

/// <summary>A draft as the card and agent see it: no token hash, no fingerprints.</summary>
public sealed class DraftView
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public required string Intent { get; init; }
    public required string ProposedByName { get; init; }
    public required DateTimeOffset ProposedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public string? Channel { get; init; }
    public string? UndoOf { get; init; }
    public string? RevertedBy { get; init; }
    /// <summary>Until when an undo can be drafted; null when it can't.</summary>
    public DateTimeOffset? UndoUntil { get; init; }
    public required List<DraftRow> Rows { get; init; }
    public required List<WeekView> Weeks { get; init; }
    public required DraftTotals Totals { get; init; }
    public string? Error { get; init; }
    public List<string>? StaleDays { get; init; }

    public static DraftView From(Draft d, DateTimeOffset now) => new()
    {
        Id = d.Id,
        Status = d.Status,
        Intent = d.Intent,
        ProposedByName = d.ProposedByName,
        ProposedAt = d.ProposedAt,
        ExpiresAt = d.ExpiresAt,
        CommittedAt = d.CommittedAt,
        Channel = d.Channel,
        UndoOf = d.UndoOf,
        RevertedBy = d.RevertedBy,
        UndoUntil = d.Status == DraftStatus.Committed && d.RevertedBy is null && d.CommittedAt is { } at
                    && now <= at + TimeChangeService.UndoWindow
            ? at + TimeChangeService.UndoWindow
            : null,
        Rows = d.Rows,
        Weeks = d.Weeks,
        Totals = d.Totals,
        Error = d.Error,
        StaleDays = d.StaleDays.Count > 0 ? d.StaleDays : null
    };
}

public sealed class TimeChangeResponse
{
    public required TimeChangesView View { get; init; }
    public required string Text { get; init; }
    public bool IsError { get; init; }
    /// <summary>Plan and undo only. Goes in the tool result's _meta, for the card, never in text.</summary>
    public string? ConfirmToken { get; init; }
}

/// <summary>The operations behind the six tools. Domain errors become error responses, not exceptions.</summary>
public sealed class TimeChangeService(ITimeEntryGateway pi, TimeChangePlanner planner, TimeChangeCommitter committer, TimeProvider time)
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    public Task<TimeChangeResponse> PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct)
        => Run(async () => FromPlan(await planner.PlanAsync(intent, changes, ct)));

    public Task<TimeChangeResponse> CommitAsync(string draftId, CancellationToken ct)
        => Run(async () => FromDraft(await committer.CommitAsync(
            await pi.GetCurrentUserAsync(ct), draftId, CommitChannel.Agent, null, ct)));

    public Task<TimeChangeResponse> ConfirmAsync(string draftId, string confirmToken, CancellationToken ct)
        => Run(async () => FromDraft(await committer.CommitAsync(
            await pi.GetCurrentUserAsync(ct), draftId, CommitChannel.ConfirmedInChat, confirmToken, ct)));

    public Task<TimeChangeResponse> CancelAsync(string draftId, CancellationToken ct)
        => Run(async () => FromDraft(await committer.CancelAsync(await pi.GetCurrentUserAsync(ct), draftId, ct)));

    public Task<TimeChangeResponse> GetAsync(string id, CancellationToken ct)
        => Run(async () => FromDraft(await committer.LoadAsync(await pi.GetCurrentUserAsync(ct), id, ct)));

    public Task<TimeChangeResponse> UndoAsync(string receiptId, CancellationToken ct) => Run(async () =>
    {
        var receipt = await committer.LoadAsync(await pi.GetCurrentUserAsync(ct), receiptId, ct);
        if (receipt.Status != DraftStatus.Committed)
            throw new TimeChangeException("undo_unavailable",
                $"{receipt.Id} is {receipt.Status}, not committed, so there is nothing to undo.");
        if (receipt.RevertedBy is not null)
            throw new TimeChangeException("undo_unavailable", $"{receipt.Id} was already undone by {receipt.RevertedBy}.");
        if (receipt.CommittedAt is not { } at || time.GetUtcNow() > at + UndoWindow)
            throw new TimeChangeException("undo_unavailable",
                $"Undo for {receipt.Id} was available for 24 hours after it was committed, and that has passed.");
        return FromPlan(await planner.PlanUndoAsync(receipt, ct));
    });

    private static async Task<TimeChangeResponse> Run(Func<Task<TimeChangeResponse>> action)
    {
        try
        {
            return await action();
        }
        catch (TimeChangeException ex)
        {
            return Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error($"Something went wrong: {ex.Message}");
        }

        static TimeChangeResponse Error(string message)
        {
            var view = new TimeChangesView { Kind = "error", Message = message };
            return new TimeChangeResponse { View = view, Text = TimeChangeText.For(view), IsError = true };
        }
    }

    private TimeChangeResponse FromPlan(PlanResult plan)
    {
        if (plan.Draft is null)
        {
            var view = new TimeChangesView { Kind = "clarification", Message = "No draft was created.", Clarifications = plan.Clarifications };
            return new TimeChangeResponse { View = view, Text = TimeChangeText.For(view) };
        }
        var drafted = FromDraft(plan.Draft);
        return new TimeChangeResponse { View = drafted.View, Text = drafted.Text, ConfirmToken = plan.ConfirmToken };
    }

    private TimeChangeResponse FromDraft(Draft draft)
    {
        var view = new TimeChangesView { Kind = "draft", Draft = DraftView.From(draft, time.GetUtcNow()) };
        return new TimeChangeResponse { View = view, Text = TimeChangeText.For(view) };
    }
}
```

`core/TimeChanges/TimeChangeText.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// The text block of every time-change result. It must read on its own (clients without cards
/// show only this) and tell the model what to do next.
/// </summary>
public static class TimeChangeText
{
    public static string For(TimeChangesView view) => view.Kind switch
    {
        "clarification" => Clarify(view.Clarifications ?? new()),
        "draft" when view.Draft is { } d => Draft(d),
        _ => view.Message ?? "Something went wrong."
    };

    private static string Clarify(List<Clarification> items)
    {
        var sb = new StringBuilder("No draft was created. Resolve these and call plan_time_changes again:");
        foreach (var c in items)
        {
            sb.Append($"\n- Change {c.ChangeIndex + 1}: {c.Message}");
            if (c.Candidates.Count > 0)
                sb.Append(" Candidates: ").Append(string.Join("; ", c.Candidates.Select(x => $"{x.Name} ({x.Id})")));
        }
        return sb.ToString();
    }

    private static string Draft(DraftView d)
    {
        var t = d.Totals;
        var sb = new StringBuilder();
        sb.Append(d.Status switch
        {
            DraftStatus.Draft =>
                $"{(d.UndoOf is null ? "Draft" : $"Undo draft (reverses {d.UndoOf})")} {d.Id}: {Count(t.Changes, "change", "changes")} " +
                $"across {Count(t.Days, "day", "days")} for {Count(t.People, "person", "people")}, net {Signed(t.NetHours)}h " +
                $"(billable {Signed(t.BillableDelta)}h). Nothing has changed yet. Summarize the draft for the user and ask them " +
                "to confirm. They can press Confirm on the card, or tell you to commit it with commit_time_changes.",
            DraftStatus.Committing => $"Draft {d.Id} is being committed right now. Check again with get_time_changes.",
            DraftStatus.Committed =>
                $"Committed {d.Id}: {Count(t.Changes, "change", "changes")} applied " +
                $"({(d.Channel == CommitChannel.Agent ? "committed by the agent" : "confirmed by the user in chat")})." +
                (d.RevertedBy is not null ? $" It has since been undone by {d.RevertedBy}."
                 : d.UndoUntil is { } until ? $" Undo is available until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC with undo_time_changes."
                 : ""),
            DraftStatus.Stale =>
                $"Nothing was changed: Project Insight changed after draft {d.Id} was made ({string.Join(", ", d.StaleDays ?? new())}). " +
                "Read the current entries and draft the changes again.",
            DraftStatus.Failed => $"Nothing was kept from draft {d.Id}. {d.Error}",
            DraftStatus.Cancelled => $"Draft {d.Id} was cancelled. Nothing was changed.",
            DraftStatus.Expired => $"Draft {d.Id} expired without being confirmed. Nothing was changed. Draft the changes again if they are still wanted.",
            _ => $"Draft {d.Id} is {d.Status}."
        });
        sb.Append($"\nIntent: {d.Intent}");
        foreach (var r in d.Rows)
            sb.Append('\n').Append(Row(r, d));
        var reverseFailed = d.Rows.Where(r => r.Outcome == RowOutcome.ReverseFailed).ToList();
        if (reverseFailed.Count > 0)
            sb.Append("\nThese could not be reversed and need checking in Project Insight: ")
              .Append(string.Join("; ", reverseFailed.Select(r => $"change {r.Index + 1} ({r.TimeEntryId ?? r.SavedId})")));
        return sb.ToString();
    }

    private static string Row(DraftRow r, DraftView d)
    {
        var v = (r.After ?? r.Before)!;
        var who = d.Weeks.Any(w => w.UserId == r.UserId && w.IsCaller) ? "You" : r.UserName;
        var hours = r.Action switch
        {
            ChangeAction.Create => $"+{H(r.After!.Hours)}h",
            ChangeAction.Delete => $"−{H(r.Before!.Hours)}h",
            _ => $"{H(r.Before!.Hours)}h → {H(r.After!.Hours)}h"
        };
        var line = new StringBuilder(
            $"{r.Index + 1}. {r.Action} · {who} · {v.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture)} · {v.ProjectName} › {v.TaskName} · {hours}");
        if (v.Billable) line.Append(" (billable)");
        if (v.Description is { } desc) line.Append($" — \"{desc}\"");
        if (r is { Action: ChangeAction.Update, Before: { } b, After: { } a })
        {
            if (b.Date != a.Date) line.Append($"; moved from {b.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture)}");
            if (b.TaskId != a.TaskId) line.Append($"; was {b.ProjectName} › {b.TaskName}");
        }
        foreach (var w in r.Warnings) line.Append($" [warning: {w.Message}]");
        if (r.Outcome is { } outcome && outcome != RowOutcome.Applied)
            line.Append($" [{outcome.Replace('_', ' ')}{(r.Error is null ? "" : ": " + r.Error)}]");
        return line.ToString();
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string H(decimal h) => h.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Signed(decimal h) => h > 0 ? "+" + H(h) : h < 0 ? "−" + H(-h) : "±0";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeServiceTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add core/TimeChanges/TimeChangeService.cs core/TimeChanges/TimeChangeText.cs tests/TimeChanges/TimeChangeServiceTests.cs
git commit -m "feat(time-changes): service facade, views and model-facing text"
```

---

### Task 8: The review card

**Files:**
- Create: `core/TimeChanges/Card/time-changes.html`
- Create: `core/TimeChanges/Card/TimeChangeCard.cs`
- Modify: `core/ProjectInsightMCP.Core.csproj` (embed the card)
- Create: `tests/TimeChanges/TimeChangeCardTests.cs`

**Interfaces:**
- Consumes: the JSON shape of `TimeChangesView` (Task 7), camelCase as serialized by `JsonSerializerDefaults.Web`. The tool names and argument names come from Task 9: `get_time_changes {id}`, `confirm_time_changes {draftId, confirmToken}`, `cancel_time_changes {draftId}`, `undo_time_changes {receiptId}`. The confirm token arrives in the tool result's `_meta.confirmToken`.
- Produces: `TimeChangeCard.Html : string`.

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/TimeChangeCardTests.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class TimeChangeCardTests
{
    private static readonly string Html = TimeChangeCard.Html;

    [Fact]
    public void Card_is_embedded_and_self_contained()
    {
        Assert.StartsWith("<!doctype html>", Html, StringComparison.OrdinalIgnoreCase);
        foreach (var banned in new[]
                 {
                     "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function",
                     "fetch(", "XMLHttpRequest", "http://", "https://", "<link", "src=", "@import"
                 })
            Assert.DoesNotContain(banned, Html);
    }

    [Fact]
    public void Card_speaks_the_mcp_apps_protocol_and_calls_the_time_change_tools()
    {
        foreach (var expected in new[]
                 {
                     "ui/initialize", "ui/notifications/initialized", "ui/notifications/tool-result",
                     "ui/notifications/host-context-changed", "ui/notifications/size-changed", "ui/open-link",
                     "tools/call", "get_time_changes", "confirm_time_changes", "cancel_time_changes", "undo_time_changes",
                     "structuredContent", "confirmToken", "event.source !== window.parent"
                 })
            Assert.Contains(expected, Html);
    }

    [Fact]
    public void Card_has_light_and_dark_tokens()
    {
        Assert.Contains(":root {", Html);
        Assert.Contains("@media (prefers-color-scheme: dark)", Html);
        Assert.Contains(":root:not([data-theme=\"light\"])", Html);
        Assert.Contains(":root[data-theme=\"dark\"]", Html);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeCardTests"`
Expected: build FAILS — `TimeChangeCard` does not exist.

- [ ] **Step 3: Implement**

Add to `core/ProjectInsightMCP.Core.csproj`, as a new `<ItemGroup>` before the `InternalsVisibleTo` group:

```xml
  <ItemGroup>
    <EmbeddedResource Include="TimeChanges/Card/time-changes.html" LogicalName="ProjectInsightMCP.TimeChanges.Card.time-changes.html" />
  </ItemGroup>
```

`core/TimeChanges/Card/TimeChangeCard.cs`:

```csharp
namespace ProjectInsightMCP.TimeChanges;

/// <summary>The MCP Apps review card, embedded in Core so any host can serve it.</summary>
public static class TimeChangeCard
{
    private const string ResourceName = "ProjectInsightMCP.TimeChanges.Card.time-changes.html";

    private static readonly Lazy<string> Content = new(() =>
    {
        using var stream = typeof(TimeChangeCard).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Html => Content.Value;
}
```

`core/TimeChanges/Card/time-changes.html`. This is the whole file. It's self-contained, and every DOM node is created with `createElement` and text nodes.

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Time changes</title>
<style>
  :root {
    --bg: #ffffff; --fg: #1f2328; --mut: #656d76; --faint: #8c959f; --line: #d8dee4; --soft: #f6f8fa;
    --acc: #0b6bcb; --pos: #1a7f37; --neg: #cf222e; --warn: #9a6700; --warnbg: #fff8c5; --chg: #f0f7ff;
    --okbg: #dafbe1; --okfg: #116329; --stalebg: #fff8c5; --stalefg: #7d4e00; --failbg: #ffebe9; --failfg: #a40e26;
    --mutebg: #eaeef2; --mutefg: #57606a; --draftbg: #ddf4ff; --draftfg: #0550ae; --otherbg: #fbefff; --otherfg: #8250df;
    --primary: #1f883d; --primaryfg: #ffffff;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #0d1117; --fg: #e6edf3; --mut: #9198a1; --faint: #6e7681; --line: #30363d; --soft: #161b22;
      --acc: #4493f8; --pos: #3fb950; --neg: #f85149; --warn: #d29922; --warnbg: #3b2e0a; --chg: #0c2d4a;
      --okbg: #0f3d1e; --okfg: #56d364; --stalebg: #3b2e0a; --stalefg: #e3b341; --failbg: #490202; --failfg: #ff7b72;
      --mutebg: #21262d; --mutefg: #9198a1; --draftbg: #0c2d4a; --draftfg: #79c0ff; --otherbg: #2a1a3e; --otherfg: #d2a8ff;
      --primary: #238636; --primaryfg: #ffffff;
    }
  }
  :root[data-theme="dark"] {
    --bg: #0d1117; --fg: #e6edf3; --mut: #9198a1; --faint: #6e7681; --line: #30363d; --soft: #161b22;
    --acc: #4493f8; --pos: #3fb950; --neg: #f85149; --warn: #d29922; --warnbg: #3b2e0a; --chg: #0c2d4a;
    --okbg: #0f3d1e; --okfg: #56d364; --stalebg: #3b2e0a; --stalefg: #e3b341; --failbg: #490202; --failfg: #ff7b72;
    --mutebg: #21262d; --mutefg: #9198a1; --draftbg: #0c2d4a; --draftfg: #79c0ff; --otherbg: #2a1a3e; --otherfg: #d2a8ff;
    --primary: #238636; --primaryfg: #ffffff;
  }
  * { box-sizing: border-box; }
  html, body { margin: 0; background: var(--bg); color: var(--fg); }
  body { font: 13px/1.45 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
  .card { padding: 12px 14px; max-width: 640px; }
  .muted { color: var(--mut); }
  .pill { display: inline-block; font-size: 11px; font-weight: 600; padding: 2px 8px; border-radius: 999px; }
  .p-draft { background: var(--draftbg); color: var(--draftfg); }
  .p-ok { background: var(--okbg); color: var(--okfg); }
  .p-stale { background: var(--stalebg); color: var(--stalefg); }
  .p-fail { background: var(--failbg); color: var(--failfg); }
  .p-mute { background: var(--mutebg); color: var(--mutefg); }
  .intent { font-size: 14px; font-weight: 600; margin-top: 4px; }
  .by { color: var(--mut); font-size: 12px; margin-bottom: 8px; }
  .kpis { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; margin: 8px 0 6px; }
  .kpi { background: var(--soft); border-radius: 8px; padding: 6px 8px; min-width: 0; }
  .kpi b { display: block; font-size: 17px; font-variant-numeric: tabular-nums; }
  .kpi span { font-size: 11px; color: var(--mut); }
  .pos { color: var(--pos); } .neg { color: var(--neg); } .warn { color: var(--warn); }
  .note { border-radius: 8px; padding: 8px 10px; font-size: 12px; margin: 8px 0; }
  .n-stale { background: var(--stalebg); color: var(--stalefg); }
  .n-fail { background: var(--failbg); color: var(--failfg); }
  .n-mute { background: var(--soft); color: var(--mutefg); }
  .week { border-top: 1px solid var(--line); padding-top: 8px; margin-top: 8px; }
  .whead { display: grid; grid-template-columns: 1fr auto 52px; gap: 8px; align-items: baseline; margin-bottom: 4px; }
  .other { font-size: 11px; font-weight: 600; padding: 1px 6px; border-radius: 4px; background: var(--otherbg); color: var(--otherfg); margin-left: 6px; }
  .link { font-size: 11px; color: var(--acc); margin-left: 6px; text-decoration: none; cursor: pointer; }
  .tot { font-variant-numeric: tabular-nums; white-space: nowrap; color: var(--mut); }
  .tot b { color: var(--fg); }
  .d { font-variant-numeric: tabular-nums; text-align: right; font-weight: 600; }
  .dline { display: grid; grid-template-columns: 12px 1fr auto 52px; gap: 8px; align-items: baseline; padding: 3px 0; cursor: pointer; }
  .dline:focus-visible { outline: 2px solid var(--acc); outline-offset: 2px; border-radius: 4px; }
  .dline.quiet { color: var(--faint); }
  .dline.quiet .tot { color: var(--faint); }
  .car { color: var(--faint); font-size: 10px; }
  .rows { margin: 2px 0 6px 20px; }
  .row { display: grid; grid-template-columns: 12px 1fr auto 52px; gap: 8px; align-items: baseline; padding: 3px 6px; margin: 0 -6px; border-radius: 6px; }
  .row.ctx { color: var(--faint); }
  .row.ctx .hrs { color: var(--faint); }
  .row.chg { background: var(--chg); }
  .row.del .label { text-decoration: line-through; text-decoration-color: var(--neg); }
  .act { font-weight: 700; text-align: center; }
  .a-c { color: var(--pos); } .a-d { color: var(--neg); } .a-u { color: var(--acc); }
  .task { min-width: 0; overflow-wrap: anywhere; }
  .task small { display: block; color: var(--mut); }
  .hrs { font-variant-numeric: tabular-nums; white-space: nowrap; color: var(--mut); }
  .hrs b { color: var(--fg); }
  .bill { font-size: 10px; padding: 0 5px; border-radius: 4px; border: 1px solid var(--line); color: var(--mut); margin-left: 4px; }
  .w { display: inline-block; font-size: 11px; padding: 1px 6px; border-radius: 4px; background: var(--warnbg); color: var(--warn); margin: 2px 4px 0 0; }
  .st { display: inline-block; font-size: 11px; font-weight: 600; padding: 0 6px; border-radius: 4px; margin-left: 4px; background: var(--mutebg); color: var(--mutefg); }
  .st-rejected, .st-reverse_failed { background: var(--failbg); color: var(--failfg); }
  .err { color: var(--failfg); }
  .btns { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 12px; }
  .btn { border: 1px solid var(--line); background: var(--bg); color: var(--fg); border-radius: 6px; padding: 5px 12px; font: inherit; font-weight: 600; cursor: pointer; }
  .btn.primary { background: var(--primary); border-color: var(--primary); color: var(--primaryfg); }
  .btn:disabled { opacity: 0.5; cursor: default; }
  .hint { font-size: 11px; color: var(--faint); }
  .error { color: var(--failfg); }
  @media (max-width: 420px) { .kpis { grid-template-columns: repeat(2, 1fr); } }
</style>
</head>
<body>
<div id="app" class="card"></div>
<script>
(() => {
  "use strict";

  const app = document.getElementById("app");
  const state = { view: null, token: null, busy: false, error: null, toggled: new Set(), refreshed: false };
  const pending = new Map();
  let nextId = 1;

  // ---- MCP Apps messaging: JSON-RPC 2.0 over postMessage with the host ----

  function send(message) { window.parent.postMessage(message, "*"); }

  function notify(method, params) { send({ jsonrpc: "2.0", method, params }); }

  function request(method, params) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      send({ jsonrpc: "2.0", id, method, params });
      setTimeout(() => {
        if (pending.delete(id)) reject(new Error("The host did not reply."));
      }, 60000);
    });
  }

  window.addEventListener("message", (event) => {
    if (event.source !== window.parent) return;
    const msg = event.data;
    if (!msg || msg.jsonrpc !== "2.0") return;

    if (msg.id !== undefined && !msg.method) {
      const waiting = pending.get(msg.id);
      if (!waiting) return;
      pending.delete(msg.id);
      if (msg.error) waiting.reject(new Error(msg.error.message || "The request failed."));
      else waiting.resolve(msg.result);
      return;
    }

    switch (msg.method) {
      case "ui/notifications/tool-result":
        onToolResult(msg.params);
        break;
      case "ui/notifications/host-context-changed":
        applyHostContext(msg.params);
        break;
      case "ui/resource-teardown":
        if (msg.id !== undefined) send({ jsonrpc: "2.0", id: msg.id, result: {} });
        break;
      default:
        if (msg.id !== undefined && msg.method)
          send({ jsonrpc: "2.0", id: msg.id, error: { code: -32601, message: "Method not found" } });
    }
  });

  function applyHostContext(context) {
    if (context && (context.theme === "dark" || context.theme === "light"))
      document.documentElement.dataset.theme = context.theme;
  }

  // ---- tool results ----

  function readView(result) {
    if (!result) return null;
    if (result.structuredContent && result.structuredContent.kind) return result.structuredContent;
    const text = (result.content || []).find((c) => c && c.type === "text");
    if (text) {
      try {
        const parsed = JSON.parse(text.text);
        if (parsed && parsed.kind) return parsed;
      } catch (_) { /* plain text, not JSON */ }
    }
    return null;
  }

  function textOf(result) {
    const text = result && (result.content || []).find((c) => c && c.type === "text");
    return text ? text.text : "";
  }

  function takeToken(result) {
    const token = result && result._meta && result._meta.confirmToken;
    if (token) state.token = token;
  }

  function onToolResult(result) {
    const view = readView(result);
    takeToken(result);
    if (!view) {
      state.error = textOf(result) || null;
      render();
      return;
    }
    state.view = view;
    state.error = null;
    render();
    // A re-shown card (scrolled back to, reloaded) must show the draft's current status.
    if (!state.refreshed && view.draft && view.draft.id) {
      state.refreshed = true;
      refresh(view.draft.id);
    }
  }

  async function refresh(id) {
    try {
      const result = await request("tools/call", { name: "get_time_changes", arguments: { id } });
      const view = readView(result);
      if (view && view.kind === "draft") {
        state.view = view;
        render();
      }
    } catch (_) { /* keep showing what we have */ }
  }

  async function act(name, args) {
    if (state.busy) return;
    state.busy = true;
    state.error = null;
    render();
    try {
      const result = await request("tools/call", { name, arguments: args });
      takeToken(result);
      const view = readView(result);
      if (view && view.kind === "draft") state.view = view;
      else state.error = (view && view.message) || textOf(result) || "Something went wrong.";
    } catch (e) {
      state.error = e.message;
    }
    state.busy = false;
    render();
  }

  function openLink(url) {
    request("ui/open-link", { url }).catch(() => window.open(url, "_blank", "noopener"));
  }

  // ---- formatting ----

  function hrs(n) {
    const x = Math.round(Number(n || 0) * 100) / 100;
    return Number.isInteger(x) ? x.toFixed(1) : String(x);
  }

  function signed(n) {
    const x = Number(n || 0);
    return (x > 0 ? "+" : x < 0 ? "−" : "±") + hrs(Math.abs(x));
  }

  // Dates arrive as yyyy-MM-dd; build them in local time so they never shift a day.
  function day(iso, options) {
    const [y, m, d] = String(iso).split("-").map(Number);
    return new Date(y, m - 1, d).toLocaleDateString(undefined, options);
  }

  function when(iso) {
    const d = new Date(iso);
    return isNaN(d) ? "" : d.toLocaleString(undefined, { weekday: "short", hour: "numeric", minute: "2-digit" });
  }

  // ---- DOM helpers (text nodes only, never HTML strings) ----

  function el(tag, cls, ...kids) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    for (const kid of kids.flat()) {
      if (kid === null || kid === undefined || kid === false) continue;
      node.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
    }
    return node;
  }

  function delta(n) {
    const x = Number(n || 0);
    return el("span", "d " + (x > 0 ? "pos" : x < 0 ? "neg" : ""), x === 0 ? "" : signed(x));
  }

  function button(label, cls, onClick) {
    const b = el("button", "btn " + cls, label);
    b.type = "button";
    b.disabled = state.busy;
    b.addEventListener("click", onClick);
    return b;
  }

  // ---- rendering ----

  const PILL = {
    draft: ["Draft · nothing changed yet", "p-draft"],
    committing: ["Committing…", "p-draft"],
    stale: ["Out of date · nothing was changed", "p-stale"],
    failed: ["Failed · rolled back", "p-fail"],
    cancelled: ["Cancelled", "p-mute"],
    expired: ["Expired", "p-mute"]
  };

  const OUTCOME = {
    reversed: "reversed",
    rejected: "rejected",
    not_attempted: "not attempted",
    reverse_failed: "could not reverse — check this entry"
  };

  function render() {
    const v = state.view;
    app.replaceChildren();
    if (!v) {
      app.append(el("p", state.error ? "error" : "muted", state.error || "Waiting for the draft…"));
    } else if (v.kind !== "draft" || !v.draft) {
      app.append(el("p", "error", v.message || "Nothing to show."));
    } else {
      const d = v.draft;
      app.append(header(d), totals(d));
      if (d.status === "stale")
        app.append(el("div", "note n-stale",
          "Project Insight changed after this draft was made: " + (d.staleDays || []).join(", ") +
          ". Nothing was written. Ask the agent to draft again."));
      if (d.status === "failed" && d.error) app.append(el("div", "note n-fail", d.error));
      if (d.status === "expired") app.append(el("div", "note n-mute", "Not confirmed within 24 hours. Nothing was changed."));
      for (const w of d.weeks || []) app.append(week(d, w));
      app.append(actions(d));
      if (state.error) app.append(el("div", "note n-fail", state.error));
    }
    reportSize();
  }

  function header(d) {
    let [label, cls] = PILL[d.status] || [d.status, "p-mute"];
    if (d.status === "committed") {
      label = d.channel === "agent" ? "Committed by the agent" : "Committed · confirmed in chat";
      cls = "p-ok";
    } else if (d.status === "draft" && d.undoOf) {
      label = "Draft · undo of earlier changes";
    }
    const by = d.status === "committed"
      ? "Written by the agent · committed " + when(d.committedAt)
      : d.status === "draft"
        ? "Written by the agent · expires " + when(d.expiresAt)
        : "Written by the agent · " + when(d.proposedAt);
    return el("div", "", el("span", "pill " + cls, label), el("div", "intent", d.intent), el("div", "by", by));
  }

  function totals(d) {
    const t = d.totals || {};
    const kpi = (value, label, cls) => el("div", "kpi", el("b", cls || "", value), el("span", "", label));
    return el("div", "kpis",
      kpi(signed(t.netHours) + "h", "net · billable " + signed(t.billableDelta), t.netHours > 0 ? "pos" : t.netHours < 0 ? "neg" : ""),
      kpi(String(t.changes || 0), "changes"),
      kpi((t.days || 0) + " · " + (t.people || 0), "days · people"),
      kpi(String(t.warnings || 0), "warnings", t.warnings ? "warn" : ""));
  }

  function week(d, w) {
    const title = el("span", "", el("b", "", w.isCaller ? "You" : w.userName),
      w.isCaller ? null : el("span", "other", "someone else’s time"),
      " · week of " + day(w.start, { day: "numeric", month: "short" }));
    if (w.timesheetUrl) {
      const link = el("a", "link", "open in PI ↗");
      link.href = "#";
      link.addEventListener("click", (e) => { e.preventDefault(); openLink(w.timesheetUrl); });
      title.append(link);
    }
    const box = el("div", "week",
      el("div", "whead", title, el("span", "tot", hrs(w.totalBefore) + " → ", el("b", "", hrs(w.totalAfter) + "h")),
        delta(w.totalAfter - w.totalBefore)));
    for (const dv of w.days || []) box.append(...dayBlock(d, w, dv));
    return box;
  }

  function dayBlock(d, w, dv) {
    const key = w.userId + "|" + dv.date;
    // Days with changes start open; others start closed. A click flips that.
    const open = dv.hasChanges !== state.toggled.has(key);
    const warnings = (dv.rowIndexes || []).reduce((n, i) => n + ((d.rows[i] && d.rows[i].warnings) || []).length, 0);
    const line = el("div", "dline" + (dv.hasChanges ? "" : " quiet"),
      el("span", "car", open ? "▼" : "▶"),
      el("span", "", day(dv.date, { weekday: "short", day: "numeric", month: "short" }),
        warnings ? el("span", "w", "⚠ " + warnings) : null),
      el("span", "tot", dv.hasChanges ? [hrs(dv.totalBefore) + " → ", el("b", "", hrs(dv.totalAfter) + "h")] : hrs(dv.totalBefore) + "h"),
      dv.hasChanges ? delta(dv.totalAfter - dv.totalBefore) : el("span", ""));
    line.tabIndex = 0;
    line.setAttribute("role", "button");
    line.setAttribute("aria-expanded", String(open));
    const flip = () => {
      if (state.toggled.has(key)) state.toggled.delete(key); else state.toggled.add(key);
      render();
    };
    line.addEventListener("click", flip);
    line.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); flip(); } });

    if (!open) return [line];
    const rows = el("div", "rows");
    for (const c of dv.context || []) rows.append(contextRow(c));
    for (const i of dv.rowIndexes || []) if (d.rows[i]) rows.append(changeRow(d.rows[i], dv.date));
    if (!rows.childNodes.length) rows.append(el("div", "row ctx", el("span", ""), el("span", "task", "No entries"), el("span", ""), el("span", "")));
    return [line, rows];
  }

  function contextRow(c) {
    return el("div", "row ctx",
      el("span", ""),
      el("span", "task", el("span", "label", c.projectName + " › " + c.taskName), c.billable ? el("span", "bill", "billable") : null,
        c.description ? el("small", "", c.description) : null),
      el("span", "hrs", hrs(c.hours)),
      el("span", ""));
  }

  function changeRow(r, date) {
    const moved = r.action === "update" && r.before && r.after && r.before.date !== r.after.date;
    const away = moved && r.before.date === date;   // this day loses the entry
    const into = moved && r.after.date === date;     // this day gains it
    const removing = r.action === "delete" || away;
    const adding = r.action === "create" || into;
    const v = removing ? r.before : r.after;
    const [symbol, symbolClass] = adding ? ["+", "a-c"] : removing ? ["−", "a-d"] : ["~", "a-u"];
    const from = adding ? "—" : hrs(r.before.hours);
    const to = removing ? "—" : hrs(r.after.hours);
    const diff = (removing ? 0 : r.after.hours) - (adding ? 0 : r.before.hours);
    const task = el("span", "task",
      el("span", "label", v.projectName + " › " + v.taskName),
      v.billable ? el("span", "bill", "billable") : null,
      v.description ? el("small", "", v.description) : null,
      r.action === "update" && r.before.taskId !== r.after.taskId && !removing
        ? el("small", "", "was " + r.before.projectName + " › " + r.before.taskName) : null,
      away ? el("small", "", "moved to " + day(r.after.date, { weekday: "short", day: "numeric", month: "short" })) : null,
      into ? el("small", "", "moved from " + day(r.before.date, { weekday: "short", day: "numeric", month: "short" })) : null,
      (r.warnings || []).map((x) => el("span", "w", "⚠ " + x.message)),
      r.outcome && r.outcome !== "applied" ? el("span", "st st-" + r.outcome, OUTCOME[r.outcome] || r.outcome) : null,
      r.outcome && r.outcome !== "applied" && r.error ? el("small", "err", r.error) : null);
    return el("div", "row chg" + (removing ? " del" : ""),
      el("span", "act " + symbolClass, symbol), task,
      el("span", "hrs", from + " → ", el("b", "", to)),
      delta(diff));
  }

  function actions(d) {
    const bar = el("div", "btns");
    const count = (d.rows || []).length;
    if (d.status === "draft") {
      const confirm = button("Confirm " + count + " change" + (count === 1 ? "" : "s"), "primary",
        () => act("confirm_time_changes", { draftId: d.id, confirmToken: state.token }));
      if (!state.token) {
        confirm.disabled = true;
        confirm.title = "Ask the agent to commit this draft.";
      }
      bar.append(confirm, button("Cancel", "", () => act("cancel_time_changes", { draftId: d.id })));
    } else if (d.status === "stale") {
      bar.append(button("Cancel draft", "", () => act("cancel_time_changes", { draftId: d.id })));
    } else if (d.status === "committed" && d.revertedBy) {
      bar.append(el("span", "hint", "Undone by " + d.revertedBy));
    } else if (d.status === "committed" && d.undoUntil && new Date(d.undoUntil) > new Date()) {
      bar.append(button("Draft an undo", "", () => act("undo_time_changes", { receiptId: d.id })),
        el("span", "hint", "available until " + when(d.undoUntil)));
    }
    if (state.busy) bar.append(el("span", "hint", "Working…"));
    return bar;
  }

  function reportSize() {
    requestAnimationFrame(() => {
      const root = document.documentElement;
      notify("ui/notifications/size-changed", {
        width: Math.ceil(root.getBoundingClientRect().width),
        height: Math.ceil(root.scrollHeight)
      });
    });
  }

  // ---- start: render immediately, then handshake (render even if the handshake fails) ----

  async function start() {
    render();
    try {
      const result = await request("ui/initialize", {
        protocolVersion: "2026-01-26",
        appInfo: { name: "project-insight-time-changes", version: "1.0.0" },
        appCapabilities: {}
      });
      applyHostContext(result && result.hostContext);
    } catch (_) { /* hosts that don't answer still get a rendered card */ }
    notify("ui/notifications/initialized", {});
  }

  start();
})();
</script>
</body>
</html>
```

- [ ] **Step 4: Run the tests, then syntax-check the script**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeCardTests"`
Expected: PASS (3 tests).

Then check the inline script parses, if `node` is installed. Write the extracted script to the session scratchpad, not the repo:

```bash
python3 -c 'import re,sys; s=open("core/TimeChanges/Card/time-changes.html").read(); print(re.search(r"<script>(.*)</script>", s, re.S).group(1))' > "$SCRATCH/card.js" && node --check "$SCRATCH/card.js" && echo "card script OK"
```

Expected: `card script OK`. If node is missing, skip this; the live test in Task 10 renders the card.

- [ ] **Step 5: Commit**

```bash
git add core/ProjectInsightMCP.Core.csproj core/TimeChanges/Card tests/TimeChanges/TimeChangeCardTests.cs
git commit -m "feat(time-changes): MCP Apps review card"
```

---

### Task 9: SDK wiring — six tools, card resource, gate filter, destructiveHint

**Files:**
- Modify: `src-stdio/ProjectInsightMCP.Stdio.csproj`
- Create: `src-stdio/TimeChangeTools.cs`
- Modify: `src-stdio/ProjectInsightServer.cs`
- Modify: `core/Shared/McpModels.cs` (`McpTool`)
- Modify: `core/Services/McpServerService.cs` (three time-entry tool definitions)
- Modify: `tests/SdkServerTests.cs`
- Create: `tests/TimeChanges/JsonSchemaCheck.cs`
- Create: `tests/TimeChangeToolsTests.cs`
- Regenerate: `tests/Snapshots/tools-list.json`; create `tests/Snapshots/time-change-tools.json`
- Modify: `docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md` (Code layout section)

**Interfaces:**
- Consumes: `TimeChangeService`, `TimeChangesView`, `TimeChangeResponse`, `ChangeInput` and `TimeChangeCard.Html` (Tasks 7–8); `FakeGateway` and `TimeChangePlannerTests.Seeded()` (tests).
- Produces:
  - `TimeChangeTools.Create() : IReadOnlyList<McpServerTool>`, `TimeChangeTools.CreateCard() : McpServerResource` and `TimeChangeTools.CardUri`
  - config key `ProjectInsight:DraftsPath`, which overrides the drafts folder (tests use it)
  - `TimeChangeToolsTests.ToolNames : string[]`
  - `McpToolAnnotations` with `DestructiveHint`, and `McpTool.Annotations`

- [ ] **Step 1: Write the failing tests**

`tests/TimeChanges/JsonSchemaCheck.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectInsightMCP.Tests.TimeChanges;

/// <summary>
/// Just enough JSON Schema to check structuredContent against a tool's outputSchema: type
/// (including type arrays and integer/number), required, properties (unknown properties fail),
/// items, additionalProperties and local $ref.
/// </summary>
public static class JsonSchemaCheck
{
    public static List<string> Validate(JsonElement schema, JsonNode? value)
    {
        var errors = new List<string>();
        Check(schema, schema, value, "$", errors);
        return errors;
    }

    private static void Check(JsonElement root, JsonElement schema, JsonNode? value, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True)
            return;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Check(root, Resolve(root, reference.GetString()!), value, path, errors);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()!).ToList()
                : [type.GetString()!];
            var actual = KindOf(value);
            if (!allowed.Contains(actual) && !(actual == "integer" && allowed.Contains("number")))
            {
                errors.Add($"{path}: expected {string.Join("|", allowed)}, got {actual}");
                return;
            }
        }
        if (value is JsonObject obj)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
                    if (!obj.ContainsKey(name))
                        errors.Add($"{path}: missing required '{name}'");
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
            foreach (var (name, child) in obj)
            {
                if (hasProperties && properties.TryGetProperty(name, out var childSchema))
                    Check(root, childSchema, child, $"{path}.{name}", errors);
                else if (hasAdditional && additional.ValueKind == JsonValueKind.Object)
                    Check(root, additional, child, $"{path}.{name}", errors);
                else if (hasProperties)
                    errors.Add($"{path}: '{name}' is not in the schema");
            }
        }
        if (value is JsonArray array && schema.TryGetProperty("items", out var items))
            for (var i = 0; i < array.Count; i++)
                Check(root, items, array[i], $"{path}[{i}]", errors);
    }

    private static string KindOf(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => v.TryGetValue<decimal>(out var d) && d == decimal.Truncate(d) ? "integer" : "number",
            _ => "null"
        },
        _ => "unknown"
    };

    private static JsonElement Resolve(JsonElement root, string pointer)
    {
        var node = root;
        foreach (var part in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
        return node;
    }
}
```

`tests/TimeChangeToolsTests.cs`:

```csharp
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProjectInsightMCP.Stdio;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests;

/// <summary>The six time-change tools and the card over the wire, through the real SDK host wiring.</summary>
public sealed class TimeChangeToolsTests : IAsyncLifetime
{
    public static readonly string[] ToolNames =
    [
        "plan_time_changes", "commit_time_changes", "confirm_time_changes",
        "cancel_time_changes", "undo_time_changes", "get_time_changes"
    ];

    private const string CardUri = "ui://project-insight/time-changes.html";
    private readonly FakeGateway _pi = TimeChangePlannerTests.Seeded();
    private readonly string _drafts = Path.Combine(Path.GetTempPath(), "pi-tools-" + Guid.NewGuid().ToString("N"));
    private IHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectInsight:BaseUrl"] = "https://pi.test",
            ["ProjectInsight:ApiToken"] = "test-token",
            ["ProjectInsight:DraftsPath"] = _drafts
        });
        builder.Services.AddLogging();
        builder.Services.AddProjectInsightServices(builder.Configuration);
        builder.Services.AddSingleton<ITimeEntryGateway>(_pi);
        builder.Services.AddProjectInsightMcp().WithDrainingStreamServerTransport(
            () => clientToServer.Reader.AsStream(), () => serverToClient.Writer.AsStream());
        _host = builder.Build();
        await _host.StartAsync();
        _client = await McpClient.CreateAsync(new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        if (Directory.Exists(_drafts)) Directory.Delete(_drafts, recursive: true);
    }

    private static Dictionary<string, object?> PlanArgs() => new()
    {
        ["intent"] = "Log Tuesday",
        ["changes"] = new object[]
        {
            new { action = "create", date = "2026-10-27", hours = 4, project = "Acme Portal", task = "Development", billable = true },
            new { action = "update", timeEntryId = "te-1", hours = 1.5 },
            new { action = "delete", timeEntryId = "te-4" }
        }
    };

    private static string DraftId(CallToolResult r) => r.StructuredContent!.Value.GetProperty("draft").GetProperty("id").GetString()!;

    private async Task<Dictionary<string, Tool>> ListTimeChangeTools()
        => (await _client.ListToolsAsync()).Where(t => ToolNames.Contains(t.Name)).ToDictionary(t => t.Name, t => t.ProtocolTool);

    [Fact]
    public async Task Six_tools_link_to_the_card_with_the_right_visibility_and_annotations()
    {
        var tools = await ListTimeChangeTools();
        Assert.Equal(ToolNames.Order(), tools.Keys.Order());

        string[] Visibility(string name) => tools[name].Meta!["ui"]!["visibility"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        foreach (var tool in tools.Values)
        {
            Assert.Equal(CardUri, tool.Meta!["ui"]!["resourceUri"]!.GetValue<string>());
            Assert.NotNull(tool.OutputSchema);
        }
        Assert.Equal(["model"], Visibility("plan_time_changes"));
        Assert.Equal(["model"], Visibility("commit_time_changes"));
        Assert.Equal(["app"], Visibility("confirm_time_changes"));
        foreach (var name in new[] { "cancel_time_changes", "undo_time_changes", "get_time_changes" })
            Assert.Equal(["model", "app"], Visibility(name));
        Assert.True(tools["commit_time_changes"].Annotations!.DestructiveHint);
        Assert.True(tools["confirm_time_changes"].Annotations!.DestructiveHint);
        foreach (var name in new[] { "plan_time_changes", "undo_time_changes", "get_time_changes" })
            Assert.True(tools[name].Annotations!.ReadOnlyHint);
    }

    [Fact]
    public async Task Time_change_tools_match_snapshot()
    {
        var tools = await ListTimeChangeTools();
        var current = new JsonArray(ToolNames.Select(n => JsonSerializer.SerializeToNode(tools[n], McpJsonUtilities.DefaultOptions)).ToArray());
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
            File.WriteAllText(path, current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), current),
            "time-change tools changed. If intentional, regenerate with UPDATE_SNAPSHOTS=1.");
    }

    [Fact]
    public async Task Card_resource_is_listed_and_readable_as_an_mcp_app()
    {
        var listed = Assert.Single(await _client.ListResourcesAsync(), r => r.ProtocolResource.Uri == CardUri);
        Assert.Equal("text/html;profile=mcp-app", listed.ProtocolResource.MimeType);

        var read = await _client.ReadResourceAsync(CardUri);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("text/html;profile=mcp-app", contents.MimeType);
        Assert.Equal(TimeChangeCard.Html, contents.Text);
        Assert.Empty(contents.Meta!["ui"]!["csp"]!["connectDomains"]!.AsArray());
    }

    [Fact]
    public async Task Structured_content_matches_the_output_schema_for_every_outcome()
    {
        var tools = (await _client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        var results = new List<(string Tool, CallToolResult Result)>();
        async Task<CallToolResult> Run(string tool, Dictionary<string, object?> args)
        {
            var result = await _client.CallToolAsync(tool, args);
            results.Add((tool, result));
            return result;
        }

        var draftId = DraftId(await Run("plan_time_changes", PlanArgs()));
        await Run("plan_time_changes", new()
        {
            ["intent"] = "x",
            ["changes"] = new object[] { new { action = "create", date = "2026-10-27", hours = 1, project = "Acme", task = "Development" } }
        });
        await Run("plan_time_changes", new() { ["intent"] = "", ["changes"] = new object[] { new { action = "delete", timeEntryId = "te-1" } } });
        await Run("get_time_changes", new() { ["id"] = draftId });
        await Run("confirm_time_changes", new() { ["draftId"] = draftId, ["confirmToken"] = "wrong" });
        await Run("commit_time_changes", new() { ["draftId"] = draftId });
        var undoId = DraftId(await Run("undo_time_changes", new() { ["receiptId"] = draftId }));
        await Run("cancel_time_changes", new() { ["draftId"] = undoId });

        var nullsWritten = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var (tool, result) in results)
        {
            var schema = tools[tool].OutputSchema!.Value;
            var asSent = JsonSchemaCheck.Validate(schema, JsonNode.Parse(result.StructuredContent!.Value.GetRawText()));
            Assert.True(asSent.Count == 0, $"{tool}: {string.Join("; ", asSent)}");

            var view = result.StructuredContent.Value.Deserialize<TimeChangesView>(nullsWritten)!;
            var withNulls = JsonSchemaCheck.Validate(schema, JsonSerializer.SerializeToNode(view, nullsWritten));
            Assert.True(withNulls.Count == 0, $"{tool} (nulls written): {string.Join("; ", withNulls)}");
        }
        Assert.Equal([false, false, true, false, true, false, false, false], results.Select(r => r.Result.IsError == true));
    }

    [Fact]
    public async Task Confirm_token_reaches_the_card_in_meta_only()
    {
        var plan = await _client.CallToolAsync("plan_time_changes", PlanArgs());
        var token = plan.Meta!["confirmToken"]!.GetValue<string>();
        Assert.DoesNotContain(token, Assert.IsType<TextContentBlock>(Assert.Single(plan.Content)).Text);
        Assert.DoesNotContain(token, plan.StructuredContent!.Value.GetRawText());

        var get = await _client.CallToolAsync("get_time_changes", new Dictionary<string, object?> { ["id"] = DraftId(plan) });
        Assert.Null(get.Meta?["confirmToken"]);

        var confirm = await _client.CallToolAsync("confirm_time_changes",
            new Dictionary<string, object?> { ["draftId"] = DraftId(plan), ["confirmToken"] = token });
        Assert.Equal("committed", confirm.StructuredContent!.Value.GetProperty("draft").GetProperty("status").GetString());
        Assert.Equal(["create:te-100", "update:te-1", "delete:te-4"], _pi.Writes);
    }

    [Fact]
    public async Task Time_change_calls_share_the_one_at_a_time_gate()
    {
        _pi.Delay = TimeSpan.FromMilliseconds(100);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            _client.CallToolAsync("get_time_changes", new Dictionary<string, object?> { ["id"] = "d_none" }).AsTask()));

        Assert.Equal(1, _pi.MaxConcurrent);
    }

    [Fact]
    public void Schema_check_rejects_mismatches()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"a":{"type":"integer"},"b":{"type":["string","null"]}},"required":["a"]}
            """).RootElement;

        Assert.Empty(JsonSchemaCheck.Validate(schema, JsonNode.Parse("""{"a":1,"b":null}""")));
        Assert.NotEmpty(JsonSchemaCheck.Validate(schema, JsonNode.Parse("""{"b":"x"}""")));
        Assert.NotEmpty(JsonSchemaCheck.Validate(schema, JsonNode.Parse("""{"a":"1"}""")));
        Assert.NotEmpty(JsonSchemaCheck.Validate(schema, JsonNode.Parse("""{"a":1,"c":true}""")));
    }

    private static string SnapshotPath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "Snapshots", "time-change-tools.json");
}
```

Edit `tests/SdkServerTests.cs`:
1. In `InitializeAsync`'s `AddInMemoryCollection`, add `["ProjectInsight:DraftsPath"] = Path.Combine(Path.GetTempPath(), "pi-sdk-drafts-" + Guid.NewGuid().ToString("N"))`.
2. Replace the body of `Tools_list_over_the_wire_matches_snapshot` so that it compares only the existing (adapter) tools with the snapshot, and checks that the six new ones are listed:

```csharp
    [Fact]
    public async Task Tools_list_over_the_wire_matches_snapshot()
    {
        var all = await _client.ListToolsAsync();
        var tools = all.Where(t => !TimeChangeToolsTests.ToolNames.Contains(t.Name)).ToList();
        var snapshot = ToolListSnapshotTests.LoadSnapshot();

        Assert.Equal(snapshot.Count, tools.Count);
        for (var i = 0; i < snapshot.Count; i++)
        {
            // Whole tool object as the SDK puts it on the wire, so added fields (title,
            // annotations, outputSchema, _meta) fail here too, not just changed ones.
            var wire = JsonSerializer.SerializeToNode(tools[i].ProtocolTool, McpJsonUtilities.DefaultOptions);
            Assert.True(JsonNode.DeepEquals(snapshot[i], wire),
                $"tool {tools[i].Name} differs on the wire:\n{wire}");
        }
        Assert.Equal(TimeChangeToolsTests.ToolNames.Order(),
            all.Select(t => t.Name).Where(TimeChangeToolsTests.ToolNames.Contains).Order());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests --filter "FullyQualifiedName~TimeChangeToolsTests|FullyQualifiedName~SdkServerTests"`
Expected: build FAILS — `ProjectInsightMCP.Stdio.TimeChangeTools` does not exist yet. Even when `TimeChangeToolsTests` compiles, the tests fail because the six tools aren't registered.

- [ ] **Step 3: Implement**

`src-stdio/ProjectInsightMCP.Stdio.csproj`: add the package reference next to `ModelContextProtocol`, and suppress the experimental-API diagnostic in the first `PropertyGroup`:

```xml
    <PackageReference Include="ModelContextProtocol.Extensions.Apps" Version="2.3.0" />
```

```xml
    <!-- ModelContextProtocol.Extensions.Apps is marked experimental (MCPEXP003). -->
    <NoWarn>$(NoWarn);MCPEXP003</NoWarn>
```

`core/Shared/McpModels.cs`: in `McpTool`, after `InputSchema`, add the property below. Then add the class after `McpTool`:

```csharp
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpToolAnnotations? Annotations { get; set; }
```

```csharp
/// <summary>MCP tool annotations: hints for clients and harnesses deciding what to allow.</summary>
public class McpToolAnnotations
{
    public static McpToolAnnotations Destructive => new() { DestructiveHint = true };

    [JsonPropertyName("destructiveHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DestructiveHint { get; set; }
}
```

`core/Services/McpServerService.cs`: in `InitializeTools()`, add `Annotations = McpToolAnnotations.Destructive,` on the line after each of `Name = "create_time_entry",`, `Name = "delete_time_entry",` and `Name = "update_time_entry",`.

`src-stdio/TimeChangeTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Stdio;

/// <summary>
/// The six time-change tools and the review card as SDK-native primitives. The logic lives in
/// Core (<see cref="TimeChangeService"/>); this file only shapes MCP inputs and results.
/// </summary>
internal static class TimeChangeTools
{
    public const string CardUri = "ui://project-insight/time-changes.html";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // One schema for all six tools: they all return a TimeChangesView.
    private static readonly JsonElement OutputSchema = AIJsonUtilities.CreateJsonSchema(typeof(TimeChangesView), serializerOptions: Json);

    private static readonly string[] Model = [McpUiToolVisibility.Model];
    private static readonly string[] App = [McpUiToolVisibility.App];
    private static readonly string[] ModelAndApp = [McpUiToolVisibility.Model, McpUiToolVisibility.App];

    private const string PlanDescription =
        "Draft changes to Project Insight time entries for the user to review. Writes nothing to Project Insight: " +
        "returns a draft (shown to the user as a review card where supported), or a clarification when a name is " +
        "ambiguous. Each change is one of: create {date, hours, taskId or project + task names, description?, billable?, user?}, " +
        "update {timeEntryId plus only the fields to change}, delete {timeEntryId}. After planning, summarize the draft " +
        "and ask the user to confirm; only commit when they agree.";

    private const string CommitDescription =
        "Apply a draft from plan_time_changes or undo_time_changes to Project Insight, all or nothing. Only call this " +
        "after the user has agreed to the draft. Refused if the affected days changed in Project Insight since the draft " +
        "was made (draft again) or if the draft expired (24 hours).";

    private const string UndoDescription =
        "Draft the reverse of a committed draft (pass its id as receiptId). Returns a new draft that must be confirmed " +
        "or committed like any other. Available for 24 hours after commit.";

    public static IReadOnlyList<McpServerTool> Create() =>
    [
        Tool("plan_time_changes", PlanDescription, Plan, Model, readOnly: true),
        Tool("commit_time_changes", CommitDescription, Commit, Model, destructive: true),
        Tool("confirm_time_changes", "Used by the review card's Confirm button. Agents use commit_time_changes instead.",
            Confirm, App, destructive: true),
        Tool("cancel_time_changes", "Cancel a time-change draft so it can't be committed. Nothing in Project Insight changes.",
            Cancel, ModelAndApp),
        Tool("undo_time_changes", UndoDescription, Undo, ModelAndApp, readOnly: true),
        Tool("get_time_changes", "Show a time-change draft or receipt by id, with its current status.", Get, ModelAndApp, readOnly: true)
    ];

    public static McpServerResource CreateCard() => McpServerResource.Create(
        () => new TextResourceContents
        {
            Uri = CardUri,
            MimeType = McpApps.HtmlMimeType,
            Text = TimeChangeCard.Html,
            // MCP Apps reads UI metadata from the resource contents: no network access for the card.
            Meta = new JsonObject
            {
                ["ui"] = new JsonObject
                {
                    ["csp"] = new JsonObject { ["connectDomains"] = new JsonArray(), ["resourceDomains"] = new JsonArray() },
                    ["prefersBorder"] = true
                }
            }
        },
        new McpServerResourceCreateOptions
        {
            UriTemplate = CardUri,
            Name = "time-changes",
            Title = "Time changes review card",
            Description = "Review card for Project Insight time-entry drafts.",
            MimeType = McpApps.HtmlMimeType
        });

    private static McpServerTool Tool(string name, string description, Delegate handler, string[] visibility,
        bool readOnly = false, bool destructive = false)
    {
        var tool = McpServerTool.Create(handler, new McpServerToolCreateOptions
        {
            Name = name,
            Description = description,
            ReadOnly = readOnly ? true : null,
            Destructive = destructive ? true : null,
            UseStructuredContent = true,
            OutputSchema = OutputSchema,
            SerializerOptions = Json
        });
        McpApps.SetAppUi(tool, new McpUiToolMeta { ResourceUri = CardUri, Visibility = [.. visibility] });
        return tool;
    }

    private static async Task<CallToolResult> Plan(
        [Description("One line saying what these changes are for, e.g. \"Log Tuesday from H4T\".")] string intent,
        [Description("The changes to draft, 1 to 100.")] List<ChangeInput> changes,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).PlanAsync(intent, changes, cancellationToken));

    private static async Task<CallToolResult> Commit(
        [Description("Draft id from plan_time_changes or undo_time_changes.")] string draftId,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).CommitAsync(draftId, cancellationToken));

    private static async Task<CallToolResult> Confirm(
        [Description("Draft id.")] string draftId,
        [Description("The one-time token from the draft's result metadata.")] string confirmToken,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).ConfirmAsync(draftId, confirmToken, cancellationToken));

    private static async Task<CallToolResult> Cancel(
        [Description("Draft id.")] string draftId,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).CancelAsync(draftId, cancellationToken));

    private static async Task<CallToolResult> Undo(
        [Description("Id of the committed draft to reverse.")] string receiptId,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).UndoAsync(receiptId, cancellationToken));

    private static async Task<CallToolResult> Get(
        [Description("Draft or receipt id.")] string id,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).GetAsync(id, cancellationToken));

    private static TimeChangeService Service(RequestContext<CallToolRequestParams> context)
        => context.Services!.GetRequiredService<TimeChangeService>();

    internal static CallToolResult ToResult(TimeChangeResponse response) => new()
    {
        Content = [new TextContentBlock { Text = response.Text }],
        StructuredContent = JsonSerializer.SerializeToElement(response.View, Json),
        IsError = response.IsError,
        // The card needs the token to confirm; per MCP Apps, _meta is not meant for model context.
        Meta = response.ConfirmToken is null ? null : new JsonObject { ["confirmToken"] = response.ConfirmToken }
    };
}
```

`src-stdio/ProjectInsightServer.cs`:

1. Add `using ProjectInsightMCP.TimeChanges;`.
2. In `AddProjectInsightServices`, before `return services;`, add:

```csharp
        // Time-change drafts (plan → review card → confirm/cancel/undo).
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDraftStore>(sp => new FileDraftStore(
            configuration["ProjectInsight:DraftsPath"] is { Length: > 0 } path ? path : FileDraftStore.DefaultRoot,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITimeEntryGateway, ProjectInsightTimeEntryGateway>();
        services.AddSingleton<TimeChangePlanner>();
        services.AddSingleton<TimeChangeCommitter>();
        services.AddSingleton<TimeChangeService>();
```

3. In `AddProjectInsightMcp`, replace the `.WithCallToolHandler(...)` block and chain the new registrations:

```csharp
            .WithCallToolHandler(async (ctx, ct) =>
            {
                if (ctx.Params is null)
                    throw new McpProtocolException("Invalid params: parameters must be provided", McpErrorCode.InvalidParams);

                var mcp = ctx.Services!.GetRequiredService<McpServerService>();
                var call = new McpToolCall
                {
                    Name = ctx.Params.Name,
                    Arguments = ToLegacyArguments(ctx.Params.Arguments)
                };
                return ToSdkResult(await mcp.ExecuteToolAsync(call, CancellationToken.None));
            })
            .WithTools(TimeChangeTools.Create())
            .WithResources([TimeChangeTools.CreateCard()])
            // Every tool call, adapter or SDK-native, goes through the gate. Cancellation only
            // applies while a call is queued. Once started it runs to completion, as before the SDK
            // (the SDK then sends no response, per spec): stopping midway could leave a multi-step
            // write (e.g. add_task_resource, or a time-change commit) half-applied.
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
                new ValueTask<CallToolResult>(context.Services!.GetRequiredService<ToolCallGate>().RunAsync(
                    () => next(context, CancellationToken.None).AsTask(), cancellationToken))));
```

4. In `ToSdkTool`, map annotations:

```csharp
    internal static Tool ToSdkTool(McpTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema, SchemaOptions),
        Annotations = tool.Annotations is null ? null : new ToolAnnotations { DestructiveHint = tool.Annotations.DestructiveHint }
    };
```

In the spec, `docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md`, Code layout section: move `TimeChangeTools.cs` from `core/TimeChanges/` to under `src-stdio/`, with the note "SDK registration lives in the stdio host so Core stays SDK-free (the net8.0 Functions host references Core)".

- [ ] **Step 4: Regenerate the snapshots, then run everything**

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests --filter "FullyQualifiedName~ToolListSnapshotTests|FullyQualifiedName~TimeChangeToolsTests.Time_change_tools_match_snapshot"
git diff --stat tests/Snapshots/tools-list.json
dotnet test tests
```

Expected:
- the `tools-list.json` diff only adds `"annotations": {"destructiveHint": true}` to `create_time_entry`, `delete_time_entry` and `update_time_entry`
- `time-change-tools.json` is created with six entries
- the full suite PASSES, including `RawStdioTests` (EOF draining, cancellation, -32602 without params) and `SdkServerTests` concurrency

Inspect `time-change-tools.json` by eye: descriptions, `inputSchema` (`intent`, `changes[]` with the `ChangeInput` fields and their descriptions), `outputSchema`, `annotations` and `_meta.ui`. It must not contain a `context` or `cancellationToken` parameter.

Then build all three hosts:

```bash
dotnet build core -c Release && dotnet build src -c Release && dotnet build src-stdio -c Release
```

Expected: 0 warnings, 0 errors. The Functions host must still build, which proves Core stays SDK-free.

- [ ] **Step 5: Commit**

```bash
git add src-stdio core/Shared/McpModels.cs core/Services/McpServerService.cs tests ../../docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md
git commit -m "feat(time-changes): register six SDK tools and the review card; gate covers all tool calls"
```

---

### Task 10: Release 2.1.0, docs, local install and live test

**Files:**
- Modify: `src-stdio/ProjectInsightMCP.Stdio.csproj` (`<Version>2.1.0</Version>`)
- Modify: `mcpb/build/manifest.json` (`"version": "2.1.0"`)
- Modify: `src-stdio/README.md` (tool table and a new section)
- Modify: `../../docs/agents.md` (new section under "Available MCP Tools")
- Modify: `../../docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md` (status line and live-test results)

- [ ] **Step 1: Version and docs**

Set `<Version>2.1.0</Version>` in the stdio csproj and `"version": "2.1.0"` in `mcpb/build/manifest.json`.
In the spec, change the status line to `**Status:** implemented on branch \`phase1-time-changes\` (PR pending live test)`.

In `src-stdio/README.md`, under `### Time Management`, change the `create_time_entry` row and add rows after it:

```markdown
| `create_time_entry` | Create a time entry against a task (writes immediately) |
| `update_time_entry` | Update a time entry (writes immediately) |
| `delete_time_entry` | Delete a time entry (writes immediately) |
| `plan_time_changes` | Draft time-entry changes for review; writes nothing |
| `commit_time_changes` | Apply a reviewed draft, all or nothing |
| `cancel_time_changes` | Cancel a draft |
| `undo_time_changes` | Draft the reverse of committed changes (24h) |
| `get_time_changes` | Show a draft or receipt and its status |
```

Add this section before `## Available Fields Reference`:

```markdown
## Reviewable time changes

`plan_time_changes` lets an agent draft several time-entry changes at once (create, update, delete;
your own time or anyone else's you can edit in Project Insight) without writing anything. In Claude
Desktop the draft appears as a card showing each affected week, with existing entries for context,
and Confirm / Cancel buttons; elsewhere it reads as text. Commits are all-or-nothing: if any change
is rejected, the ones already made are reversed, and if the affected days changed in Project Insight
since the draft was made, nothing is written. Committed changes can be undone for 24 hours.

Drafts are kept in `~/.projectinsight/drafts/` (readable only by you) and removed after 7 days.

The original `create_time_entry`, `update_time_entry` and `delete_time_entry` tools still write
immediately and are marked `destructiveHint`. If you want every change reviewed, block those three and
`commit_time_changes` in your client's tool permissions; the card's Confirm button then remains the
only way to write time.

After installing or updating, restart Claude Desktop (or reconnect the server): tools and the card
are cached when the client connects.
```

In `docs/agents.md`, add after the `search_projects` subsection:

```markdown
### Time-change drafts (`plan_time_changes` → `commit_time_changes`)

Prefer drafts over direct `create/update/delete_time_entry` calls when changing several entries or
anyone else's time. `plan_time_changes` takes an `intent` and up to 100 `changes`, writes nothing,
and returns a draft (or a clarification listing candidates when a project, task or person name is
ambiguous). Summarize the draft and ask the user; commit with `commit_time_changes` only after they
agree, or let them press Confirm on the card. A stale result means the affected days changed in
Project Insight since planning: read them again and re-plan. `undo_time_changes` drafts the reverse
of a commit for 24 hours.
```

- [ ] **Step 2: Full verification**

```bash
dotnet build core -c Release && dotnet build src -c Release && dotnet build src-stdio -c Release
for i in 1 2 3; do dotnet test tests -c Release 2>&1 | tail -1; done
git diff origin/main --stat -- . ':!*.json' | tail -1
git diff origin/main | grep -nE 'ApiToken"?\s*[:=]\s*"[A-Za-z0-9]{12,}|BEGIN [A-Z]+ PRIVATE KEY|AccountKey=' || echo "secret scan clean"
```

Expected: 0 warnings, every test run passes, `secret scan clean`.

- [ ] **Step 3: Commit**

```bash
git add src-stdio/ProjectInsightMCP.Stdio.csproj mcpb/build/manifest.json src-stdio/README.md ../../docs/agents.md ../../docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md
git commit -m "chore: release 2.1.0 — reviewable time changes"
```

- [ ] **Step 4: Install a release candidate locally**

Pack to the session scratchpad. The prerelease version keeps the real 2.1.0 out of the NuGet cache.

```bash
dotnet pack src-stdio -c Release -p:Version=2.1.0-rc.1 -o "$SCRATCH/nupkg"
dotnet tool update -g ProjectInsight.MCP --add-source "$SCRATCH/nupkg" --version 2.1.0-rc.1
```

Smoke-test it the way Claude Desktop launches it. The script reads the env from the Desktop config without printing the token:

```bash
python3 - <<'PY'
import json, os, subprocess
cfg = json.load(open(os.path.expanduser("~/Library/Application Support/Claude/claude_desktop_config.json")))["mcpServers"]["project-insight"]
msgs = [
  {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}},
  {"jsonrpc":"2.0","method":"notifications/initialized"},
  {"jsonrpc":"2.0","id":2,"method":"tools/list"},
  {"jsonrpc":"2.0","id":3,"method":"resources/list"},
]
p = subprocess.run([cfg["command"]], input="".join(json.dumps(m)+"\n" for m in msgs), capture_output=True, text=True, env=dict(os.environ, **cfg["env"]), timeout=60)
r = {d["id"]: d for d in map(json.loads, filter(str.strip, p.stdout.splitlines()))}
tools = r[2]["result"]["tools"]
print("version", r[1]["result"]["serverInfo"]["version"], "| tools", len(tools),
      "| ui tools", sum(1 for t in tools if t.get("_meta", {}).get("ui")),
      "| resources", [x["uri"] for x in r[3]["result"]["resources"]])
PY
```

Expected: `version 2.1.0-rc.1 | tools 52 | ui tools 6 | resources ['ui://project-insight/time-changes.html']`.

- [ ] **Step 5: Push and open the PR (do not merge)**

```bash
git push -u origin phase1-time-changes
gh pr create --base main --title "Phase 1: reviewable time-entry changes with an MCP Apps card (2.1.0)" --body-file <(cat <<'EOF'
Closes #3.

Agents can draft time-entry changes (`plan_time_changes`); the user reviews them in a card showing each affected week in context and confirms, cancels or later undoes them. Commits are all-or-nothing and refused if PI changed since the draft. The existing time-entry tools are unchanged apart from `destructiveHint`.

**Merging publishes ProjectInsight.MCP 2.1.0.** To be merged after live testing in Claude Desktop (checklist in the spec).

Design: `docs/superpowers/specs/2026-10-29-phase1-time-changes-card-design.md` · Plan: `docs/superpowers/plans/2026-10-29-phase1-time-changes-card.md`

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)
```

- [ ] **Step 6: Live test in Claude Desktop (the user, with the agent's help)**

Restart Claude Desktop. Make sure the old 1.3.3 extension is off (Settings → Extensions). Then, on your own time only, against one task you choose:

1. Ask Claude to log two entries on one day and change an existing one. Expect a card:
   - one week block, with the changed day open showing existing entries greyed
   - other days showing totals only
   - totals and warnings correct
2. **Confirm** in the card. Expect the pill "Committed · confirmed in chat", and the entries visible in PI.
3. **Draft an undo** in the card, then **Confirm** it. Expect PI back to how it started, and the original card showing "Undone by d_…" after you scroll back and it refreshes.
4. **Stale:** ask for a change, edit that day in PI by hand, then press Confirm. Expect "Out of date · nothing was changed". Then **Cancel draft**.
5. **Cancel:** ask for a change, press Cancel, and check PI is unchanged.
6. **Agent path:** ask for a change and tell Claude "commit it". Expect `commit_time_changes` to run, and the card shows "Committed by the agent".
7. **Token visibility:** after a plan, ask Claude to quote any confirmation token it can see in the last tool result. Record whether it can see the 32-hex token.
8. **Claude Code:** in a session outside this repo, repeat steps 1 and 6. The text alone should be enough to follow.
9. **Clean-up:** undo anything still committed, and check PI matches its state before testing.

Record the results under "Verification" in the spec, including the token-visibility finding. If the model can see the token, add to the README section: "Card-only confirm is a hint in Claude, not a guarantee; block `commit_time_changes` to require a person." Commit and push those doc updates to the PR branch.
