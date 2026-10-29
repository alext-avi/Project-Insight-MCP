using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.Stdio;
using Xunit;
using static ProjectInsightMCP.Tests.McpServerTestHarness;

namespace ProjectInsightMCP.Tests;

/// <summary>
/// End-to-end through the official SDK: a real McpClient talks to the server as wired by
/// the stdio host, over in-memory pipes, with Project Insight HTTP stubbed.
/// </summary>
public sealed class SdkServerTests : IAsyncLifetime
{
    private readonly StubHttpMessageHandler _pi = new(Array.Empty<HttpResponseMessage>());
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
            ["ProjectInsight:ApiToken"] = "test-token"
        });
        builder.Services.AddLogging();
        builder.Services.AddProjectInsightServices(builder.Configuration);
        builder.Services.AddSingleton(sp => new ProjectInsightApiService(
            new HttpClient(_pi), NullLogger<ProjectInsightApiService>.Instance,
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ApiTokenAccessor>()));
        builder.Services.AddProjectInsightMcp().WithDrainingStreamServerTransport(
            () => clientToServer.Reader.AsStream(), () => serverToClient.Writer.AsStream());

        _host = builder.Build();
        await _host.StartAsync();

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void Handshake_reports_server_identity_and_tools_capability()
    {
        Assert.Equal(ProjectInsightServer.ServerName, _client.ServerInfo!.Name);
        Assert.NotNull(_client.ServerCapabilities.Tools);
    }

    [Fact]
    public async Task Tools_list_over_the_wire_matches_snapshot()
    {
        var tools = await _client.ListToolsAsync();
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
    }

    [Fact]
    public async Task Tool_call_reaches_project_insight_and_returns_text()
    {
        var result = await _client.CallToolAsync("delete_time_entry",
            new Dictionary<string, object?> { ["timeEntryId"] = "te-123" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Successfully deleted time entry te-123", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var request = Assert.Single(_pi.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith("/api/time-entry/te-123", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Concurrent_tool_calls_run_one_at_a_time_like_the_pre_sdk_loop()
    {
        // The pre-SDK stdio loop handled one request at a time, and tool handlers (device
        // activation in particular) rely on that. The SDK dispatches requests concurrently.
        _pi.Delay = TimeSpan.FromMilliseconds(150);

        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            _client.CallToolAsync("delete_time_entry",
                new Dictionary<string, object?> { ["timeEntryId"] = $"te-{i}" }).AsTask()));

        Assert.All(results, r => Assert.NotEqual(true, r.IsError));
        Assert.Equal(4, _pi.Requests.Count);
        Assert.Equal(1, _pi.MaxConcurrent);
    }

    [Fact]
    public async Task Failed_tool_call_is_reported_as_tool_error()
    {
        var result = await _client.CallToolAsync("delete_time_entry", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("timeEntryId parameter is required",
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Empty(_pi.Requests);
    }

    [Fact]
    public async Task Unknown_tool_is_reported_as_tool_error()
    {
        var result = await _client.CallToolAsync("no_such_tool", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

}
