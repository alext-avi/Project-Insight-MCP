using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// One JSON file per record at &lt;root&gt;/&lt;ownerId&gt;/&lt;id&gt;.json, readable only by the
/// current OS user. Writes go to a temp file and are renamed into place. Status changes hold an
/// exclusive lock file, so two server processes (two Claude windows) can't both commit a draft.
/// </summary>
public sealed partial class FileDraftStore(string root, TimeProvider time) : IDraftStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".projectinsight", "drafts");

    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [GeneratedRegex("^d_[a-z0-9]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex OwnerPattern();

    public async Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default)
    {
        var path = PathFor(ownerId, id);
        return path is null ? null : await ReadAsync(path, ct);
    }

    public async Task CreateAsync(Draft draft, CancellationToken ct = default)
    {
        var path = PathFor(draft.ProposedBy, draft.Id)
            ?? throw new ArgumentException($"Invalid draft id or owner: {draft.Id}");
        EnsureDirectory(Path.GetDirectoryName(path)!);
        using (await LockAsync(path, ct))
        {
            if (File.Exists(path))
                throw new InvalidOperationException($"Draft {draft.Id} already exists.");
            draft.UpdatedAt = time.GetUtcNow();
            await WriteAsync(path, draft, ct);
        }
    }

    public async Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default)
    {
        var path = PathFor(ownerId, id);
        if (path is null || !File.Exists(path))
            return null;
        using (await LockAsync(path, ct))
        {
            var draft = await ReadAsync(path, ct);
            if (draft is null || draft.Status != expectedStatus)
                return null;
            update(draft);
            draft.UpdatedAt = time.GetUtcNow();
            await WriteAsync(path, draft, ct);
            return draft;
        }
    }

    public async Task PruneAsync(string ownerId, CancellationToken ct = default)
    {
        if (!OwnerPattern().IsMatch(ownerId))
            return;
        var dir = Path.Combine(root, ownerId);
        if (!Directory.Exists(dir))
            return;
        var cutoff = time.GetUtcNow() - Keep;
        foreach (var file in Directory.EnumerateFiles(dir, "d_*.json"))
        {
            var draft = await ReadAsync(file, ct);
            if (draft is null || draft.UpdatedAt >= cutoff)
                continue;
            File.Delete(file);
            File.Delete(Path.ChangeExtension(file, ".lock"));
        }
    }

    public async Task PruneAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (OwnerPattern().IsMatch(name))
                await PruneAsync(name, ct);
        }
    }

    private string? PathFor(string ownerId, string id)
        => OwnerPattern().IsMatch(ownerId) && IdPattern().IsMatch(id) ? Path.Combine(root, ownerId, id + ".json") : null;

    private static async Task<Draft?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<Draft>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(string path, Draft draft, CancellationToken ct)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await JsonSerializer.SerializeAsync(stream, draft, Json, ct);
        }
        File.Move(temp, path, overwrite: true);
    }

    private void EnsureDirectory(string dir)
    {
        foreach (var d in new[] { root, dir })
        {
            Directory.CreateDirectory(d);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // FileShare.None takes an OS-level exclusive lock (flock on Unix), honoured across processes
    // and across FileStreams in one process. The lock file is left in place: deleting it on close
    // would let two waiters lock different inodes.
    private static async Task<IDisposable> LockAsync(string path, CancellationToken ct)
    {
        var lockPath = Path.ChangeExtension(path, ".lock");
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, ct);
            }
        }
    }
}
