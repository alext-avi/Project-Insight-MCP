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
            new() { Action = "create", Date = "2026-10-27", Hours = 3m, TaskId = "t-4", User = "Jane" }
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
}
