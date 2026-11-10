using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace ProjectInsightMCP.Web.Storage;

/// <summary>Shared helpers: one partition, OData date filters (the LINQ form doesn't translate).</summary>
internal static class TableUtil
{
    public const string Partition = "p";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>An OData datetime literal. Invariant culture: the default formats ':' with the culture's time separator.</summary>
    public static string DateTime(DateTimeOffset at)
        => string.Create(CultureInfo.InvariantCulture, $"datetime'{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");

    public static string Before(string property, DateTimeOffset at)
        => $"PartitionKey eq '{Partition}' and {property} lt {DateTime(at)}";

    public static string NotBefore(string property, DateTimeOffset at)
        => $"PartitionKey eq '{Partition}' and {property} ge {DateTime(at)}";

    /// <summary>A value for a single-quoted OData string literal.</summary>
    public static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public static async Task DeleteMatchingAsync(TableClient table, string filter, CancellationToken ct)
    {
        await foreach (var e in table.QueryAsync<TableEntity>(filter, select: ["PartitionKey", "RowKey"], cancellationToken: ct))
        {
            try
            {
                // Only the row as read: one changed since (a client touched by a sign-in) may no longer match.
                await table.DeleteEntityAsync(Partition, e.RowKey, e.ETag, ct);
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
            }
        }
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
            // A missing row is not an exception here: the loser gets a 404 response back.
            var deleted = await table.DeleteEntityAsync(Partition, codeHash, entity.ETag, ct);
            if (deleted.Status == 404)
                return null;
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

    public async Task<SessionCreate> TryCreateAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct)
    {
        // Add, then count, and take the row back if it went over a cap. Table storage can't count and add in one
        // step, and count-then-add let a parallel burst all count before any added. This way the racer that added
        // last counts every row added before it, so no set of accepted sessions exceeds a cap; racers near a cap may
        // refuse each other instead, and can start again.
        await table.AddEntityAsync(ToEntity(session), ct);
        SessionCreate outcome;
        try
        {
            outcome = await CheckCapsAsync(session, maxOutstanding, maxPerClient, now, ct);
        }
        catch
        {
            await ForgetQuietlyAsync(session.Id);
            throw;
        }
        if (outcome != SessionCreate.Created)
            await table.DeleteEntityAsync(Partition, session.Id, ETag.All, CancellationToken.None);
        return outcome;
    }

    private async Task<SessionCreate> CheckCapsAsync(SignInSession session, int maxOutstanding, int maxPerClient, DateTimeOffset now, CancellationToken ct)
    {
        var live = TableUtil.NotBefore("ExpiresAt", now);
        // The new row is among those counted when it is live itself; the caps apply to the others.
        var self = session.ExpiresAt >= now ? 1 : 0;
        if (session.ClientKeyHash is { } key
            && await TableUtil.CountAtMostAsync(table, $"{live} and ClientKey eq '{TableUtil.Quote(key)}'", Plus(maxPerClient, self), ct) - self >= maxPerClient)
            return SessionCreate.ClientFull;
        if (await TableUtil.CountAtMostAsync(table, live, Plus(maxOutstanding, self), ct) - self >= maxOutstanding)
            return SessionCreate.Full;
        return SessionCreate.Created;

        static int Plus(int max, int self) => (int)Math.Min(int.MaxValue, (long)max + self);
    }

    private async Task ForgetQuietlyAsync(string id)
    {
        try
        {
            await table.DeleteEntityAsync(Partition, id, ETag.All, CancellationToken.None);
        }
        catch (RequestFailedException)
        {
            // It expires with the session life anyway.
        }
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

    // A scan of the one partition, which the caps keep to a few hundred rows.
    public Task DeleteByCookieAsync(string cookieHash, CancellationToken ct)
        => TableUtil.DeleteMatchingAsync(table, $"PartitionKey eq '{Partition}' and CookieHash eq '{TableUtil.Quote(cookieHash)}'", ct);

    public Task PruneAsync(DateTimeOffset now, CancellationToken ct)
        => TableUtil.DeleteMatchingAsync(table, TableUtil.Before("ExpiresAt", now), ct);

    private static TableEntity ToEntity(SignInSession s) => new(Partition, s.Id)
    {
        ["Json"] = JsonSerializer.Serialize(s with { Version = null }, TableUtil.Json),
        ["ExpiresAt"] = s.ExpiresAt,
        ["ClientKey"] = s.ClientKeyHash,
        ["CookieHash"] = s.CookieHash
    };
}

public sealed class TableClientStore(TableClient table) : IClientStore
{
    private const string Partition = TableUtil.Partition;

    public async Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct)
        => (await ReadAsync(clientId, ct))?.Client;

    private async Task<(RegisteredClient Client, ETag ETag)?> ReadAsync(string clientId, CancellationToken ct)
    {
        var found = await table.GetEntityIfExistsAsync<TableEntity>(Partition, clientId, cancellationToken: ct);
        return found.HasValue
            ? (JsonSerializer.Deserialize<RegisteredClient>(found.Value!.GetString("Json"), TableUtil.Json)!, found.Value.ETag)
            : null;
    }

    public async Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct)
    {
        if (await TableUtil.CountAtMostAsync(table, $"PartitionKey eq '{Partition}'", max, ct) >= max
            && !await TryEvictOldestNeverUsedAsync(ct))
            return false;
        await table.AddEntityAsync(ToEntity(client), ct);
        return true;
    }

    private async Task<bool> TryEvictOldestNeverUsedAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Tables can't sort, but the cap keeps this scan to a few hundred rows.
            TableEntity? oldest = null;
            await foreach (var e in table.QueryAsync<TableEntity>($"PartitionKey eq '{Partition}' and Used eq false",
                select: ["PartitionKey", "RowKey", "LastUsed"], cancellationToken: ct))
                if (oldest is null || e.GetDateTimeOffset("LastUsed") < oldest.GetDateTimeOffset("LastUsed"))
                    oldest = e;
            if (oldest is null)
                return false;
            try
            {
                // Only the row as read: a sign-in that finished meanwhile (TouchAsync) changes the ETag and keeps it.
                await table.DeleteEntityAsync(Partition, oldest.RowKey, oldest.ETag, ct);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
                // Touched or evicted by someone else; pick again.
            }
        }
        return false;
    }

    public async Task TouchAsync(string clientId, DateTimeOffset at, bool signedIn, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await ReadAsync(clientId, ct) is not { } read)
                return; // pruned meanwhile
            var (current, etag) = read;
            var used = current.Used || signedIn;
            try
            {
                // Merge only the changed fields, and only over the row as read: otherwise a start touch that read
                // Used=false could write it back over a sign-in that finished meanwhile.
                await table.UpdateEntityAsync(new TableEntity(Partition, clientId)
                {
                    ["Json"] = JsonSerializer.Serialize(current with { LastUsed = at, Used = used }, TableUtil.Json),
                    ["LastUsed"] = at,
                    ["Used"] = used
                }, etag, TableUpdateMode.Merge, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Changed since the read; read again.
            }
        }
    }

    public Task PruneAsync(DateTimeOffset unusedSince, DateTimeOffset neverUsedSince, CancellationToken ct)
        => TableUtil.DeleteMatchingAsync(table,
            $"PartitionKey eq '{Partition}' and ((Used eq true and LastUsed lt {TableUtil.DateTime(unusedSince)}) or (Used eq false and LastUsed lt {TableUtil.DateTime(neverUsedSince)}))", ct);

    private static TableEntity ToEntity(RegisteredClient c) => new(Partition, c.ClientId)
    {
        ["Json"] = JsonSerializer.Serialize(c, TableUtil.Json),
        ["LastUsed"] = c.LastUsed,
        ["Used"] = c.Used
    };
}
