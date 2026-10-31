using ProjectInsightMCP.Server;
using Xunit;

namespace ProjectInsightMCP.Tests;

public class ToolCallGateTests
{
    [Fact]
    public async Task Calls_with_the_same_key_run_one_at_a_time()
    {
        var gate = new ToolCallGate();
        var running = 0;
        var maxRunning = 0;

        async Task<int> Call()
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
            return 0;
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gate.RunAsync("user-a", Call, CancellationToken.None)));
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task Calls_with_different_keys_run_at_the_same_time()
    {
        var gate = new ToolCallGate();
        var bothStarted = new TaskCompletionSource();
        var started = 0;

        async Task<int> Call()
        {
            if (Interlocked.Increment(ref started) == 2)
                bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return 0;
        }

        await Task.WhenAll(gate.RunAsync("user-a", Call, CancellationToken.None),
                           gate.RunAsync("user-b", Call, CancellationToken.None));
        Assert.True(bothStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Version_drops_the_source_revision()
        => Assert.Equal("2.1.0", ProjectInsightServer.VersionOf("2.1.0+abc123"));
}
