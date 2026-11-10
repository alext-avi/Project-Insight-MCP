using Microsoft.Extensions.Options;
using ProjectInsightMCP.TimeChanges;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Hosting;

/// <summary>Deletes expired sign-in records every 5 minutes, and old drafts and unused clients hourly.</summary>
public sealed class PruneService(IServiceProvider services, TimeProvider time, IOptions<AuthOptions> auth, ILogger<PruneService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    // A used client must outlive the token it was issued for, or it is forgotten just when it must sign in again.
    private static readonly TimeSpan ClientGrace = TimeSpan.FromDays(30);
    // A registration nobody signed in with: the cap of 500 must not be fillable by those.
    private static readonly TimeSpan ClientNeverUsedFor = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private DateTimeOffset _nextHourly = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every, time);
        do
        {
            await PruneOnceAsync(stop);
        }
        while (await timer.WaitForNextTickAsync(stop));
    }

    public async Task PruneOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await TryAsync("sign-in sessions", () => services.GetRequiredService<ISessionStore>().PruneAsync(now, ct), ct);
        await TryAsync("authorization codes", () => services.GetRequiredService<ICodeStore>().PruneAsync(now, ct), ct);
        if (HourlyDue(now))
        {
            await TryAsync("drafts", () => services.GetRequiredService<IDraftStore>().PruneAllAsync(ct), ct);
            await TryAsync("clients", () => services.GetRequiredService<IClientStore>().PruneAsync(now - TimeSpan.FromDays(auth.Value.PiTokenLifetimeDays) - ClientGrace, now - ClientNeverUsedFor, ct), ct);
        }
    }

    private bool HourlyDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now < _nextHourly)
                return false;
            _nextHourly = now + TimeSpan.FromHours(1);
            return true;
        }
    }

    // A failed prune is retried next tick; it must never stop the server.
    private async Task TryAsync(string what, Func<Task> prune, CancellationToken ct)
    {
        try
        {
            await prune();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Pruning {What} failed", what);
        }
    }
}
