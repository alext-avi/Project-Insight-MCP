using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class FileDraftStoreTests : IDisposable
{
    private readonly TempDraftRoot _dir = new();
    private readonly ManualTimeProvider _time = new(Drafts.T0);

    private string _root => _dir.Root;

    public void Dispose() => _dir.Dispose();

    private FileDraftStore Store() => new(_root, _time);

    [Fact]
    public async Task Created_draft_reads_back_and_is_private_to_its_owner()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);

        var read = await Store().GetAsync("u-1", draft.Id);
        Assert.Equal(draft.Intent, read!.Intent);
        Assert.Equal(Drafts.T0, read.UpdatedAt);
        Assert.Null(await Store().GetAsync("u-2", draft.Id));

        if (!OperatingSystem.IsWindows())
        {
            var file = Path.Combine(_root, "u-1", draft.Id + ".json");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(_root, "u-1")));
        }
    }

    [Fact]
    public async Task An_existing_drafts_folder_keeps_its_own_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                     UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, shared);

        await Store().CreateAsync(Drafts.New());

        Assert.Equal(shared, File.GetUnixFileMode(_root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(_root, "u-1")));
    }

    [Theory]
    [InlineData("../u-2/d_abc")]
    [InlineData("d_ABC/../../x")]
    [InlineData("")]
    public async Task Ids_with_path_characters_are_not_found(string id)
        => Assert.Null(await Store().GetAsync("u-1", id));

    [Fact]
    public async Task Corrupt_file_reads_as_not_found()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        await File.WriteAllTextAsync(Path.Combine(_root, "u-1", draft.Id + ".json"), "{ not json");

        Assert.Null(await Store().GetAsync("u-1", draft.Id));
    }

    [Fact]
    public async Task Transition_applies_only_from_the_expected_status()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        _time.Now = Drafts.T0.AddMinutes(5);

        Assert.Null(await Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Committed, d => d.Status = DraftStatus.Cancelled));
        var moved = await Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Cancelled);

        Assert.Equal(DraftStatus.Cancelled, moved!.Status);
        Assert.Equal(_time.Now, moved.UpdatedAt);
        Assert.Equal(DraftStatus.Cancelled, (await Store().GetAsync("u-1", draft.Id))!.Status);
    }

    [Fact]
    public async Task Concurrent_claims_from_separate_store_instances_have_exactly_one_winner()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing))));

        Assert.Single(claims, c => c is not null);
    }

    [Fact]
    public async Task Creating_the_same_id_twice_fails()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CreateAsync(draft));
    }

    [Fact]
    public async Task Prune_removes_records_untouched_for_seven_days()
    {
        var old = Drafts.New();
        var recent = Drafts.New();
        await Store().CreateAsync(old);
        _time.Now = Drafts.T0.AddDays(6);
        await Store().CreateAsync(recent);

        _time.Now = Drafts.T0.AddDays(7).AddMinutes(1);
        await Store().PruneAsync("u-1");

        Assert.Null(await Store().GetAsync("u-1", old.Id));
        Assert.NotNull(await Store().GetAsync("u-1", recent.Id));
        Assert.False(File.Exists(Path.Combine(_root, "u-1", old.Id + ".lock")));
    }

    [Fact]
    public async Task Prune_all_prunes_every_owner()
    {
        var oldA = Drafts.New("u-1");
        var oldB = Drafts.New("u-2");
        await Store().CreateAsync(oldA);
        await Store().CreateAsync(oldB);
        _time.Now = Drafts.T0.AddDays(6);
        var recentA = Drafts.New("u-1");
        var recentB = Drafts.New("u-2");
        await Store().CreateAsync(recentA);
        await Store().CreateAsync(recentB);

        _time.Now = Drafts.T0.AddDays(7).AddMinutes(1);
        await Store().PruneAllAsync();

        Assert.Null(await Store().GetAsync("u-1", oldA.Id));
        Assert.Null(await Store().GetAsync("u-2", oldB.Id));
        Assert.NotNull(await Store().GetAsync("u-1", recentA.Id));
        Assert.NotNull(await Store().GetAsync("u-2", recentB.Id));
    }

    [Fact]
    public async Task Prune_all_with_no_root_does_nothing()
        => await Store().PruneAllAsync();

    [Fact]
    public async Task Writes_leave_no_temp_files_behind()
    {
        var draft = Drafts.New();
        await Store().CreateAsync(draft);
        await Store().TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Cancelled);

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "u-1"), "*.tmp"));
    }
}
