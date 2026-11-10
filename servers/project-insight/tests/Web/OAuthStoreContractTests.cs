using Azure.Data.Tables;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public abstract class CodeStoreContract
{
    protected abstract bool Available { get; }
    protected abstract Task<ICodeStore> CreateAsync();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task A_code_can_be_taken_once()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.SaveAsync(new StoredCode("h1", [1, 2, 3], Now.AddMinutes(5)), default);

        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.TryTakeAsync("h1", default))!.Payload);
        Assert.Null(await store.TryTakeAsync("h1", default));
    }

    [SkippableFact]
    public async Task Concurrent_takes_give_each_code_to_exactly_one_caller()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();

        for (var i = 0; i < 50; i++)
        {
            var hash = "race" + i;
            await store.SaveAsync(new StoredCode(hash, [9], Now.AddMinutes(5)), default);
            using var gate = new ManualResetEventSlim();
            var takers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await store.TryTakeAsync(hash, default);
            })).ToArray();
            gate.Set();

            var results = await Task.WhenAll(takers);

            Assert.True(results.Count(r => r is not null) == 1, $"code {hash} was taken by {results.Count(r => r is not null)} callers");
        }
    }

    [SkippableFact]
    public async Task Pruning_twice_in_a_row_is_harmless()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.SaveAsync(new StoredCode("old", [1], Now.AddMinutes(-1)), default);

        await store.PruneAsync(Now, default);
        await store.PruneAsync(Now, default);
    }

    [SkippableFact]
    public async Task Prune_removes_only_expired_codes()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.SaveAsync(new StoredCode("old", [1], Now.AddMinutes(-1)), default);
        await store.SaveAsync(new StoredCode("new", [2], Now.AddMinutes(5)), default);

        await store.PruneAsync(Now, default);

        Assert.Null(await store.TryTakeAsync("old", default));
        Assert.NotNull(await store.TryTakeAsync("new", default));
    }
}

public sealed class MemoryCodeStoreTests : CodeStoreContract
{
    protected override bool Available => true;
    protected override Task<ICodeStore> CreateAsync() => Task.FromResult<ICodeStore>(new MemoryCodeStore());
}

public sealed class TableCodeStoreTests : CodeStoreContract
{
    protected override bool Available => AzuriteStorage.Available;
    protected override async Task<ICodeStore> CreateAsync()
    {
        var table = new TableClient(AzuriteStorage.ConnectionString, AzuriteStorage.UniqueName("codes"));
        await table.CreateIfNotExistsAsync();
        return new TableCodeStore(table);
    }
}

public abstract class ClientStoreContract
{
    protected abstract bool Available { get; }
    protected abstract Task<IClientStore> CreateAsync();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static RegisteredClient Client(string id, DateTimeOffset? lastUsed = null, bool used = false) =>
        new(id, "Claude", ["https://claude.ai/api/mcp/auth_callback", "http://127.0.0.1:5000/callback"], lastUsed ?? Now, used);

    [SkippableFact]
    public async Task A_added_client_reads_back_with_its_redirect_uris()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();

        Assert.True(await store.TryAddAsync(Client("c1"), 10, default));

        var read = await store.GetAsync("c1", default);
        Assert.Equal("Claude", read!.ClientName);
        Assert.Equal(Client("c1").RedirectUris, read.RedirectUris);
        Assert.Null(await store.GetAsync("missing", default));
    }

    [SkippableFact]
    public async Task At_the_cap_the_oldest_never_used_client_makes_room()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryAddAsync(Client("signed-in", Now.AddHours(-3), used: true), 3, default);
        await store.TryAddAsync(Client("never-older", Now.AddHours(-2)), 3, default);
        await store.TryAddAsync(Client("never-newer", Now.AddHours(-1)), 3, default);

        Assert.True(await store.TryAddAsync(Client("new"), 3, default));

        Assert.NotNull(await store.GetAsync("new", default));
        Assert.NotNull(await store.GetAsync("signed-in", default));
        Assert.Null(await store.GetAsync("never-older", default));
        Assert.NotNull(await store.GetAsync("never-newer", default));
    }

    [SkippableFact]
    public async Task Adding_stops_at_the_cap_only_when_every_client_has_signed_in()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();

        Assert.True(await store.TryAddAsync(Client("a", used: true), 2, default));
        Assert.True(await store.TryAddAsync(Client("b", used: true), 2, default));
        Assert.False(await store.TryAddAsync(Client("c"), 2, default));
        Assert.Null(await store.GetAsync("c", default));
        Assert.NotNull(await store.GetAsync("a", default));
    }

    [SkippableFact]
    public async Task Touch_updates_last_used_and_prune_removes_only_clients_unused_since_the_cutoff()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryAddAsync(Client("stale", Now.AddDays(-100)), 10, default);
        await store.TryAddAsync(Client("touched", Now.AddDays(-100)), 10, default);

        await store.TouchAsync("touched", Now, default);
        Assert.Equal(Now, (await store.GetAsync("touched", default))!.LastUsed);
        Assert.True((await store.GetAsync("touched", default))!.Used);

        await store.PruneAsync(Now.AddDays(-30), Now.AddHours(-24), default);

        Assert.Null(await store.GetAsync("stale", default));
        Assert.NotNull(await store.GetAsync("touched", default));
    }

    [SkippableFact]
    public async Task Prune_applies_the_used_cutoff_to_used_clients_and_the_never_used_cutoff_to_the_rest()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var unusedSince = Now.AddDays(-120);
        var neverUsedSince = Now.AddHours(-24);
        await store.TryAddAsync(Client("used-recent", Now.AddDays(-100), used: true), 10, default);
        await store.TryAddAsync(Client("used-old", Now.AddDays(-130), used: true), 10, default);
        await store.TryAddAsync(Client("never-fresh", Now.AddHours(-1)), 10, default);
        await store.TryAddAsync(Client("never-old", Now.AddHours(-25)), 10, default);

        await store.PruneAsync(unusedSince, neverUsedSince, default);

        Assert.NotNull(await store.GetAsync("used-recent", default));
        Assert.Null(await store.GetAsync("used-old", default));
        Assert.NotNull(await store.GetAsync("never-fresh", default));
        Assert.Null(await store.GetAsync("never-old", default));
    }

    [SkippableFact]
    public async Task A_touch_marks_the_client_used_so_the_never_used_cutoff_no_longer_applies()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryAddAsync(Client("signed-in-once", Now.AddHours(-48)), 10, default);
        await store.TouchAsync("signed-in-once", Now.AddHours(-47), default);

        await store.PruneAsync(Now.AddDays(-120), Now.AddHours(-24), default);

        Assert.NotNull(await store.GetAsync("signed-in-once", default));
    }
}

public sealed class MemoryClientStoreTests : ClientStoreContract
{
    protected override bool Available => true;
    protected override Task<IClientStore> CreateAsync() => Task.FromResult<IClientStore>(new MemoryClientStore());
}

public sealed class TableClientStoreTests : ClientStoreContract
{
    protected override bool Available => AzuriteStorage.Available;
    protected override async Task<IClientStore> CreateAsync()
    {
        var table = new TableClient(AzuriteStorage.ConnectionString, AzuriteStorage.UniqueName("clients"));
        await table.CreateIfNotExistsAsync();
        return new TableClientStore(table);
    }
}

public abstract class SessionStoreContract
{
    protected abstract bool Available { get; }
    protected abstract Task<ISessionStore> CreateAsync();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static SignInSession Session(string id, DateTimeOffset? expires = null) => new()
    {
        Id = id,
        ClientId = "c1",
        RedirectUri = "https://claude.ai/api/mcp/auth_callback",
        State = "st",
        CodeChallenge = "chal",
        Resource = "https://mcp.test/mcp",
        PiActivationId = "act-1",
        PiActivationUrl = "https://pi.test/activate/1",
        CookieHash = "ck",
        ExpiresAt = expires ?? Now.AddMinutes(10)
    };

    [SkippableFact]
    public async Task A_created_session_round_trips_every_field_with_a_version()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var s = Session("s1");

        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(s, 10, 10, Now, default));

        var read = (await store.GetAsync("s1", default))!;
        Assert.NotNull(read.Version);
        Assert.Equal(s with { Version = read.Version }, read);
        Assert.Null(await store.GetAsync("missing", default));
    }

    [SkippableFact]
    public async Task Creating_stops_at_the_cap_and_expired_sessions_do_not_count()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(Session("old", Now.AddMinutes(-1)), 2, 10, Now, default));
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(Session("a"), 2, 10, Now, default));
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(Session("b"), 2, 10, Now, default));

        Assert.Equal(SessionCreate.Full, await store.TryCreateAsync(Session("c"), 2, 10, Now, default));
    }

    [SkippableFact]
    public async Task Each_client_key_has_its_own_cap_below_the_global_one()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        SignInSession From(string id, string key, DateTimeOffset? expires = null) => Session(id, expires) with { ClientKeyHash = key };
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(From("old", "k1", Now.AddMinutes(-1)), 10, 2, Now, default));
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(From("a", "k1"), 10, 2, Now, default));
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(From("b", "k1"), 10, 2, Now, default));

        Assert.Equal(SessionCreate.ClientFull, await store.TryCreateAsync(From("c", "k1"), 10, 2, Now, default));
        Assert.Null(await store.GetAsync("c", default));
        Assert.Equal(SessionCreate.Created, await store.TryCreateAsync(From("d", "k2"), 10, 2, Now, default));
        Assert.Equal("k2", (await store.GetAsync("d", default))!.ClientKeyHash);
    }

    [SkippableFact]
    public async Task Replace_is_compare_and_set_on_the_version()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryCreateAsync(Session("s1"), 10, 10, Now, default);
        var first = (await store.GetAsync("s1", default))!;

        Assert.True(await store.TryReplaceAsync(first with { Attempts = 1 }, default));

        var second = (await store.GetAsync("s1", default))!;
        Assert.Equal(1, second.Attempts);
        Assert.NotEqual(first.Version, second.Version);
        Assert.False(await store.TryReplaceAsync(first with { Attempts = 2 }, default));
        Assert.Equal(1, (await store.GetAsync("s1", default))!.Attempts);
    }

    [SkippableFact]
    public async Task Concurrent_replaces_from_the_same_read_let_exactly_one_win()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryCreateAsync(Session("s1"), 10, 10, Now, default);
        var read = (await store.GetAsync("s1", default))!;

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => store.TryReplaceAsync(read with { Attempts = i + 1, Status = SessionStatus.Completing }, default)));

        Assert.Single(results, r => r);
    }

    [SkippableFact]
    public async Task Deleting_a_missing_session_and_pruning_twice_do_not_throw()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryCreateAsync(Session("old", Now.AddMinutes(-1)), 10, 10, Now, default);

        await store.DeleteAsync("never-existed", default);
        await store.PruneAsync(Now, default);
        await store.PruneAsync(Now, default);
    }

    [SkippableFact]
    public async Task Delete_removes_a_session_and_prune_removes_only_expired_ones()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.TryCreateAsync(Session("gone"), 10, 10, Now, default);
        await store.TryCreateAsync(Session("old", Now.AddMinutes(-1)), 10, 10, Now, default);
        await store.TryCreateAsync(Session("live"), 10, 10, Now, default);

        await store.DeleteAsync("gone", default);
        await store.PruneAsync(Now, default);

        Assert.Null(await store.GetAsync("gone", default));
        Assert.Null(await store.GetAsync("old", default));
        Assert.NotNull(await store.GetAsync("live", default));
    }
}

public sealed class MemorySessionStoreTests : SessionStoreContract
{
    protected override bool Available => true;
    protected override Task<ISessionStore> CreateAsync() => Task.FromResult<ISessionStore>(new MemorySessionStore());
}

public sealed class TableSessionStoreTests : SessionStoreContract
{
    protected override bool Available => AzuriteStorage.Available;
    protected override async Task<ISessionStore> CreateAsync()
    {
        var table = new TableClient(AzuriteStorage.ConnectionString, AzuriteStorage.UniqueName("sessions"));
        await table.CreateIfNotExistsAsync();
        return new TableSessionStore(table);
    }
}
