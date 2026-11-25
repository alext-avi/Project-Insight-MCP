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

    [Fact]
    public async Task A_redo_marks_the_original_receipt_redone_and_undoing_the_redo_clears_it()
    {
        var (draft, _) = await Plan();
        var r1 = await Commit(draft.Id);
        var u1 = await Commit((await _planner.PlanUndoAsync(r1, default)).Draft!.Id);
        var r2 = await Commit((await _planner.PlanUndoAsync(u1, default)).Draft!.Id);
        Assert.Equal(DraftStatus.Committed, r2.Status);

        var original = (await _store.GetAsync("u-1", r1.Id))!;
        Assert.Equal((u1.Id, r2.Id), (original.RevertedBy, original.RedoneBy));
        Assert.Equal((r2.Id, (string?)null), ((await _store.GetAsync("u-1", u1.Id))!.RevertedBy, (await _store.GetAsync("u-1", u1.Id))!.RedoneBy));
        Assert.Equal(1.5m, Entry("te-1")!.Hours);
        var view = DraftView.From(original, Drafts.T0);
        Assert.Equal(r2.Id, view.RedoneBy);
        Assert.Contains($"It has since been undone by {u1.Id}. Its changes were put back by {r2.Id}.",
            TimeChangeText.For(new TimeChangesView { Kind = "draft", Draft = view }, new DateOnly(2026, 10, 27)));

        // Undoing the redo takes the original's changes out again; the first undo is back in effect.
        var u2 = await Commit((await _planner.PlanUndoAsync(r2, default)).Draft!.Id);
        Assert.Null((await _store.GetAsync("u-1", r1.Id))!.RedoneBy);
        Assert.Equal(u2.Id, (await _store.GetAsync("u-1", u1.Id))!.RedoneBy);
        Assert.Equal(1.0m, Entry("te-1")!.Hours);
    }

    // ---- PTO rows ----

    private static ChangeInput CreatePto(string date, decimal hours) =>
        new() { Action = "create_pto", Date = date, Hours = hours, TimeCodeId = "tc-1" };

    [Fact]
    public async Task Commit_pto_only_draft_creates_pto_without_extra_time_entry()
    {
        var (draft, _) = await Plan([CreatePto("2026-10-26", 3m), CreatePto("2026-10-27", 1.75m)]);

        var receipt = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, receipt.Status);
        Assert.Equal(["create_pto:pto-100", "create_pto:pto-101"], _pi.Writes);
        Assert.Equal(["pto-100", "pto-101"], receipt.Rows.Select(r => r.SavedId));
        Assert.Equal(2, _pi.Pto.Count);
        Assert.Equal(2, _pi.Entries.Count(e => e.IsPto));
    }

    [Fact]
    public async Task Mixed_commit_failure_rolls_back_pto()
    {
        var (draft, _) = await Plan([CreatePto("2026-10-26", 3m),
            new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" }]);
        _pi.FailWrite = (op, _) => op == "create" ? "nope" : null;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Empty(_pi.Pto);
        Assert.DoesNotContain(_pi.Entries, e => e.IsPto);
    }

    [Fact]
    public async Task Deleting_a_pto_makes_only_the_pto_write_and_leaves_the_linked_entry_to_pi()
    {
        var (draft, _) = await Plan([new() { Action = "delete_pto", PtoEntryId = AddWedPto().Id }]);

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, result.Status);
        Assert.Equal(RowOutcome.Applied, result.Rows[0].Outcome);
        Assert.Equal(["delete_pto:pto-1"], _pi.Writes);
        Assert.DoesNotContain(_pi.Entries, e => e.IsPto);
    }

    [Fact]
    public async Task Rolling_back_a_created_pto_makes_only_the_pto_write()
    {
        var (draft, _) = await Plan([CreatePto("2026-10-26", 3m),
            new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" }]);
        _pi.FailWrite = (op, _) => op == "create" ? "nope" : null;

        var result = await Commit(draft.Id);

        Assert.Equal([RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal(["create_pto:pto-100", "delete_pto:pto-100"], _pi.Writes);
    }

    [Fact]
    public async Task An_unanswered_pto_write_is_unknown_and_not_reversed_while_earlier_rows_are()
    {
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" }, CreatePto("2026-10-26", 3m)]);
        _pi.UncertainWrite = (op, _) => op == "create_pto" ? "timeout" : null;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal([RowOutcome.Reversed, RowOutcome.Unknown], result.Rows.Select(r => r.Outcome));
        Assert.DoesNotContain(_pi.Writes, w => w.StartsWith("delete_pto"));
    }

    [Fact]
    public async Task Create_pto_without_a_saved_id_is_unknown_not_applied()
    {
        var (draft, _) = await Plan([CreatePto("2026-10-26", 3m)]);
        var real = _pi;
        // PI answers yes but gives no id: the record can't be reversed or undone, so it is flagged for checking.
        var committer = new TimeChangeCommitter(new NoIdGateway(real), _store, _time);

        var result = await committer.CommitAsync(Alex, draft.Id, CommitChannel.Agent, null, default);

        Assert.Equal(DraftStatus.Failed, result.Status);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal(RowOutcome.Unknown, result.Rows[0].Outcome);
    }

    private PiPto AddWedPto() => _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Wed, 4m);

    [Fact]
    public async Task Update_pto_commit_sends_every_field_and_reverse_restores_before()
    {
        AddWedPto();
        var (draft, _) = await Plan([new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m },
            new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" }]);
        _pi.FailWrite = (op, _) => op == "create" ? "nope" : null;

        var result = await Commit(draft.Id);

        Assert.Equal([RowOutcome.Reversed, RowOutcome.Rejected], result.Rows.Select(r => r.Outcome));
        Assert.Equal(["update_pto:pto-1", "update_pto:pto-1"], _pi.Writes);
        Assert.Equal(4m, _pi.Pto.Single().Hours);
        Assert.Equal(4m, Entry("te-pto-pto-1")!.Hours);
    }

    [Fact]
    public async Task Rollback_of_a_deleted_pto_recreates_it_and_records_the_new_id()
    {
        AddWedPto();
        var (draft, _) = await Plan([new() { Action = "delete_pto", PtoEntryId = "pto-1" },
            new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" }]);
        _pi.FailWrite = (op, _) => op == "create" ? "nope" : null;

        var result = await Commit(draft.Id);

        Assert.Equal(RowOutcome.Reversed, result.Rows[0].Outcome);
        var pto = Assert.Single(_pi.Pto);
        Assert.Equal(pto.Id, result.Rows[0].SavedId);
        Assert.Equal(4m, pto.Hours);
    }

    [Fact]
    public async Task Stale_when_pto_changes_on_affected_day()
    {
        _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Wed, 2m);
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-28", Hours = 1m, TaskId = "t-1" }]);
        _pi.Pto[0] = _pi.Pto[0] with { Hours = 3m };
        var i = _pi.Entries.FindIndex(e => e.Id == "te-pto-pto-1");
        _pi.Entries[i] = _pi.Entries[i] with { Hours = 3m };

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Stale, result.Status);
        Assert.Contains(result.StaleDays, d => d.Contains("Wed 28 Oct"));
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task Stale_when_the_pto_being_deleted_is_approved_after_planning()
    {
        AddWedPto();
        var (draft, _) = await Plan([new() { Action = "delete_pto", PtoEntryId = "pto-1" }]);
        Assert.Null(draft.Rows[0].PtoApproved); // planned without the approval warning
        _pi.Pto[0] = _pi.Pto[0] with { Approved = true };

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Stale, result.Status);
        Assert.Empty(_pi.Writes);
    }

    [Fact]
    public async Task Unchanged_pto_on_affected_day_is_not_stale()
    {
        _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Wed, 2m);
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-28", Hours = 1m, TaskId = "t-1" }]);

        Assert.Equal(DraftStatus.Committed, (await Commit(draft.Id)).Status);
    }

    [Fact]
    public async Task Stale_check_skips_pto_for_unread_people()
    {
        _pi.PtoListThrows = true;
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-27", Hours = 1m, TaskId = "t-1" }]);
        Assert.Equal(["u-1"], draft.PtoUnreadUsers);
        _pi.PtoListThrows = false;
        var calls = _pi.ListPtoCalls;

        var result = await Commit(draft.Id);

        Assert.Equal(DraftStatus.Committed, result.Status);
        Assert.Equal(calls, _pi.ListPtoCalls);
    }

    // A 2.1.0 draft: no kind, ptoEntryId, linkedTimeEntryId, ptoApproved, timeCodeId, timeCodeName, ptoUnreadUsers, ptoDelta,
    // isPto or fingerprintVersion. It creates 4h on Tue 27 Oct, fingerprinted over te-3 and te-4.
    private const string Draft210Json = """
        {
          "id": "d_vyhsj9w8a5", "status": "draft", "intent": "Log Tuesday",
          "proposedBy": "u-1", "proposedByName": "Alex Example",
          "proposedAt": "2026-10-27T09:00:00+00:00", "expiresAt": "2026-10-28T09:00:00+00:00",
          "statusChangedAt": "2026-10-27T09:00:00+00:00", "updatedAt": "2026-10-27T09:00:00+00:00",
          "committedAt": null, "channel": null, "undoOf": null, "undoOfCommittedAt": null,
          "revertedBy": null, "redoneBy": null,
          "confirmTokenHash": "0000000000000000000000000000000000000000000000000000000000000000",
          "confirmTokenUsed": false,
          "rows": [
            { "index": 0, "action": "create", "userId": "u-1", "userName": "Alex Example",
              "timeEntryId": null, "savedId": null, "before": null,
              "after": { "taskId": "t-1", "taskName": "Development", "projectId": "p-1", "projectName": "Acme Portal",
                         "projectColor": null, "date": "2026-10-27", "hours": 4, "billable": true, "description": "Order sync" },
              "warnings": [], "outcome": null, "error": null }
          ],
          "weeks": [
            { "userId": "u-1", "userName": "Alex Example", "avatar": null, "isCaller": true,
              "start": "2026-10-26", "end": "2026-11-01", "timesheetId": null, "timesheetUrl": null,
              "submitted": false, "approved": false, "locked": false, "totalBefore": 16.5, "totalAfter": 20.5,
              "days": [
                { "date": "2026-10-27", "totalBefore": 2.5, "totalAfter": 6.5, "hasChanges": true, "rowIndexes": [0],
                  "context": [
                    { "timeEntryId": "te-3", "projectName": "Internal", "projectColor": null, "taskName": "Email & admin",
                      "hours": 0.5, "billable": false, "description": null },
                    { "timeEntryId": "te-4", "projectName": "Acme Portal", "projectColor": null, "taskName": "Development",
                      "hours": 2.0, "billable": true, "description": "API integration" }
                  ] }
              ] }
          ],
          "dayFingerprints": { "u-1|2026-10-27": "5821536CB72A2C65ACA75174DCED2848893C0B30196DEED92397A8C300DB4455" },
          "totals": { "netHours": 4, "billableDelta": 4, "nonBillableDelta": 0, "changes": 1, "days": 1, "people": 1, "warnings": 0 },
          "error": null, "outcomeUnknown": false, "staleDays": []
        }
        """;

    private async Task<string> Store210Draft()
    {
        var dir = Path.Combine(_dir.Root, "u-1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "d_vyhsj9w8a5.json"), Draft210Json);
        return "d_vyhsj9w8a5";
    }

    [Fact]
    public async Task Stored_2_1_0_draft_still_commits()
    {
        var result = await Commit(await Store210Draft());

        Assert.Equal(DraftStatus.Committed, result.Status);
        Assert.Equal(["create:te-100"], _pi.Writes);
    }

    [Fact]
    public async Task Stored_2_1_0_draft_on_a_day_with_pto_still_commits_without_reading_pto()
    {
        // 2.1.0 never saw PTO or its linked entry, so they can't have been in its fingerprint.
        _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Tue, 3m);
        var id = await Store210Draft();

        var result = await Commit(id);

        Assert.Equal(DraftStatus.Committed, result.Status);
        Assert.Equal(["create:te-100"], _pi.Writes);
        Assert.Equal(0, _pi.ListPtoCalls);
    }

    [Fact]
    public async Task Stored_2_1_0_draft_still_goes_stale_when_a_work_entry_on_its_day_changes()
    {
        _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Tue, 3m);
        var id = await Store210Draft();
        var i = _pi.Entries.FindIndex(e => e.Id == "te-3");
        _pi.Entries[i] = _pi.Entries[i] with { Hours = 1m };

        Assert.Equal(DraftStatus.Stale, (await Commit(id)).Status);
    }

    [Fact]
    public async Task New_drafts_record_the_fingerprint_version_and_a_new_draft_on_a_pto_day_still_sees_pto_changes()
    {
        _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Tue, 3m);
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-27", Hours = 1m, TaskId = "t-1" }]);
        Assert.Equal(Draft.CurrentFingerprintVersion, draft.FingerprintVersion);
        _pi.Pto[0] = _pi.Pto[0] with { Description = "Dentist" };

        Assert.Equal(DraftStatus.Stale, (await Commit(draft.Id)).Status);
    }

    [Fact]
    public async Task Stored_2_1_0_draft_still_commits_when_the_pto_list_cant_be_read()
    {
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-27", Hours = 1m, TaskId = "t-1" }]);
        await _store.TryTransitionAsync("u-1", draft.Id, DraftStatus.Draft, d => { d.PtoUnreadUsers = null; d.FingerprintVersion = null; });
        _pi.PtoListThrows = true;

        Assert.Equal(DraftStatus.Committed, (await Commit(draft.Id)).Status);
    }

    [Fact]
    public async Task A_draft_on_a_day_without_pto_commits_when_the_pto_list_cant_be_read_at_commit()
    {
        var (draft, _) = await Plan([new() { Action = "create", Date = "2026-10-27", Hours = 1m, TaskId = "t-1" }]);
        _pi.PtoListThrows = true;

        Assert.Equal(DraftStatus.Committed, (await Commit(draft.Id)).Status);
    }

    private async Task<Draft> CommitPlan(ChangeInput change)
    {
        var plan = await _planner.PlanAsync("PTO", [change], default);
        return await Commit(plan.Draft!.Id);
    }

    private async Task<Draft> UndoAndCommit(Draft receipt)
    {
        var undo = await _planner.PlanUndoAsync(receipt, default);
        Assert.NotNull(undo.Draft);
        var result = await Commit(undo.Draft!.Id);
        Assert.Equal(DraftStatus.Committed, result.Status);
        return result;
    }

    [Fact]
    public async Task Undo_of_created_pto_deletes_it_and_its_linked_entry()
    {
        var receipt = await CommitPlan(CreatePto("2026-10-26", 3m));
        Assert.Single(_pi.Pto);

        await UndoAndCommit(receipt);

        Assert.Empty(_pi.Pto);
        Assert.DoesNotContain(_pi.Entries, e => e.IsPto);
    }

    [Fact]
    public async Task Undo_of_updated_pto_restores_before_values()
    {
        AddWedPto();
        var receipt = await CommitPlan(new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m, Date = "2026-10-29" });
        Assert.Equal((new DateOnly(2026, 10, 29), 2m), (_pi.Pto[0].Date, _pi.Pto[0].Hours));

        await UndoAndCommit(receipt);

        Assert.Equal((TimeChangePlannerTests.Wed, 4m, "tc-1"), (_pi.Pto[0].Date, _pi.Pto[0].Hours, _pi.Pto[0].TimeCodeId));
    }

    [Fact]
    public async Task Undo_of_deleted_pto_recreates_it_and_the_redo_deletes_the_new_id()
    {
        AddWedPto();
        var receipt = await CommitPlan(new() { Action = "delete_pto", PtoEntryId = "pto-1" });
        Assert.Empty(_pi.Pto);

        var undo = await UndoAndCommit(receipt);

        var recreated = Assert.Single(_pi.Pto);
        Assert.NotEqual("pto-1", recreated.Id);
        Assert.Equal((TimeChangePlannerTests.Wed, 4m, "u-1"), (recreated.Date, recreated.Hours, recreated.UserId));

        await UndoAndCommit(undo);

        Assert.Empty(_pi.Pto);
        Assert.Contains($"delete_pto:{recreated.Id}", _pi.Writes);
    }

    [Fact]
    public async Task Undo_of_a_deleted_pto_whose_code_is_on_no_other_record_keeps_the_codes_name()
    {
        AddWedPto();
        _pi.AddPto("pto-2", "u-1", TimeChangePlannerTests.Mon, 4m, "tc-2", "Bereavement");
        var receipt = await CommitPlan(new() { Action = "delete_pto", PtoEntryId = "pto-2" });
        Assert.Equal(DraftStatus.Committed, receipt.Status);

        var undo = (await _planner.PlanUndoAsync(receipt, default)).Draft!;

        var row = undo.Rows.Single();
        Assert.Equal(("tc-2", "Bereavement"), (row.After!.TimeCodeId, row.After.TimeCodeName));
        Assert.DoesNotContain(row.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
        Assert.Contains("PTO · Bereavement", TimeChangeText.For(new TimeChangesView { Kind = "draft", Draft = DraftView.From(undo, Drafts.T0) }, TimeChangePlannerTests.Wed));
    }

    [Fact]
    public async Task Undo_of_a_pto_code_change_keeps_the_old_codes_name()
    {
        _pi.AddPto("pto-1", "u-1", TimeChangePlannerTests.Wed, 4m, "tc-2", "Bereavement");
        _pi.AddPto("pto-2", "u-1", TimeChangePlannerTests.Mon, 4m, "tc-1", "PTO");
        var receipt = await CommitPlan(new() { Action = "update_pto", PtoEntryId = "pto-1", TimeCodeId = "tc-1" });
        Assert.Equal(DraftStatus.Committed, receipt.Status);
        Assert.DoesNotContain(_pi.Pto, p => p.TimeCodeId == "tc-2");

        var row = (await _planner.PlanUndoAsync(receipt, default)).Draft!.Rows.Single();

        Assert.Equal(("tc-2", "Bereavement"), (row.After!.TimeCodeId, row.After.TimeCodeName));
        Assert.DoesNotContain(row.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
    }

    [Fact]
    public async Task Undo_after_pto_deleted_in_pi_clarifies()
    {
        var receipt = await CommitPlan(CreatePto("2026-10-26", 3m));
        _pi.Pto.Clear();

        var undo = await _planner.PlanUndoAsync(receipt, default);

        Assert.Null(undo.Draft);
        Assert.Contains(undo.Clarifications!, c => c.Field == "ptoEntryId");
    }

    /// <summary>Wraps a gateway so CreatePtoAsync reports success without an id.</summary>
    private sealed class NoIdGateway(FakeGateway inner) : ITimeEntryGateway
    {
        public Task<PiWrite> CreatePtoAsync(PtoWrite w, CancellationToken ct) => Task.FromResult(new PiWrite(true, null, null));
        public Task<PiUser> GetCurrentUserAsync(CancellationToken ct) => inner.GetCurrentUserAsync(ct);
        public Task<PiUser?> GetUserAsync(string userId, CancellationToken ct) => inner.GetUserAsync(userId, ct);
        public Task<IReadOnlyList<PiUser>> FindUsersAsync(string name, CancellationToken ct) => inner.FindUsersAsync(name, ct);
        public Task<IReadOnlyList<PiNamed>> ListOpenProjectsAsync(CancellationToken ct) => inner.ListOpenProjectsAsync(ct);
        public Task<IReadOnlyList<PiNamed>> ListTasksAsync(string projectId, CancellationToken ct) => inner.ListTasksAsync(projectId, ct);
        public Task<PiTask?> GetTaskAsync(string taskId, CancellationToken ct) => inner.GetTaskAsync(taskId, ct);
        public Task<PiEntry?> GetEntryAsync(string timeEntryId, CancellationToken ct) => inner.GetEntryAsync(timeEntryId, ct);
        public Task<IReadOnlyList<PiEntry>> SearchEntriesAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct) => inner.SearchEntriesAsync(userId, start, end, ct);
        public Task<IReadOnlyList<PiTimeSheet>> SearchTimeSheetsAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct) => inner.SearchTimeSheetsAsync(userId, start, end, ct);
        public Task<PiWrite> CreateEntryAsync(EntryWrite w, CancellationToken ct) => inner.CreateEntryAsync(w, ct);
        public Task<PiWrite> UpdateEntryAsync(string timeEntryId, EntryWrite w, CancellationToken ct) => inner.UpdateEntryAsync(timeEntryId, w, ct);
        public Task<PiWrite> DeleteEntryAsync(string timeEntryId, CancellationToken ct) => inner.DeleteEntryAsync(timeEntryId, ct);
        public Task<IReadOnlyList<PiPto>> ListPtoAsync(string userId, CancellationToken ct) => inner.ListPtoAsync(userId, ct);
        public Task<IReadOnlyList<PiTimeCode>> ListPtoTimeCodesAsync(string userId, CancellationToken ct) => inner.ListPtoTimeCodesAsync(userId, ct);
        public Task<PiPto?> GetPtoAsync(string ptoEntryId, CancellationToken ct) => inner.GetPtoAsync(ptoEntryId, ct);
        public Task<PiWrite> UpdatePtoAsync(string ptoEntryId, PtoWrite w, CancellationToken ct) => inner.UpdatePtoAsync(ptoEntryId, w, ct);
        public Task<PiWrite> DeletePtoAsync(string ptoEntryId, CancellationToken ct) => inner.DeletePtoAsync(ptoEntryId, ct);
        public Task<IReadOnlyDictionary<string, PiLook>> GetUserLooksAsync(IReadOnlyCollection<string> userIds, CancellationToken ct) => inner.GetUserLooksAsync(userIds, ct);
        public Task<string?> GetPhotoAsync(string photoUrl, CancellationToken ct) => inner.GetPhotoAsync(photoUrl, ct);
    }
}
