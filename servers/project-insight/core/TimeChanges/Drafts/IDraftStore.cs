namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// Drafts and receipts, partitioned by owner (the proposer's PI user id). A draft is only ever
/// visible to its owner, which is how "only the proposer may act on it" is enforced.
/// </summary>
public interface IDraftStore
{
    Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default);

    /// <summary>Stores a new draft under <see cref="Draft.ProposedBy"/>. Throws if the id exists.</summary>
    Task CreateAsync(Draft draft, CancellationToken ct = default);

    /// <summary>
    /// Under an exclusive lock: re-reads the draft and, only if its status is
    /// <paramref name="expectedStatus"/>, applies <paramref name="update"/> and saves it.
    /// Returns the saved draft, or null if it is missing or in another status.
    /// </summary>
    Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default);

    /// <summary>Deletes the owner's records untouched for 7 days.</summary>
    Task PruneAsync(string ownerId, CancellationToken ct = default);

    /// <summary>Prunes every owner's records. Called once at server startup.</summary>
    Task PruneAllAsync(CancellationToken ct = default);
}
