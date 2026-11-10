namespace ProjectInsightMCP.Web.Storage;

/// <summary>
/// <paramref name="LastUsed"/> is the registration time until the first sign-in starts, then the latest start or
/// finish; <paramref name="Used"/> says whether a sign-in ever finished, so a registration nobody finished can be
/// pruned sooner.
/// </summary>
public sealed record RegisteredClient(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, DateTimeOffset LastUsed, bool Used = false);

public interface IClientStore
{
    Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct);
    /// <summary>
    /// At <paramref name="max"/> clients, removes the oldest never-used one to make room, so registrations nobody
    /// signs in with can't lock out new ones. False only when every client held has signed in.
    /// </summary>
    Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct);
    /// <summary>
    /// Sets LastUsed to <paramref name="at"/>, and marks the client used when <paramref name="signedIn"/> (a sign-in
    /// finished). A start only refreshes LastUsed: it keeps the client off the eviction and never-used prune lists while
    /// the sign-in runs, but anyone can start one, so it must not earn the longer life of a used client.
    /// </summary>
    Task TouchAsync(string clientId, DateTimeOffset at, bool signedIn, CancellationToken ct);
    /// <summary>Removes used clients last used before <paramref name="unusedSince"/> and never-used ones registered before <paramref name="neverUsedSince"/>.</summary>
    Task PruneAsync(DateTimeOffset unusedSince, DateTimeOffset neverUsedSince, CancellationToken ct);
}

/// <summary>Starting: reserved, waiting for PI to open the activation. Pending: waiting for the user's code.</summary>
public static class SessionStatus { public const string Starting = "starting", Pending = "pending", Completing = "completing"; }

public enum SessionCreate { Created, Full, ClientFull }

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
    /// <summary>Keyed hash of the creating client's rate-limit partition (its IPv4 address or IPv6 /64), for the per-client cap.</summary>
    public string? ClientKeyHash { get; init; }
    /// <summary>Concurrency token set by the store on read; ignored on create.</summary>
    public string? Version { get; init; }
}

public interface ISessionStore
{
    /// <summary>
    /// Refused when <paramref name="maxOutstanding"/> unexpired sessions exist, or <paramref name="maxPerClient"/>
    /// with the session's <see cref="SignInSession.ClientKeyHash"/>. The caps hold under concurrent creates: racing
    /// creates may refuse each other near a cap, but never add more than it allows.
    /// </summary>
    Task<SessionCreate> TryCreateAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct);
    Task<SignInSession?> GetAsync(string id, CancellationToken ct);
    /// <summary>Saves <paramref name="updated"/> only if the stored session still has updated.Version.</summary>
    Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    /// <summary>Deletes the sessions bound to the browser cookie whose hash is <paramref name="cookieHash"/>.</summary>
    Task DeleteByCookieAsync(string cookieHash, CancellationToken ct);
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
