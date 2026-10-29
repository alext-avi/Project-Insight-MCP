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
