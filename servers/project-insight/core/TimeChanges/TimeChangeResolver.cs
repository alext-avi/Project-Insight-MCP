namespace ProjectInsightMCP.TimeChanges;

public static class NameMatch
{
    public sealed record Result<T>(T? Match, IReadOnlyList<T> Candidates) where T : class;

    /// <summary>
    /// A unique case-insensitive exact name wins; otherwise a unique partial match; otherwise no
    /// match, with up to five candidates (shortest names first).
    /// </summary>
    public static Result<T> Find<T>(IEnumerable<T> items, Func<T, string> name, string query) where T : class
    {
        var q = query.Trim();
        var list = items.ToList();
        var exact = list.Where(i => string.Equals(name(i).Trim(), q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return new(exact[0], exact);
        if (exact.Count > 1)
            return new(null, exact.Take(5).ToList());

        var partial = list.Where(i => name(i).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => name(i).Length).ThenBy(name, StringComparer.OrdinalIgnoreCase).ToList();
        return partial.Count == 1 ? new(partial[0], partial) : new(null, partial.Take(5).ToList());
    }
}

/// <summary>Turns the names an agent gives into PI ids. One instance per plan (it caches lookups).</summary>
public sealed class TimeChangeResolver(ITimeEntryGateway pi)
{
    /// <param name="Unknown">A time code id taken as given because this person has no PTO using it.</param>
    public sealed record Resolved<T>(T? Value, Clarification? Clarification, bool Unknown = false) where T : class;

    private IReadOnlyList<PiNamed>? _projects;
    private readonly Dictionary<string, IReadOnlyList<PiNamed>> _tasks = new();
    private readonly Dictionary<string, PiUser?> _users = new();
    private readonly Dictionary<string, PiPtoList> _pto = new();

    public async Task<Resolved<PiUser>> UserAsync(int index, string? user, PiUser caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user))
            return new(caller, null);
        var query = user.Trim();
        if (query == caller.Id)
            return new(caller, null);
        if (await TryUserByIdAsync(query, ct) is { } byId)
            return new(byId, null);

        // Whose time it is must not be a guess, so a person needs their exact name; a unique partial
        // match is only offered back. Tasks and projects keep partial matching.
        var match = NameMatch.Find(await pi.FindUsersAsync(query, ct), u => u.Name, query);
        if (match.Match is { } m && string.Equals(m.Name.Trim(), query, StringComparison.OrdinalIgnoreCase))
            return new(m, null);
        return new(null, Clarify(index, "user", query,
            match.Match is { } partial
                ? $"No user named \"{query}\" exactly; did you mean {partial.Name}?"
                : match.Candidates.Count == 0
                    ? $"No active user matches \"{query}\"."
                    : $"{match.Candidates.Count} users match \"{query}\". Use one of their ids.",
            match.Candidates.Select(u => new Candidate { Id = u.Id, Name = u.Name })));
    }

    // The query may be a name, which PI can reject as an id with a non-404 error; treat that as
    // "not an id" and let the name search run (it throws on its own if PI is really down).
    private async Task<PiUser?> TryUserByIdAsync(string query, CancellationToken ct)
    {
        try
        {
            return await UserByIdAsync(query, ct);
        }
        catch (TimeChangeException e) when (e.Code == "pi_unavailable")
        {
            return null;
        }
    }

    public async Task<PiUser?> UserByIdAsync(string userId, CancellationToken ct)
    {
        if (!_users.TryGetValue(userId, out var user))
            _users[userId] = user = await pi.GetUserAsync(userId, ct);
        return user;
    }

    public async Task<Resolved<PiTask>> TaskAsync(int index, string? taskId, string? project, string? task, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var byId = await pi.GetTaskAsync(taskId.Trim(), ct);
            return byId is not null
                ? new(byId, null)
                : new(null, Clarify(index, "taskId", taskId, $"No task with id {taskId}."));
        }
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(task))
            return new(null, Clarify(index, "task", task ?? project ?? "", "Give taskId, or both project and task names."));

        _projects ??= await pi.ListOpenProjectsAsync(ct);
        var p = NameMatch.Find(_projects, x => x.Name, project);
        if (p.Match is null)
            return new(null, Clarify(index, "project", project,
                p.Candidates.Count == 0
                    ? $"No open project matches \"{project}\"."
                    : $"{p.Candidates.Count} projects match \"{project}\". Use the exact name or a taskId.",
                p.Candidates.Select(c => new Candidate { Id = c.Id, Name = c.Name })));

        if (!_tasks.TryGetValue(p.Match.Id, out var tasks))
            _tasks[p.Match.Id] = tasks = await pi.ListTasksAsync(p.Match.Id, ct);
        var t = NameMatch.Find(tasks, x => x.Name, task);
        if (t.Match is null)
            return new(null, Clarify(index, "task", task,
                t.Candidates.Count == 0
                    ? $"No task in {p.Match.Name} matches \"{task}\"."
                    : $"{t.Candidates.Count} tasks in {p.Match.Name} match \"{task}\". Use the exact name or a taskId.",
                t.Candidates.Select(c => new Candidate { Id = c.Id, Name = c.Name })));

        return new(new PiTask(t.Match.Id, t.Match.Name, p.Match.Id, p.Match.Name), null);
    }

    /// <summary>A person's PTO records, from their PTO list read once per plan.</summary>
    public async Task<IReadOnlyList<PiPto>> PtoAsync(string userId, CancellationToken ct)
        => (await PtoListAsync(userId, ct)).Records;

    /// <summary>
    /// A person's PTO list, read once per plan for both their records and their time codes. A failed read isn't
    /// cached, so it throws to every caller.
    /// </summary>
    private async Task<PiPtoList> PtoListAsync(string userId, CancellationToken ct)
    {
        if (!_pto.TryGetValue(userId, out var list))
            _pto[userId] = list = await pi.ReadPtoAsync(userId, ct);
        return list;
    }

    /// <summary>
    /// A PTO time code: the id as given (flagged unknown when this person has never used it), else an exact
    /// name, else a unique partial name. PI has no time-code list, so the known codes are the ones on this
    /// person's PTO records.
    /// </summary>
    /// <param name="restored">Undo only: the code (with the name a receipt recorded) the change puts back. An id
    /// matching it that's on none of this person's records is taken with that name, not flagged unknown.</param>
    public async Task<Resolved<PiTimeCode>> TimeCodeAsync(int index, string userId, string? timeCodeId, string? timeCode,
        PiTimeCode? restored, CancellationToken ct)
    {
        var known = (await PtoListAsync(userId, ct)).TimeCodes;
        if (!string.IsNullOrWhiteSpace(timeCodeId))
        {
            var id = timeCodeId.Trim();
            // PI's ids are GUIDs, which an agent may give in either case; keep PI's own spelling.
            if (known.FirstOrDefault(c => SameId(c.Id, id)) is { } byId)
            {
                if (!string.IsNullOrWhiteSpace(timeCode) && !NameFits(known, byId, timeCode.Trim()))
                    throw new TimeChangeException("invalid_change",
                        $"Change {index + 1}: timeCode \"{timeCode.Trim()}\" and timeCodeId {byId.Id} (\"{byId.Name.Trim()}\") name different " +
                        "time codes. Give the one you mean, or just the timeCodeId.");
                return new(byId, null);
            }
            if (restored is not null && SameId(restored.Id, id))
                return new(restored, null);
            return new(new PiTimeCode(id, id), null, Unknown: true);
        }
        if (string.IsNullOrWhiteSpace(timeCode))
            return new(null, Clarify(index, "timeCode", "", "Give timeCode or timeCodeId.", CodeCandidates(known)));

        // An exact name wins, else a partial one. Among several matches, the only active one wins: PI keeps old,
        // inactive codes on past records, often under the same name as their replacement.
        var query = timeCode.Trim();
        var exact = known.Where(c => string.Equals(c.Name.Trim(), query, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : known.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
            return new(matches[0], null);
        if (matches.Count(c => c.IsActive) == 1)
            return new(matches.Single(c => c.IsActive), null);
        if (matches.Count == 0)
            return new(null, Clarify(index, "timeCode", query, $"No PTO time code matches \"{query}\".", CodeCandidates(known)));
        var sameName = matches.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return new(null, Clarify(index, "timeCode", query,
            sameName
                ? $"{matches.Count} PTO time codes are named \"{matches[0].Name.Trim()}\". Use the timeCodeId of the one you mean."
                : $"{matches.Count} PTO time codes match \"{query}\". Use the exact name or a timeCodeId.",
            CodeCandidates(matches)));
    }

    /// <summary>
    /// Whether a time code name, given alongside an id, could mean that id's code. As name resolution: when some code
    /// has the name exactly (ignoring case) the id's code must be one of those; otherwise its name must contain it. The
    /// id settles which of several codes a name fits, so the match needn't be unique.
    /// </summary>
    private static bool NameFits(IReadOnlyList<PiTimeCode> known, PiTimeCode code, string query)
    {
        static bool Exact(PiTimeCode c, string q) => string.Equals(c.Name.Trim(), q, StringComparison.OrdinalIgnoreCase);
        return known.Any(c => Exact(c, query)) ? Exact(code, query) : code.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Candidate> CodeCandidates(IEnumerable<PiTimeCode> codes)
        => codes.Select(c => new Candidate { Id = c.Id, Name = c.IsActive ? c.Name : c.Name + " (inactive)" });

    /// <summary>Whether two PI ids (GUIDs) are the same, ignoring case.</summary>
    public static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Clarification Clarify(int index, string field, string query, string message, IEnumerable<Candidate>? candidates = null)
        => new() { ChangeIndex = index, Field = field, Query = query, Message = message, Candidates = candidates?.ToList() ?? new() };
}
