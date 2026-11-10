using System.Collections.Concurrent;

namespace ProjectInsightMCP.Web.Storage;

/// <summary>In-process client store for tests and local runs only; nothing survives a restart.</summary>
public sealed class MemoryClientStore : IClientStore
{
    private readonly ConcurrentDictionary<string, RegisteredClient> _clients = new();
    private readonly object _gate = new();

    public Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct)
        => Task.FromResult(_clients.GetValueOrDefault(clientId));

    public Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_clients.Count >= max)
            {
                var oldest = _clients.Values.Where(c => !c.Used).MinBy(c => c.LastUsed);
                if (oldest is null)
                    return Task.FromResult(false);
                _clients.TryRemove(oldest.ClientId, out _);
            }
            _clients[client.ClientId] = client;
            return Task.FromResult(true);
        }
    }

    public Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
            if (_clients.TryGetValue(clientId, out var c))
                _clients[clientId] = c with { LastUsed = at, Used = true };
        return Task.CompletedTask;
    }

    public Task PruneAsync(DateTimeOffset unusedSince, DateTimeOffset neverUsedSince, CancellationToken ct)
    {
        lock (_gate)
            foreach (var (id, c) in _clients)
                if (c.LastUsed < (c.Used ? unusedSince : neverUsedSince))
                    _clients.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

/// <summary>In-process session store for tests and local runs only; nothing survives a restart.</summary>
public sealed class MemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SignInSession> _sessions = new();
    private readonly object _gate = new();
    private long _version;

    private string NextVersion() => Interlocked.Increment(ref _version).ToString();

    public Task<SessionCreate> TryCreateAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var live = _sessions.Values.Where(s => s.ExpiresAt > now).ToList();
            if (session.ClientKeyHash is not null && live.Count(s => s.ClientKeyHash == session.ClientKeyHash) >= maxPerClient)
                return Task.FromResult(SessionCreate.ClientFull);
            if (live.Count >= maxOutstanding)
                return Task.FromResult(SessionCreate.Full);
            _sessions[session.Id] = session with { Version = NextVersion() };
            return Task.FromResult(SessionCreate.Created);
        }
    }

    public Task<SignInSession?> GetAsync(string id, CancellationToken ct)
        => Task.FromResult(_sessions.GetValueOrDefault(id));

    public Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(updated.Id, out var current) || current.Version != updated.Version)
            return Task.FromResult(false);
        // The stored instance is the comparand, so of two racing replaces only one lands.
        return Task.FromResult(_sessions.TryUpdate(updated.Id, updated with { Version = NextVersion() }, current));
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        _sessions.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (id, s) in _sessions)
            if (s.ExpiresAt <= now)
                _sessions.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

/// <summary>In-process authorization-code store for tests and local runs only; nothing survives a restart.</summary>
public sealed class MemoryCodeStore : ICodeStore
{
    private readonly ConcurrentDictionary<string, StoredCode> _codes = new();

    public Task SaveAsync(StoredCode code, CancellationToken ct)
    {
        _codes[code.CodeHash] = code;
        return Task.CompletedTask;
    }

    public Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct)
        => Task.FromResult(_codes.TryRemove(codeHash, out var code) ? code : null);

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (hash, c) in _codes)
            if (c.ExpiresAt <= now)
                _codes.TryRemove(hash, out _);
        return Task.CompletedTask;
    }
}
