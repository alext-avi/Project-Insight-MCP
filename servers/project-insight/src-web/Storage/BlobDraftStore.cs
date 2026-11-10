using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Web.Storage;

/// <summary>
/// Drafts as blobs at &lt;ownerId&gt;/&lt;id&gt;.json. Status changes are compare-and-set on the blob's
/// ETag, the same guarantee FileDraftStore gets from its lock file. Owner ids are lower-cased: PI's GUID
/// casing is not guaranteed stable, and one user must always find their own drafts.
/// </summary>
public sealed partial class BlobDraftStore(BlobContainerClient container, TimeProvider time, ILogger<BlobDraftStore>? logger = null) : IDraftStore
{
    public const string Unavailable = "Drafts storage isn't available right now. Try again in a moment.";
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    // Every plan prunes its owner's drafts, which reads each of them; PruneService covers everyone hourly anyway.
    private static readonly TimeSpan OwnerPruneEvery = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxRetries = 5;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPruned = new(StringComparer.Ordinal);

    [GeneratedRegex("^d_[a-z0-9]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex OwnerPattern();

    public Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default) => Guard(async () =>
        BlobFor(ownerId, id) is { } blob ? (await ReadAsync(blob, ct))?.Draft : null);

    public Task CreateAsync(Draft draft, CancellationToken ct = default) => Guard(async () =>
    {
        var blob = BlobFor(draft.ProposedBy, draft.Id) ?? throw new ArgumentException($"Invalid draft id or owner: {draft.Id}");
        draft.UpdatedAt = time.GetUtcNow();
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(draft, Json),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new InvalidOperationException($"Draft {draft.Id} already exists.");
        }
        return true;
    });

    public Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default) => Guard(async () =>
    {
        if (BlobFor(ownerId, id) is not { } blob)
            return null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (await ReadAsync(blob, ct) is not var (draft, etag) || draft.Status != expectedStatus)
                return null;
            update(draft);
            draft.UpdatedAt = time.GetUtcNow();
            try
            {
                await blob.UploadAsync(BinaryData.FromObjectAsJson(draft, Json),
                    new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = etag } }, ct);
                return draft;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Someone else changed it; re-read and re-check the status.
            }
        }
        return (Draft?)null;
    });

    /// <summary>The planner's per-owner prune: at most hourly per owner, since it reads every draft the owner has.</summary>
    public Task PruneAsync(string ownerId, CancellationToken ct = default) => Guard(async () =>
    {
        if (!OwnerPattern().IsMatch(ownerId))
            return true;
        var owner = ownerId.ToLowerInvariant();
        var now = time.GetUtcNow();
        if (_lastPruned.TryGetValue(owner, out var last) && now - last < OwnerPruneEvery)
            return true;
        _lastPruned[owner] = now;
        await PruneOwnerAsync(owner, now, ct);
        return true;
    });

    public Task PruneAllAsync(CancellationToken ct = default) => Guard(async () =>
    {
        await foreach (var item in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", null, ct))
            if (item.IsPrefix && OwnerPattern().IsMatch(item.Prefix.TrimEnd('/')))
            {
                var owner = item.Prefix.TrimEnd('/').ToLowerInvariant();
                var now = time.GetUtcNow();
                _lastPruned[owner] = now;
                await PruneOwnerAsync(owner, now, ct);
            }
        return true;
    });

    private async Task PruneOwnerAsync(string owner, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - Keep;
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, owner + "/", ct))
        {
            var blob = container.GetBlobClient(item.Name);
            if (await ReadAsync(blob, ct) is not var (draft, etag) || draft.UpdatedAt >= cutoff)
                continue;
            try
            {
                await blob.DeleteIfExistsAsync(conditions: new BlobRequestConditions { IfMatch = etag }, cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Changed since it was read, so no longer untouched.
            }
        }
    }

    // A storage error carries its status, error code, response body and headers. The type and status go to the log;
    // the tool result gets a short fixed message, as PI errors get a clipped one.
    private async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is RequestFailedException or AggregateException)
        {
            (logger ?? NullLogger<BlobDraftStore>.Instance).LogWarning("Draft storage failed: {Error} (HTTP {Status})",
                ex.GetType().Name, (ex as RequestFailedException)?.Status);
            throw new TimeChangeException("storage_unavailable", Unavailable);
        }
    }

    private BlobClient? BlobFor(string ownerId, string id)
        => OwnerPattern().IsMatch(ownerId) && IdPattern().IsMatch(id) ? container.GetBlobClient($"{ownerId.ToLowerInvariant()}/{id}.json") : null;

    private static async Task<(Draft Draft, ETag ETag)?> ReadAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            var result = await blob.DownloadContentAsync(ct);
            return result.Value.Content.ToObjectFromJson<Draft>(Json) is { } draft ? (draft, result.Value.Details.ETag) : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
