using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace ProjectInsightMCP.Web.Storage;

/// <summary>Shared helpers: one partition, OData date filters (the LINQ form doesn't translate).</summary>
internal static class TableUtil
{
    public const string Partition = "p";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Before(string property, DateTimeOffset at)
        => $"PartitionKey eq '{Partition}' and {property} lt datetime'{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'";

    public static string NotBefore(string property, DateTimeOffset at)
        => $"PartitionKey eq '{Partition}' and {property} ge datetime'{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'";

    public static async Task DeleteMatchingAsync(TableClient table, string filter, CancellationToken ct)
    {
        await foreach (var e in table.QueryAsync<TableEntity>(filter, select: ["PartitionKey", "RowKey"], cancellationToken: ct))
            await table.DeleteEntityAsync(Partition, e.RowKey, ETag.All, ct);
    }

    public static async Task<int> CountAtMostAsync(TableClient table, string filter, int limit, CancellationToken ct)
    {
        var n = 0;
        await foreach (var _ in table.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: ct))
            if (++n >= limit)
                break;
        return n;
    }
}

public sealed class TableCodeStore(TableClient table) : ICodeStore
{
    private const string Partition = TableUtil.Partition;

    public Task SaveAsync(StoredCode code, CancellationToken ct) =>
        table.AddEntityAsync(new TableEntity(Partition, code.CodeHash)
        {
            ["Payload"] = code.Payload,
            ["ExpiresAt"] = code.ExpiresAt
        }, ct);

    public async Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct)
    {
        var found = await table.GetEntityIfExistsAsync<TableEntity>(Partition, codeHash, cancellationToken: ct);
        if (!found.HasValue)
            return null;
        var entity = found.Value!;
        try
        {
            // ETag-conditional delete: of two concurrent takers, only one deletes.
            await table.DeleteEntityAsync(Partition, codeHash, entity.ETag, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return null;
        }
        return new StoredCode(codeHash, entity.GetBinary("Payload"), entity.GetDateTimeOffset("ExpiresAt")!.Value);
    }

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct)
        => TableUtil.DeleteMatchingAsync(table, TableUtil.Before("ExpiresAt", now), ct);
}

public sealed class TableSessionStore(TableClient table) : ISessionStore
{
    private const string Partition = TableUtil.Partition;

    public async Task<bool> TryCreateAsync(SignInSession session, int maxOutstanding, DateTimeOffset now, CancellationToken ct)
    {
        if (await TableUtil.CountAtMostAsync(table, TableUtil.NotBefore("ExpiresAt", now), maxOutstanding, ct) >= maxOutstanding)
            return false;
        await table.AddEntityAsync(ToEntity(session), ct);
        return true;
    }

    public async Task<SignInSession?> GetAsync(string id, CancellationToken ct)
    {
        var found = await table.GetEntityIfExistsAsync<TableEntity>(Partition, id, cancellationToken: ct);
        if (!found.HasValue)
            return null;
        var entity = found.Value!;
        return JsonSerializer.Deserialize<SignInSession>(entity.GetString("Json"), TableUtil.Json)! with { Version = entity.ETag.ToString() };
    }

    public async Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct)
    {
        if (updated.Version is null)
            return false;
        try
        {
            await table.UpdateEntityAsync(ToEntity(updated), new ETag(updated.Version), TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
        => await table.DeleteEntityAsync(Partition, id, ETag.All, ct);

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct)
        => TableUtil.DeleteMatchingAsync(table, TableUtil.Before("ExpiresAt", now), ct);

    private static TableEntity ToEntity(SignInSession s) => new(Partition, s.Id)
    {
        ["Json"] = JsonSerializer.Serialize(s with { Version = null }, TableUtil.Json),
        ["ExpiresAt"] = s.ExpiresAt
    };
}

public sealed class TableClientStore(TableClient table) : IClientStore
{
    private const string Partition = TableUtil.Partition;

    public async Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct)
    {
        var found = await table.GetEntityIfExistsAsync<TableEntity>(Partition, clientId, cancellationToken: ct);
        return found.HasValue ? JsonSerializer.Deserialize<RegisteredClient>(found.Value!.GetString("Json"), TableUtil.Json) : null;
    }

    public async Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct)
    {
        if (await TableUtil.CountAtMostAsync(table, $"PartitionKey eq '{Partition}'", max, ct) >= max)
            return false;
        await table.AddEntityAsync(ToEntity(client), ct);
        return true;
    }

    public async Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct)
    {
        var current = await GetAsync(clientId, ct);
        if (current is null)
            return;
        try
        {
            // Merge only the changed fields; a missing row (pruned meanwhile) is fine.
            await table.UpdateEntityAsync(new TableEntity(Partition, clientId)
            {
                ["Json"] = JsonSerializer.Serialize(current with { LastUsed = at }, TableUtil.Json),
                ["LastUsed"] = at
            }, ETag.All, TableUpdateMode.Merge, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public Task PruneAsync(DateTimeOffset unusedSince, CancellationToken ct)
        => TableUtil.DeleteMatchingAsync(table, TableUtil.Before("LastUsed", unusedSince), ct);

    private static TableEntity ToEntity(RegisteredClient c) => new(Partition, c.ClientId)
    {
        ["Json"] = JsonSerializer.Serialize(c, TableUtil.Json),
        ["LastUsed"] = c.LastUsed
    };
}
