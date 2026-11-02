using Azure.Storage.Blobs;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public abstract class DraftStoreContract : IDisposable
{
    protected readonly ManualTimeProvider Time = new(Drafts.T0);
    protected abstract bool Available { get; }
    protected abstract Task<IDraftStore> CreateAsync();
    public virtual void Dispose() { }

    [SkippableFact]
    public async Task A_created_draft_reads_back_for_its_owner_only()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var draft = Drafts.New();
        await store.CreateAsync(draft);

        Assert.Equal(draft.Intent, (await store.GetAsync("u-1", draft.Id))!.Intent);
        Assert.Null(await store.GetAsync("u-2", draft.Id));
    }

    [SkippableFact]
    public async Task Creating_an_existing_id_throws()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var draft = Drafts.New(id: "d_dup1");
        await store.CreateAsync(draft);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Drafts.New(id: "d_dup1")));
    }

    [SkippableFact]
    public async Task Transition_applies_only_from_the_expected_status()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var draft = Drafts.New();
        await store.CreateAsync(draft);

        Assert.Null(await store.TryTransitionAsync("u-1", draft.Id, DraftStatus.Committing, d => d.Status = DraftStatus.Committed));
        Assert.Equal(DraftStatus.Draft, (await store.GetAsync("u-1", draft.Id))!.Status);

        var moved = await store.TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing);
        Assert.Equal(DraftStatus.Committing, moved!.Status);
        Assert.Equal(DraftStatus.Committing, (await store.GetAsync("u-1", draft.Id))!.Status);
        Assert.Null(await store.TryTransitionAsync("u-2", draft.Id, DraftStatus.Committing, d => d.Status = DraftStatus.Committed));
    }

    [SkippableFact]
    public async Task Concurrent_transitions_succeed_exactly_once()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var draft = Drafts.New();
        await store.CreateAsync(draft);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing)));

        Assert.Single(results, r => r is not null);
    }

    [SkippableFact]
    public async Task Prune_deletes_drafts_untouched_for_seven_days_and_keeps_newer_ones()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        var old = Drafts.New(id: "d_old1");
        await store.CreateAsync(old);
        Time.Now = Drafts.T0.AddDays(6);
        var fresh = Drafts.New(id: "d_new1");
        await store.CreateAsync(fresh);
        Time.Now = Drafts.T0.AddDays(8);

        await store.PruneAsync("u-1");

        Assert.Null(await store.GetAsync("u-1", old.Id));
        Assert.NotNull(await store.GetAsync("u-1", fresh.Id));
    }

    [SkippableFact]
    public async Task PruneAll_does_the_same_across_owners()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.CreateAsync(Drafts.New("u-1", "d_old1"));
        await store.CreateAsync(Drafts.New("u-2", "d_old2"));
        Time.Now = Drafts.T0.AddDays(6);
        await store.CreateAsync(Drafts.New("u-2", "d_new2"));
        Time.Now = Drafts.T0.AddDays(8);

        await store.PruneAllAsync();

        Assert.Null(await store.GetAsync("u-1", "d_old1"));
        Assert.Null(await store.GetAsync("u-2", "d_old2"));
        Assert.NotNull(await store.GetAsync("u-2", "d_new2"));
    }

    [SkippableTheory]
    [InlineData("u-1", "../u-2/d_abc")]
    [InlineData("u-1", "")]
    [InlineData("../u-2", "d_abc")]
    [InlineData("", "d_abc")]
    public async Task Ids_or_owners_with_path_characters_are_not_found(string owner, string id)
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.CreateAsync(Drafts.New("u-2", "d_abc"));

        Assert.Null(await store.GetAsync(owner, id));
    }
}

public sealed class FileDraftStoreContractTests : DraftStoreContract
{
    private readonly TempDraftRoot _dir = new();
    protected override bool Available => true;
    protected override Task<IDraftStore> CreateAsync() => Task.FromResult<IDraftStore>(new FileDraftStore(_dir.Root, Time));
    public override void Dispose() => _dir.Dispose();
}

public sealed class BlobDraftStoreContractTests : DraftStoreContract
{
    protected override bool Available => AzuriteStorage.Available;
    protected override async Task<IDraftStore> CreateAsync()
    {
        var container = new BlobContainerClient(AzuriteStorage.ConnectionString, AzuriteStorage.UniqueName("drafts"));
        await container.CreateIfNotExistsAsync();
        return new BlobDraftStore(container, Time);
    }
}
