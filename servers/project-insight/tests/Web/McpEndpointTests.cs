using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class McpEndpointTests
{
    private static readonly string[] SignInTools = ["complete_authentication", "link_pi_account", "complete_pi_link"];

    [Fact]
    public async Task Web_tools_are_the_stdio_tools_without_the_sign_in_tools()
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        var expected = ToolListSnapshotTests.LoadSnapshot().Select(t => t!["name"]!.GetValue<string>())
            .Concat(TimeChangeToolsTests.ToolNames)
            .Except(SignInTools)
            .Order();
        Assert.Equal(expected, (await mcp.ListToolsAsync()).Select(t => t.Name).Order());
        Assert.Contains(await mcp.ListResourcesAsync(), r => r.Uri == "ui://project-insight/time-changes.html");
    }

    [Fact]
    public async Task Instructions_name_only_tools_the_web_host_lists()
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        Assert.Equal(ProjectInsightMCP.Server.ProjectInsightServer.Instructions, mcp.ServerInstructions);
        var listed = (await mcp.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        var named = System.Text.RegularExpressions.Regex.Matches(mcp.ServerInstructions!, @"\b[a-z]+(?:_[a-z]+)+\b")
            .Select(m => m.Value).ToList();
        Assert.NotEmpty(named);
        Assert.All(named, name => Assert.Contains(name, listed));
    }

    [Fact]
    public async Task The_organizations_own_guidance_follows_the_instructions()
    {
        using var factory = new WebFactory();
        factory.Settings["ProjectInsight:Instructions"] = "  Timesheets are due Monday by noon.  ";
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        Assert.StartsWith(ProjectInsightMCP.Server.ProjectInsightServer.Instructions, mcp.ServerInstructions);
        Assert.EndsWith("\n\nGuidance from this organization:\nTimesheets are due Monday by noon.", mcp.ServerInstructions);
    }

    [Theory]
    [InlineData("link_pi_account")]
    [InlineData("complete_pi_link")]
    [InlineData("complete_authentication")]
    public async Task Sign_in_tools_cannot_be_called(string name)
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await mcp.CallToolAsync(name, new Dictionary<string, object?> { ["activationCode"] = "x" }));
    }

    [Fact]
    public async Task Each_users_calls_carry_their_own_token()
    {
        using var factory = new WebFactory();
        factory.Pi.ToolCallDelay = TimeSpan.FromMilliseconds(100);
        await using var a = await factory.ConnectAsync(PiStub.TokenA);
        await using var b = await factory.ConnectAsync(PiStub.TokenB);

        await Task.WhenAll(Enumerable.Range(1, 3).SelectMany(i => new[]
        {
            a.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = $"te-a-{i}" }).AsTask(),
            b.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = $"te-b-{i}" }).AsTask()
        }));

        var deletes = factory.Pi.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Equal(6, deletes.Count);
        Assert.All(deletes, r => Assert.Equal(
            r.Uri.AbsolutePath.Contains("te-a-") ? $"Bearer {PiStub.TokenA}" : $"Bearer {PiStub.TokenB}",
            r.Authorization));
    }

    [Fact]
    public async Task One_users_calls_queue_while_another_users_run()
    {
        using var factory = new WebFactory();
        factory.Pi.ToolCallDelay = TimeSpan.FromMilliseconds(150);
        await using var a = await factory.ConnectAsync(PiStub.TokenA);
        await using var b = await factory.ConnectAsync(PiStub.TokenB);

        await Task.WhenAll(
            a.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-a-1" }).AsTask(),
            a.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-a-2" }).AsTask(),
            b.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-b-1" }).AsTask(),
            b.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-b-2" }).AsTask());

        Assert.Equal(2, factory.Pi.MaxToolCallsInFlight);
    }

    [Fact]
    public async Task Drafts_are_visible_only_to_their_owner()
    {
        using var factory = new WebFactory();
        await using var a = await factory.ConnectAsync(PiStub.TokenA);
        await using var b = await factory.ConnectAsync(PiStub.TokenB);
        var draft = Drafts.New(owner: PiStub.UserA);
        await factory.Services.GetRequiredService<IDraftStore>().CreateAsync(draft);

        var mine = await a.CallToolAsync("get_time_changes", new Dictionary<string, object?> { ["id"] = draft.Id });
        var theirs = await b.CallToolAsync("get_time_changes", new Dictionary<string, object?> { ["id"] = draft.Id });

        Assert.NotEqual(true, mine.IsError);
        Assert.Contains(draft.Intent, string.Join(" ", mine.Content.OfType<TextContentBlock>().Select(c => c.Text)) + mine.StructuredContent);
        Assert.Equal(true, theirs.IsError);
        Assert.DoesNotContain(draft.Intent, string.Join(" ", theirs.Content.OfType<TextContentBlock>().Select(c => c.Text)) + theirs.StructuredContent);
    }

    [Fact]
    public async Task Get_on_the_stateless_endpoint_is_405()
    {
        using var factory = new WebFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + PiStub.TokenA);
        request.Headers.Accept.ParseAdd("text/event-stream");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await factory.Client().SendAsync(request)).StatusCode);
    }
}
