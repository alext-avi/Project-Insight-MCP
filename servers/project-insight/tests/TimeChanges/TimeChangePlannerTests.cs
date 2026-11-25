using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TimeChangePlannerTests : IDisposable
{
    internal static readonly DateOnly Mon = new(2026, 10, 26), Tue = new(2026, 10, 27), Wed = new(2026, 10, 28);

    private readonly TempDraftRoot _dir = new();
    private readonly ManualTimeProvider _time = new(Drafts.T0);
    private readonly FakeGateway _pi = Seeded();
    private readonly FileDraftStore _store;
    private readonly TimeChangePlanner _planner;

    public TimeChangePlannerTests()
    {
        _store = new FileDraftStore(_dir.Root, _time);
        _planner = new TimeChangePlanner(_pi, _store, _time);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    /// <summary>Alex's week of 26 Oct: Mon 6h, Tue 2.5h, Wed 8h.</summary>
    internal static FakeGateway Seeded()
    {
        var pi = new FakeGateway();
        pi.Add("te-1", "u-1", Mon, 1.0m, "t-2");
        pi.Add("te-2", "u-1", Mon, 5.0m, "t-1");
        pi.Add("te-3", "u-1", Tue, 0.5m, "t-6", billable: false);
        pi.Add("te-4", "u-1", Tue, 2.0m, "t-1", description: "API integration");
        pi.Add("te-5", "u-1", Wed, 8.0m, "t-1");
        return pi;
    }

    internal static List<ChangeInput> TypicalChanges() =>
    [
        new() { Action = "create", Date = "2026-10-27", Hours = 4m, Project = "Acme Portal", Task = "Development", Billable = true, Description = "Order sync" },
        new() { Action = "update", TimeEntryId = "te-1", Hours = 1.5m },
        new() { Action = "delete", TimeEntryId = "te-4" }
    ];

    private sealed class PruneFailsStore(IDraftStore inner, Exception failure) : IDraftStore
    {
        public Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default) => inner.GetAsync(ownerId, id, ct);
        public Task CreateAsync(Draft draft, CancellationToken ct = default) => inner.CreateAsync(draft, ct);
        public Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default)
            => inner.TryTransitionAsync(ownerId, id, expectedStatus, update, ct);
        public Task PruneAsync(string ownerId, CancellationToken ct = default) => throw failure;
        public Task PruneAllAsync(CancellationToken ct = default) => throw failure;
    }

    [Fact]
    public async Task A_prune_that_fails_after_the_draft_is_saved_does_not_fail_the_plan()
    {
        // Not only file-system errors: a storage service throws its own exception types (throttling, a lost race).
        var planner = new TimeChangePlanner(_pi, new PruneFailsStore(_store, new InvalidOperationException("storage throttled")), _time);

        var result = await planner.PlanAsync("Log Tuesday", TypicalChanges(), default);

        Assert.NotNull(result.Draft);
        Assert.NotNull(await _store.GetAsync("u-1", result.Draft!.Id));
    }

    [Fact]
    public async Task Cancellation_during_the_prune_still_cancels_the_plan()
    {
        var planner = new TimeChangePlanner(_pi, new PruneFailsStore(_store, new OperationCanceledException()), _time);

        await Assert.ThrowsAsync<OperationCanceledException>(() => planner.PlanAsync("Log Tuesday", TypicalChanges(), default));
    }

    [Fact]
    public async Task Plan_stores_a_draft_and_writes_nothing_to_project_insight()
    {
        var result = await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default);

        var draft = result.Draft!;
        Assert.Empty(_pi.Writes);
        Assert.Equal(DraftStatus.Draft, draft.Status);
        Assert.Equal(Drafts.T0.AddHours(24), draft.ExpiresAt);
        Assert.True(DraftIds.TokenMatches(result.ConfirmToken!, draft.ConfirmTokenHash));
        Assert.Equal(["create", "update", "delete"], draft.Rows.Select(r => r.Action));
        Assert.Equal(1.0m, draft.Rows[1].Before!.Hours);
        Assert.Equal(1.5m, draft.Rows[1].After!.Hours);
        Assert.Equal("Client calls", draft.Rows[1].After!.TaskName);
        Assert.NotNull(await _store.GetAsync("u-1", draft.Id));
    }

    [Fact]
    public async Task Week_view_shows_every_weekday_with_context_and_totals()
    {
        var draft = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;

        var week = Assert.Single(draft.Weeks);
        Assert.True(week.IsCaller);
        Assert.Equal((Mon, new DateOnly(2026, 11, 1)), (week.Start, week.End));
        Assert.Equal([Mon, Tue, Wed, Mon.AddDays(3), Mon.AddDays(4)], week.Days.Select(d => d.Date)); // no empty weekend
        Assert.Equal((16.5m, 19.0m), (week.TotalBefore, week.TotalAfter));

        var tue = week.Days[1];
        Assert.True(tue.HasChanges);
        Assert.Equal((2.5m, 4.5m), (tue.TotalBefore, tue.TotalAfter));
        Assert.Equal(["te-3"], tue.Context.Select(c => c.TimeEntryId)); // te-4 is being deleted, so it's a row
        Assert.Equal([0, 2], tue.RowIndexes);

        var wed = week.Days[2];
        Assert.False(wed.HasChanges);
        Assert.Equal((8m, 8m), (wed.TotalBefore, wed.TotalAfter));

        Assert.Equal(["u-1|2026-10-26", "u-1|2026-10-27"], draft.DayFingerprints.Keys.Order());
        Assert.Equal((3, 2, 1), (draft.Totals.Changes, draft.Totals.Days, draft.Totals.People));
        Assert.Equal(2.5m, draft.Totals.NetHours);

        Assert.Single(_pi.SearchEntriesCalls, c => c.UserId == "u-1");
        Assert.Single(_pi.SearchTimeSheetsCalls, c => c.UserId == "u-1");
    }

    [Fact]
    public async Task Totals_and_quarter_hour_checks_use_exact_decimals()
    {
        _pi.Add("te-6", "u-1", Wed.AddDays(1), 0.1m);
        _pi.Add("te-7", "u-1", Wed.AddDays(1), 0.2m);
        var draft = (await _planner.PlanAsync("Odd hours",
        [
            new() { Action = "create", Date = "2026-10-29", Hours = 0.1m, TaskId = "t-5" },
            new() { Action = "create", Date = "2026-10-29", Hours = 1.25m, TaskId = "t-6" }
        ], default)).Draft!;

        var thu = draft.Weeks[0].Days.Single(d => d.Date == Wed.AddDays(1));
        Assert.Equal(0.3m, thu.TotalBefore);
        Assert.Equal(1.65m, thu.TotalAfter);
        Assert.Contains(draft.Rows[0].Warnings, w => w.Code == WarningCode.NotQuarterHour);
        Assert.DoesNotContain(draft.Rows[1].Warnings, w => w.Code == WarningCode.NotQuarterHour);
        Assert.Equal(1.35m, draft.Totals.NetHours);
    }

    [Fact]
    public async Task Ambiguous_names_return_candidates_and_create_no_draft()
    {
        var result = await _planner.PlanAsync("Log",
            [new() { Action = "create", Date = "2026-10-27", Hours = 1m, Project = "Acme", Task = "Development" }], default);

        Assert.Null(result.Draft);
        var c = Assert.Single(result.Clarifications);
        Assert.Equal(["Acme Portal", "Acme Support"], c.Candidates.Select(x => x.Name));
        Assert.False(Directory.Exists(Path.Combine(_dir.Root, "u-1")) && Directory.EnumerateFiles(Path.Combine(_dir.Root, "u-1"), "*.json").Any());
    }

    [Fact]
    public async Task The_same_entry_in_two_changes_is_refused()
    {
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanAsync("Fix",
            [new() { Action = "update", TimeEntryId = "te-1", Hours = 2m }, new() { Action = "delete", TimeEntryId = "te-1" }], default));

        Assert.Equal("invalid_change", ex.Code);
        Assert.Contains("te-1 appears in more than one change", ex.Message);
    }

    [Fact]
    public async Task Moving_an_entry_to_another_week_shows_both_weeks()
    {
        var draft = (await _planner.PlanAsync("Move",
            [new() { Action = "update", TimeEntryId = "te-5", Date = "2026-11-03" }], default)).Draft!;

        Assert.Equal([Mon, new DateOnly(2026, 11, 2)], draft.Weeks.Select(w => w.Start));
        Assert.Equal((8m, 0m), Day(draft, Wed));
        Assert.Equal((0m, 8m), Day(draft, new DateOnly(2026, 11, 3)));
        Assert.Equal(["u-1|2026-10-28", "u-1|2026-11-03"], draft.DayFingerprints.Keys.Order());
        Assert.Equal(0m, draft.Totals.NetHours);
        Assert.Equal(2, _pi.SearchEntriesCalls.Count);
        Assert.Equal(_pi.SearchEntriesCalls.Count, _pi.SearchEntriesCalls.Distinct().Count());
        Assert.Single(_pi.SearchTimeSheetsCalls);

        static (decimal, decimal) Day(Draft d, DateOnly date)
        {
            var day = d.Weeks.SelectMany(w => w.Days).Single(x => x.Date == date);
            return (day.TotalBefore, day.TotalAfter);
        }
    }

    [Fact]
    public async Task Rows_carry_warnings()
    {
        _pi.Sheets["u-2"] = [new PiTimeSheet("ts-2", Mon, new DateOnly(2026, 11, 1), true, false, false, "https://pi.test/ts-2")];
        var draft = (await _planner.PlanAsync("Mixed",
        [
            new() { Action = "create", Date = "2026-10-28", Hours = 6m, TaskId = "t-1" },
            new() { Action = "create", Date = "2026-10-27", Hours = 2m, TaskId = "t-1" },
            new() { Action = "create", Date = "2026-10-27", Hours = 3m, TaskId = "t-4", User = "Jane Doe" }
        ], default)).Draft!;

        Assert.Contains(draft.Rows[0].Warnings, w => w.Code == WarningCode.Over12Hours && w.Message.Contains("14h"));
        Assert.Contains(draft.Rows[1].Warnings, w => w.Code == WarningCode.LikelyDuplicate);
        Assert.Equal([WarningCode.TimesheetSubmitted, WarningCode.OtherPerson], draft.Rows[2].Warnings.Select(w => w.Code));
        var jane = draft.Weeks.Single(w => w.UserId == "u-2");
        Assert.False(jane.IsCaller);
        Assert.Equal(("ts-2", "https://pi.test/ts-2"), (jane.TimesheetId, jane.TimesheetUrl));
        Assert.Equal(4, draft.Totals.Warnings);
    }

    [Theory]
    [InlineData(true, false, false, WarningCode.TimesheetSubmitted)]
    [InlineData(false, true, false, WarningCode.TimesheetApproved)]
    [InlineData(false, false, true, WarningCode.TimesheetLocked)]
    [InlineData(true, true, true, WarningCode.TimesheetLocked)]
    public async Task Timesheet_status_warns_on_the_row(bool submitted, bool approved, bool locked, string expected)
    {
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-1", Mon, new DateOnly(2026, 11, 1), submitted, approved, locked, "https://pi.test/ts-1")];
        var draft = (await _planner.PlanAsync("Log",
            [new() { Action = "create", Date = "2026-10-29", Hours = 1m, TaskId = "t-5" }], default)).Draft!;

        Assert.Equal([expected], draft.Rows[0].Warnings.Select(w => w.Code));
    }

    [Theory]
    [InlineData("""{"action":"create","date":"27/10/2026","hours":1,"taskId":"t-1"}""", "date must be yyyy-MM-dd")]
    [InlineData("""{"action":"create","date":"2026-10-27","hours":0,"taskId":"t-1"}""", "hours must be more than 0")]
    [InlineData("""{"action":"create","date":"2026-10-27","hours":25,"taskId":"t-1"}""", "at most 24")]
    [InlineData("""{"action":"create","hours":1,"taskId":"t-1"}""", "date must be yyyy-MM-dd")]
    [InlineData("""{"action":"move","timeEntryId":"te-1"}""", "action must be create, update or delete")]
    [InlineData("""{"action":"update","timeEntryId":"te-1","hours":1}""", "changes nothing")]
    [InlineData("""{"action":"update","timeEntryId":"te-1","user":"Jane"}""", "can't change whose time")]
    [InlineData("""{"action":"delete"}""", "needs timeEntryId")]
    public async Task Invalid_changes_are_refused_with_a_reason(string json, string reason)
    {
        var change = System.Text.Json.JsonSerializer.Deserialize<ChangeInput>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanAsync("x", [change], default));
        Assert.Contains(reason, ex.Message);
        Assert.StartsWith("Change 1:", ex.Message);
    }

    [Fact]
    public async Task More_than_a_hundred_changes_is_refused()
    {
        var changes = Enumerable.Range(0, 101).Select(_ => new ChangeInput { Action = "delete", TimeEntryId = "te-1" }).ToList();
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanAsync("x", changes, default));
        Assert.Equal("too_many_changes", ex.Code);
    }

    [Fact]
    public async Task Unknown_entry_is_a_clarification()
    {
        var result = await _planner.PlanAsync("x", [new() { Action = "delete", TimeEntryId = "te-404" }], default);
        Assert.Equal("timeEntryId", Assert.Single(result.Clarifications).Field);
    }

    [Fact]
    public async Task Moving_an_entry_out_of_a_locked_week_warns()
    {
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-1", Mon, new DateOnly(2026, 11, 1), false, false, true, "https://pi.test/ts-1")];
        var draft = (await _planner.PlanAsync("Move",
            [new() { Action = "update", TimeEntryId = "te-5", Date = "2026-11-03" }], default)).Draft!;

        Assert.Equal([WarningCode.TimesheetLocked], draft.Rows[0].Warnings.Select(w => w.Code));
        Assert.Equal(1, draft.Totals.Warnings);
    }

    [Fact]
    public async Task Only_a_committed_receipt_can_be_undone()
    {
        var planned = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;
        planned.Status = DraftStatus.Failed;

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => _planner.PlanUndoAsync(planned, default));
        Assert.Equal("not_undoable", ex.Code);
    }

    [Fact]
    public async Task Undo_skips_rows_that_were_not_applied()
    {
        var planned = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;
        planned.Status = DraftStatus.Committed;
        planned.Rows[0].Outcome = RowOutcome.Rejected;
        planned.Rows[1].Outcome = RowOutcome.Applied;
        planned.Rows[2].Outcome = RowOutcome.Reversed;
        _pi.Entries[_pi.Entries.FindIndex(e => e.Id == "te-1")] = _pi.Entries.First(e => e.Id == "te-1") with { Hours = 1.5m };

        var undo = (await _planner.PlanUndoAsync(planned, default)).Draft!;

        Assert.Equal(["update"], undo.Rows.Select(r => r.Action));
    }

    [Fact]
    public async Task Undo_draft_reverses_each_action()
    {
        // A receipt as the committer leaves it: te-100 was created, te-1 updated 1.0 → 1.5, te-4 deleted.
        var planned = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;
        _pi.Add("te-100", "u-1", Tue, 4m, "t-1", description: "Order sync");
        _pi.Entries[_pi.Entries.FindIndex(e => e.Id == "te-1")] = _pi.Entries.First(e => e.Id == "te-1") with { Hours = 1.5m };
        _pi.Entries.RemoveAll(e => e.Id == "te-4");
        planned.Status = DraftStatus.Committed;
        planned.Rows[0].SavedId = "te-100";
        planned.CommittedAt = Drafts.T0.AddMinutes(30);

        var undo = (await _planner.PlanUndoAsync(planned, default)).Draft!;

        Assert.Equal(planned.Id, undo.UndoOf);
        Assert.Equal(planned.CommittedAt, undo.UndoOfCommittedAt);
        Assert.Equal("Undo: Log Tuesday", undo.Intent);
        Assert.Equal(["delete", "update", "create"], undo.Rows.Select(r => r.Action));
        Assert.Equal("te-100", undo.Rows[0].TimeEntryId);
        Assert.Equal(1.0m, undo.Rows[1].After!.Hours);
        Assert.Equal(("t-1", 2.0m, "API integration"), (undo.Rows[2].After!.TaskId, undo.Rows[2].After!.Hours, undo.Rows[2].After!.Description));
        Assert.Equal(0m, undo.Totals.NetHours + planned.Totals.NetHours);
    }

    [Fact]
    public async Task Without_a_timesheet_the_week_takes_the_shape_of_the_persons_last_one()
    {
        // A Sunday-to-Saturday company: the last timesheet was 20-26 Sep, none exists for this week yet.
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-old", new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 26), true, true, false, null)];
        _pi.Add("te-sun", "u-1", new DateOnly(2026, 10, 25), 2m, "t-1");

        var draft = (await _planner.PlanAsync("Fix Monday", [new() { Action = "update", TimeEntryId = "te-1", Hours = 1.5m }], default)).Draft!;

        var week = Assert.Single(draft.Weeks);
        Assert.Equal((new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 31)), (week.Start, week.End));
        Assert.Null(week.TimesheetId);
        Assert.Equal((new DateOnly(2026, 10, 25), 2m), (week.Days[0].Date, week.Days[0].TotalBefore));
        Assert.Equal(18.5m, week.TotalBefore);
        Assert.Empty(draft.Rows[0].Warnings);
        var call = Assert.Single(_pi.SearchTimeSheetsCalls);
        Assert.Equal(Mon.AddDays(-TimeChangePlanner.PeriodLookbackDays), call.Start);
    }

    [Fact]
    public async Task Each_week_carries_the_persons_pi_avatar_with_their_photo_inlined()
    {
        _pi.Looks["u-1"] = new PiLook("AE", "#17a7c9", "#ffffff", "/Sites/Files/x/UserPhotos/y/ae.jpeg");
        _pi.Looks["u-2"] = new PiLook("JD", "#be2edd", "#ffffff", null);
        _pi.Photos["/Sites/Files/x/UserPhotos/y/ae.jpeg"] = "data:image/jpeg;base64,AAAA";

        var draft = (await _planner.PlanAsync("Log Tuesday",
            [TypicalChanges()[0], new() { Action = "create", User = "Jane Doe", Date = "2026-10-27", Hours = 3m, TaskId = "t-4" }], default)).Draft!;

        var alex = draft.Weeks.Single(w => w.UserId == "u-1").Avatar!;
        Assert.Equal(("AE", "#17a7c9", "#ffffff", "data:image/jpeg;base64,AAAA"), (alex.Initials, alex.Background, alex.Foreground, alex.Photo));
        var jane = draft.Weeks.Single(w => w.UserId == "u-2").Avatar!;
        Assert.Equal(("JD", "#be2edd", null), (jane.Initials, jane.Background, jane.Photo));
    }

    [Fact]
    public async Task A_failed_avatar_lookup_still_plans_the_draft_without_avatars()
    {
        _pi.LooksThrow = new TimeChangeException("pi_unavailable", "API returned InternalServerError");

        var draft = (await _planner.PlanAsync("Log Tuesday", TypicalChanges(), default)).Draft!;

        Assert.All(draft.Weeks, w => Assert.Null(w.Avatar));
    }

    [Fact]
    public async Task Project_colours_follow_rows_and_context_entries()
    {
        var pi = new FakeGateway();
        pi.ProjectColors["p-1"] = "#be2edd";
        pi.Add("te-1", "u-1", Mon, 1.0m, "t-2");
        pi.Add("te-3", "u-1", Mon, 0.5m, "t-6", billable: false);
        var planner = new TimeChangePlanner(pi, _store, _time);

        var draft = (await planner.PlanAsync("Fix Monday",
            [new() { Action = "update", TimeEntryId = "te-1", Hours = 1.5m },
             new() { Action = "update", TimeEntryId = "te-3", TaskId = "t-1" }], default)).Draft!;

        Assert.Equal(("#be2edd", "#be2edd"), (draft.Rows[0].Before!.ProjectColor, draft.Rows[0].After!.ProjectColor));
        Assert.Equal((null, "#be2edd"), (draft.Rows[1].Before!.ProjectColor, draft.Rows[1].After!.ProjectColor));
    }

    [Fact]
    public async Task Context_entries_carry_their_project_colour()
    {
        var pi = new FakeGateway();
        pi.ProjectColors["p-1"] = "#be2edd";
        pi.Add("te-2", "u-1", Mon, 5.0m, "t-1");
        pi.Add("te-3", "u-1", Mon, 0.5m, "t-6", billable: false);
        var planner = new TimeChangePlanner(pi, _store, _time);

        var draft = (await planner.PlanAsync("Monday", [new() { Action = "update", TimeEntryId = "te-3", Hours = 1m }], default)).Draft!;

        var context = draft.Weeks.Single().Days.Single(d => d.Date == Mon).Context;
        Assert.Equal("#be2edd", Assert.Single(context).ProjectColor);
    }

    [Fact]
    public async Task Undoing_an_undo_is_a_redo_of_the_original_changes()
    {
        var planned = (await _planner.PlanAsync("Log Tuesday", [TypicalChanges()[0]], default)).Draft!;
        _pi.Add("te-100", "u-1", Tue, 4m, "t-1", description: "Order sync");
        planned.Status = DraftStatus.Committed;
        planned.Rows[0].SavedId = "te-100";
        planned.CommittedAt = Drafts.T0;
        var undo = (await _planner.PlanUndoAsync(planned, default)).Draft!;
        _pi.Entries.RemoveAll(e => e.Id == "te-100");
        undo.Status = DraftStatus.Committed;
        undo.CommittedAt = Drafts.T0.AddMinutes(5);

        var redo = (await _planner.PlanUndoAsync(undo, default)).Draft!;

        Assert.Equal("Redo: Log Tuesday", redo.Intent);
        Assert.Equal(undo.Id, redo.UndoOf);
        Assert.Equal([("create", 4m)], redo.Rows.Select(r => (r.Action, r.After!.Hours)));
        Assert.Equal(planned.Totals.NetHours, redo.Totals.NetHours);
    }

    [Fact]
    public async Task Undo_can_recreate_a_deleted_zero_hour_entry_and_restore_zero_hours()
    {
        _pi.Add("te-0", "u-1", Wed, 0m, "t-5", billable: false);
        _pi.Add("te-z", "u-1", Wed, 0m, "t-6", billable: false);
        var planned = (await _planner.PlanAsync("Tidy Wednesday",
            [new() { Action = "delete", TimeEntryId = "te-0" }, new() { Action = "update", TimeEntryId = "te-z", Hours = 1m }], default)).Draft!;
        _pi.Entries.RemoveAll(e => e.Id == "te-0");
        _pi.Entries[_pi.Entries.FindIndex(e => e.Id == "te-z")] = _pi.Entries.First(e => e.Id == "te-z") with { Hours = 1m };
        planned.Status = DraftStatus.Committed;
        planned.CommittedAt = Drafts.T0;

        var undo = (await _planner.PlanUndoAsync(planned, default)).Draft!;

        Assert.Equal([("create", 0m), ("update", 0m)], undo.Rows.Select(r => (r.Action, r.After!.Hours)));
    }

    [Fact]
    public async Task A_user_supplied_update_still_needs_more_than_zero_hours()
    {
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() =>
            _planner.PlanAsync("x", [new() { Action = "update", TimeEntryId = "te-1", Hours = 0m }], default));
        Assert.Contains("hours must be more than 0", ex.Message);
    }

    [Theory]
    [InlineData("2025-10-27", "Date is 365 days ago (Mon 27 Oct 2025)")]
    [InlineData("2026-09-25", "Date is 32 days ago (Fri 25 Sep 2026)")]
    [InlineData("2026-11-04", "Date is 8 days in the future (Wed 4 Nov 2026)")]
    [InlineData("2026-09-26", null)]
    [InlineData("2026-11-03", null)]
    public async Task A_date_more_than_a_month_back_or_a_week_ahead_warns(string date, string? expected)
    {
        var draft = (await _planner.PlanAsync("Log", [new() { Action = "create", Date = date, Hours = 1m, TaskId = "t-5" }], default)).Draft!;

        Assert.Equal(expected is null ? [] : new[] { expected },
            draft.Rows[0].Warnings.Where(w => w.Code == WarningCode.FarDate).Select(w => w.Message));
    }

    [Fact]
    public async Task Deleting_an_old_entry_warns_on_its_date_but_an_undo_never_does()
    {
        _pi.Add("te-old", "u-1", new DateOnly(2025, 10, 28), 2m, "t-5");
        var delete = (await _planner.PlanAsync("Tidy", [new() { Action = "delete", TimeEntryId = "te-old" }], default)).Draft!;
        Assert.Contains(delete.Rows[0].Warnings, w => w.Code == WarningCode.FarDate && w.Message.Contains("Tue 28 Oct 2025"));

        _pi.Entries.RemoveAll(e => e.Id == "te-old");
        delete.Status = DraftStatus.Committed;
        delete.CommittedAt = Drafts.T0;
        var undo = (await _planner.PlanUndoAsync(delete, default)).Draft!;

        Assert.Equal(["create"], undo.Rows.Select(r => r.Action));
        Assert.DoesNotContain(undo.Rows[0].Warnings, w => w.Code == WarningCode.FarDate);
    }

    [Fact]
    public async Task A_made_up_week_stops_where_a_real_timesheet_starts_and_that_timesheet_keeps_its_dates()
    {
        // Two-weekly sheets until 18 Oct, nothing for 19-25 Oct, then a weekly sheet for 26 Oct-1 Nov.
        _pi.Sheets["u-1"] =
        [
            new PiTimeSheet("ts-1", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18), true, false, false, null),
            new PiTimeSheet("ts-2", Mon, new DateOnly(2026, 11, 1), true, false, false, "https://pi.test/ts-2")
        ];

        var draft = (await _planner.PlanAsync("Log",
        [
            new() { Action = "create", Date = "2026-10-21", Hours = 1m, TaskId = "t-5" },
            new() { Action = "create", Date = "2026-10-27", Hours = 1m, TaskId = "t-5" }
        ], default)).Draft!;

        Assert.Equal([(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), (string?)null), (Mon, new DateOnly(2026, 11, 1), "ts-2")],
            draft.Weeks.Select(w => (w.Start, w.End, w.TimesheetId)));
        Assert.DoesNotContain(draft.Rows[0].Warnings, w => w.Code == WarningCode.TimesheetSubmitted);
        Assert.Contains(draft.Rows[1].Warnings, w => w.Code == WarningCode.TimesheetSubmitted);
    }

    [Fact]
    public async Task A_made_up_week_stops_before_a_later_timesheet_even_when_no_change_falls_in_it()
    {
        _pi.Sheets["u-1"] =
        [
            new PiTimeSheet("ts-1", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 18), true, false, false, null),
            new PiTimeSheet("ts-2", Mon, new DateOnly(2026, 11, 1), true, false, false, null)
        ];

        var draft = (await _planner.PlanAsync("Log", [new() { Action = "create", Date = "2026-10-21", Hours = 1m, TaskId = "t-5" }], default)).Draft!;

        var week = Assert.Single(draft.Weeks);
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25)), (week.Start, week.End));
    }

    [Fact]
    public async Task Undoing_a_redo_is_an_undo_again_never_undo_of_redo()
    {
        var planned = (await _planner.PlanAsync("Log Tuesday", [TypicalChanges()[0]], default)).Draft!;
        _pi.Add("te-100", "u-1", Tue, 4m, "t-1", description: "Order sync");
        planned.Status = DraftStatus.Committed;
        planned.Rows[0].SavedId = "te-100";
        planned.CommittedAt = Drafts.T0;
        var undo = (await _planner.PlanUndoAsync(planned, default)).Draft!;
        _pi.Entries.RemoveAll(e => e.Id == "te-100");
        undo.Status = DraftStatus.Committed;
        undo.CommittedAt = Drafts.T0;
        var redo = (await _planner.PlanUndoAsync(undo, default)).Draft!;
        _pi.Add("te-101", "u-1", Tue, 4m, "t-1", description: "Order sync");
        redo.Status = DraftStatus.Committed;
        redo.Rows[0].SavedId = "te-101";
        redo.CommittedAt = Drafts.T0;

        var undoAgain = (await _planner.PlanUndoAsync(redo, default)).Draft!;

        Assert.Equal(("Undo: Log Tuesday", "Redo: Log Tuesday", "Undo: Log Tuesday"), (undo.Intent, redo.Intent, undoAgain.Intent));
        Assert.Equal(redo.Id, undoAgain.UndoOf);
        Assert.Equal([("delete", "te-101")], undoAgain.Rows.Select(r => (r.Action, r.TimeEntryId)));
    }

    [Fact]
    public async Task Photos_for_different_people_are_fetched_at_the_same_time()
    {
        _pi.Looks["u-1"] = new PiLook("AE", null, null, "/Sites/Files/x/ae.jpeg");
        _pi.Looks["u-2"] = new PiLook("JD", null, null, "/Sites/Files/x/jd.jpeg");
        _pi.Photos["/Sites/Files/x/ae.jpeg"] = "data:image/jpeg;base64,AAAA";
        _pi.Photos["/Sites/Files/x/jd.jpeg"] = "data:image/jpeg;base64,BBBB";
        _pi.Delay = TimeSpan.FromMilliseconds(20);

        var draft = (await _planner.PlanAsync("Log Tuesday",
            [TypicalChanges()[0], new() { Action = "create", User = "Jane Doe", Date = "2026-10-27", Hours = 3m, TaskId = "t-4" }], default)).Draft!;

        Assert.Equal(["data:image/jpeg;base64,AAAA", "data:image/jpeg;base64,BBBB"], draft.Weeks.Select(w => w.Avatar!.Photo));
        Assert.Equal(2, _pi.MaxConcurrent);
    }

    // ---- PTO rows ----

    private Task<PlanResult> PlanPto(ChangeInput change) => _planner.PlanAsync("PTO", [change], default);

    private Task<PlanResult> PlanPto(ChangeInput first, ChangeInput second) => _planner.PlanAsync("PTO", [first, second], default);

    [Fact]
    public async Task Create_pto_row_resolves_time_code_by_name()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "pto" })).Draft!.Rows[0];

        Assert.Equal(("create_pto", "pto"), (row.Action, row.Kind));
        Assert.Equal(("tc-1", "PTO", 3m), (row.After!.TimeCodeId, row.After.TimeCodeName, row.After.Hours));
        Assert.Equal(("", "", "", ""), (row.After.TaskId, row.After.TaskName, row.After.ProjectId, row.After.ProjectName));
        Assert.False(row.After.Billable);
        Assert.Null(row.TimeEntryId);
    }

    [Fact]
    public async Task Exact_time_code_name_beats_substring()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "PTO - Floating");

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "pto" })).Draft!.Rows[0];

        Assert.Equal("tc-1", row.After!.TimeCodeId);
    }

    [Fact]
    public async Task Unique_substring_resolves_the_time_code()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "Floating Holiday");

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "float" })).Draft!.Rows[0];

        Assert.Equal("tc-2", row.After!.TimeCodeId);
    }

    [Fact]
    public async Task Time_code_only_on_an_accrual_record_resolves_by_name_and_id()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");
        _pi.AddAccrual("u-1", "tc-7", "Personal Day");

        var byName = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "personal" })).Draft!.Rows[0];
        var byId = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCodeId = "tc-7" })).Draft!.Rows[0];

        Assert.Equal(("tc-7", "Personal Day"), (byName.After!.TimeCodeId, byName.After.TimeCodeName));
        Assert.DoesNotContain(byName.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
        Assert.DoesNotContain(byId.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
        Assert.Equal("Personal Day", byId.After!.TimeCodeName);
    }

    [Fact]
    public async Task Unknown_time_code_name_asks_for_clarification()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "Floating Holiday");

        var result = await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "Sabbatical" });

        Assert.Null(result.Draft);
        var c = Assert.Single(result.Clarifications);
        Assert.Equal(("timeCode", "Sabbatical", 0), (c.Field, c.Query, c.ChangeIndex));
        Assert.Equal(["tc-1", "tc-2"], c.Candidates.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task Unknown_time_code_id_warns()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCodeId = "tc-9" })).Draft!.Rows[0];

        Assert.Equal("tc-9", row.After!.TimeCodeId);
        Assert.Single(row.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
    }

    [Fact]
    public async Task Known_time_code_id_does_not_warn()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCodeId = "tc-1" })).Draft!.Rows[0];

        Assert.DoesNotContain(row.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
        Assert.Equal("PTO", row.After!.TimeCodeName);
    }

    [Fact]
    public async Task Of_two_codes_with_the_same_name_the_only_active_one_is_used()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-old", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-new", "PTO");
        _pi.InactiveCodes.Add("tc-old");

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO" })).Draft!.Rows[0];

        Assert.Equal("tc-new", row.After!.TimeCodeId);
    }

    [Fact]
    public async Task Of_several_partial_matches_the_only_active_one_is_used()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "Floating Holiday");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "Floating Holiday 2025");
        _pi.InactiveCodes.Add("tc-2");

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "float" })).Draft!.Rows[0];

        Assert.Equal("tc-1", row.After!.TimeCodeId);
    }

    [Fact]
    public async Task Two_active_codes_with_the_same_name_ask_for_a_time_code_id()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-old", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-new", "PTO");
        _pi.AddPto("pto-3", "u-1", Tue, 4m, "tc-3", "Floating Holiday");

        var result = await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO" });

        Assert.Null(result.Draft);
        var c = Assert.Single(result.Clarifications);
        Assert.DoesNotContain("exact name", c.Message);
        Assert.Contains("timeCodeId", c.Message);
        Assert.Equal(["tc-new", "tc-old"], c.Candidates.Select(x => x.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Inactive_candidates_are_marked()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-old", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-older", "PTO");
        _pi.InactiveCodes.UnionWith(["tc-old", "tc-older"]);

        var c = Assert.Single((await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO" })).Clarifications);

        Assert.All(c.Candidates, x => Assert.Equal("PTO (inactive)", x.Name));
    }

    [Fact]
    public async Task A_time_code_id_matches_whatever_its_case()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCodeId = "TC-1" })).Draft!.Rows[0];

        Assert.Equal(("tc-1", "PTO"), (row.After!.TimeCodeId, row.After.TimeCodeName));
        Assert.DoesNotContain(row.Warnings, w => w.Code == WarningCode.UnknownTimeCode);
    }

    [Fact]
    public async Task An_update_pto_giving_the_same_time_code_id_in_another_case_changes_nothing()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", TimeCodeId = "TC-1" }));

        Assert.Contains("changes nothing", ex.Message);
    }

    [Fact]
    public async Task A_create_pto_duplicating_a_pto_by_a_differently_cased_code_id_warns()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");

        var row = (await PlanPto(new() { Action = "create_pto", Date = "2026-10-28", Hours = 2m, TimeCodeId = "TC-1" })).Draft!.Rows[0];

        Assert.Contains(row.Warnings, w => w.Code == WarningCode.LikelyDuplicate);
    }

    [Fact]
    public async Task A_failed_pto_list_surfaces_as_pi_unavailable()
    {
        _pi.PtoListThrows = true;

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() =>
            PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO" }));

        Assert.Equal("pi_unavailable", ex.Code);
    }

    [Fact]
    public async Task Pto_actions_reject_task_and_billable()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() =>
            PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO", Billable = true }));
        Assert.Contains("Change 1", ex.Message);

        var ex2 = await Assert.ThrowsAsync<TimeChangeException>(() =>
            PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO", TaskId = "t-1" }));
        Assert.Contains("Change 1", ex2.Message);
    }

    [Fact]
    public async Task Update_pto_reads_before_values()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var row = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m })).Draft!.Rows[0];

        Assert.Equal(("update_pto", "pto-1", "te-pto-pto-1", null), (row.Action, row.PtoEntryId, row.LinkedTimeEntryId, row.TimeEntryId));
        Assert.Equal((4m, 2m), (row.Before!.Hours, row.After!.Hours));
        Assert.Equal("tc-1", row.After.TimeCodeId);
        Assert.Null(row.PtoApproved);
    }

    [Fact]
    public async Task Update_pto_can_change_the_time_code()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "Floating Holiday");

        var row = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", TimeCode = "floating" })).Draft!.Rows[0];

        Assert.Equal(("tc-1", "tc-2"), (row.Before!.TimeCodeId, row.After!.TimeCodeId));
    }

    [Fact]
    public async Task Update_pto_that_changes_nothing_is_refused()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 4m }));

        Assert.Contains("Change 1", ex.Message);
    }

    [Fact]
    public async Task Delete_pto_row_has_before_values_only()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var row = (await PlanPto(new() { Action = "delete_pto", PtoEntryId = "pto-1" })).Draft!.Rows[0];

        Assert.Equal(("delete_pto", "pto", "pto-1", "te-pto-pto-1"), (row.Action, row.Kind, row.PtoEntryId, row.LinkedTimeEntryId));
        Assert.Null(row.After);
        Assert.Equal(4m, row.Before!.Hours);
    }

    [Fact]
    public async Task Missing_pto_id_asks_for_clarification()
    {
        var result = await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-x", Hours = 2m });

        var c = Assert.Single(result.Clarifications);
        Assert.Equal(("ptoEntryId", "pto-x", "No PTO entry with id pto-x."), (c.Field, c.Query, c.Message));
    }

    [Fact]
    public async Task Update_pto_without_an_id_is_refused()
    {
        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "delete_pto" }));

        Assert.Contains("ptoEntryId", ex.Message);
    }

    [Fact]
    public async Task Time_change_on_pto_linked_entry_is_refused()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "update", TimeEntryId = "te-pto-pto-1", Hours = 1m }));
        Assert.Contains("update_pto", ex.Message);

        var ex2 = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "delete", TimeEntryId = "te-pto-pto-1" }));
        Assert.Contains("delete_pto", ex2.Message);
    }

    [Fact]
    public async Task Approved_pto_is_flagged_on_the_row()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, approved: true);

        var row = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m })).Draft!.Rows[0];

        Assert.True(row.PtoApproved);
    }

    [Fact]
    public async Task Same_pto_id_twice_is_refused()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(
            new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m },
            new() { Action = "delete_pto", PtoEntryId = "pto-1" }));

        Assert.Contains("Change 2", ex.Message);
    }

    [Fact]
    public async Task Time_actions_reject_pto_only_fields_and_pto_actions_reject_timeEntryId()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var e1 = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "update", TimeEntryId = "te-1", PtoEntryId = "pto-1", Hours = 1m }));
        var e2 = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "create_pto", Date = "2026-10-29", Hours = 3m, TimeCode = "PTO", TimeEntryId = "te-1" }));
        var e3 = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "create", Date = "2026-10-29", Hours = 3m, TaskId = "t-1", TimeCode = "PTO" }));

        Assert.All(new[] { e1, e2, e3 }, e => { Assert.Equal("invalid_change", e.Code); Assert.Contains("Change 1", e.Message); });
    }

    // ---- PTO in views, totals, fingerprints and warnings ----

    private static DayView Day(Draft d, DateOnly date, string userId = "u-1") => d.Weeks.First(w => w.UserId == userId).Days.Single(x => x.Date == date);

    private static ChangeInput CreatePto(string date, decimal hours) => new() { Action = "create_pto", Date = date, Hours = hours, TimeCodeId = "tc-1" };

    private static ChangeInput CreateTime(string date, decimal hours) => new() { Action = "create", Date = date, Hours = hours, TaskId = "t-1" };

    [Fact]
    public async Task Pto_only_draft_totals_include_existing_and_proposed_pto()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var draft = (await PlanPto(CreatePto("2026-10-26", 3m), CreatePto("2026-10-27", 1.75m))).Draft!;

        Assert.Equal((6m, 9m), (Day(draft, Mon).TotalBefore, Day(draft, Mon).TotalAfter));
        Assert.Equal((2.5m, 4.25m), (Day(draft, Tue).TotalBefore, Day(draft, Tue).TotalAfter));
        Assert.Equal((12m, 12m), (Day(draft, Wed).TotalBefore, Day(draft, Wed).TotalAfter));
        var week = draft.Weeks.Single();
        Assert.Equal((20.5m, 25.25m), (week.TotalBefore, week.TotalAfter));
        var pto = Assert.Single(Day(draft, Wed).Context, c => c.IsPto == true);
        Assert.Equal(("PTO", "te-pto-pto-1"), (pto.TimeCodeName, pto.TimeEntryId));
        Assert.Equal((4.75m, 4.75m), (draft.Totals.PtoDelta, draft.Totals.NonBillableDelta));
        Assert.All(Day(draft, Mon).Context, c => Assert.Null(c.IsPto));
    }

    [Fact]
    public async Task Mixed_draft_counts_linked_entry_once()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var draft = (await _planner.PlanAsync("Mixed",
            [CreateTime("2026-10-26", 2m), new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m }], default)).Draft!;

        Assert.Equal(10m, Day(draft, Wed).TotalAfter);
        Assert.Equal(12m, Day(draft, Wed).TotalBefore);
        Assert.Equal(["te-5"], Day(draft, Wed).Context.Select(c => c.TimeEntryId));
        Assert.Equal(-2m, draft.Totals.PtoDelta);
    }

    [Fact]
    public async Task Swap_pto_for_work_nets_zero()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var draft = (await _planner.PlanAsync("Swap",
            [new() { Action = "delete_pto", PtoEntryId = "pto-1" }, CreateTime("2026-10-28", 4m)], default)).Draft!;

        Assert.Equal((12m, 12m), (Day(draft, Wed).TotalBefore, Day(draft, Wed).TotalAfter));
        Assert.DoesNotContain(Day(draft, Wed).Context, c => c.IsPto == true);
    }

    [Fact]
    public async Task Update_pto_into_next_period_shows_both_weeks()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var draft = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Date = "2026-11-04" })).Draft!;

        Assert.Equal(2, draft.Weeks.Count);
        var (first, second) = (draft.Weeks[0], draft.Weeks[1]);
        Assert.Equal(-4m, first.TotalAfter - first.TotalBefore);
        Assert.Equal(4m, second.TotalAfter - second.TotalBefore);
        Assert.Equal(0m, draft.Totals.PtoDelta);
    }

    private static bool Over40(DraftRow r) => r.Warnings.Any(w => w.Code == "over_40h_week");

    [Fact]
    public async Task Over_40h_week_warns_time_and_pto_rows()
    {
        // 16.5 on file, plus 8 + 8 + 8.5 = 41.
        var draft = (await _planner.PlanAsync("Big week",
            [CreateTime("2026-10-29", 8m), CreatePto("2026-10-30", 8m), CreateTime("2026-10-27", 8.5m)], default)).Draft!;

        Assert.All(draft.Rows, r => Assert.True(Over40(r)));
    }

    [Fact]
    public async Task Exactly_40h_does_not_warn()
    {
        var draft = (await _planner.PlanAsync("Exactly", [CreateTime("2026-10-29", 8m), CreateTime("2026-10-30", 8m), CreateTime("2026-10-27", 7.5m)], default)).Draft!;

        Assert.All(draft.Rows, r => Assert.False(Over40(r)));
    }

    [Fact]
    public async Task Over_40h_is_per_7_day_chunk_in_two_week_period()
    {
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-1", Mon, new DateOnly(2026, 11, 8), false, false, false, null)];
        _pi.Add("te-20", "u-1", new DateOnly(2026, 10, 29), 9m);
        _pi.Add("te-21", "u-1", new DateOnly(2026, 10, 30), 9m); // week one: 16.5 + 18 = 34.5
        foreach (var (d, h) in new[] { (2, 8m), (3, 8m), (4, 8m), (5, 8m), (6, 3m) })
            _pi.Add($"te-3{d}", "u-1", new DateOnly(2026, 11, d), h); // week two: 35

        var draft = (await _planner.PlanAsync("Two weeks",
            [new() { Action = "update", TimeEntryId = "te-1", Hours = 2m }, CreateTime("2026-11-02", 1m)], default)).Draft!;

        var week = draft.Weeks.Single();
        Assert.Equal((Mon, new DateOnly(2026, 11, 8)), (week.Start, week.End));
        Assert.True(week.TotalAfter > 40m);
        Assert.All(draft.Rows, r => Assert.False(Over40(r)));
    }

    [Fact]
    public async Task Over_40h_warns_only_rows_in_the_heavy_chunk_of_a_two_week_period()
    {
        _pi.Sheets["u-1"] = [new PiTimeSheet("ts-1", Mon, new DateOnly(2026, 11, 8), false, false, false, null)];
        foreach (var (d, h) in new[] { (2, 8m), (3, 8m), (4, 8m), (5, 8m), (6, 8m) })
            _pi.Add($"te-3{d}", "u-1", new DateOnly(2026, 11, d), h); // week two: 40

        var draft = (await _planner.PlanAsync("Heavy second week",
            [CreateTime("2026-10-27", 1m), CreateTime("2026-11-03", 1m)], default)).Draft!;

        Assert.False(Over40(draft.Rows[0]));
        Assert.True(Over40(draft.Rows[1]));
    }

    [Fact]
    public async Task Over_8h_day_warns_only_pto_rows()
    {
        var pto = (await PlanPto(CreatePto("2026-10-26", 3m))).Draft!;
        var time = (await _planner.PlanAsync("Time", [CreateTime("2026-10-26", 3m)], default)).Draft!;

        Assert.Contains(pto.Rows[0].Warnings, w => w.Code == "over_8h_day");
        Assert.DoesNotContain(time.Rows[0].Warnings, w => w.Code == "over_8h_day");
    }

    [Fact]
    public async Task Over_8h_day_needs_more_than_8()
    {
        var draft = (await PlanPto(CreatePto("2026-10-27", 5.5m))).Draft!; // Tue 2.5 + 5.5 = 8

        Assert.DoesNotContain(draft.Rows[0].Warnings, w => w.Code == "over_8h_day");
    }

    [Fact]
    public async Task Pto_not_quarter_hour_warns_once()
    {
        var draft = (await PlanPto(CreatePto("2026-10-27", 1.3m))).Draft!;

        Assert.Single(draft.Rows[0].Warnings, w => w.Code == "not_quarter_hour");
    }

    [Fact]
    public async Task Pto_list_unreadable_for_time_only_person_is_skipped()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);
        _pi.PtoListThrows = true;

        var draft = (await _planner.PlanAsync("Time", [CreateTime("2026-10-27", 1m)], default)).Draft!;

        Assert.Equal(["u-1"], draft.PtoUnreadUsers);
        var ctx = Assert.Single(Day(draft, Wed).Context, c => c.IsPto == true);
        Assert.Null(ctx.TimeCodeName);
        Assert.Equal(12m, Day(draft, Wed).TotalBefore);
        // The fingerprint keeps the shape the commit will compute without PTO.
        Assert.Equal(Fingerprint.Compute(_pi.Entries.Where(e => e.Date == Tue), null), draft.DayFingerprints[Weeks.DayKey("u-1", Tue)]);
    }

    [Fact]
    public async Task Readable_pto_leaves_no_unread_users()
    {
        var draft = (await _planner.PlanAsync("Time", [CreateTime("2026-10-27", 1m)], default)).Draft!;

        Assert.Null(draft.PtoUnreadUsers);
    }

    [Fact]
    public async Task Pto_list_unreadable_with_pto_rows_fails()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);
        _pi.PtoListThrows = true;

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(CreatePto("2026-10-27", 1m)));
        Assert.Equal("pi_unavailable", ex.Code);
        var ex2 = await Assert.ThrowsAsync<TimeChangeException>(() => PlanPto(new() { Action = "delete_pto", PtoEntryId = "pto-1" }));
        Assert.Equal("pi_unavailable", ex2.Code);
    }

    [Fact]
    public async Task Each_users_pto_list_is_read_once_per_plan()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        await _planner.PlanAsync("Mixed", [CreatePto("2026-10-26", 1m), CreateTime("2026-10-27", 1m), new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 3m }], default);

        Assert.Equal(1, _pi.ListPtoCalls);
    }

    [Theory]
    [InlineData("update_pto")]
    [InlineData("delete_pto")]
    public async Task Approved_pto_update_or_delete_warns(string action)
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, approved: true);

        var row = (await PlanPto(new() { Action = action, PtoEntryId = "pto-1", Hours = action == "update_pto" ? 2m : null })).Draft!.Rows[0];

        var w = Assert.Single(row.Warnings, w => w.Code == "pto_approved");
        Assert.Contains("unapproved", w.Message);
    }

    [Fact]
    public async Task Unapproved_pto_has_no_approved_warning()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var row = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m })).Draft!.Rows[0];

        Assert.DoesNotContain(row.Warnings, w => w.Code == "pto_approved");
    }

    [Fact]
    public async Task A_day_with_pto_fingerprints_with_the_pto()
    {
        var pto = _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var draft = (await _planner.PlanAsync("Time", [CreateTime("2026-10-28", 1m)], default)).Draft!;

        Assert.Equal(Fingerprint.Compute(_pi.Entries.Where(e => e.Date == Wed), null, [pto]), draft.DayFingerprints[Weeks.DayKey("u-1", Wed)]);
        Assert.NotEqual(Fingerprint.Compute(_pi.Entries.Where(e => e.Date == Wed), null), draft.DayFingerprints[Weeks.DayKey("u-1", Wed)]);
    }

    [Fact]
    public async Task Create_pto_duplicating_an_existing_pto_warns()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "Floating");

        var draft = (await PlanPto(CreatePto("2026-10-28", 2m), new() { Action = "create_pto", Date = "2026-10-28", Hours = 1m, TimeCodeId = "tc-2" })).Draft!;

        Assert.Contains(draft.Rows[0].Warnings, w => w.Code == "likely_duplicate");
        Assert.DoesNotContain(draft.Rows[1].Warnings, w => w.Code == "likely_duplicate");
    }

    [Fact]
    public async Task Create_pto_duplicating_an_earlier_row_warns()
    {
        _pi.AddPto("pto-1", "u-1", Mon, 4m);

        var draft = (await PlanPto(CreatePto("2026-10-27", 2m), CreatePto("2026-10-27", 1m))).Draft!;

        Assert.DoesNotContain(draft.Rows[0].Warnings, w => w.Code == "likely_duplicate");
        Assert.Contains(draft.Rows[1].Warnings, w => w.Code == "likely_duplicate");
    }

    [Fact]
    public async Task Create_pto_over_a_pto_being_deleted_is_not_a_duplicate()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m);

        var draft = (await PlanPto(new() { Action = "delete_pto", PtoEntryId = "pto-1" }, CreatePto("2026-10-28", 3m))).Draft!;

        Assert.DoesNotContain(draft.Rows[1].Warnings, w => w.Code == "likely_duplicate");
    }

    [Fact]
    public async Task Create_pto_where_an_update_moves_a_pto_off_the_day_is_not_a_duplicate()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");

        var draft = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Date = "2026-10-29" }, CreatePto("2026-10-28", 2m))).Draft!;

        Assert.DoesNotContain(draft.Rows[1].Warnings, w => w.Code == "likely_duplicate");
    }

    [Fact]
    public async Task Create_pto_where_an_update_changes_a_ptos_code_is_not_a_duplicate()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");
        _pi.AddPto("pto-2", "u-1", Mon, 4m, "tc-2", "Floating");

        var draft = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", TimeCodeId = "tc-2" }, CreatePto("2026-10-28", 2m))).Draft!;

        Assert.DoesNotContain(draft.Rows[1].Warnings, w => w.Code == "likely_duplicate");
    }

    [Fact]
    public async Task Create_pto_where_an_update_only_changes_hours_is_still_a_duplicate()
    {
        _pi.AddPto("pto-1", "u-1", Wed, 4m, "tc-1", "PTO");

        var draft = (await PlanPto(new() { Action = "update_pto", PtoEntryId = "pto-1", Hours = 2m }, CreatePto("2026-10-28", 2m))).Draft!;

        Assert.Contains(draft.Rows[1].Warnings, w => w.Code == "likely_duplicate");
    }

    [Fact]
    public async Task A_time_create_is_not_a_duplicate_of_pto_hours()
    {
        _pi.AddPto("pto-1", "u-1", Mon, 4m);

        var draft = (await _planner.PlanAsync("Time", [CreateTime("2026-10-26", 4m)], default)).Draft!;

        Assert.DoesNotContain(draft.Rows[0].Warnings, w => w.Code == "likely_duplicate");
    }
}
