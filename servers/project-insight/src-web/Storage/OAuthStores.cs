namespace ProjectInsightMCP.Web.Storage;

public sealed record RegisteredClient(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, DateTimeOffset LastUsed);

public interface IClientStore
{
    Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct);
    /// <summary>False when the store already holds <paramref name="max"/> clients.</summary>
    Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct);
    Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct);
    Task PruneAsync(DateTimeOffset unusedSince, CancellationToken ct);
}

public static class SessionStatus { public const string Pending = "pending", Completing = "completing"; }

public sealed record SignInSession
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public string? State { get; init; }
    public required string CodeChallenge { get; init; }
    public string? Resource { get; init; }
    public required string PiActivationId { get; init; }
    public required string PiActivationUrl { get; init; }
    public required string CookieHash { get; init; }
    public int Attempts { get; init; }
    public string Status { get; init; } = SessionStatus.Pending;
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Concurrency token set by the store on read; ignored on create.</summary>
    public string? Version { get; init; }
}

public interface ISessionStore
{
    /// <summary>False when <paramref name="maxOutstanding"/> unexpired sessions already exist.</summary>
    Task<bool> TryCreateAsync(SignInSession session, int maxOutstanding, DateTimeOffset now, CancellationToken ct);
    Task<SignInSession?> GetAsync(string id, CancellationToken ct);
    /// <summary>Saves <paramref name="updated"/> only if the stored session still has updated.Version.</summary>
    Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task PruneAsync(DateTimeOffset now, CancellationToken ct);
}

public sealed record StoredCode(string CodeHash, byte[] Payload, DateTimeOffset ExpiresAt);

public interface ICodeStore
{
    Task SaveAsync(StoredCode code, CancellationToken ct);
    /// <summary>Removes and returns the code; null if missing or another caller took it first.</summary>
    Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct);
    Task PruneAsync(DateTimeOffset now, CancellationToken ct);
}
