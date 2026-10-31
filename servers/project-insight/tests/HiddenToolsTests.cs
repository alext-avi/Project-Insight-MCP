using System.IO.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProjectInsightMCP.Server;
using ProjectInsightMCP.Stdio;
using Xunit;

namespace ProjectInsightMCP.Tests;

/// <summary>A host can hide tools: hidden tools leave tools/list and calls to them are refused.</summary>
public sealed class HiddenToolsTests : IAsyncLifetime
{
    private static readonly string[] Hidden = ["link_pi_account", "get_time_changes"];
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
            ["ProjectInsight:DraftsPath"] = Path.Combine(Path.GetTempPath(), "pi-hidden-" + Guid.NewGuid().ToString("N"))
        });
        builder.Services.AddLogging();
        builder.Services.AddProjectInsightServices(builder.Configuration);
        builder.Services.AddProjectInsightMcp(new ProjectInsightMcpOptions { ExposeTool = name => !Hidden.Contains(name) })
            .WithDrainingStreamServerTransport(() => clientToServer.Reader.AsStream(), () => serverToClient.Writer.AsStream());
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
    }

    [Fact]
    public async Task Hidden_tools_are_not_listed()
    {
        var names = (await _client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.DoesNotContain("link_pi_account", names);   // a Core tool
        Assert.DoesNotContain("get_time_changes", names);  // an SDK-native tool
        Assert.Contains("search_projects", names);
    }

    [Theory]
    [InlineData("link_pi_account")]
    [InlineData("get_time_changes")]
    public async Task Hidden_tools_cannot_be_called(string name)
        => await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await _client.CallToolAsync(name, new Dictionary<string, object?> { ["id"] = "d_x" }));
}
