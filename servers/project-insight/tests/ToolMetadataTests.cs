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
