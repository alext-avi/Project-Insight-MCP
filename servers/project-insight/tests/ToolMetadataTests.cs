using System.Net;
using ProjectInsightMCP.Services;
using Xunit;
using static ProjectInsightMCP.Tests.McpServerTestHarness;

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
    [InlineData("get_task_resources", new[] { "assigned", "task" })]
    [InlineData("list_user_pto", new[] { "plan_time_changes", "create_pto", "timecodeid" })]
    public void Description_contains_agent_matchable_vocabulary(string tool, string[] required)
    {
        var description = Description(tool);
        foreach (var word in required)
            Assert.Contains(word, description);
    }

    [Fact]
    public async Task List_user_pto_points_to_plan_time_changes_for_the_time_code_ids()
    {
        var (mcp, _) = CreateServer(Json(HttpStatusCode.OK, """
            [{"Id":"pto-1","User_Id":"u-1","Date":"2026-11-02T00:00:00","PTOHours":8,"TimeCode_Id":"tc-1","TimeCode":{"Id":"tc-1","Name":"PTO"}}]
            """));

        var result = await mcp.ExecuteToolAsync(Call("list_user_pto", "{\"userId\":\"u-1\"}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("timeCodeId in a plan_time_changes create_pto change", result.Content[0].Text!);
    }
}
