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
    private readonly TimeChangeService _service;

    public TimeChangeServiceTests()
    {
        var store = new FileDraftStore(_dir.Root, _time);
        _service = new TimeChangeService(_pi, new TimeChangePlanner(_pi, store, _time), new TimeChangeCommitter(_pi, store, _time), _time);
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
}
