using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Tests.TimeChanges;

/// <summary>In-memory Project Insight for time-change tests.</summary>
public sealed class FakeGateway : ITimeEntryGateway
{
    public PiUser Caller { get; set; } = new("u-1", "Alex Example");

    public List<PiUser> Users { get; } = [new("u-1", "Alex Example"), new("u-2", "Jane Doe")];

    public List<PiNamed> Projects { get; } =
        [new("p-1", "Acme Portal"), new("p-2", "Acme Support"), new("p-3", "Internal")];

    public Dictionary<string, List<PiNamed>> Tasks { get; } = new()
    {
        ["p-1"] = [new("t-1", "Development"), new("t-2", "Client calls"), new("t-3", "Design review")],
        ["p-2"] = [new("t-4", "Development")],
        ["p-3"] = [new("t-5", "Staff meeting"), new("t-6", "Email & admin")]
    };

    public List<PiEntry> Entries { get; } = new();

    /// <summary>Timesheets per user id.</summary>
    public Dictionary<string, List<PiTimeSheet>> Sheets { get; } = new();

    /// <summary>Successful writes, in order: "create:te-100", "update:te-1", "delete:te-4".</summary>
    public List<string> Writes { get; } = new();

    /// <summary>Given (operation, entry id or null for create), returns an error to fail that write.</summary>
    public Func<string, string?, string?>? FailWrite { get; set; }

    /// <summary>Every SearchEntriesAsync call, in order, so tests can assert each person-week is read once.</summary>
    public List<(string UserId, DateOnly Start, DateOnly End)> SearchEntriesCalls { get; } = new();

    /// <summary>Every SearchTimeSheetsAsync call, in order.</summary>
    public List<(string UserId, DateOnly Start, DateOnly End)> SearchTimeSheetsCalls { get; } = new();

    /// <summary>When true, GetUserAsync for an id not in Users throws pi_unavailable (PI rejecting a name as an id).</summary>
    public bool UnknownUserIdThrows { get; set; }

    /// <summary>Invoked after each successful write with the running write count.</summary>
    public Action<int>? AfterWrite { get; set; }

    /// <summary>Given (operation, entry id or null for create), returns an exception to throw from that write.</summary>
    public Func<string, string?, Exception?>? ThrowOnWrite { get; set; }

    /// <summary>When set, SearchEntriesAsync throws it.</summary>
    public Exception? SearchEntriesThrows { get; set; }

    public TimeSpan Delay { get; set; }
    public int MaxConcurrent => _max;
    private int _inFlight, _max, _next = 100;

    public PiEntry Add(string id, string userId, DateOnly date, decimal hours, string taskId = "t-1",
        bool billable = true, string? description = null)
    {
        var task = GetTask(taskId)!;
        var entry = new PiEntry(id, userId, date, hours, billable, description, task.Id, task.Name, task.ProjectId, task.ProjectName, null);
        Entries.Add(entry);
        return entry;
    }

    public async Task<PiUser> GetCurrentUserAsync(CancellationToken ct) { await Enter(); return Caller; }

    public async Task<PiUser?> GetUserAsync(string userId, CancellationToken ct)
    {
        await Enter();
        var user = Users.FirstOrDefault(u => u.Id == userId);
        if (user is null && UnknownUserIdThrows)
            throw new TimeChangeException("pi_unavailable", "API returned InternalServerError");
        return user;
    }

    public async Task<IReadOnlyList<PiUser>> FindUsersAsync(string name, CancellationToken ct)
    { await Enter(); return Users.Where(u => u.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList(); }

    public async Task<IReadOnlyList<PiNamed>> ListOpenProjectsAsync(CancellationToken ct) { await Enter(); return Projects.ToList(); }

    public async Task<IReadOnlyList<PiNamed>> ListTasksAsync(string projectId, CancellationToken ct)
    { await Enter(); return Tasks.GetValueOrDefault(projectId)?.ToList() ?? []; }

    public async Task<PiTask?> GetTaskAsync(string taskId, CancellationToken ct) { await Enter(); return GetTask(taskId); }

    public async Task<PiEntry?> GetEntryAsync(string timeEntryId, CancellationToken ct)
    { await Enter(); return Entries.FirstOrDefault(e => e.Id == timeEntryId); }

    public async Task<IReadOnlyList<PiEntry>> SearchEntriesAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        lock (SearchEntriesCalls) SearchEntriesCalls.Add((userId, start, end));
        await Enter();
        if (SearchEntriesThrows is { } boom) throw boom;
        return Entries.Where(e => e.UserId == userId && e.Date >= start && e.Date <= end).ToList();
    }

    public async Task<IReadOnlyList<PiTimeSheet>> SearchTimeSheetsAsync(string userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        lock (SearchTimeSheetsCalls) SearchTimeSheetsCalls.Add((userId, start, end));
        await Enter();
        return (Sheets.GetValueOrDefault(userId) ?? []).Where(s => s.Start <= end && s.End >= start).ToList();
    }

    public async Task<PiWrite> CreateEntryAsync(EntryWrite w, CancellationToken ct)
    {
        await Enter();
        if (ThrowOnWrite?.Invoke("create", null) is { } thrown) throw thrown;
        if (ct.IsCancellationRequested) return new PiWrite(false, null, "The operation was canceled.");
        if (FailWrite?.Invoke("create", null) is { } error) return new PiWrite(false, null, error);
        var id = $"te-{_next++}";
        var task = GetTask(w.TaskId)!;
        Entries.Add(new PiEntry(id, w.UserId, w.Date, w.Hours, w.Billable, w.Description, task.Id, task.Name, task.ProjectId, task.ProjectName, w.TimeSheetId));
        Writes.Add($"create:{id}");
        AfterWrite?.Invoke(Writes.Count);
        return new PiWrite(true, id, null);
    }

    public async Task<PiWrite> UpdateEntryAsync(string timeEntryId, EntryWrite w, CancellationToken ct)
    {
        await Enter();
        if (ThrowOnWrite?.Invoke("update", timeEntryId) is { } thrown) throw thrown;
        if (ct.IsCancellationRequested) return new PiWrite(false, null, "The operation was canceled.");
        if (FailWrite?.Invoke("update", timeEntryId) is { } error) return new PiWrite(false, null, error);
        var i = Entries.FindIndex(e => e.Id == timeEntryId);
        if (i < 0) return new PiWrite(false, null, "Not found");
        var task = GetTask(w.TaskId)!;
        Entries[i] = Entries[i] with
        {
            Date = w.Date, Hours = w.Hours, Billable = w.Billable, Description = w.Description,
            TaskId = task.Id, TaskName = task.Name, ProjectId = task.ProjectId, ProjectName = task.ProjectName
        };
        Writes.Add($"update:{timeEntryId}");
        AfterWrite?.Invoke(Writes.Count);
        return new PiWrite(true, timeEntryId, null);
    }

    public async Task<PiWrite> DeleteEntryAsync(string timeEntryId, CancellationToken ct)
    {
        await Enter();
        if (ThrowOnWrite?.Invoke("delete", timeEntryId) is { } thrown) throw thrown;
        if (ct.IsCancellationRequested) return new PiWrite(false, null, "The operation was canceled.");
        if (FailWrite?.Invoke("delete", timeEntryId) is { } error) return new PiWrite(false, null, error);
        if (Entries.RemoveAll(e => e.Id == timeEntryId) == 0) return new PiWrite(false, null, "Not found");
        Writes.Add($"delete:{timeEntryId}");
        AfterWrite?.Invoke(Writes.Count);
        return new PiWrite(true, null, null);
    }

    private PiTask? GetTask(string taskId)
    {
        foreach (var (projectId, tasks) in Tasks)
            if (tasks.FirstOrDefault(t => t.Id == taskId) is { } task)
                return new PiTask(task.Id, task.Name, projectId, Projects.First(p => p.Id == projectId).Name);
        return null;
    }

    private async Task Enter()
    {
        var n = Interlocked.Increment(ref _inFlight);
        int seen;
        while (n > (seen = _max))
            Interlocked.CompareExchange(ref _max, n, seen);
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
