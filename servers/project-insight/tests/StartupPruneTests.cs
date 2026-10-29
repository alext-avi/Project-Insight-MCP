using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Stdio;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests;

public sealed class StartupPruneTests : IDisposable
{
    private readonly TempDraftRoot _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Startup_prune_removes_drafts_untouched_for_seven_days()
    {
        var time = new ManualTimeProvider(Drafts.T0);
        var services = new ServiceCollection()
            .AddSingleton<IDraftStore>(new FileDraftStore(_dir.Root, time))
            .BuildServiceProvider();
        var store = services.GetRequiredService<IDraftStore>();
        var old = Drafts.New();
        await store.CreateAsync(old);
        time.Now = Drafts.T0.AddDays(8);

        await ProjectInsightServer.PruneDraftsAtStartupAsync(services);

        Assert.Null(await store.GetAsync("u-1", old.Id));
    }

    [Fact]
    public async Task Startup_prune_failure_does_not_stop_the_server()
    {
        // No IDraftStore registered: resolving it throws. The prune must swallow that.
        await ProjectInsightServer.PruneDraftsAtStartupAsync(new ServiceCollection().BuildServiceProvider());
    }
}
