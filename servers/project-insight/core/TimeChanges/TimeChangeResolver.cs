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
    public sealed record Resolved<T>(T? Value, Clarification? Clarification) where T : class;

    private IReadOnlyList<PiNamed>? _projects;
    private readonly Dictionary<string, IReadOnlyList<PiNamed>> _tasks = new();
    private readonly Dictionary<string, PiUser?> _users = new();

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

    private static Clarification Clarify(int index, string field, string query, string message, IEnumerable<Candidate>? candidates = null)
        => new() { ChangeIndex = index, Field = field, Query = query, Message = message, Candidates = candidates?.ToList() ?? new() };
}
