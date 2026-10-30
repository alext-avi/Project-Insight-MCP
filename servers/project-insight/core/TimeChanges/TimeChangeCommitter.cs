namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// Commits, cancels and loads drafts. PI has no transactions, so a commit checks that nothing
/// changed since planning, applies rows in order, and on the first failure reverses what it
/// applied, last first. Every status change goes through the store's locked transition.
/// </summary>
public sealed class TimeChangeCommitter(ITimeEntryGateway pi, IDraftStore store, TimeProvider time)
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Loads the caller's draft, first marking it expired, or failed if a commit was interrupted.</summary>
    public async Task<Draft> LoadAsync(PiUser caller, string id, CancellationToken ct)
    {
        var draft = await store.GetAsync(caller.Id, (id ?? "").Trim(), ct)
            ?? throw new TimeChangeException("not_found", $"No draft or receipt \"{id}\" for {caller.Name}.");
        var now = time.GetUtcNow();
        if (draft.Status == DraftStatus.Draft && now > draft.ExpiresAt)
            return await store.TryTransitionAsync(caller.Id, draft.Id, DraftStatus.Draft,
                d => SetStatus(d, DraftStatus.Expired, now), ct) ?? await LoadAsync(caller, draft.Id, ct);
        if (draft.Status == DraftStatus.Committing && now - draft.StatusChangedAt > StuckAfter)
            return await store.TryTransitionAsync(caller.Id, draft.Id, DraftStatus.Committing, d =>
            {
                SetStatus(d, DraftStatus.Failed, now);
                d.OutcomeUnknown = true;
                d.Error = "The commit was interrupted, so the outcome is unknown. Check these days in Project Insight: "
                          + string.Join(", ", ChangedDayLabels(d, Weeks.Today(time))) + ".";
            }, ct) ?? await LoadAsync(caller, draft.Id, ct);
        return draft;
    }

    public async Task<Draft> CommitAsync(PiUser caller, string draftId, string channel, string? confirmToken, CancellationToken ct)
    {
        var draft = await LoadAsync(caller, draftId, ct);
        // A confirm token is single-use, so it is checked before the early return below:
        // reusing it after a commit must fail, not quietly hand back the receipt.
        if (channel == CommitChannel.ConfirmedInChat &&
            (draft.ConfirmTokenUsed || confirmToken is null || !DraftIds.TokenMatches(confirmToken, draft.ConfirmTokenHash)))
            throw new TimeChangeException("bad_confirm_token",
                "That confirmation isn't valid for this draft. Confirm from the draft's own card, or ask the agent to commit it.");
        if (draft.Status != DraftStatus.Draft)
            return draft; // committed, stale, cancelled…: report it as it is

        var now = time.GetUtcNow();
        var claimed = await store.TryTransitionAsync(caller.Id, draft.Id, DraftStatus.Draft, d =>
        {
            SetStatus(d, DraftStatus.Committing, now);
            d.Channel = channel;
            if (channel == CommitChannel.ConfirmedInChat)
                d.ConfirmTokenUsed = true;
        }, ct);
        if (claimed is null)
            return await LoadAsync(caller, draft.Id, ct); // another process claimed it first

        // From here the draft is claimed. The request token must not cut a commit short: a half-applied
        // commit has to be rolled back and recorded, so every remaining step uses CancellationToken.None
        // (each PI call is bounded by the HTTP client's own timeout).
        List<string> stale;
        try
        {
            stale = await FindStaleDaysAsync(claimed, ct);
        }
        catch
        {
            // Nothing was written: hand the draft (and its confirm token) back so it can be committed again.
            await store.TryTransitionAsync(caller.Id, claimed.Id, DraftStatus.Committing, d =>
            {
                SetStatus(d, DraftStatus.Draft, time.GetUtcNow());
                d.Channel = null;
                d.ConfirmTokenUsed = false;
            }, CancellationToken.None);
            throw;
        }
        if (stale.Count > 0)
            return await FinishAsync(caller, claimed, d => { d.Status = DraftStatus.Stale; d.StaleDays = stale; });

        var applied = new List<DraftRow>();
        for (var i = 0; i < claimed.Rows.Count; i++)
        {
            var row = claimed.Rows[i];
            var result = await SafeAsync(() => ApplyAsync(claimed, row, CancellationToken.None));
            if (result.Success)
            {
                row.Outcome = RowOutcome.Applied;
                if (row.Action == ChangeAction.Create)
                    row.SavedId = result.SavedId;
                applied.Add(row);
                continue;
            }

            // No answer from PI (timeout, dropped connection) means the write may have been applied:
            // it can't be reversed or reported as rejected, only flagged for the user to check.
            var unknown = result.Uncertain;
            row.Outcome = unknown ? RowOutcome.Unknown : RowOutcome.Rejected;
            row.Error = result.Error;
            foreach (var later in claimed.Rows.Skip(i + 1))
                later.Outcome = RowOutcome.NotAttempted;
            for (var j = applied.Count - 1; j >= 0; j--)
            {
                var done = applied[j];
                var reversed = await SafeAsync(() => ReverseAsync(claimed, done, CancellationToken.None));
                done.Outcome = reversed.Success ? RowOutcome.Reversed : RowOutcome.ReverseFailed;
                if (!reversed.Success)
                    done.Error = reversed.Error;
                else if (done.Action == ChangeAction.Delete)
                    done.SavedId = reversed.SavedId; // recreated under a new id
            }
            var error = unknown
                ? UnansweredError(claimed, row, result.Error, Weeks.Today(time))
                : $"Project Insight rejected change {i + 1} of {claimed.Rows.Count}: {result.Error}";
            return await FinishAsync(caller, claimed, d =>
            {
                d.Status = DraftStatus.Failed;
                d.Error = error;
                d.OutcomeUnknown = unknown;
            });
        }

        var committed = await FinishAsync(caller, claimed, d =>
        {
            d.Status = DraftStatus.Committed;
            d.CommittedAt = time.GetUtcNow();
        });
        if (committed.UndoOf is { } receiptId)
        {
            try
            {
                await store.TryTransitionAsync(caller.Id, receiptId, DraftStatus.Committed, r => r.RevertedBy = committed.Id, CancellationToken.None);
            }
            catch (Exception)
            {
                // The undo is committed in PI; failing to note that on the old receipt must not turn it into
                // an error, which could prompt a second undo. The receipt then still offers undo, and a second
                // undo draft would show the entries as already reverted or flag likely duplicates.
            }
        }
        return committed;
    }

    public async Task<Draft> CancelAsync(PiUser caller, string draftId, CancellationToken ct)
    {
        var draft = await LoadAsync(caller, draftId, ct);
        if (draft.Status is not (DraftStatus.Draft or DraftStatus.Stale))
            return draft;
        var now = time.GetUtcNow();
        return await store.TryTransitionAsync(caller.Id, draft.Id, draft.Status, d => SetStatus(d, DraftStatus.Cancelled, now), ct)
            ?? await LoadAsync(caller, draft.Id, ct);
    }

    /// <summary>"You · Tue 27 Oct" for each day the draft changes, in date order.</summary>
    public static IEnumerable<string> ChangedDayLabels(Draft d, DateOnly today)
        => d.DayFingerprints.Keys.Order(StringComparer.Ordinal).Select(key =>
        {
            var (userId, date) = Weeks.ParseDayKey(key);
            var week = d.Weeks.FirstOrDefault(w => w.UserId == userId);
            return Weeks.DayLabel(week is null ? userId : week.IsCaller ? "You" : week.UserName, date, today);
        });

    private static string UnansweredError(Draft draft, DraftRow row, string? detail, DateOnly today)
    {
        var week = draft.Weeks.FirstOrDefault(w => w.UserId == row.UserId);
        var who = week is { IsCaller: true } ? "You" : row.UserName;
        var days = new[] { row.Before?.Date, row.After?.Date }.OfType<DateOnly>().Distinct().Order()
            .Select(date => Weeks.DayLabel(who, date, today)).ToList();
        return $"Project Insight didn't answer for change {row.Index + 1} ({string.Join(", ", days)}), so it may have been saved. " +
               $"Check {(days.Count == 1 ? "that day" : "those days")} in Project Insight." +
               (string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})");
    }

    private async Task<List<string>> FindStaleDaysAsync(Draft draft, CancellationToken ct)
    {
        var stale = new List<string>();
        var today = Weeks.Today(time);
        foreach (var week in draft.Weeks)
        {
            var days = draft.DayFingerprints.Keys
                .Select(k => (Key: k, Parsed: Weeks.ParseDayKey(k)))
                .Where(k => k.Parsed.UserId == week.UserId && week.Start <= k.Parsed.Date && k.Parsed.Date <= week.End)
                .Select(k => (k.Key, Date: k.Parsed.Date))
                .OrderBy(k => k.Date)
                .ToList();
            if (days.Count == 0)
                continue;
            var entries = await pi.SearchEntriesAsync(week.UserId, week.Start, week.End, ct);
            var sheets = await pi.SearchTimeSheetsAsync(week.UserId, week.Start, week.End, ct);
            foreach (var (key, date) in days)
            {
                var now = Fingerprint.Compute(entries.Where(e => e.Date == date), Weeks.Covering(sheets, date));
                if (now != draft.DayFingerprints[key])
                    stale.Add(Weeks.DayLabel(week.IsCaller ? "You" : week.UserName, date, today));
            }
        }
        return stale;
    }

    private Task<PiWrite> ApplyAsync(Draft draft, DraftRow row, CancellationToken ct) => row.Action switch
    {
        ChangeAction.Create => pi.CreateEntryAsync(Write(draft, row, row.After!), ct),
        ChangeAction.Update => pi.UpdateEntryAsync(row.TimeEntryId!, Write(draft, row, row.After!), ct),
        _ => pi.DeleteEntryAsync(row.TimeEntryId!, ct)
    };

    private Task<PiWrite> ReverseAsync(Draft draft, DraftRow row, CancellationToken ct) => row.Action switch
    {
        ChangeAction.Create => pi.DeleteEntryAsync(row.SavedId!, ct),
        ChangeAction.Update => pi.UpdateEntryAsync(row.TimeEntryId!, Write(draft, row, row.Before!), ct),
        _ => pi.CreateEntryAsync(Write(draft, row, row.Before!), ct)
    };

    private static EntryWrite Write(Draft draft, DraftRow row, EntryValues v)
        => new(row.UserId, v.TaskId, v.Date, v.Hours, v.Billable, v.Description,
            draft.Weeks.FirstOrDefault(w => w.UserId == row.UserId && w.Start <= v.Date && v.Date <= w.End)?.TimesheetId);

    private static async Task<PiWrite> SafeAsync(Func<Task<PiWrite>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex)
        {
            // A write that threw got no answer from PI, so it may have been applied.
            return new PiWrite(false, null, ProjectInsightTimeEntryGateway.Clip(ex.Message), Uncertain: true);
        }
    }

    private async Task<Draft> FinishAsync(PiUser caller, Draft working, Action<Draft> outcome)
    {
        var now = time.GetUtcNow();
        return await store.TryTransitionAsync(caller.Id, working.Id, DraftStatus.Committing, d =>
        {
            d.Rows = working.Rows;
            outcome(d);
            d.StatusChangedAt = now;
        }, CancellationToken.None) ?? throw new InvalidOperationException($"Draft {working.Id} left 'committing' while it was being committed.");
    }

    private static void SetStatus(Draft d, string status, DateTimeOffset now)
    {
        d.Status = status;
        d.StatusChangedAt = now;
    }
}
