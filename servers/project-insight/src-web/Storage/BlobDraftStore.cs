using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Web.Storage;

/// <summary>
/// Drafts as blobs at &lt;ownerId&gt;/&lt;id&gt;.json. Status changes are compare-and-set on the blob's
/// ETag, the same guarantee FileDraftStore gets from its lock file. Owner ids are lower-cased: PI's GUID
/// casing is not guaranteed stable, and one user must always find their own drafts.
/// </summary>
public sealed partial class BlobDraftStore(BlobContainerClient container, TimeProvider time) : IDraftStore
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxRetries = 5;

    [GeneratedRegex("^d_[a-z0-9]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex OwnerPattern();

    public async Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default)
        => BlobFor(ownerId, id) is { } blob ? (await ReadAsync(blob, ct))?.Draft : null;

    public async Task CreateAsync(Draft draft, CancellationToken ct = default)
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
    }

    public async Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default)
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
        return null;
    }

    public async Task PruneAsync(string ownerId, CancellationToken ct = default)
    {
        if (!OwnerPattern().IsMatch(ownerId))
            return;
        var cutoff = time.GetUtcNow() - Keep;
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, ownerId.ToLowerInvariant() + "/", ct))
        {
            var blob = container.GetBlobClient(item.Name);
            if (await ReadAsync(blob, ct) is var (draft, etag) && draft.UpdatedAt < cutoff)
                await blob.DeleteIfExistsAsync(conditions: new BlobRequestConditions { IfMatch = etag }, cancellationToken: ct);
        }
    }

    public async Task PruneAllAsync(CancellationToken ct = default)
    {
        await foreach (var item in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", null, ct))
            if (item.IsPrefix && OwnerPattern().IsMatch(item.Prefix.TrimEnd('/')))
                await PruneAsync(item.Prefix.TrimEnd('/'), ct);
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
