using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

public sealed record ClientMetadataDocument(string? ClientName, IReadOnlyList<string> RedirectUris);

/// <summary>
/// Client ID Metadata Documents, from allowlisted hosts only. Anything else is refused before a
/// request is made, so an /authorize link can't turn this server into a fetcher for arbitrary URLs.
/// </summary>
public sealed class CimdFetcher(IHttpClientFactory http, IMemoryCache cache, IOptions<AuthOptions> auth, ILogger<CimdFetcher> logger)
{
    public const string HttpClientName = "cimd";
    public const int MaxBytes = 5 * 1024;
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinKeep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(5);
    // HttpClient.Timeout stops at the headers (ResponseHeadersRead); this bounds the body read too.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public async Task<ClientMetadataDocument?> FetchAsync(string clientId, CancellationToken ct)
    {
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || url.AbsolutePath == "/" || !auth.Value.CimdHosts.Contains(url.Host))
            return null;

        var key = "cimd:" + clientId;
        if (cache.TryGetValue(key, out ClientMetadataDocument? known))
            return known;

        var (doc, maxAge) = await DownloadAsync(url, clientId, ct);
        // max-age=0 / no-store / absent would make Set throw or defeat the cache; floor it, and cap it at a day.
        var keep = maxAge is { } age && age > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Clamp(age.Ticks, MinKeep.Ticks, KeepFor.Ticks)) : MinKeep;
        cache.Set(key, doc, doc is null ? FailedFor : keep);
        return doc;
    }

    private async Task<(ClientMetadataDocument?, TimeSpan?)> DownloadAsync(Uri url, string clientId, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Deadline);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, null);

            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), limit.Token)) > 0)
                read += n;
            if (read > MaxBytes)
                return (null, null);

            using var json = JsonDocument.Parse(buffer.AsMemory(0, read));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != clientId
                || !root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
                return (null, null);
            var redirects = uris.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList();
            if (redirects.Count == 0)
                return (null, null);
            var name = root.TryGetProperty("client_name", out var n2) && n2.ValueKind == JsonValueKind.String ? n2.GetString() : null;
            return (new ClientMetadataDocument(name is { Length: <= 100 } ? name : null, redirects), response.Headers.CacheControl?.MaxAge);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Client metadata fetch failed: {Error}", ex.GetType().Name);
            return (null, null);
        }
    }
}
