using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.Stdio;
using Xunit;

namespace ProjectInsightMCP.Tests;

/// <summary>
/// Raw JSON-RPC lines into the server, as a stdio client or a one-shot <c>printf … | projectinsight-mcp</c>
/// pipeline writes them. (The SDK's own McpClient can't drive these cases: it adds params to every
/// request and, on its newer protocol version, never sends notifications/cancelled.)
/// </summary>
public sealed class RawStdioTests
{
    private const string Init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static string Cancel(int id) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id, reason = "user" }
    });

    /// <summary>Marker line: wait briefly before writing the rest, so the call before it starts first.</summary>
    private const string Pause = "<pause>";

    private static string Call(int id, string entry) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id, method = "tools/call",
        @params = new { name = "delete_time_entry", arguments = new { timeEntryId = entry } }
    });

    [Fact]
    public async Task Requests_written_before_stdin_closes_are_all_answered()
    {
        var pi = new StubHttpMessageHandler(Array.Empty<HttpResponseMessage>()) { Delay = TimeSpan.FromMilliseconds(300) };

        var responses = await RunOneShot(pi,
            Init, Initialized,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            Call(3, "te-a"), Call(4, "te-b"),
            """{"jsonrpc":"2.0","id":"five","method":"ping"}""");

        Assert.Equal(new[] { "1", "2", "3", "4", "five" }, responses.Keys.Order());
        Assert.Equal("Successfully deleted time entry te-b",
            (string?)responses["4"]["result"]!["content"]![0]!["text"]);
        Assert.Equal(2, pi.Requests.Count);
    }

    [Fact]
    public async Task Cancelling_a_running_call_lets_it_finish_rather_than_abort_midway()
    {
        var pi = new StubHttpMessageHandler(Array.Empty<HttpResponseMessage>()) { Delay = TimeSpan.FromMilliseconds(400) };

        var responses = await RunOneShot(pi,
            Init, Initialized, Call(3, "te-running"), Pause, Cancel(3));

        // The stub records a request only if its (cancellable) delay ran to the end; stdin EOF
        // right after the cancel also waits for the running call.
        Assert.EndsWith("/te-running", Assert.Single(pi.Requests).Uri.AbsolutePath);
        Assert.DoesNotContain("3", responses.Keys);
    }

    [Fact]
    public async Task Tool_call_without_params_is_invalid_params_like_before()
    {
        var pi = new StubHttpMessageHandler(Array.Empty<HttpResponseMessage>());

        var responses = await RunOneShot(pi,
            Init, Initialized,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call"}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":null}""");

        Assert.Equal(-32602, (int?)responses["3"]["error"]!["code"]);
        Assert.Equal(-32602, (int?)responses["4"]["error"]!["code"]);
    }

    [Fact]
    public async Task A_cancelled_queued_call_never_runs_and_does_not_hold_up_exit()
    {
        var pi = new StubHttpMessageHandler(Array.Empty<HttpResponseMessage>()) { Delay = TimeSpan.FromMilliseconds(300) };

        // A long quiet period, so exiting promptly shows the cancel was tracked, not timed out.
        var sw = Stopwatch.StartNew();
        var responses = await RunOneShot(pi, TimeSpan.FromSeconds(10),
            Init, Initialized, Call(3, "te-a"), Pause, Call(4, "te-queued"), Cancel(4));

        Assert.Contains("3", responses.Keys);
        Assert.DoesNotContain("4", responses.Keys);
        Assert.Single(pi.Requests);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task A_request_that_is_never_answered_is_given_up_on_after_the_quiet_period()
    {
        // Drives the wrapper directly: a request is read, no answer is ever written.
        var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":7,"method":"x"}""" + "\n"));
        var stdio = new DrainingStdio(input, new MemoryStream(), () => false, TimeSpan.FromMilliseconds(300));
        var buffer = new byte[4096];

        var sw = Stopwatch.StartNew();
        while (await stdio.Input.ReadAsync(buffer) > 0) { }

        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        Assert.Equal(1, stdio.PendingCount);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"m"}""", "n:1", "m", null, false)]
    [InlineData("""{"id":"1","jsonrpc":"2.0","result":{"x":{"id":9}}}""", "s:1", null, null, true)]
    [InlineData("""{"jsonrpc":"2.0","id":3,"error":{"code":-1,"message":"m"}}""", "n:3", null, null, true)]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"x":{"requestId":1},"requestId":"r"}}""", null, "notifications/cancelled", "s:r", false)]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"m"}""", null, "m", null, false)]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"m"}""", null, "m", null, false)]
    public void Envelope_reader_picks_out_ids_like_the_sdk(string line, string? id, string? method, string? cancelled, bool isResponse)
    {
        Assert.True(DrainingStdio.TryReadEnvelope(Encoding.UTF8.GetBytes(line), out var actualId,
            out var actualMethod, out var actualCancelled, out var actualIsResponse));
        Assert.Equal((id, method, cancelled, isResponse), (actualId, actualMethod, actualCancelled, actualIsResponse));
    }

    [Theory]
    [InlineData("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"m\"}]")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,")]
    [InlineData("")]
    public void Envelope_reader_ignores_lines_it_cannot_read(string line)
    {
        Assert.False(DrainingStdio.TryReadEnvelope(Encoding.UTF8.GetBytes(line), out _, out _, out _, out _));
    }

    /// <summary>Writes the lines, closes the server's input, and collects responses until the host exits.</summary>
    private static async Task<Dictionary<string, JsonNode>> RunOneShot(StubHttpMessageHandler pi, params string[] lines)
        => await RunOneShot(pi, TimeSpan.FromMilliseconds(500), lines);

    private static async Task<Dictionary<string, JsonNode>> RunOneShot(StubHttpMessageHandler pi, TimeSpan quietPeriod, params string[] lines)
    {
        var input = new Pipe();
        var output = new Pipe();

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectInsight:BaseUrl"] = "https://pi.test",
            ["ProjectInsight:ApiToken"] = "test-token"
        });
        builder.Services.AddLogging();
        builder.Services.AddProjectInsightServices(builder.Configuration);
        builder.Services.AddSingleton(sp => new ProjectInsightApiService(
            new HttpClient(pi), NullLogger<ProjectInsightApiService>.Instance,
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ApiTokenAccessor>()));
        builder.Services.AddProjectInsightMcp().WithDrainingStreamServerTransport(
            () => input.Reader.AsStream(), () => output.Writer.AsStream(), quietPeriod);

        using var host = builder.Build();
        var responses = new Dictionary<string, JsonNode>();
        var reading = Task.Run(async () =>
        {
            using var reader = new StreamReader(output.Reader.AsStream());
            while (await reader.ReadLineAsync() is { } line)
            {
                var node = JsonNode.Parse(line)!;
                if (node["id"] is { } id)
                    responses[id.ToString()] = node;
            }
        });

        // The host stops by itself once the session ends; give up well before the test runner would.
        var running = host.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var line in lines)
        {
            if (line == Pause)
                await Task.Delay(100);
            else
                await input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }
        await input.Writer.CompleteAsync();

        await running;
        await output.Writer.CompleteAsync();
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        return responses;
    }
}
