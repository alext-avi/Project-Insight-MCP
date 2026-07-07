# Agent-Friendly Resource Assignment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Project Insight MCP resource-assignment tools discoverable and trustworthy to AI agents, so agents stop reporting they "can't assign resources."

**Architecture:** Three source changes in `servers/project-insight/src`, all in `McpServerService.cs`: (1) rewrite the six resource-tool descriptions to lead with the verbs agents match on and state the full capability; (2) fix `add_project_resource` so the advertised role/allocation params are actually applied (ensure → follow-up save) instead of silently dropped; (3) make `add_task_resource` auto-ensure project membership so its stated precondition disappears. Add a shared error-formatter for actionable failures. Introduce the repo's first xUnit test project that drives the real public dispatcher (`ExecuteToolAsync`) through a fake `HttpMessageHandler`, asserting on the outgoing HTTP calls.

**Tech Stack:** C# / .NET 8, Azure Functions isolated worker (source project), `System.Text.Json`, xUnit + `Microsoft.NET.Test.Sdk`.

## Global Constraints

- Target framework: **net8.0** (matches `servers/project-insight/src/ProjectInsightMCP.csproj`).
- Source project assembly name (default): **`ProjectInsightMCP`**; namespace `ProjectInsightMCP.Services`. MCP types live in `Shared.MCP` (compile-linked from `shared/McpModels.cs`).
- **Do NOT rename any tool** — names stay (`add_project_resource`, `update_project_resource`, `remove_project_resource`, `get_project_resources`, `add_task_resource`, `remove_task_resource`). Only descriptions and handler behavior change.
- **Do NOT change the PI REST API** or add new `ProjectInsightApiService` HTTP methods — all callees already exist: `EnsureProjectResourceAsync`, `SaveProjectResourceAsync`, `GetTaskAsync`, `SaveTaskResourceAsync`.
- Commit messages: **omit the `Co-Authored-By` trailer** (user preference).
- Arguments arrive as `Dictionary<string, object>` whose values are `System.Text.Json.JsonElement`. Read typed params with `is JsonElement` + `ValueKind` checks (the pattern already used in `UpdateProjectResourceAsync`).

## Key facts (verified against the codebase)

- `McpServerService` ctor: `McpServerService(ProjectInsightApiService projectInsightApi, ILogger<McpServerService> logger, ApiTokenAccessor apiTokenAccessor, DeviceActivationService? deviceActivation = null, KeyVaultTokenService? keyVaultTokenService = null)`.
- `ExecuteToolAsync(McpToolCall toolCall, CancellationToken)` — currently `private`; the token gate returns an error unless `apiTokenAccessor.HasToken` is true.
- `InitializeTools()` — currently `private static List<McpTool>`.
- `ProjectInsightApiService` ctor: `(HttpClient httpClient, ILogger<ProjectInsightApiService> logger, IConfiguration configuration, ApiTokenAccessor tokenAccessor)`. It sets `HttpClient.BaseAddress` from config key `ProjectInsight:BaseUrl` (only if set and not containing `"yourdomain"`).
- Endpoints used: ensure → `POST /api/project/ensure-user-is-project-resource/{projectId}?userId={userId}`; project resource save → `POST /api/project-resource`; task fetch → `GET /api/task/{taskId}?modelProperties=...`; task resource save → `POST /api/task-resource`.
- Types: `McpToolCall { string Name; Dictionary<string,object> Arguments }`, `McpToolResult { List<McpContent> Content; bool IsError }`, `McpContent { string Type; string? Text }`, `McpTool { string Name; string Description; object InputSchema }`.
- `TaskSaveResult { string? SavedId; bool Success; List<ApiError>? Errors }`; `EnsureProjectResourceAsync`/`SaveProjectResourceAsync` return `ApiResponse<TaskSaveResult>` with `Data/Success/Message/Errors`. On non-2xx: `Success=false`, `Message="API returned {StatusCode}"` (e.g. `"API returned NotFound"`), `Errors=[<body>]`.
- `ProjectResource` JSON prop names: `id`, `project_Id`, `user_Id`, `isProjectManager`, `isProjectScheduler`, `percentAllocationDefault`, `resourceTypeDefaultId`.
- `ProjectTask.ProjectId` ← JSON `project_Id`.
- `TaskResource { id, task_Id, user_Id, isActive, workHours, percentAllocationDefault }`.

## File Structure

- `servers/project-insight/src/ProjectInsightMCP.csproj` — add `InternalsVisibleTo` for the test assembly (modify).
- `servers/project-insight/src/Services/McpServerService.cs` — visibility bumps; description rewrites; `AddProjectResourceAsync` + `AddTaskResourceAsync` behavior; new private `FormatApiError` helper (modify).
- `servers/project-insight/tests/ProjectInsightMCP.Tests.csproj` — new test project (create).
- `servers/project-insight/tests/StubHttpMessageHandler.cs` — fake handler recording requests + returning queued responses (create).
- `servers/project-insight/tests/McpServerTestHarness.cs` — factory + JSON helpers building a real `McpServerService` over the stub (create).
- `servers/project-insight/tests/ToolMetadataTests.cs` — description assertions (create).
- `servers/project-insight/tests/ResourceAssignmentTests.cs` — behavior assertions (create).

---

## Task 1: Test project scaffold + tool-description rewrite

Bundles the test-project setup with the first deliverable (metadata), because the description assertions are the smallest thing that needs the project to exist and `InitializeTools()` to be reachable.

**Files:**
- Create: `servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`
- Create: `servers/project-insight/tests/ToolMetadataTests.cs`
- Modify: `servers/project-insight/src/ProjectInsightMCP.csproj`
- Modify: `servers/project-insight/src/Services/McpServerService.cs` (make `InitializeTools` internal; rewrite 6 descriptions)

**Interfaces:**
- Consumes: `McpServerService.InitializeTools()` (after visibility bump), `Shared.MCP.McpTool`.
- Produces: a buildable `ProjectInsightMCP.Tests` project other tasks add tests to.

- [ ] **Step 1: Create the test project file**

Create `servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <AssemblyName>ProjectInsightMCP.Tests</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../src/ProjectInsightMCP.csproj" />
  </ItemGroup>

</Project>
```

Note: the source project uses `Microsoft.NET.Sdk.Functions`. If the `ProjectReference` above fails to build (Functions SDK output quirks), fall back to compiling the needed sources directly by replacing the `ProjectReference` `ItemGroup` with explicit `<Compile Include="../src/Services/*.cs" />`, `<Compile Include="../src/Models/ProjectInsightModels.cs" />`, `<Compile Include="../../../shared/McpModels.cs" />` plus the matching package references from the src csproj — but try the `ProjectReference` first.

- [ ] **Step 2: Allow the test assembly to see internals**

In `servers/project-insight/src/ProjectInsightMCP.csproj`, add this item group (anywhere inside `<Project>`):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ProjectInsightMCP.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: Make `InitializeTools` reachable from tests**

In `servers/project-insight/src/Services/McpServerService.cs`, change the signature at `InitializeTools`:

```csharp
    internal static List<McpTool> InitializeTools()
```

(was `private static List<McpTool> InitializeTools()`)

- [ ] **Step 4: Write the failing description test**

Create `servers/project-insight/tests/ToolMetadataTests.cs`:

```csharp
using ProjectInsightMCP.Services;
using Xunit;

namespace ProjectInsightMCP.Tests;

public class ToolMetadataTests
{
    private static string Description(string toolName)
    {
        var tool = McpServerService.InitializeTools().Single(t => t.Name == toolName);
        return tool.Description.ToLowerInvariant();
    }

    [Theory]
    [InlineData("add_project_resource", new[] { "assign", "project manager", "allocation" })]
    [InlineData("add_task_resource", new[] { "assign", "automatically" })]
    [InlineData("update_project_resource", new[] { "project manager", "allocation" })]
    [InlineData("remove_project_resource", new[] { "unassign" })]
    [InlineData("remove_task_resource", new[] { "unassign" })]
    [InlineData("get_project_resources", new[] { "assigned" })]
    public void Description_contains_agent_matchable_vocabulary(string tool, string[] required)
    {
        var description = Description(tool);
        foreach (var word in required)
            Assert.Contains(word, description);
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`
Expected: build succeeds, `add_project_resource` case FAILS (current description lacks "assign"/"project manager"/"allocation").

- [ ] **Step 6: Rewrite the six descriptions**

In `servers/project-insight/src/Services/McpServerService.cs`, replace each `Description = "..."` line in the corresponding tool definition (find each by its `Name = "..."` line):

`get_project_resources` (currently: `"Get contributors/team members assigned to a project with roles, allocation %, hours, and task counts."`):

```csharp
                Description = "List the users assigned to a project (team members, project managers, schedulers) with their roles, allocation %, hours, and task counts. Use this to see who is staffed on a project, or to get a resource assignment id for update_project_resource / remove_project_resource.",
```

`add_project_resource` (currently: `"Add a user as a team member/contributor to a project. Use search_users to find the user ID and search_projects to find the project ID first."`):

```csharp
                Description = "Assign (staff / allocate) a user to a project - as a team member, project manager, or scheduler, with an optional default allocation %. This is THE tool for putting a person on a project. Idempotent and safe to re-run. Pass projectId + userId (GUIDs); if you only have names, resolve them with search_projects / search_users first.",
```

`update_project_resource` (currently: `"Update an existing project resource assignment (change role, allocation %, PM status, etc.). Get the resource ID from get_project_resources first."`):

```csharp
                Description = "Change an existing project assignment: promote to/from project manager or scheduler, adjust allocation %, activate/deactivate, or set resource type. Needs the resource assignment id from get_project_resources.",
```

`remove_project_resource` (currently: `"Remove a user from a project (delete the resource assignment). Get the resource ID from get_project_resources first."`):

```csharp
                Description = "Unassign a user from a project (delete the assignment). Needs the resource assignment id from get_project_resources.",
```

`add_task_resource` (currently: `"Assign a user to a specific task. The user should already be a project resource. Use get_project_tasks to find the task ID and search_users to find the user ID."`):

```csharp
                Description = "Assign (staff) a user to a specific task, with optional planned work hours and allocation %. If the user isn't on the project yet, they are added to the project automatically. Pass taskId + userId (GUIDs); use get_project_tasks / search_users to resolve them.",
```

`remove_task_resource` (currently: `"Remove a user from a task (delete the task resource assignment)."`):

```csharp
                Description = "Unassign a user from a task (delete the task assignment). Needs the task resource assignment id.",
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`
Expected: PASS (all `Theory` cases green).

- [ ] **Step 8: Commit**

```bash
git add servers/project-insight/tests/ProjectInsightMCP.Tests.csproj \
        servers/project-insight/tests/ToolMetadataTests.cs \
        servers/project-insight/src/ProjectInsightMCP.csproj \
        servers/project-insight/src/Services/McpServerService.cs
git commit -m "feat: agent-friendly descriptions for resource-assignment tools + test project"
```

---

## Task 2: Honesty fix for `add_project_resource`

Make the advertised role/allocation params actually apply instead of being silently dropped. Introduces the HTTP-level test harness (setup folded here — the first behavior test needs it).

**Files:**
- Create: `servers/project-insight/tests/StubHttpMessageHandler.cs`
- Create: `servers/project-insight/tests/McpServerTestHarness.cs`
- Create: `servers/project-insight/tests/ResourceAssignmentTests.cs`
- Modify: `servers/project-insight/src/Services/McpServerService.cs` (make `ExecuteToolAsync` internal; rewrite `AddProjectResourceAsync`; add `FormatApiError`)

**Interfaces:**
- Consumes: `McpServerService.ExecuteToolAsync(McpToolCall, CancellationToken)` (after visibility bump); `ProjectInsightApiService`, `ApiTokenAccessor`.
- Produces: `StubHttpMessageHandler` (records `Requests` as `RecordedRequest(HttpMethod Method, Uri Uri, string Body)`, returns queued responses FIFO); `McpServerTestHarness.CreateServer(params HttpResponseMessage[])` → `(McpServerService mcp, StubHttpMessageHandler handler)`; `McpServerTestHarness.Json(HttpStatusCode, string)`; `McpServerTestHarness.Args(string)`; `McpServerTestHarness.ReadJson<T>(string)`. Also the private `FormatApiError(string action, string? message, List<string>? errors)` in `McpServerService`.

- [ ] **Step 1: Make `ExecuteToolAsync` reachable from tests**

In `servers/project-insight/src/Services/McpServerService.cs`, change:

```csharp
    internal async Task<McpToolResult> ExecuteToolAsync(McpToolCall toolCall, CancellationToken cancellationToken)
```

(was `private async Task<McpToolResult> ExecuteToolAsync(...)`)

- [ ] **Step 2: Create the fake HTTP handler**

Create `servers/project-insight/tests/StubHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace ProjectInsightMCP.Tests;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public List<RecordedRequest> Requests { get; } = new();

    public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        => _responses = new Queue<HttpResponseMessage>(responses);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK)
              { Content = new StringContent("{\"success\":true}") };
    }
}
```

- [ ] **Step 3: Create the test harness**

Create `servers/project-insight/tests/McpServerTestHarness.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Services;
using Shared.MCP;

namespace ProjectInsightMCP.Tests;

public static class McpServerTestHarness
{
    private static readonly JsonSerializerOptions ReadOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static (McpServerService mcp, StubHttpMessageHandler handler) CreateServer(
        params HttpResponseMessage[] responses)
    {
        var handler = new StubHttpMessageHandler(responses);
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectInsight:BaseUrl"] = "https://pi.test"
            })
            .Build();
        var token = new ApiTokenAccessor { ApiToken = "test-token" };
        var api = new ProjectInsightApiService(
            httpClient, NullLogger<ProjectInsightApiService>.Instance, config, token);
        var mcp = new McpServerService(
            api, NullLogger<McpServerService>.Instance, token);
        return (mcp, handler);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Mirrors how the MCP layer populates arguments: values are JsonElement.
    public static Dictionary<string, object> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);

    public static T ReadJson<T>(string json)
        => JsonSerializer.Deserialize<T>(json, ReadOptions)!;

    public static McpToolCall Call(string name, string argsJson)
        => new() { Name = name, Arguments = Args(argsJson) };
}
```

- [ ] **Step 4: Write the failing behavior tests**

Create `servers/project-insight/tests/ResourceAssignmentTests.cs`:

```csharp
using System.Net;
using ProjectInsightMCP.Models;
using Xunit;
using static ProjectInsightMCP.Tests.McpServerTestHarness;

namespace ProjectInsightMCP.Tests;

public class ResourceAssignmentTests
{
    [Fact]
    public async Task AddProjectResource_with_role_and_allocation_applies_them()
    {
        var (mcp, handler) = CreateServer(
            Json(HttpStatusCode.OK, "{\"savedId\":\"res-123\",\"success\":true}"),  // ensure
            Json(HttpStatusCode.OK, "{\"savedId\":\"res-123\",\"success\":true}")); // follow-up save

        var result = await mcp.ExecuteToolAsync(
            Call("add_project_resource",
                 "{\"projectId\":\"proj-1\",\"userId\":\"user-1\",\"isProjectManager\":true,\"percentAllocationDefault\":50}"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("ensure-user-is-project-resource", handler.Requests[0].Uri.AbsolutePath);
        Assert.EndsWith("/api/project-resource", handler.Requests[1].Uri.AbsolutePath);

        var saved = ReadJson<ProjectResource>(handler.Requests[1].Body);
        Assert.Equal("res-123", saved.Id);
        Assert.True(saved.IsProjectManager);
        Assert.Equal(50m, saved.PercentAllocationDefault);
    }

    [Fact]
    public async Task AddProjectResource_without_optional_params_only_calls_ensure()
    {
        var (mcp, handler) = CreateServer(
            Json(HttpStatusCode.OK, "{\"savedId\":\"res-9\",\"success\":true}"));

        var result = await mcp.ExecuteToolAsync(
            Call("add_project_resource", "{\"projectId\":\"p\",\"userId\":\"u\"}"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Single(handler.Requests);
        Assert.Contains("ensure-user-is-project-resource", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task AddProjectResource_reports_partial_failure_when_role_save_fails()
    {
        var (mcp, handler) = CreateServer(
            Json(HttpStatusCode.OK, "{\"savedId\":\"res-1\",\"success\":true}"), // ensure OK
            Json(HttpStatusCode.BadRequest, "bad request"));                     // save fails

        var result = await mcp.ExecuteToolAsync(
            Call("add_project_resource",
                 "{\"projectId\":\"p\",\"userId\":\"u\",\"isProjectManager\":true}"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("was added to the project", result.Content[0].Text!);
        Assert.Contains("role", result.Content[0].Text!.ToLowerInvariant());
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj --filter ResourceAssignmentTests`
Expected: FAIL — today `AddProjectResource_with_role_and_allocation_applies_them` records only **1** request (params dropped), and the partial-failure test's wording is absent.

- [ ] **Step 6: Add the `FormatApiError` helper**

In `servers/project-insight/src/Services/McpServerService.cs`, add this private method (place it near the other resource handlers, e.g. just above `AddProjectResourceAsync`):

```csharp
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
```

- [ ] **Step 7: Rewrite `AddProjectResourceAsync`**

Replace the entire body of `AddProjectResourceAsync` (from its opening `{` after the signature at `McpServerService.cs:674` through its closing `}`) with:

```csharp
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
            if (arguments.TryGetValue("resourceTypeDefaultId", out var rtId) && !string.IsNullOrEmpty(rtId?.ToString()))
            {
                resource.ResourceTypeDefaultId = rtId.ToString();
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
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj --filter ResourceAssignmentTests`
Expected: PASS (all three facts green).

- [ ] **Step 9: Commit**

```bash
git add servers/project-insight/tests/StubHttpMessageHandler.cs \
        servers/project-insight/tests/McpServerTestHarness.cs \
        servers/project-insight/tests/ResourceAssignmentTests.cs \
        servers/project-insight/src/Services/McpServerService.cs
git commit -m "fix: add_project_resource honors role and allocation params instead of dropping them"
```

---

## Task 3: `add_task_resource` auto-ensures project membership

Remove the "user should already be a project resource" precondition by resolving the task's project and ensuring membership before assigning to the task.

**Files:**
- Modify: `servers/project-insight/src/Services/McpServerService.cs` (`AddTaskResourceAsync`)
- Modify: `servers/project-insight/tests/ResourceAssignmentTests.cs` (add test)

**Interfaces:**
- Consumes: `_projectInsightApi.GetTaskAsync(taskId, "Project_Id", ct)` → `ApiResponse<ProjectTask>` (`ProjectTask.ProjectId`); `_projectInsightApi.EnsureProjectResourceAsync`; `_projectInsightApi.SaveTaskResourceAsync`.
- Produces: no new public surface.

- [ ] **Step 1: Write the failing test**

Append to `servers/project-insight/tests/ResourceAssignmentTests.cs` inside the class:

```csharp
    [Fact]
    public async Task AddTaskResource_auto_ensures_project_membership_first()
    {
        var (mcp, handler) = CreateServer(
            Json(HttpStatusCode.OK, "{\"id\":\"task-1\",\"project_Id\":\"proj-9\"}"), // get task
            Json(HttpStatusCode.OK, "{\"savedId\":\"pr-1\",\"success\":true}"),        // ensure project resource
            Json(HttpStatusCode.OK, "{\"savedId\":\"tr-1\",\"success\":true}"));       // save task resource

        var result = await mcp.ExecuteToolAsync(
            Call("add_task_resource",
                 "{\"taskId\":\"task-1\",\"userId\":\"user-1\",\"workHours\":8}"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/api/task/task-1", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("ensure-user-is-project-resource/proj-9", handler.Requests[1].Uri.AbsolutePath);
        Assert.EndsWith("/api/task-resource", handler.Requests[2].Uri.AbsolutePath);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj --filter AddTaskResource_auto_ensures_project_membership_first`
Expected: FAIL — today only 1 request is made (`POST /api/task-resource`); there is no `GET /api/task/...` and no ensure call.

- [ ] **Step 3: Rewrite `AddTaskResourceAsync`**

In `servers/project-insight/src/Services/McpServerService.cs`, in `AddTaskResourceAsync`, keep the existing `taskId`/`userId` validation blocks, then replace everything from the construction of `var resource = new TaskResource { ... }` down to (but not including) the final success `return`, with:

```csharp
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
```

Note: this preserves the existing `taskIdObj`/`userIdObj` variables set by the validation blocks above, and leaves the subsequent `if (!response.Success ...)` failure block and the final success `return` in place. (Optionally, update that failure block's text to use `FormatApiError("assign user to task", response.Message, response.Errors)` for consistency.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`
Expected: PASS (all resource + metadata tests green).

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/tests/ResourceAssignmentTests.cs \
        servers/project-insight/src/Services/McpServerService.cs
git commit -m "feat: add_task_resource auto-ensures project membership before assigning"
```

---

## Task 4: Actionable error messages on the ensure failure path

`FormatApiError` (added in Task 2) is already wired into `AddProjectResourceAsync` and `AddTaskResourceAsync`. This task locks in the agent-actionable mapping with a test, and applies the same helper to `update_project_resource` / `remove_project_resource` / `remove_task_resource` for consistency.

**Files:**
- Modify: `servers/project-insight/tests/ResourceAssignmentTests.cs` (add test)
- Modify: `servers/project-insight/src/Services/McpServerService.cs` (`UpdateProjectResourceAsync`, `RemoveProjectResourceAsync`, `RemoveTaskResourceAsync` failure text)

**Interfaces:**
- Consumes: `FormatApiError` (from Task 2).
- Produces: no new surface.

- [ ] **Step 1: Write the failing test**

Append to `servers/project-insight/tests/ResourceAssignmentTests.cs` inside the class:

```csharp
    [Fact]
    public async Task AddProjectResource_maps_404_to_actionable_message()
    {
        var (mcp, _) = CreateServer(
            Json(HttpStatusCode.NotFound, "resource not found"));

        var result = await mcp.ExecuteToolAsync(
            Call("add_project_resource", "{\"projectId\":\"bad-guid\",\"userId\":\"bad-guid\"}"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("verify the IDs", result.Content[0].Text!);
    }
```

- [ ] **Step 2: Run the test to verify it passes or fails as expected**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj --filter AddProjectResource_maps_404_to_actionable_message`
Expected: This should already PASS because `AddProjectResourceAsync` calls `FormatApiError` (Task 2). If it FAILS, confirm `EnsureProjectResourceAsync` sets `Message = "API returned NotFound"` on a 404 (it does) and that `FormatApiError`'s `Contains("NotFound")` branch fires.

- [ ] **Step 3: Apply `FormatApiError` to the remaining resource handlers**

In `servers/project-insight/src/Services/McpServerService.cs`, replace the failure-message text in these three handlers so they use the helper.

In `UpdateProjectResourceAsync`, replace:

```csharp
            var errorMsg = response.Errors?.Count > 0 ? string.Join("; ", response.Errors) : response.Message ?? "Unknown error";
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Failed to update project resource: {errorMsg}" } },
                IsError = true
            };
```

with:

```csharp
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("update the project assignment", response.Message, response.Errors) } },
                IsError = true
            };
```

In `RemoveProjectResourceAsync`, replace:

```csharp
            var errorMsg = response.Errors?.Count > 0 ? string.Join("; ", response.Errors) : response.Message ?? "Unknown error";
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = $"Failed to remove project resource: {errorMsg}" } },
                IsError = true
            };
```

with:

```csharp
            return new McpToolResult
            {
                Content = new List<McpContent> { new() { Type = "text", Text = FormatApiError("unassign the user from the project", response.Message, response.Errors) } },
                IsError = true
            };
```

In `RemoveTaskResourceAsync`, find its equivalent failure block (the `errorMsg` + `"Failed to remove task resource: ..."` return) and replace it the same way, using `FormatApiError("unassign the user from the task", response.Message, response.Errors)`.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`
Expected: PASS (all tests green).

- [ ] **Step 5: Build the source project to confirm no regressions**

Run: `dotnet build servers/project-insight/src/ProjectInsightMCP.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add servers/project-insight/tests/ResourceAssignmentTests.cs \
        servers/project-insight/src/Services/McpServerService.cs
git commit -m "feat: actionable API error messages across resource-assignment tools"
```

---

## Self-Review

**Spec coverage:**
- §1 Metadata rewrite (all 6 tools) → Task 1 (Step 6) + `ToolMetadataTests`.
- §2 Honesty fix for `add_project_resource` → Task 2 (ensure → follow-up save; SavedId-missing handled by explicit partial-success error rather than a lookup — the spec's fallback lookup is deferred as a documented option, since it depends on unobserved API behavior; no silent success either way).
- §3 Task auto-ensure → Task 3.
- §4 Actionable errors → Task 2 (`FormatApiError`, wired into add paths) + Task 4 (test + remaining handlers).
- §5 Verification / first xUnit project → Task 1 (scaffold) + Tasks 2–4 (tests).

**Deviation from spec (intentional):** the spec's Risk #1 fallback (look up the resource id via `get_project_resources` when `ensure` returns an empty `SavedId` for an already-existing member) is **not** built. Instead, that case returns an explicit, actionable error telling the agent to use `get_project_resources` + `update_project_resource`. Rationale: YAGNI until real API behavior is observed; the honesty goal (never claim a role was set when it wasn't) is met either way. If manual testing shows `ensure` routinely returns empty `SavedId` for existing members, add the lookup as a follow-up.

**Type consistency:** `FormatApiError(string, string?, List<string>?)` defined in Task 2 and reused in Tasks 3–4. `RecordedRequest`, `CreateServer`, `Json`, `Args`, `ReadJson`, `Call` defined in Task 2's harness and used in Tasks 2–4. `ProjectResource` / `TaskResource` / `ProjectTask` property names match the models.

**Placeholder scan:** none — every code step contains complete code and exact run commands.
