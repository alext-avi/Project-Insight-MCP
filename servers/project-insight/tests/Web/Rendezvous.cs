using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>
/// Once armed, holds the first <paramref name="count"/> callers until all have arrived, so a race is forced
/// rather than hoped for. Bounded: a caller that never arrives fails the test instead of hanging it.
/// </summary>
internal sealed class Rendezvous(int count = 2)
{
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;
    private volatile bool _armed;

    public void Arm() => _armed = true;

    public async Task ArriveAsync()
    {
        if (!_armed)
            return;
        var n = Interlocked.Increment(ref _arrived);
        if (n == count)
            _all.TrySetResult();
        if (n <= count)
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>Sessions whose reads meet at <paramref name="meet"/>: racing posts all see the same version.</summary>
internal sealed class RendezvousSessionStore(ISessionStore inner, Rendezvous meet) : ISessionStore
{
    public Task<SessionCreate> TryCreateAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct)
        => inner.TryCreateAsync(session, maxOutstanding, maxPerClient, now, ct);

    public async Task<SignInSession?> GetAsync(string id, CancellationToken ct)
    {
        var session = await inner.GetAsync(id, ct);
        await meet.ArriveAsync();
        return session;
    }

    public Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct) => inner.TryReplaceAsync(updated, ct);
    public Task DeleteAsync(string id, CancellationToken ct) => inner.DeleteAsync(id, ct);
    public Task DeleteByCookieAsync(string cookieHash, CancellationToken ct) => inner.DeleteByCookieAsync(cookieHash, ct);
    public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => inner.PruneAsync(now, ct);
}

/// <summary>Codes whose takes meet at <paramref name="meet"/> first: racing redemptions take together.</summary>
internal sealed class RendezvousCodeStore(ICodeStore inner, Rendezvous meet) : ICodeStore
{
    public Task SaveAsync(StoredCode code, CancellationToken ct) => inner.SaveAsync(code, ct);

    public async Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct)
    {
        await meet.ArriveAsync();
        return await inner.TryTakeAsync(codeHash, ct);
    }

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => inner.PruneAsync(now, ct);
}
