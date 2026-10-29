using ProjectInsightMCP.TimeChanges;
using Xunit;
using static ProjectInsightMCP.Tests.TimeChanges.TimeChangePlannerTests;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TimeChangeCommitterTests : IDisposable
{
    private readonly TempDraftRoot _dir = new();
    private readonly ManualTimeProvider _time = new(Drafts.T0);
    private readonly FakeGateway _pi = Seeded();
    private readonly FileDraftStore _store;
    private readonly TimeChangePlanner _planner;
    private readonly TimeChangeCommitter _committer;

    public TimeChangeCommitterTests()
    {
        _store = new FileDraftStore(_dir.Root, _time);
        _planner = new TimeChangePlanner(_pi, _store, _time);
        _committer = new TimeChangeCommitter(_pi, _store, _time);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    private PiUser Alex => _pi.Caller;

    private async Task<(Draft Draft, string Token)> Plan(List<ChangeInput>? changes = null)
    {
        var result = await _planner.PlanAsync("Log Tuesday", changes ?? TypicalChanges(), default);
        return (result.Draft!, result.ConfirmToken!);
    }

    private Task<Draft> Commit(string id) => _committer.CommitAsync(Alex, id, CommitChannel.Agent, null, default);

    private PiEntry? Entry(string id) => _pi.Entries.FirstOrDefault(e => e.Id == id);

    [Fact]
    public async Task Commit_applies_every_row_in_order_and_keeps_a_receipt()
    {
        var (draft, _) = await Plan();

        var receipt = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, receipt.Status);
        Assert.Equal((CommitChannel.Agent, Drafts.T0), (receipt.Channel, receipt.CommittedAt));
        Assert.Equal(["create:te-100", "update:te-1", "delete:te-4"], _pi.Writes);
        Assert.All(receipt.Rows, r => Assert.Equal(RowOutcome.Applied, r.Outcome));
        Assert.Equal("te-100", receipt.Rows[0].SavedId);
        Assert.Equal(1.5m, Entry("te-1")!.Hours);
        Assert.Null(Entry("te-4"));
        Assert.Equal(DraftStatus.Committed, (await _store.GetAsync("u-1", draft.Id))!.Status);
    }

    [Fact]
    public async Task A_rejected_change_reverses_the_earlier_ones_last_first()
    {
        var (draft, _) = await Plan();
        _pi.FailWrite = (op, id) => op == "delete" && id == "te-4" ? "Task is closed to time entry." : null;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.Contains("change 3 of 3", result.Error);
        Assert.Contains("Task is closed to time entry.", result.Error);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal(["create:te-100", "update:te-1", "update:te-1", "delete:te-100"], _pi.Writes);
        foreach (var (key, fingerprint) in draft.DayFingerprints)
        {
            var (_, date) = Weeks.ParseDayKey(key);
            Assert.Equal(fingerprint, Fingerprint.Compute(_pi.Entries.Where(e => e.UserId == "u-1" && e.Date == date), null));
        }
    }

    [Fact]
    public async Task A_failed_reversal_is_reported_on_its_row()
    {
        var (draft, _) = await Plan();
        _pi.FailWrite = (op, _) => op == "delete" ? "Locked" : null;

        var result = await Commit(draft.Id);

        Assert.Equal([RowOutcome.ReverseFailed, RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal("Locked", result.Rows[0].Error);
    }

    [Fact]
    public async Task A_reversed_delete_recreates_the_entry()
    {
        var (draft, _) = await Plan([new() { Action = "delete", TimeEntryId = "te-4" }, new() { Action = "update", TimeEntryId = "te-1", Hours = 3m }]);
        _pi.FailWrite = (op, _) => op == "update" ? "Nope" : null;

        var result = await Commit(draft.Id);

        Assert.Equal(RowOutcome.Reversed, result.Rows[0].Outcome);
        Assert.Equal("te-100", result.Rows[0].SavedId);
        var recreated = Entry("te-100")!;
        Assert.Equal((TimeChangePlannerTests.Tue, 2.0m, "t-1", "API integration"), (recreated.Date, recreated.Hours, recreated.TaskId, recreated.Description));
    }

    [Fact]
    public async Task A_change_in_pi_since_planning_makes_the_draft_stale_and_writes_nothing()
    {
        var (draft, _) = await Plan();
        _pi.Add("te-9", "u-1", TimeChangePlannerTests.Tue, 1m, "t-3");

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Stale, result.Status);
        Assert.Equal(["You · Tue 27 Oct"], result.StaleDays);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task A_change_on_an_untouched_day_does_not_make_the_draft_stale()
    {
        var (draft, _) = await Plan();
        var wed = _pi.Entries.FindIndex(e => e.Id == "te-5");
        _pi.Entries[wed] = _pi.Entries[wed] with { Hours = 7m };

        Assert.Equal(DraftStatus.Committed, (await Commit(draft.Id)).Status);
    }

    [Fact]
    public async Task Submitting_the_timesheet_after_planning_makes_the_draft_stale()
    {
        var (draft, _) = await Plan();
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-1", TimeChangePlannerTests.Mon, new DateOnly(2026, 11, 1), true, false, false, null)];

        var result = await Commit(draft.Id);

        Assert.Equal(["You · Mon 26 Oct", "You · Tue 27 Oct"], result.StaleDays);
    }

    [Fact]
    public async Task An_expired_draft_cannot_be_committed()
    {
        var (draft, _) = await Plan();
        _time.Now = Drafts.T0.AddHours(24).AddMinutes(1);

        Assert.Equal(DraftStatus.Expired, (await Commit(draft.Id)).Status);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task Confirm_needs_the_drafts_token()
    {
        var (draft, token) = await Plan();

        var bad = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, DraftIds.NewConfirmToken(), default));
        var none = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, null, default));
        var receipt = await _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, token, default);

        Assert.Equal(("bad_confirm_token", "bad_confirm_token"), (bad.Code, none.Code));
        Assert.Equal((DraftStatus.Committed, CommitChannel.ConfirmedInChat, true), (receipt.Status, receipt.Channel, receipt.ConfirmTokenUsed));
    }

    [Fact]
    public async Task A_used_confirm_token_is_rejected()
    {
        var (draft, token) = await Plan();
        await _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, token, default);

        var again = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, token, default));

        Assert.Equal("bad_confirm_token", again.Code);
        Assert.Equal(3, _pi.Writes.Count);
    }

    [Fact]
    public async Task Committing_twice_applies_once()
    {
        var (draft, _) = await Plan();
        await Commit(draft.Id);
        var again = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, again.Status);
        Assert.Equal(3, _pi.Writes.Count);
    }

    [Fact]
    public async Task Another_user_cannot_see_or_act_on_the_draft()
    {
        var (draft, token) = await Plan();
        var jane = new PiUser("u-2", "Jane Doe");

        foreach (var attempt in new Func<Task>[]
        {
            () => _committer.LoadAsync(jane, draft.Id, default),
            () => _committer.CommitAsync(jane, draft.Id, CommitChannel.Agent, null, default),
            () => _committer.CancelAsync(jane, draft.Id, default),
            () => _committer.CommitAsync(jane, draft.Id, CommitChannel.ConfirmedInChat, token, default)
        })
            Assert.Equal("not_found", (await Assert.ThrowsAsync<TimeChangeException>(attempt)).Code);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task A_commit_interrupted_midway_is_reported_as_failed_after_ten_minutes()
    {
        var (draft, _) = await Plan();
        await _store.TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing);

        _time.Now = Drafts.T0.AddMinutes(5);
        Assert.Equal(DraftStatus.Committing, (await _committer.LoadAsync(Alex, draft.Id, default)).Status);

        _time.Now = Drafts.T0.AddMinutes(11);
        var loaded = await _committer.LoadAsync(Alex, draft.Id, default);
        Assert.Equal(DraftStatus.Failed, loaded.Status);
        Assert.Contains("interrupted", loaded.Error);
        Assert.Contains("You · Mon 26 Oct", loaded.Error);
        Assert.True(loaded.OutcomeUnknown);
    }

    [Fact]
    public async Task Cancel_stops_a_draft_but_not_a_receipt()
    {
        var (draft, _) = await Plan();
        Assert.Equal(DraftStatus.Cancelled, (await _committer.CancelAsync(Alex, draft.Id, default)).Status);
        Assert.Equal(DraftStatus.Cancelled, (await Commit(draft.Id)).Status);
        Assert.Empty(_pi.Writes);

        var (second, _) = await Plan([new() { Action = "update", TimeEntryId = "te-5", Hours = 7m }]);
        await Commit(second.Id);
        Assert.Equal(DraftStatus.Committed, (await _committer.CancelAsync(Alex, second.Id, default)).Status);
    }

    [Fact]
    public async Task Cancelling_the_request_mid_commit_still_rolls_back()
    {
        var (draft, _) = await Plan();
        using var cts = new CancellationTokenSource();
        _pi.AfterWrite = n => { if (n == 1) cts.Cancel(); };
        _pi.FailWrite = (op, id) => op == "delete" && id == "te-4" ? "Task is closed to time entry." : null;

        var result = await _committer.CommitAsync(Alex, draft.Id, CommitChannel.Agent, null, cts.Token);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal(["create:te-100", "update:te-1", "update:te-1", "delete:te-100"], _pi.Writes);
        Assert.Equal(1.0m, Entry("te-1")!.Hours);
        Assert.Null(Entry("te-100"));
        var reloaded = await _committer.LoadAsync(Alex, draft.Id, default);
        Assert.Equal(DraftStatus.Failed, reloaded.Status);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Reversed, RowOutcome.Rejected], reloaded.Rows.Select(r => r.Outcome));
    }

    [Fact]
    public async Task A_gateway_that_throws_on_a_write_leaves_that_change_unknown()
    {
        var (draft, _) = await Plan();
        _pi.ThrowOnWrite = (op, id) => op == "delete" && id == "te-4" ? new HttpRequestException("connection reset") : null;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.True(result.OutcomeUnknown);
        Assert.Contains("connection reset", result.Error);
        Assert.Contains("didn't answer for change 3 (You · Tue 27 Oct)", result.Error);
        Assert.Contains("may have been saved", result.Error);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Reversed, RowOutcome.Unknown], result.Rows.Select(r => r.Outcome));
        Assert.Equal(1.0m, Entry("te-1")!.Hours);
        Assert.Null(Entry("te-100"));
    }

    [Fact]
    public async Task A_write_pi_did_not_answer_is_unknown_and_earlier_changes_are_still_reversed()
    {
        var (draft, _) = await Plan();
        _pi.UncertainWrite = (op, id) => op == "delete" && id == "te-4" ? "Internal error occurred The request timed out." : null;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Reversed, RowOutcome.Unknown], result.Rows.Select(r => r.Outcome));
        Assert.Equal(["create:te-100", "update:te-1", "update:te-1", "delete:te-100"], _pi.Writes);
        Assert.Contains("didn't answer for change 3 (You · Tue 27 Oct)", result.Error);
        Assert.Contains("Check that day in Project Insight", result.Error);
        Assert.DoesNotContain("rejected", result.Error);
        var stored = (await _store.GetAsync("u-1", draft.Id))!;
        Assert.True(stored.OutcomeUnknown);
        Assert.Equal(RowOutcome.Unknown, stored.Rows[2].Outcome);
    }

    [Fact]
    public async Task A_definite_rejection_is_not_an_unknown_outcome()
    {
        var (draft, _) = await Plan();
        _pi.FailWrite = (op, id) => op == "delete" && id == "te-4" ? "Task is closed to time entry." : null;

        var result = await Commit(draft.Id);

        Assert.False(result.OutcomeUnknown);
        Assert.Equal(RowOutcome.Rejected, result.Rows[2].Outcome);
    }

    [Fact]
    public async Task Failing_to_mark_the_receipt_reverted_does_not_fail_a_committed_undo()
    {
        var (draft, _) = await Plan();
        var receipt = await Commit(draft.Id);
        var undo = (await _planner.PlanUndoAsync(receipt, default)).Draft!;
        var committer = new TimeChangeCommitter(_pi, new ReceiptWriteFails(_store, receipt.Id), _time);

        var result = await committer.CommitAsync(Alex, undo.Id, CommitChannel.Agent, null, default);

        Assert.Equal(DraftStatus.Committed, result.Status);
        Assert.Null(Entry("te-100"));
        Assert.Equal(1.0m, Entry("te-1")!.Hours);
    }

    /// <summary>A store whose write to one receipt fails, as a full disk or a lock timeout would.</summary>
    private sealed class ReceiptWriteFails(IDraftStore inner, string receiptId) : IDraftStore
    {
        public Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default) => inner.GetAsync(ownerId, id, ct);
        public Task CreateAsync(Draft draft, CancellationToken ct = default) => inner.CreateAsync(draft, ct);
        public Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default)
            => id == receiptId ? throw new IOException("disk full") : inner.TryTransitionAsync(ownerId, id, expectedStatus, update, ct);
        public Task PruneAsync(string ownerId, CancellationToken ct = default) => inner.PruneAsync(ownerId, ct);
        public Task PruneAllAsync(CancellationToken ct = default) => inner.PruneAllAsync(ct);
    }

    [Fact]
    public async Task A_pi_read_failure_during_commit_leaves_the_draft_committable()
    {
        var (draft, token) = await Plan();
        _pi.SearchEntriesThrows = new TimeChangeException("pi_unavailable", "PI is down");

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, token, default));

        Assert.Equal("pi_unavailable", ex.Code);
        Assert.Empty(_pi.Writes);
        var reloaded = await _committer.LoadAsync(Alex, draft.Id, default);
        Assert.Equal(DraftStatus.Draft, reloaded.Status);
        Assert.False(reloaded.ConfirmTokenUsed);

        _pi.SearchEntriesThrows = null;
        var receipt = await _committer.CommitAsync(Alex, draft.Id, CommitChannel.ConfirmedInChat, token, default);
        Assert.Equal(DraftStatus.Committed, receipt.Status);
    }

    [Fact]
    public async Task Committing_an_undo_restores_pi_and_marks_the_receipt_reverted()
    {
        var (draft, _) = await Plan();
        var receipt = await Commit(draft.Id);

        var undo = (await _planner.PlanUndoAsync(receipt, default)).Draft!;
        Assert.Equal(DraftStatus.Committed, (await Commit(undo.Id)).Status);

        Assert.Equal(undo.Id, (await _store.GetAsync("u-1", receipt.Id))!.RevertedBy);
        Assert.Null(Entry("te-100"));
        Assert.Equal(1.0m, Entry("te-1")!.Hours);
        Assert.Contains(_pi.Entries, e => e.Date == TimeChangePlannerTests.Tue && e.TaskId == "t-1" && e.Hours == 2.0m && e.Description == "API integration");
    }
}
