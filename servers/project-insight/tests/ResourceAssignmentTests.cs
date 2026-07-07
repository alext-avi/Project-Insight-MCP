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
    public async Task AddProjectResource_with_resourceTypeDefaultId_sets_it_without_corruption()
    {
        var (mcp, handler) = CreateServer(
            Json(HttpStatusCode.OK, "{\"savedId\":\"res-rt\",\"success\":true}"),  // ensure
            Json(HttpStatusCode.OK, "{\"savedId\":\"res-rt\",\"success\":true}")); // follow-up save

        var result = await mcp.ExecuteToolAsync(
            Call("add_project_resource",
                 "{\"projectId\":\"proj-rt\",\"userId\":\"user-rt\",\"resourceTypeDefaultId\":\"rt-guid-123\"}"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(2, handler.Requests.Count);

        var saved = ReadJson<ProjectInsightMCP.Models.ProjectResource>(handler.Requests[1].Body);
        Assert.Equal("rt-guid-123", saved.ResourceTypeDefaultId);
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
}
