using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
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

    [SkippableFact]
    public async Task Owner_ids_that_differ_only_in_case_reach_the_same_drafts()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.CreateAsync(Drafts.New("AB-1", "d_case1"));

        Assert.NotNull(await store.GetAsync("ab-1", "d_case1"));
        Assert.NotNull(await store.TryTransitionAsync("aB-1", "d_case1", DraftStatus.Draft, d => d.Status = DraftStatus.Committing));
        Time.Now = Drafts.T0.AddDays(8);
        await store.PruneAsync("Ab-1");
        Assert.Null(await store.GetAsync("AB-1", "d_case1"));
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

    [Fact]
    public async Task A_draft_saved_under_a_mixed_case_owner_folder_before_lower_casing_is_still_found()
    {
        using var other = new TempDraftRoot();
        await new FileDraftStore(other.Root, Time).CreateAsync(Drafts.New("ab-1", "d_legacy1"));
        Directory.CreateDirectory(Path.Combine(_dir.Root, "AB-1"));
        File.Copy(Path.Combine(other.Root, "ab-1", "d_legacy1.json"), Path.Combine(_dir.Root, "AB-1", "d_legacy1.json"));
        var store = await CreateAsync();

        Assert.NotNull(await store.GetAsync("AB-1", "d_legacy1"));
        Assert.NotNull(await store.TryTransitionAsync("AB-1", "d_legacy1", DraftStatus.Draft, d => d.Status = DraftStatus.Committing));
    }
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

    [SkippableFact]
    public async Task A_storage_failure_reaches_the_caller_as_a_short_fixed_message()
    {
        Skip.IfNot(Available);
        // A container that doesn't exist: every write fails with the service's full error text.
        var store = new BlobDraftStore(new BlobContainerClient(AzuriteStorage.ConnectionString, AzuriteStorage.UniqueName("missing")), Time);

        var create = await Assert.ThrowsAsync<TimeChangeException>(() => store.CreateAsync(Drafts.New()));
        var prune = await Assert.ThrowsAsync<TimeChangeException>(() => store.PruneAllAsync());

        Assert.Equal(BlobDraftStore.Unavailable, create.Message);
        Assert.Equal(BlobDraftStore.Unavailable, prune.Message);
    }

    [SkippableFact]
    public async Task A_plans_prune_reads_an_owners_drafts_at_most_hourly()
    {
        Skip.IfNot(Available);
        var container = new CountingContainer(AzuriteStorage.ConnectionString!, AzuriteStorage.UniqueName("drafts"));
        await container.CreateIfNotExistsAsync();
        var store = new BlobDraftStore(container, Time);
        await store.CreateAsync(Drafts.New(id: "d_one1"));
        await store.CreateAsync(Drafts.New(id: "d_two2"));

        await store.PruneAsync("u-1");
        var afterFirst = container.Downloads;
        await store.PruneAsync("u-1");
        var afterSecond = container.Downloads;
        Time.Now += TimeSpan.FromMinutes(61);
        await store.PruneAsync("u-1");

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(4, container.Downloads);
    }

    [SkippableFact]
    public async Task A_failed_plan_prune_does_not_hold_off_the_next_one()
    {
        Skip.IfNot(Available);
        var container = new CountingContainer(AzuriteStorage.ConnectionString!, AzuriteStorage.UniqueName("drafts"));
        await container.CreateIfNotExistsAsync();
        var store = new BlobDraftStore(container, Time);
        await store.CreateAsync(Drafts.New(id: "d_old1"));
        Time.Now += TimeSpan.FromDays(8);

        container.FailDownloads = true;
        await Assert.ThrowsAsync<TimeChangeException>(() => store.PruneAsync("u-1"));
        container.FailDownloads = false;
        await store.PruneAsync("u-1");

        Assert.Null(await store.GetAsync("u-1", "d_old1"));
    }

    [SkippableFact]
    public async Task A_failed_full_prune_does_not_hold_off_a_plans_prune()
    {
        Skip.IfNot(Available);
        var container = new CountingContainer(AzuriteStorage.ConnectionString!, AzuriteStorage.UniqueName("drafts"));
        await container.CreateIfNotExistsAsync();
        var store = new BlobDraftStore(container, Time);
        await store.CreateAsync(Drafts.New(id: "d_old1"));
        Time.Now += TimeSpan.FromDays(8);

        container.FailDownloads = true;
        await Assert.ThrowsAsync<TimeChangeException>(() => store.PruneAllAsync());
        container.FailDownloads = false;
        await store.PruneAsync("u-1");

        Assert.Null(await store.GetAsync("u-1", "d_old1"));
    }

    [SkippableFact]
    public async Task A_storage_failure_keeps_its_cause_for_the_log_but_not_for_the_caller()
    {
        Skip.IfNot(Available);
        var container = new CountingContainer(AzuriteStorage.ConnectionString!, AzuriteStorage.UniqueName("drafts"));
        await container.CreateIfNotExistsAsync();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var store = new BlobDraftStore(container, Time, factory.CreateLogger<BlobDraftStore>());
        await store.CreateAsync(Drafts.New(id: "d_one1"));

        container.FailDownloads = true;
        var failure = await Assert.ThrowsAsync<TimeChangeException>(() => store.GetAsync("u-1", "d_one1"));

        Assert.Equal(BlobDraftStore.Unavailable, failure.Message);
        Assert.Equal("storage_unavailable", failure.Code);
        var cause = Assert.IsType<Azure.RequestFailedException>(failure.InnerException);
        Assert.Equal(503, cause.Status);
        Assert.Contains(logs.Lines, l => l.Contains(CountingContainer.FailureDetail, StringComparison.Ordinal));
    }

    private sealed class CountingContainer : BlobContainerClient
    {
        private readonly string _connectionString;
        private int _downloads;

        public CountingContainer(string connectionString, string name) : base(connectionString, name) => _connectionString = connectionString;

        public int Downloads => _downloads;
        public const string FailureDetail = "storage-detail-for-the-log";
        public volatile bool FailDownloads;
        public override BlobClient GetBlobClient(string blobName) => new CountingBlob(this, _connectionString, Name, blobName);

        private sealed class CountingBlob(CountingContainer owner, string connectionString, string container, string name)
            : BlobClient(connectionString, container, name)
        {
            public override Task<Azure.Response<Azure.Storage.Blobs.Models.BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._downloads);
                return owner.FailDownloads
                    ? throw new Azure.RequestFailedException(503, "Service unavailable: " + FailureDetail)
                    : base.DownloadContentAsync(cancellationToken);
            }
        }
    }
}
