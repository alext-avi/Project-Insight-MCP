using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProjectInsightMCP.TimeChanges;

public sealed class PlanResult
{
    public Draft? Draft { get; init; }
    public string? ConfirmToken { get; init; }
    public List<Clarification> Clarifications { get; init; } = new();
}

/// <summary>
/// Builds drafts: resolves names, reads each affected person-week once, and works out context,
/// totals, warnings and day fingerprints. Reads from PI; never writes to it.
/// </summary>
public sealed class TimeChangePlanner(ITimeEntryGateway pi, IDraftStore store, TimeProvider time, ILogger<TimeChangePlanner>? logger = null)
{
    public const int MaxChanges = 100;
    public const int MaxIntentLength = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Task<PlanResult> PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct)
        => PlanCoreAsync(intent, changes, undoOf: null, undoOfCommittedAt: null, restoredCodes: null, ct);

    /// <summary>A new draft that reverses a committed receipt's rows.</summary>
    public Task<PlanResult> PlanUndoAsync(Draft receipt, CancellationToken ct)
    {
        if (receipt.Status != DraftStatus.Committed)
            throw new TimeChangeException("not_undoable", "Only committed changes can be undone.");
        var undone = receipt.Rows
            .Where(r => r.Outcome is null or RowOutcome.Applied)
            .Where(r => !(r.Action == ChangeAction.CreatePto && r.SavedId is null)) // can't be addressed, so can't be undone
            .ToList();
        // The code a PTO update or delete restores may be on none of the person's PTO records any more, so the
        // resolver couldn't name it. The receipt still knows its name.
        var restoredCodes = undone
            .Select(r => r.Action is ChangeAction.UpdatePto or ChangeAction.DeletePto && r.Before?.TimeCodeId is { } id
                ? new PiTimeCode(id, r.Before.TimeCodeName ?? id)
                : null)
            .ToList();
        var changes = undone
            .Select(r => r.Action switch
        {
            ChangeAction.Create => new ChangeInput { Action = ChangeAction.Delete, TimeEntryId = r.SavedId },
            ChangeAction.Update => new ChangeInput
            {
                Action = ChangeAction.Update, TimeEntryId = r.TimeEntryId, TaskId = r.Before!.TaskId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Billable = r.Before.Billable, Description = r.Before.Description ?? ""
            },
            ChangeAction.CreatePto => new ChangeInput { Action = ChangeAction.DeletePto, PtoEntryId = r.SavedId },
            ChangeAction.UpdatePto => new ChangeInput
            {
                Action = ChangeAction.UpdatePto, PtoEntryId = r.PtoEntryId, TimeCodeId = r.Before!.TimeCodeId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Description = r.Before.Description ?? ""
            },
            ChangeAction.DeletePto => new ChangeInput
            {
                Action = ChangeAction.CreatePto, User = r.UserId, TimeCodeId = r.Before!.TimeCodeId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Description = r.Before.Description
            },
            _ => new ChangeInput
            {
                Action = ChangeAction.Create, User = r.UserId, TaskId = r.Before!.TaskId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Billable = r.Before.Billable, Description = r.Before.Description
            }
        }).ToList();
        // Undoing an undo puts the original changes back, so it's a redo; undoing a redo is an undo again.
        // Labels go by kind, never stacking: X → "Undo: X" → "Redo: X" → "Undo: X".
        var isRedo = receipt.UndoOf is not null && receipt.Intent.StartsWith(UndoPrefix, StringComparison.Ordinal);
        var intent = (isRedo ? RedoPrefix : UndoPrefix) + WithoutUndoPrefix(receipt.Intent);
        return PlanCoreAsync(intent, changes, receipt.Id, receipt.CommittedAt, restoredCodes, ct);
    }

    private const string UndoPrefix = "Undo: ", RedoPrefix = "Redo: ";

    private static string WithoutUndoPrefix(string intent)
        => intent.StartsWith(UndoPrefix, StringComparison.Ordinal) ? intent[UndoPrefix.Length..]
            : intent.StartsWith(RedoPrefix, StringComparison.Ordinal) ? intent[RedoPrefix.Length..]
            : intent;

    private sealed class WeekData
    {
        public required PiUser User { get; init; }
        public required DateOnly Start { get; init; }
        public required DateOnly End { get; init; }
        public PiTimeSheet? Sheet { get; init; }
        public required IReadOnlyList<PiEntry> Entries { get; init; }
        /// <summary>The person's PTO records dated in this week. Empty when their PTO list couldn't be read.</summary>
        public IReadOnlyList<PiPto> Pto { get; init; } = [];
        public bool Contains(string userId, DateOnly date) => User.Id == userId && Start <= date && date <= End;
    }

    /// <param name="restoredCodes">Undo only, one per change: the PTO time code (id and name) the change puts back,
    /// taken from the receipt, so it's named and not flagged unknown when no PTO record uses it any more.</param>
    private async Task<PlanResult> PlanCoreAsync(string intent, IReadOnlyList<ChangeInput>? changes, string? undoOf, DateTimeOffset? undoOfCommittedAt,
        IReadOnlyList<PiTimeCode?>? restoredCodes, CancellationToken ct)
    {
        intent = (intent ?? "").Trim();
        if (intent.Length == 0)
            throw new TimeChangeException("invalid_change", "Give a one-line intent saying what these changes are for.");
        if (intent.Length > MaxIntentLength)
            intent = intent[..MaxIntentLength];
        if (changes is null || changes.Count == 0)
            throw new TimeChangeException("invalid_change", "Give at least one change.");
        if (changes.Count > MaxChanges)
            throw new TimeChangeException("too_many_changes",
                $"A draft holds at most {MaxChanges} changes; this one has {changes.Count}. Split it into several drafts.");

        var caller = await pi.GetCurrentUserAsync(ct);
        var resolver = new TimeChangeResolver(pi);
        var clarifications = new List<Clarification>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<DraftRow>();
        for (var i = 0; i < changes.Count; i++)
        {
            if (await BuildRowAsync(i, changes[i], caller, resolver, clarifications, seen, isUndo: undoOf is not null, restoredCodes?[i], ct) is { } row)
                rows.Add(row);
        }

        if (clarifications.Count > 0)
            return new PlanResult { Clarifications = clarifications };
        if (rows.Count == 0)
            throw new TimeChangeException("nothing_to_change", "None of these changes would change anything.");
        for (var i = 0; i < rows.Count; i++)
            rows[i].Index = i;

        var (weeks, ptoUnread) = await LoadWeeksAsync(rows, resolver, ct);
        var now = time.GetUtcNow();
        var token = DraftIds.NewConfirmToken();
        var draft = new Draft
        {
            Id = DraftIds.NewDraftId(),
            Status = DraftStatus.Draft,
            Intent = intent,
            ProposedBy = caller.Id,
            ProposedByName = caller.Name,
            ProposedAt = now,
            ExpiresAt = now + Lifetime,
            StatusChangedAt = now,
            UndoOf = undoOf,
            UndoOfCommittedAt = undoOfCommittedAt,
            ConfirmTokenHash = DraftIds.Hash(token),
            Rows = rows,
            FingerprintVersion = Draft.CurrentFingerprintVersion,
            PtoUnreadUsers = ptoUnread.Count > 0 ? ptoUnread.Order(StringComparer.Ordinal).ToList() : null
        };
        BuildViews(draft, weeks, caller, Weeks.Today(time));
        await AddAvatarsAsync(draft, ct);

        await store.CreateAsync(draft, ct);
        try
        {
            await store.PruneAsync(caller.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pruning is housekeeping, and the draft is already saved: never fail a plan over it.
            (logger ?? NullLogger<TimeChangePlanner>.Instance).LogWarning("Pruning old drafts after a plan failed: {Error}", ex.GetType().Name);
        }
        return new PlanResult { Draft = draft, ConfirmToken = token };
    }

    private async Task<DraftRow?> BuildRowAsync(int i, ChangeInput c, PiUser caller, TimeChangeResolver resolver,
        List<Clarification> clarifications, HashSet<string> seen, bool isUndo, PiTimeCode? restoredCode, CancellationToken ct)
    {
        var n = i + 1;
        var action = (c.Action ?? "").Trim().ToLowerInvariant();
        if (!ChangeAction.IsPto(action) && (c.PtoEntryId is not null || c.TimeCode is not null || c.TimeCodeId is not null))
            throw Invalid(n, $"{action} takes no ptoEntryId, timeCode or timeCodeId. Those are for create_pto, update_pto and delete_pto.");
        if (ChangeAction.IsPto(action))
            return await BuildPtoRowAsync(i, action, c, caller, resolver, clarifications, seen, isUndo, restoredCode, ct);
        if (action == ChangeAction.Create)
        {
            var date = ParseDate(n, c.Date);
            var hours = ParseHours(n, c.Hours, isUndo);
            var user = await resolver.UserAsync(i, c.User, caller, ct);
            var task = await resolver.TaskAsync(i, c.TaskId, c.Project, c.Task, ct);
            if (user.Clarification is { } uc) clarifications.Add(uc);
            if (task.Clarification is { } tc) clarifications.Add(tc);
            if (user.Value is null || task.Value is null)
                return null;
            return new DraftRow
            {
                Index = i, Action = action, UserId = user.Value.Id, UserName = user.Value.Name,
                After = new EntryValues
                {
                    TaskId = task.Value.Id, TaskName = task.Value.Name, ProjectId = task.Value.ProjectId,
                    ProjectName = task.Value.ProjectName, ProjectColor = task.Value.ProjectColor, Date = date, Hours = hours,
                    Billable = c.Billable ?? false, Description = Blank(c.Description)
                }
            };
        }

        if (action is not (ChangeAction.Update or ChangeAction.Delete))
            throw Invalid(n, "action must be create, update or delete, or create_pto, update_pto or delete_pto.");
        if (string.IsNullOrWhiteSpace(c.TimeEntryId))
            throw Invalid(n, $"{action} needs timeEntryId.");
        var id = c.TimeEntryId.Trim();
        if (!seen.Add(id))
            throw Invalid(n, $"time entry {id} appears in more than one change. Combine them into one change.");

        var entry = await pi.GetEntryAsync(id, ct);
        if (entry is null)
        {
            clarifications.Add(new Clarification { ChangeIndex = i, Field = "timeEntryId", Query = id, Message = $"No time entry with id {id}." });
            return null;
        }
        // Editing a PTO's linked entry directly would leave the PTO and its entry disagreeing. The entry's shape (no
        // task) says so, and so does the owner's PTO list, which names the PTO entry when it can be read.
        var linkedPto = await LinkedPtoAsync(entry, resolver, ct);
        if (entry.IsPto || linkedPto is not null)
            throw Invalid(n, $"time entry {id} belongs to a PTO entry. Use {action}_pto with " +
                             (linkedPto is null ? "the PTO entry's id" : $"PTO entry {linkedPto.Id}") + " instead.");
        var owner = entry.UserId == caller.Id
            ? caller
            : await resolver.UserByIdAsync(entry.UserId, ct) ?? new PiUser(entry.UserId, entry.UserId);
        var before = new EntryValues
        {
            TaskId = entry.TaskId, TaskName = entry.TaskName, ProjectId = entry.ProjectId, ProjectName = entry.ProjectName,
            ProjectColor = entry.ProjectColor, Date = entry.Date, Hours = entry.Hours, Billable = entry.Billable, Description = Blank(entry.Description)
        };
        if (action == ChangeAction.Delete)
            return new DraftRow { Index = i, Action = action, UserId = owner.Id, UserName = owner.Name, TimeEntryId = id, Before = before };

        if (!string.IsNullOrWhiteSpace(c.User) && !isUndo)
            throw Invalid(n, "an update can't change whose time it is. Delete the entry and create a new one for the other person.");
        var after = before.Clone();
        if (c.Date is not null) after.Date = ParseDate(n, c.Date);
        if (c.Hours is not null) after.Hours = ParseHours(n, c.Hours, isUndo);
        if (c.Billable is not null) after.Billable = c.Billable.Value;
        if (c.Description is not null) after.Description = Blank(c.Description);
        if (c.TaskId is not null || c.Project is not null || c.Task is not null)
        {
            var task = await resolver.TaskAsync(i, c.TaskId, c.Project, c.Task, ct);
            if (task.Clarification is { } tc)
            {
                clarifications.Add(tc);
                return null;
            }
            after.TaskId = task.Value!.Id;
            after.TaskName = task.Value.Name;
            after.ProjectId = task.Value.ProjectId;
            after.ProjectName = task.Value.ProjectName;
            after.ProjectColor = task.Value.ProjectColor;
        }
        if (Same(before, after))
        {
            if (isUndo)
                return null; // already back to how it was
            throw Invalid(n, $"this update changes nothing on time entry {id}.");
        }
        return new DraftRow { Index = i, Action = action, UserId = owner.Id, UserName = owner.Name, TimeEntryId = id, Before = before, After = after };
    }

    private async Task<DraftRow?> BuildPtoRowAsync(int i, string action, ChangeInput c, PiUser caller, TimeChangeResolver resolver,
        List<Clarification> clarifications, HashSet<string> seen, bool isUndo, PiTimeCode? restoredCode, CancellationToken ct)
    {
        var n = i + 1;
        if (c.TimeEntryId is not null)
            throw Invalid(n, $"{action} takes no timeEntryId. Use ptoEntryId to name a PTO entry.");
        if (c.Billable is not null || c.TaskId is not null || c.Project is not null || c.Task is not null)
            throw Invalid(n, $"{action} takes no task, project or billable. PTO has a time code instead.");
        if (action == ChangeAction.CreatePto && c.PtoEntryId is not null)
            throw Invalid(n, "create_pto takes no ptoEntryId. Project Insight gives the new PTO entry its id; use update_pto to change an existing one.");
        if (action == ChangeAction.DeletePto &&
            (c.Date is not null || c.Hours is not null || c.Description is not null || c.TimeCode is not null || c.TimeCodeId is not null || c.User is not null))
            throw Invalid(n, "delete_pto takes only ptoEntryId.");

        if (action == ChangeAction.CreatePto)
        {
            var date = ParseDate(n, c.Date);
            var hours = ParseHours(n, c.Hours, isUndo);
            var user = await resolver.UserAsync(i, c.User, caller, ct);
            if (user.Clarification is { } uc)
            {
                clarifications.Add(uc);
                return null;
            }
            var code = await resolver.TimeCodeAsync(i, user.Value!.Id, c.TimeCodeId, c.TimeCode, restoredCode, ct);
            if (code.Clarification is { } cc)
            {
                clarifications.Add(cc);
                return null;
            }
            var row = new DraftRow
            {
                Index = i, Action = action, Kind = RowKind.Pto, UserId = user.Value.Id, UserName = user.Value.Name,
                After = PtoValues(code.Value!, date, hours, Blank(c.Description))
            };
            if (code.Unknown)
                Warn(row, WarningCode.UnknownTimeCode, $"Time code {code.Value!.Id} isn't one this person has used for PTO");
            return row;
        }

        if (string.IsNullOrWhiteSpace(c.PtoEntryId))
            throw Invalid(n, $"{action} needs ptoEntryId.");
        var id = c.PtoEntryId.Trim();
        if (!seen.Add("pto:" + id))
            throw Invalid(n, $"PTO entry {id} appears in more than one change. Combine them into one change.");
        var pto = await pi.GetPtoAsync(id, ct);
        if (pto is null)
        {
            clarifications.Add(new Clarification { ChangeIndex = i, Field = "ptoEntryId", Query = id, Message = $"No PTO entry with id {id}." });
            return null;
        }
        var owner = pto.UserId == caller.Id
            ? caller
            : await resolver.UserByIdAsync(pto.UserId, ct) ?? new PiUser(pto.UserId, pto.UserId);
        var before = PtoValues(new PiTimeCode(pto.TimeCodeId, pto.TimeCodeName ?? pto.TimeCodeId), pto.Date, pto.Hours, Blank(pto.Description));
        DraftRow Row(EntryValues? after) => new()
        {
            Index = i, Action = action, Kind = RowKind.Pto, UserId = owner.Id, UserName = owner.Name, PtoEntryId = id,
            LinkedTimeEntryId = pto.TimeEntryId, PtoApproved = pto.Approved ? true : null, Before = before, After = after
        };
        if (action == ChangeAction.DeletePto)
            return Row(null);

        if (!string.IsNullOrWhiteSpace(c.User) && !isUndo)
            throw Invalid(n, "an update can't change whose PTO it is. Delete the entry and create a new one for the other person.");
        var after = before.Clone();
        if (c.Date is not null) after.Date = ParseDate(n, c.Date);
        if (c.Hours is not null) after.Hours = ParseHours(n, c.Hours, isUndo);
        if (c.Description is not null) after.Description = Blank(c.Description);
        string? unknownCode = null;
        if (c.TimeCodeId is not null || c.TimeCode is not null)
        {
            var code = await resolver.TimeCodeAsync(i, owner.Id, c.TimeCodeId, c.TimeCode, restoredCode, ct);
            if (code.Clarification is { } cc)
            {
                clarifications.Add(cc);
                return null;
            }
            after.TimeCodeId = code.Value!.Id;
            after.TimeCodeName = code.Value.Name;
            if (code.Unknown) unknownCode = code.Value.Id;
        }
        if (after.Date == before.Date && after.Hours == before.Hours && TimeChangeResolver.SameId(after.TimeCodeId, before.TimeCodeId) &&
            (after.Description ?? "") == (before.Description ?? ""))
        {
            if (isUndo)
                return null;
            throw Invalid(n, $"this update changes nothing on PTO entry {id}.");
        }
        var updated = Row(after);
        if (unknownCode is { } unknown)
            Warn(updated, WarningCode.UnknownTimeCode, $"Time code {unknown} isn't one this person has used for PTO");
        return updated;
    }

    /// <summary>
    /// The PTO record linked to a time entry, from the owner's PTO list (read once per plan). Null when none links to
    /// it, or when the list can't be read; the guard then goes by the entry's shape alone.
    /// </summary>
    private static async Task<PiPto?> LinkedPtoAsync(PiEntry entry, TimeChangeResolver resolver, CancellationToken ct)
    {
        try
        {
            return (await resolver.PtoAsync(entry.UserId, ct)).FirstOrDefault(p => TimeChangeResolver.SameId(p.TimeEntryId, entry.Id));
        }
        catch (TimeChangeException e) when (e.Code == "pi_unavailable")
        {
            // A cancelled request reads as a failed one; it's a cancellation, never a reason to plan without PTO.
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>A PTO row's values. PTO has no task or project, so those stay empty.</summary>
    private static EntryValues PtoValues(PiTimeCode code, DateOnly date, decimal hours, string? description) => new()
    {
        TaskId = "", TaskName = "", ProjectId = "", ProjectName = "", Date = date, Hours = hours, Billable = false,
        Description = description, TimeCodeId = code.Id, TimeCodeName = code.Name
    };

    /// <summary>
    /// PI's own look for each person on the card: their photo, else their initials and colours.
    /// Cosmetic, so a failure here leaves the card on its fallback initials and never fails the plan.
    /// </summary>
    private async Task AddAvatarsAsync(Draft draft, CancellationToken ct)
    {
        try
        {
            var looks = await pi.GetUserLooksAsync(draft.Weeks.Select(w => w.UserId).Distinct().ToList(), ct);
            var urls = looks.Values.Select(l => l.PhotoUrl).OfType<string>().Distinct().ToList();
            var fetched = await Task.WhenAll(urls.Select(url => pi.GetPhotoAsync(url, ct)));
            var photos = urls.Zip(fetched).ToDictionary(p => p.First, p => p.Second);
            foreach (var week in draft.Weeks)
            {
                if (!looks.TryGetValue(week.UserId, out var look))
                    continue;
                week.Avatar = new UserAvatar
                {
                    Initials = look.Initials, Background = look.Background, Foreground = look.Foreground,
                    Photo = look.PhotoUrl is { } url ? photos[url] : null
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leave the avatars out.
        }
    }

    /// <summary>How far back to look for a timesheet to copy the period shape from.</summary>
    public const int PeriodLookbackDays = 26 * 7;

    /// <summary>The longest period <see cref="Weeks.PeriodLike"/> makes up (two weeks).</summary>
    private const int MaxPeriodDays = 14;

    private async Task<(List<WeekData> Weeks, HashSet<string> PtoUnread)> LoadWeeksAsync(List<DraftRow> rows, TimeChangeResolver resolver, CancellationToken ct)
    {
        var weeks = new List<WeekData>();
        var ptoUnread = new HashSet<string>();
        var ptoByUser = new Dictionary<string, IReadOnlyList<PiPto>>();
        var usersWithPtoRows = rows.Where(r => r.Kind == RowKind.Pto).Select(r => r.UserId).ToHashSet();
        var touched = rows
            .SelectMany(r => new[] { r.Before?.Date, r.After?.Date }.OfType<DateOnly>().Select(d => (r.UserId, r.UserName, Date: d)))
            .Distinct()
            .OrderBy(t => t.Date)
            .ToList();
        // One timesheet read per person: the periods covering their dates, plus earlier ones to take the
        // period shape from when no timesheet exists yet for a date (PI creates them on first submit).
        // The read also runs a period past the last date, so a made-up period can be kept clear of a real
        // timesheet that starts after it.
        var sheetsByUser = new Dictionary<string, IReadOnlyList<PiTimeSheet>>();
        foreach (var (userId, userName, date) in touched)
        {
            if (!sheetsByUser.TryGetValue(userId, out var sheets))
            {
                var dates = touched.Where(t => t.UserId == userId).Select(t => t.Date).ToList();
                sheets = await pi.SearchTimeSheetsAsync(userId, dates.Min().AddDays(-PeriodLookbackDays), dates.Max().AddDays(MaxPeriodDays - 1), ct);
                sheetsByUser[userId] = sheets;
            }
            if (!ptoByUser.TryGetValue(userId, out var pto))
            {
                try
                {
                    pto = await resolver.PtoAsync(userId, ct);
                }
                catch (TimeChangeException e) when (e.Code == "pi_unavailable" && !usersWithPtoRows.Contains(userId))
                {
                    // A cancelled request reads as a failed one; it's a cancellation, never a reason to plan without PTO.
                    ct.ThrowIfCancellationRequested();
                    // Someone may edit another person's time without being allowed to read their PTO.
                    // A time-only draft still works, with their PTO shown as plain PTO and left out of the fingerprint.
                    pto = [];
                    ptoUnread.Add(userId);
                }
                ptoByUser[userId] = pto;
            }
            // A date a real timesheet covers belongs in that timesheet's week, never a made-up one.
            var sheet = Weeks.Covering(sheets, date);
            if (weeks.Any(w => w.Contains(userId, date) && (sheet is null || w.Sheet == sheet)))
                continue;
            var (start, end) = sheet is not null ? (sheet.Start, sheet.End) : Weeks.PeriodLike(Weeks.LatestFrom(sheets, date), date, sheets);
            weeks.Add(new WeekData
            {
                User = new PiUser(userId, userName), Start = start, End = end, Sheet = sheet,
                Entries = await pi.SearchEntriesAsync(userId, start, end, ct),
                Pto = pto.Where(p => start <= p.Date && p.Date <= end).ToList()
            });
        }
        return (weeks, ptoUnread);
    }

    private static void BuildViews(Draft draft, List<WeekData> weeks, PiUser caller, DateOnly today)
    {
        var rows = draft.Rows;
        // A PTO row's linked time entry is shown through the row, not as greyed context.
        var touchedIds = rows.SelectMany(r => new[] { r.TimeEntryId, r.LinkedTimeEntryId }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var w in weeks.OrderBy(w => w.User.Id == caller.Id ? 0 : 1).ThenBy(w => w.User.Name).ThenBy(w => w.Start))
        {
            var view = new WeekView
            {
                UserId = w.User.Id, UserName = w.User.Name, IsCaller = w.User.Id == caller.Id, Start = w.Start, End = w.End,
                TimesheetId = w.Sheet?.Id, TimesheetUrl = w.Sheet?.Url,
                Submitted = w.Sheet?.Submitted == true, Approved = w.Sheet?.Approved == true, Locked = w.Sheet?.Locked == true
            };
            foreach (var date in Weeks.Range(w.Start, w.End))
            {
                var existing = w.Entries.Where(e => e.Date == date).ToList();
                var here = rows.Where(r => r.UserId == w.User.Id && (r.Before?.Date == date || r.After?.Date == date)).ToList();
                var before = existing.Sum(e => e.Hours);
                // A PTO's hours are in the day's total through its linked entry, so a PTO change takes off what that
                // entry counted, which can differ from the PTO record's hours if the entry was edited in PI. PI's
                // search can lag behind that entry, and then there is nothing to take away.
                var after = before
                    - here.Where(r => r.Before?.Date == date)
                        .Sum(r => r.Kind != RowKind.Pto
                            ? r.Before!.Hours
                            : existing.FirstOrDefault(e => TimeChangeResolver.SameId(e.Id, r.LinkedTimeEntryId))?.Hours ?? 0m)
                    + here.Where(r => r.After?.Date == date).Sum(r => r.After!.Hours);
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if (here.Count == 0 && weekend && before == 0)
                    continue;

                view.Days.Add(new DayView
                {
                    Date = date, TotalBefore = before, TotalAfter = after, HasChanges = here.Count > 0,
                    RowIndexes = here.Select(r => r.Index).ToList(),
                    Context = existing.Where(e => !touchedIds.Contains(e.Id)).Select(e => ToContext(e, w.Pto)).ToList()
                });
                if (here.Count > 0)
                    draft.DayFingerprints[Weeks.DayKey(w.User.Id, date)] = Fingerprint.Compute(existing, w.Sheet, w.Pto.Where(p => p.Date == date));
            }
            view.TotalBefore = view.Days.Sum(d => d.TotalBefore);
            view.TotalAfter = view.Days.Sum(d => d.TotalAfter);
            draft.Weeks.Add(view);
        }

        AddWarnings(draft, weeks, caller, today);

        var befores = rows.Select(r => r.Before).OfType<EntryValues>().ToList();
        var afters = rows.Select(r => r.After).OfType<EntryValues>().ToList();
        draft.Totals = new DraftTotals
        {
            NetHours = afters.Sum(a => a.Hours) - befores.Sum(b => b.Hours),
            BillableDelta = afters.Where(a => a.Billable).Sum(a => a.Hours) - befores.Where(b => b.Billable).Sum(b => b.Hours),
            NonBillableDelta = afters.Where(a => !a.Billable).Sum(a => a.Hours) - befores.Where(b => !b.Billable).Sum(b => b.Hours),
            Changes = rows.Count,
            Days = draft.DayFingerprints.Count,
            People = rows.Select(r => r.UserId).Distinct().Count(),
            Warnings = rows.Sum(r => r.Warnings.Count),
            PtoDelta = rows.Where(r => r.Kind == RowKind.Pto).Sum(r => (r.After?.Hours ?? 0m) - (r.Before?.Hours ?? 0m))
        };
    }

    /// <summary>An existing entry as greyed context. PTO's linked entries read as PTO · time code, not as a task.</summary>
    private static ContextEntry ToContext(PiEntry e, IReadOnlyList<PiPto> weekPto)
    {
        var context = new ContextEntry
        {
            TimeEntryId = e.Id, ProjectName = e.ProjectName, ProjectColor = e.ProjectColor, TaskName = e.TaskName,
            Hours = e.Hours, Billable = e.Billable, Description = e.Description
        };
        var pto = weekPto.FirstOrDefault(p => string.Equals(p.TimeEntryId, e.Id, StringComparison.OrdinalIgnoreCase));
        if (pto is not null || e.IsPto)
        {
            context.IsPto = true;
            context.TimeCodeName = pto is null ? null : string.IsNullOrWhiteSpace(pto.TimeCodeName) ? pto.TimeCodeId : pto.TimeCodeName;
        }
        return context;
    }

    /// <summary>A row dated further back or ahead than this is flagged as a likely wrong date or year.</summary>
    public const int FarPastDays = 31, FarFutureDays = 7;

    private static void AddWarnings(Draft draft, List<WeekData> weeks, PiUser caller, DateOnly today)
    {
        foreach (var row in draft.Rows)
        {
            var date = (row.After ?? row.Before)!.Date;
            var week = weeks.First(w => w.Contains(row.UserId, date));
            var day = draft.Weeks.First(v => v.UserId == row.UserId && v.Start == week.Start).Days.First(d => d.Date == date);

            var sheets = new[] { row.Before?.Date, row.After?.Date }.OfType<DateOnly>()
                .Select(d => weeks.First(w => w.Contains(row.UserId, d)).Sheet).OfType<PiTimeSheet>().ToList();
            if (sheets.Any(t => t.Locked)) Warn(row, WarningCode.TimesheetLocked, "Timesheet is locked");
            else if (sheets.Any(t => t.Approved)) Warn(row, WarningCode.TimesheetApproved, "Timesheet already approved");
            else if (sheets.Any(t => t.Submitted)) Warn(row, WarningCode.TimesheetSubmitted, "Timesheet already submitted");

            if (row.After is { } after)
            {
                if (day.TotalAfter > 12m)
                    Warn(row, WarningCode.Over12Hours, $"Day goes over 12h ({Weeks.FormatHours(day.TotalAfter)}h)");
                if (after.Hours * 4 != decimal.Truncate(after.Hours * 4))
                    Warn(row, WarningCode.NotQuarterHour, $"Not a whole quarter-hour ({Weeks.FormatHours(after.Hours)}h)");
                if (row.Kind == RowKind.Pto && day.TotalAfter > 8m)
                    Warn(row, WarningCode.Over8HoursPtoDay, $"PTO day goes over 8h ({Weeks.FormatHours(day.TotalAfter)}h)");
            }
            if (WeekHoursOver40(draft.Weeks.First(v => v.UserId == row.UserId && v.Start == week.Start), date) is { } weekHours)
                Warn(row, WarningCode.Over40HoursWeek, $"Week goes over 40h ({Weeks.FormatHours(weekHours)}h)");
            if (row.PtoApproved == true)
                Warn(row, WarningCode.PtoApproved,
                    "This PTO was already approved. PI may refuse the change or clear the approval. Undoing a delete recreates the PTO unapproved.");
            if (row.Action is ChangeAction.Create or ChangeAction.CreatePto && IsLikelyDuplicate(row, draft.Rows, week))
                Warn(row, WarningCode.LikelyDuplicate, "Looks like a duplicate of an entry already on this day");
            if (row.UserId != caller.Id)
                Warn(row, WarningCode.OtherPerson, $"Changes {row.UserName}'s time");
            // An undo restores what was there, however long ago, so only new dates are checked.
            if (draft.UndoOf is null)
            {
                var days = date.DayNumber - today.DayNumber;
                var full = date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
                if (days < -FarPastDays)
                    Warn(row, WarningCode.FarDate, $"Date is {-days} days ago ({full})");
                else if (days > FarFutureDays)
                    Warn(row, WarningCode.FarDate, $"Date is {days} days in the future ({full})");
            }
        }
    }

    /// <summary>
    /// The after-total of the 7-day span (counted from the period's start) holding <paramref name="date"/>
    /// when it's more than 40h, else null. A span cut short by the period's end is checked over the days it has.
    /// </summary>
    private static decimal? WeekHoursOver40(WeekView view, DateOnly date)
    {
        var chunkStart = view.Start.AddDays((date.DayNumber - view.Start.DayNumber) / 7 * 7);
        var total = view.Days.Where(d => d.Date >= chunkStart && d.Date < chunkStart.AddDays(7)).Sum(d => d.TotalAfter);
        return total > 40m ? total : null;
    }

    private static bool IsLikelyDuplicate(DraftRow row, List<DraftRow> rows, WeekData week)
        => row.Kind == RowKind.Pto ? IsLikelyDuplicatePto(row, rows, week) : IsLikelyDuplicateTime(row, rows, week.Entries);

    /// <summary>
    /// Same person, date and time code as an existing PTO record or an earlier create_pto row. A record this draft
    /// deletes, or moves off that date or to another code, doesn't count.
    /// </summary>
    private static bool IsLikelyDuplicatePto(DraftRow row, List<DraftRow> rows, WeekData week)
    {
        var a = row.After!;
        var goingAway = rows
            .Where(r => r.Action == ChangeAction.DeletePto ||
                        r.Action == ChangeAction.UpdatePto && (r.After!.Date != r.Before!.Date || !TimeChangeResolver.SameId(r.After.TimeCodeId, r.Before.TimeCodeId)))
            .Select(r => r.PtoEntryId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return week.Pto.Any(p => p.Date == a.Date && TimeChangeResolver.SameId(p.TimeCodeId, a.TimeCodeId) && !goingAway.Contains(p.Id)) ||
               rows.Any(r => r.Index < row.Index && r.Action == ChangeAction.CreatePto && r.UserId == row.UserId &&
                             r.After!.Date == a.Date && TimeChangeResolver.SameId(r.After.TimeCodeId, a.TimeCodeId));
    }

    private static bool IsLikelyDuplicateTime(DraftRow row, List<DraftRow> rows, IReadOnlyList<PiEntry> weekEntries)
    {
        var a = row.After!;
        var deleted = rows.Where(r => r.Action == ChangeAction.Delete).Select(r => r.TimeEntryId).ToHashSet();
        var matchesExisting = weekEntries.Any(e =>
            e.Date == a.Date && e.TaskId == a.TaskId && !deleted.Contains(e.Id) &&
            (e.Hours == a.Hours || SameText(e.Description, a.Description)));
        var matchesEarlierRow = rows.Any(r =>
            r.Index < row.Index && r.Action == ChangeAction.Create && r.UserId == row.UserId &&
            r.After!.Date == a.Date && r.After.TaskId == a.TaskId && r.After.Hours == a.Hours);
        return matchesExisting || matchesEarlierRow;
    }

    private static void Warn(DraftRow row, string code, string message) => row.Warnings.Add(new DraftWarning { Code = code, Message = message });

    private static bool SameText(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Same(EntryValues a, EntryValues b)
        => a.TaskId == b.TaskId && a.Date == b.Date && a.Hours == b.Hours && a.Billable == b.Billable &&
           (a.Description ?? "") == (b.Description ?? "");

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateOnly ParseDate(int n, string? value)
        => DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw Invalid(n, "date must be yyyy-MM-dd.");

    /// <summary>
    /// An undo restores an entry's earlier hours, which PI may hold as 0; anything the agent asks for
    /// must be more than 0.
    /// </summary>
    private static decimal ParseHours(int n, decimal? value, bool isUndo)
        => value is > 0m and <= 24m || (isUndo && value == 0m)
            ? value!.Value
            : throw Invalid(n, "hours must be more than 0 and at most 24.");

    private static TimeChangeException Invalid(int n, string message) => new("invalid_change", $"Change {n}: {message}");
}
