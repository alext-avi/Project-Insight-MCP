using System.Globalization;

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
public sealed class TimeChangePlanner(ITimeEntryGateway pi, IDraftStore store, TimeProvider time)
{
    public const int MaxChanges = 100;
    public const int MaxIntentLength = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Task<PlanResult> PlanAsync(string intent, IReadOnlyList<ChangeInput>? changes, CancellationToken ct)
        => PlanCoreAsync(intent, changes, undoOf: null, undoOfCommittedAt: null, ct);

    /// <summary>A new draft that reverses a committed receipt's rows.</summary>
    public Task<PlanResult> PlanUndoAsync(Draft receipt, CancellationToken ct)
    {
        if (receipt.Status != DraftStatus.Committed)
            throw new TimeChangeException("not_undoable", "Only committed changes can be undone.");
        var changes = receipt.Rows.Where(r => r.Outcome is null or RowOutcome.Applied).Select(r => r.Action switch
        {
            ChangeAction.Create => new ChangeInput { Action = ChangeAction.Delete, TimeEntryId = r.SavedId },
            ChangeAction.Update => new ChangeInput
            {
                Action = ChangeAction.Update, TimeEntryId = r.TimeEntryId, TaskId = r.Before!.TaskId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Billable = r.Before.Billable, Description = r.Before.Description ?? ""
            },
            _ => new ChangeInput
            {
                Action = ChangeAction.Create, User = r.UserId, TaskId = r.Before!.TaskId,
                Date = r.Before.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Hours = r.Before.Hours,
                Billable = r.Before.Billable, Description = r.Before.Description
            }
        }).ToList();
        // Undoing an undo puts the original changes back, so call it a redo rather than "Undo: Undo: …".
        var intent = receipt.UndoOf is not null && receipt.Intent.StartsWith("Undo: ", StringComparison.Ordinal)
            ? "Redo: " + receipt.Intent["Undo: ".Length..]
            : $"Undo: {receipt.Intent}";
        return PlanCoreAsync(intent, changes, receipt.Id, receipt.CommittedAt, ct);
    }

    private sealed class WeekData
    {
        public required PiUser User { get; init; }
        public required DateOnly Start { get; init; }
        public required DateOnly End { get; init; }
        public PiTimeSheet? Sheet { get; init; }
        public required IReadOnlyList<PiEntry> Entries { get; init; }
        public bool Contains(string userId, DateOnly date) => User.Id == userId && Start <= date && date <= End;
    }

    private async Task<PlanResult> PlanCoreAsync(string intent, IReadOnlyList<ChangeInput>? changes, string? undoOf, DateTimeOffset? undoOfCommittedAt, CancellationToken ct)
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
            if (await BuildRowAsync(i, changes[i], caller, resolver, clarifications, seen, isUndo: undoOf is not null, ct) is { } row)
                rows.Add(row);
        }

        if (clarifications.Count > 0)
            return new PlanResult { Clarifications = clarifications };
        if (rows.Count == 0)
            throw new TimeChangeException("nothing_to_change", "None of these changes would change anything.");
        for (var i = 0; i < rows.Count; i++)
            rows[i].Index = i;

        var weeks = await LoadWeeksAsync(rows, ct);
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
            Rows = rows
        };
        BuildViews(draft, weeks, caller);
        await AddAvatarsAsync(draft, ct);

        await store.CreateAsync(draft, ct);
        try
        {
            await store.PruneAsync(caller.Id, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping; never fail a plan over it.
        }
        return new PlanResult { Draft = draft, ConfirmToken = token };
    }

    private async Task<DraftRow?> BuildRowAsync(int i, ChangeInput c, PiUser caller, TimeChangeResolver resolver,
        List<Clarification> clarifications, HashSet<string> seen, bool isUndo, CancellationToken ct)
    {
        var n = i + 1;
        var action = (c.Action ?? "").Trim().ToLowerInvariant();
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
            throw Invalid(n, "action must be create, update or delete.");
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

    /// <summary>
    /// PI's own look for each person on the card: their photo, else their initials and colours.
    /// Cosmetic, so a failure here leaves the card on its fallback initials and never fails the plan.
    /// </summary>
    private async Task AddAvatarsAsync(Draft draft, CancellationToken ct)
    {
        try
        {
            var looks = await pi.GetUserLooksAsync(draft.Weeks.Select(w => w.UserId).Distinct().ToList(), ct);
            var photos = new Dictionary<string, string?>();
            foreach (var url in looks.Values.Select(l => l.PhotoUrl).OfType<string>().Distinct())
                photos[url] = await pi.GetPhotoAsync(url, ct);
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

    private async Task<List<WeekData>> LoadWeeksAsync(List<DraftRow> rows, CancellationToken ct)
    {
        var weeks = new List<WeekData>();
        var touched = rows
            .SelectMany(r => new[] { r.Before?.Date, r.After?.Date }.OfType<DateOnly>().Select(d => (r.UserId, r.UserName, Date: d)))
            .Distinct()
            .OrderBy(t => t.Date);
        foreach (var (userId, userName, date) in touched)
        {
            if (weeks.Any(w => w.Contains(userId, date)))
                continue;
            var sheet = Weeks.Covering(await pi.SearchTimeSheetsAsync(userId, date, date, ct), date);
            var start = sheet?.Start ?? Weeks.MondayOf(date);
            var end = sheet?.End ?? Weeks.SundayOf(date);
            weeks.Add(new WeekData
            {
                User = new PiUser(userId, userName), Start = start, End = end, Sheet = sheet,
                Entries = await pi.SearchEntriesAsync(userId, start, end, ct)
            });
        }
        return weeks;
    }

    private static void BuildViews(Draft draft, List<WeekData> weeks, PiUser caller)
    {
        var rows = draft.Rows;
        var touchedIds = rows.Select(r => r.TimeEntryId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                var after = before
                    - here.Where(r => r.Before?.Date == date).Sum(r => r.Before!.Hours)
                    + here.Where(r => r.After?.Date == date).Sum(r => r.After!.Hours);
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if (here.Count == 0 && weekend && before == 0)
                    continue;

                view.Days.Add(new DayView
                {
                    Date = date, TotalBefore = before, TotalAfter = after, HasChanges = here.Count > 0,
                    RowIndexes = here.Select(r => r.Index).ToList(),
                    Context = existing.Where(e => !touchedIds.Contains(e.Id)).Select(e => new ContextEntry
                    {
                        TimeEntryId = e.Id, ProjectName = e.ProjectName, ProjectColor = e.ProjectColor, TaskName = e.TaskName,
                        Hours = e.Hours, Billable = e.Billable, Description = e.Description
                    }).ToList()
                });
                if (here.Count > 0)
                    draft.DayFingerprints[Weeks.DayKey(w.User.Id, date)] = Fingerprint.Compute(existing, w.Sheet);
            }
            view.TotalBefore = view.Days.Sum(d => d.TotalBefore);
            view.TotalAfter = view.Days.Sum(d => d.TotalAfter);
            draft.Weeks.Add(view);
        }

        AddWarnings(draft, weeks, caller);

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
            Warnings = rows.Sum(r => r.Warnings.Count)
        };
    }

    private static void AddWarnings(Draft draft, List<WeekData> weeks, PiUser caller)
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
            }
            if (row.Action == ChangeAction.Create && IsLikelyDuplicate(row, draft.Rows, week.Entries))
                Warn(row, WarningCode.LikelyDuplicate, "Looks like a duplicate of an entry already on this day");
            if (row.UserId != caller.Id)
                Warn(row, WarningCode.OtherPerson, $"Changes {row.UserName}'s time");
        }
    }

    private static bool IsLikelyDuplicate(DraftRow row, List<DraftRow> rows, IReadOnlyList<PiEntry> weekEntries)
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
