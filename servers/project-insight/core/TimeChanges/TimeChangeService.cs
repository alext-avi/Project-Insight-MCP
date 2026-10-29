namespace ProjectInsightMCP.TimeChanges;

/// <summary>What every time-change tool returns as structuredContent (one schema for all six).</summary>
public sealed class TimeChangesView
{
    /// <summary>"draft" (a draft or receipt is in Draft), "needs_clarification" or "error".</summary>
    public required string Kind { get; init; }
    public string? Message { get; init; }
    public DraftView? Draft { get; init; }
    public List<Clarification>? Clarifications { get; init; }
}

/// <summary>A draft as the card and agent see it: no token hash, no fingerprints.</summary>
public sealed class DraftView
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public required string Intent { get; init; }
    public required string ProposedByName { get; init; }
    public required DateTimeOffset ProposedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public string? Channel { get; init; }
    public string? UndoOf { get; init; }
    /// <summary>For an undo draft: when the receipt it reverses was committed.</summary>
    public DateTimeOffset? UndoOfCommittedAt { get; init; }
    public string? RevertedBy { get; init; }
    /// <summary>Until when an undo can be drafted; null when it can't.</summary>
    public DateTimeOffset? UndoUntil { get; init; }
    public required List<DraftRow> Rows { get; init; }
    public required List<WeekView> Weeks { get; init; }
    public required DraftTotals Totals { get; init; }
    public string? Error { get; init; }
    /// <summary>True (else omitted) when a failed commit may have left unreversed changes in PI.</summary>
    public bool? OutcomeUnknown { get; init; }
    public List<string>? StaleDays { get; init; }

    public static DraftView From(Draft d, DateTimeOffset now) => new()
    {
        Id = d.Id,
        Status = d.Status,
        Intent = d.Intent,
        ProposedByName = d.ProposedByName,
        ProposedAt = d.ProposedAt,
        ExpiresAt = d.ExpiresAt,
        CommittedAt = d.CommittedAt,
        Channel = d.Channel,
        UndoOf = d.UndoOf,
        UndoOfCommittedAt = d.UndoOfCommittedAt,
        RevertedBy = d.RevertedBy,
        UndoUntil = d.Status == DraftStatus.Committed && d.RevertedBy is null && d.CommittedAt is { } at
                    && now <= at + TimeChangeService.UndoWindow
            ? at + TimeChangeService.UndoWindow
            : null,
        Rows = d.Rows,
        Weeks = d.Weeks,
        Totals = d.Totals,
        Error = d.Error,
        OutcomeUnknown = d.OutcomeUnknown ? true : null,
        StaleDays = d.StaleDays.Count > 0 ? d.StaleDays : null
    };
}

public sealed class TimeChangeResponse
{
    public required TimeChangesView View { get; init; }
    public required string Text { get; init; }
    public bool IsError { get; init; }
    /// <summary>Plan and undo only. Goes in the tool result's _meta, for the card, never in text.</summary>
    public string? ConfirmToken { get; init; }
}

/// <summary>The operations behind the six tools. Domain errors become error responses, not exceptions.</summary>
public sealed class TimeChangeService(ITimeEntryGateway pi, TimeChangePlanner planner, TimeChangeCommitter committer, TimeProvider time)
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(24);

    public Task<TimeChangeResponse> PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct)
        => Run(async () => FromPlan(await planner.PlanAsync(intent, changes, ct)));

    public Task<TimeChangeResponse> CommitAsync(string draftId, CancellationToken ct)
        => Run(async () => FromDraft(await committer.CommitAsync(
            await pi.GetCurrentUserAsync(ct), draftId, CommitChannel.Agent, null, ct)));

    public Task<TimeChangeResponse> ConfirmAsync(string draftId, string confirmToken, CancellationToken ct)
        => Run(async () => FromDraft(await committer.CommitAsync(
            await pi.GetCurrentUserAsync(ct), draftId, CommitChannel.ConfirmedInChat, confirmToken, ct)));

    public Task<TimeChangeResponse> CancelAsync(string draftId, CancellationToken ct)
        => Run(async () => FromDraft(await committer.CancelAsync(await pi.GetCurrentUserAsync(ct), draftId, ct)));

    public Task<TimeChangeResponse> GetAsync(string id, CancellationToken ct)
        => Run(async () => FromDraft(await committer.LoadAsync(await pi.GetCurrentUserAsync(ct), id, ct)));

    public Task<TimeChangeResponse> UndoAsync(string receiptId, CancellationToken ct) => Run(async () =>
    {
        var receipt = await committer.LoadAsync(await pi.GetCurrentUserAsync(ct), receiptId, ct);
        if (receipt.Status != DraftStatus.Committed)
            throw new TimeChangeException("undo_unavailable",
                $"{receipt.Id} is {receipt.Status}, not committed, so there is nothing to undo.");
        if (receipt.RevertedBy is not null)
            throw new TimeChangeException("undo_unavailable", $"{receipt.Id} was already undone by {receipt.RevertedBy}.");
        if (receipt.CommittedAt is not { } at || time.GetUtcNow() > at + UndoWindow)
            throw new TimeChangeException("undo_unavailable",
                $"Undo for {receipt.Id} was available for 24 hours after it was committed, and that has passed.");
        return FromPlan(await planner.PlanUndoAsync(receipt, ct));
    });

    private static async Task<TimeChangeResponse> Run(Func<Task<TimeChangeResponse>> action)
    {
        try
        {
            return await action();
        }
        catch (TimeChangeException ex)
        {
            return Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error($"Something went wrong: {ex.Message}");
        }

        static TimeChangeResponse Error(string message)
        {
            var view = new TimeChangesView { Kind = "error", Message = message };
            return new TimeChangeResponse { View = view, Text = TimeChangeText.For(view), IsError = true };
        }
    }

    private TimeChangeResponse FromPlan(PlanResult plan)
    {
        if (plan.Draft is null)
        {
            var view = new TimeChangesView { Kind = "needs_clarification", Message = "No draft was created.", Clarifications = plan.Clarifications };
            return new TimeChangeResponse { View = view, Text = TimeChangeText.For(view) };
        }
        var drafted = FromDraft(plan.Draft);
        return new TimeChangeResponse { View = drafted.View, Text = drafted.Text, ConfirmToken = plan.ConfirmToken };
    }

    private TimeChangeResponse FromDraft(Draft draft)
    {
        var view = new TimeChangesView { Kind = "draft", Draft = DraftView.From(draft, time.GetUtcNow()) };
        return new TimeChangeResponse { View = view, Text = TimeChangeText.For(view) };
    }
}
