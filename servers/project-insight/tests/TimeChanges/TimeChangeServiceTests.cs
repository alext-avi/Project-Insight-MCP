using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectInsightMCP.TimeChanges;
using Xunit;
using static ProjectInsightMCP.Tests.TimeChanges.TimeChangePlannerTests;

namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TimeChangeServiceTests : IDisposable
{
    private readonly TempDraftRoot _dir = new();
    private readonly ManualTimeProvider _time = new(Drafts.T0);
    private readonly FakeGateway _pi = Seeded();
    private readonly FileDraftStore _store;
    private readonly TimeChangeService _service;

    public TimeChangeServiceTests()
    {
        _store = new FileDraftStore(_dir.Root, _time);
        _service = new TimeChangeService(_pi, new TimeChangePlanner(_pi, _store, _time), new TimeChangeCommitter(_pi, _store, _time), _time);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Plan_response_carries_the_draft_the_token_and_readable_text()
    {
        var r = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);

        Assert.False(r.IsError);
        Assert.Equal("draft", r.View.Kind);
        Assert.Equal(DraftStatus.Draft, r.View.Draft!.Status);
        Assert.NotNull(r.ConfirmToken);
        Assert.Contains("Nothing has changed yet", r.Text);
        Assert.Contains("1. create · You · Tue 27 Oct · Acme Portal › Development · +4h (billable) — \"Order sync\"", r.Text);
        Assert.Contains("2. update · You · Mon 26 Oct · Acme Portal › Client calls · 1h → 1.5h", r.Text);
        Assert.Contains("3. delete · You · Tue 27 Oct · Acme Portal › Development · −2h", r.Text);
        Assert.DoesNotContain(r.ConfirmToken!, r.Text);
    }

    private static ChangeInput CreatePto(string date, decimal hours) =>
        new() { Action = "create_pto", Date = date, Hours = hours, TimeCodeId = "tc-1" };

    [Fact]
    public async Task Pto_draft_plans_commits_and_undoes_through_the_service()
    {
        // The planner names a time code from the person's PTO history; seed one outside the planned weeks, then drop it.
        _pi.AddPto("seed", "u-1", new DateOnly(2026, 10, 12), 1m);
        var plan = await _service.PlanAsync("Book PTO", [CreatePto("2026-10-26", 3m), CreatePto("2026-10-27", 1.75m)], default);

        Assert.False(plan.IsError, plan.Text);
        _pi.Pto.Clear();
        _pi.Entries.RemoveAll(e => e.IsPto);
        Assert.Contains("PTO · PTO · +3h", plan.Text);
        Assert.DoesNotContain("›", plan.Text);
        Assert.Contains(", PTO +4.75h", plan.Text);

        var commit = await _service.CommitAsync(plan.View.Draft!.Id, default);
        Assert.False(commit.IsError, commit.Text);
        Assert.Equal(DraftStatus.Committed, commit.View.Draft!.Status);
        Assert.Equal(2, _pi.Pto.Count);

        var undo = await _service.UndoAsync(commit.View.Draft.Id, default);
        Assert.False(undo.IsError, undo.Text);
        Assert.Equal(DraftStatus.Draft, undo.View.Draft!.Status);
        Assert.Contains("delete_pto · You · Mon 26 Oct · PTO · PTO · −3h", undo.Text);

        var undone = await _service.CommitAsync(undo.View.Draft.Id, default);
        Assert.False(undone.IsError, undone.Text);
        Assert.Equal(DraftStatus.Committed, undone.View.Draft!.Status);
        Assert.Empty(_pi.Pto);
        Assert.DoesNotContain(_pi.Entries, e => e.IsPto);
    }

    [Fact]
    public void Pto_row_reads_as_pto_with_time_code_and_summary_mentions_delta()
    {
        var pto = new DraftRow
        {
            Index = 0, Action = ChangeAction.UpdatePto, Kind = RowKind.Pto, UserId = "u-1", UserName = "Alex",
            Before = new EntryValues { TimeCodeId = "tc-1", TimeCodeName = "Vacation", Date = new DateOnly(2026, 10, 26), Hours = 2m, TaskId = "", ProjectId = "", ProjectName = "", TaskName = "" },
            After = new EntryValues { TimeCodeId = "tc-2", TimeCodeName = "Sick", Date = new DateOnly(2026, 10, 27), Hours = 3m, TaskId = "", ProjectId = "", ProjectName = "", TaskName = "" },
        };
        var view = new DraftView
        {
            Id = "d-1", Status = DraftStatus.Draft, Intent = "x", ProposedByName = "Alex", ProposedAt = Drafts.T0, ExpiresAt = Drafts.T0.AddHours(1), Rows = [pto],
            Weeks = [new WeekView { UserId = "u-1", UserName = "Alex", IsCaller = true, Start = new DateOnly(2026, 10, 25), End = new DateOnly(2026, 10, 31) }],
            Totals = new DraftTotals { Changes = 1, Days = 2, People = 1, NetHours = 1m, PtoDelta = 1m },
        };

        var text = TimeChangeText.For(new TimeChangesView { Kind = "draft", Draft = view }, new DateOnly(2026, 10, 27));

        Assert.Contains("1. update_pto · You · Tue 27 Oct · PTO · Sick · 2h → 3h; moved from Mon 26 Oct; was PTO · Vacation", text);
        Assert.Contains(", PTO +1h", text);
        Assert.DoesNotContain("›", text);
    }

    [Fact]
    public void A_pto_row_without_a_time_code_name_reads_as_just_pto()
    {
        static EntryValues Values(string code, decimal hours) => new() { TimeCodeId = code, Date = new DateOnly(2026, 10, 26), Hours = hours, TaskId = "", ProjectId = "", ProjectName = "", TaskName = "" };
        var pto = new DraftRow
        {
            Index = 0, Action = ChangeAction.UpdatePto, Kind = RowKind.Pto, UserId = "u-1", UserName = "Alex",
            Before = Values("tc-9", 2m), After = Values("tc-1", 3m)
        };
        var view = new DraftView
        {
            Id = "d-1", Status = DraftStatus.Draft, Intent = "x", ProposedByName = "Alex", ProposedAt = Drafts.T0, ExpiresAt = Drafts.T0.AddHours(1), Rows = [pto],
            Weeks = [new WeekView { UserId = "u-1", UserName = "Alex", IsCaller = true, Start = new DateOnly(2026, 10, 25), End = new DateOnly(2026, 10, 31) }],
            Totals = new DraftTotals { Changes = 1, Days = 1, People = 1, NetHours = 1m, PtoDelta = 1m },
        };

        var text = TimeChangeText.For(new TimeChangesView { Kind = "draft", Draft = view }, new DateOnly(2026, 10, 27));

        Assert.Contains("1. update_pto · You · Mon 26 Oct · PTO · 2h → 3h; was PTO", text);
        Assert.DoesNotContain("time off", text);
    }

    [Fact]
    public async Task Text_shows_the_year_of_a_date_outside_this_year()
    {
        var r = await _service.PlanAsync("Log", [new() { Action = "create", Date = "2025-10-27", Hours = 1m, TaskId = "t-5" }], default);

        Assert.Contains("1. create · You · Mon 27 Oct 2025 · Internal › Staff meeting · +1h", r.Text);
        Assert.Contains("[warning: Date is 365 days ago (Mon 27 Oct 2025)]", r.Text);
    }

    [Fact]
    public async Task Clarification_is_not_an_error_and_lists_candidates()
    {
        var r = await _service.PlanAsync("Log",
            [new() { Action = "create", Date = "2026-10-27", Hours = 1m, Project = "Acme", Task = "Development" }], default);

        Assert.False(r.IsError);
        Assert.Equal("needs_clarification", r.View.Kind);
        Assert.Null(r.ConfirmToken);
        Assert.Contains("No draft was created", r.Text);
        Assert.Contains("Acme Portal (p-1); Acme Support (p-2)", r.Text);
    }

    [Fact]
    public async Task Invalid_input_is_an_error_response()
    {
        var r = await _service.PlanAsync("", TypicalChanges(), default);

        Assert.True(r.IsError);
        Assert.Equal("error", r.View.Kind);
        Assert.Contains("intent", r.Text);
    }

    [Fact]
    public async Task Commit_text_offers_undo_until_the_window_closes()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var r = await _service.CommitAsync(plan.View.Draft!.Id, default);

        Assert.Equal(Drafts.T0.AddHours(24), r.View.Draft!.UndoUntil);
        Assert.Contains("Undo is available until 2026-10-28 09:00 UTC", r.Text);

        _time.Now = Drafts.T0.AddHours(25);
        Assert.Null((await _service.GetAsync(plan.View.Draft.Id, default)).View.Draft!.UndoUntil);
    }

    [Fact]
    public async Task Undo_drafts_the_reverse_and_can_only_happen_once()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var receiptId = plan.View.Draft!.Id;
        var commit = await _service.CommitAsync(receiptId, default);

        var undo = await _service.UndoAsync(receiptId, default);
        Assert.Equal(receiptId, undo.View.Draft!.UndoOf);
        Assert.NotNull(undo.View.Draft.UndoOfCommittedAt);
        Assert.Equal(commit.View.Draft!.CommittedAt, undo.View.Draft.UndoOfCommittedAt);
        Assert.NotNull(undo.ConfirmToken);
        Assert.StartsWith($"Undo draft (reverses {receiptId})", undo.Text);

        var confirmed = await _service.ConfirmAsync(undo.View.Draft.Id, undo.ConfirmToken!, default);
        Assert.Equal(DraftStatus.Committed, confirmed.View.Draft!.Status);
        Assert.Contains("It has since been undone", (await _service.GetAsync(receiptId, default)).Text);

        var again = await _service.UndoAsync(receiptId, default);
        Assert.True(again.IsError);
        Assert.Contains("already undone", again.Text);
    }

    [Fact]
    public async Task Undo_after_24_hours_is_refused()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        await _service.CommitAsync(plan.View.Draft!.Id, default);
        _time.Now = Drafts.T0.AddHours(24).AddMinutes(1);

        var r = await _service.UndoAsync(plan.View.Draft.Id, default);

        Assert.True(r.IsError);
        Assert.Contains("24 hours", r.Text);
    }

    [Fact]
    public async Task Unknown_ids_are_errors_and_get_never_returns_a_token()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);

        Assert.True((await _service.GetAsync("d_nope", default)).IsError);
        Assert.Null((await _service.GetAsync(plan.View.Draft!.Id, default)).ConfirmToken);
    }

    [Fact]
    public async Task Responses_never_contain_rates_or_money()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var commit = await _service.CommitAsync(plan.View.Draft!.Id, default);

        foreach (var r in new[] { plan, commit })
        {
            var json = JsonSerializer.Serialize(r.View, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + r.Text;
            foreach (var word in new[] { "rateBill", "rateBurden", "billableTotal", "actualTotal", "RateBill", "BillableTotal" })
                Assert.DoesNotContain(word, json);
        }
    }

    [Fact]
    public async Task Another_user_cannot_undo_a_receipt()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        await _service.CommitAsync(plan.View.Draft!.Id, default);

        _pi.Caller = new PiUser("u-2", "Jane Doe");
        var r = await _service.UndoAsync(plan.View.Draft.Id, default);

        Assert.True(r.IsError);
        Assert.Contains("No draft or receipt", r.Text);
        Assert.Single(Directory.EnumerateFiles(_dir.Root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_and_fully_reversed_text_says_nothing_changed_with_a_next_step()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        _pi.FailWrite = (op, id) => op == "delete" && id == "te-4" ? "Task is closed to time entry." : null;

        var r = await _service.CommitAsync(plan.View.Draft!.Id, default);

        Assert.Equal(DraftStatus.Failed, r.View.Draft!.Status);
        Assert.Contains("Nothing was changed", r.Text);
        Assert.Contains("every change already made was reversed", r.Text);
        Assert.Contains("Task is closed to time entry.", r.Text);
        Assert.Contains("draft the changes again", r.Text);
        Assert.DoesNotContain("Nothing was kept", r.Text);
    }

    [Fact]
    public async Task Failed_with_unreversed_changes_warns_and_never_says_nothing_changed()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        _pi.FailWrite = (op, _) => op == "delete" ? "Locked" : null;

        var r = await _service.CommitAsync(plan.View.Draft!.Id, default);

        Assert.Contains("could not be reversed", r.Text);
        Assert.Contains("partly changed", r.Text);
        Assert.Contains("check these entries in Project Insight", r.Text);
        Assert.DoesNotContain("Nothing was", r.Text);
    }

    [Fact]
    public async Task Failed_without_an_error_has_no_dangling_text()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        _pi.FailWrite = (op, id) => op == "delete" ? "x" : null;
        var failed = (await _service.CommitAsync(plan.View.Draft!.Id, default)).View.Draft!;
        var view = new TimeChangesView { Kind = "draft", Draft = DraftView.From(new Draft
        {
            Id = failed.Id, Status = DraftStatus.Failed, Intent = "i", ProposedBy = "u-1", ProposedByName = "A", ConfirmTokenHash = "h",
            ProposedAt = failed.ProposedAt, ExpiresAt = failed.ExpiresAt, Rows = [], Weeks = [], Totals = failed.Totals, Error = null
        }, Drafts.T0) };

        var first = TimeChangeText.For(view, DateOnly.FromDateTime(Drafts.T0.UtcDateTime)).Split('\n')[0];

        Assert.DoesNotContain(".  ", first);
        Assert.False(first.EndsWith(' '));
    }

    [Fact]
    public async Task Cancelled_text_has_a_next_step()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);

        var r = await _service.CancelAsync(plan.View.Draft!.Id, default);

        Assert.Contains("Nothing was changed", r.Text);
        Assert.Contains("Draft again if the user still wants these changes", r.Text);
    }

    [Fact]
    public async Task Failed_with_an_unanswered_write_says_pi_may_be_changed_and_names_the_day()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        _pi.UncertainWrite = (op, id) => op == "delete" && id == "te-4" ? "Internal error occurred The request timed out." : null;

        var r = await _service.CommitAsync(plan.View.Draft!.Id, default);

        Assert.True(r.View.Draft!.OutcomeUnknown);
        Assert.DoesNotContain("Nothing was", r.Text);
        Assert.DoesNotContain("draft the changes again if", r.Text);
        Assert.Contains("may be partly changed", r.Text);
        Assert.Contains("change 3 (You · Tue 27 Oct)", r.Text);
        Assert.Contains("Tell the user to check these entries in Project Insight.", r.Text);
    }

    [Fact]
    public async Task An_interrupted_commit_never_says_nothing_changed()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);
        var id = plan.View.Draft!.Id;
        await _store.TryTransitionAsync("u-1", id, DraftStatus.Draft, d => d.Status = DraftStatus.Committing);
        _time.Now = Drafts.T0.AddMinutes(11);

        var r = await _service.GetAsync(id, default);

        Assert.Equal(DraftStatus.Failed, r.View.Draft!.Status);
        Assert.True(r.View.Draft.OutcomeUnknown);
        Assert.DoesNotContain("Nothing was", r.Text);
        Assert.Contains("may be partly changed", r.Text);
        Assert.Contains("You · Mon 26 Oct", r.Text);
        Assert.Contains("You · Tue 27 Oct", r.Text);
        Assert.Contains("Tell the user to check these entries in Project Insight.", r.Text);
    }

    [Fact]
    public async Task Outcome_unknown_is_omitted_from_results_unless_true()
    {
        var plan = await _service.PlanAsync("Log Tuesday", TypicalChanges(), default);

        var json = JsonSerializer.Serialize(plan.View, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

        Assert.DoesNotContain("outcomeUnknown", json);
    }
}
