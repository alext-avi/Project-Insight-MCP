using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using ProjectInsightMCP.Models;

namespace ProjectInsightMCP.Web.Auth;

public enum PiIdentityStatus { Ok, Rejected, Unavailable }

public sealed record PiIdentity(PiIdentityStatus Status, string? UserId);

/// <summary>
/// Asks PI who a bearer token belongs to. PI is the only judge of a pass-through token, so this is
/// the validation. Results are cached under the token's hash, never the token.
/// </summary>
public sealed class PiIdentityClient(IHttpClientFactory http, IMemoryCache cache, IConfiguration config, ILogger<PiIdentityClient> logger)
{
    public const string HttpClientName = "pi-identity";
    private static readonly TimeSpan OkFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RejectedFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueueFor = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Caps how hard random bearer tokens can push on PI.
    private readonly SemaphoreSlim _calls = new(8, 8);

    public bool IsCached(string token) => cache.TryGetValue(Key(token), out _);

    private static string Key(string token) => "pi-identity:" + TokenHash.Of(token);

    public async Task<PiIdentity> ResolveAsync(string token, CancellationToken ct)
    {
        var key = Key(token);
        if (cache.TryGetValue(key, out PiIdentity? known))
            return known!;
        if (!await _calls.WaitAsync(QueueFor, ct))
            return new PiIdentity(PiIdentityStatus.Unavailable, null);
        try
        {
            var identity = await FetchAsync(token, ct);
            if (identity.Status == PiIdentityStatus.Ok)
                cache.Set(key, identity, OkFor);
            else if (identity.Status == PiIdentityStatus.Rejected)
                cache.Set(key, identity, RejectedFor);
            return identity;
        }
        finally
        {
            _calls.Release();
        }
    }

    private async Task<PiIdentity> FetchAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(config["ProjectInsight:BaseUrl"]!), "/api/user/me"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            var status = (int)response.StatusCode;
            // Only 401/403 say the token is bad; anything else (404, 3xx, 5xx) is PI misbehaving, so don't blame the token.
            if (status is 401 or 403)
                return new PiIdentity(PiIdentityStatus.Rejected, null);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PI identity check failed with HTTP {Status}", status);
                return new PiIdentity(PiIdentityStatus.Unavailable, null);
            }
            var user = await response.Content.ReadFromJsonAsync<User>(Json, ct);
            return string.IsNullOrEmpty(user?.Id)
                ? new PiIdentity(PiIdentityStatus.Unavailable, null)
                : new PiIdentity(PiIdentityStatus.Ok, user.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PI identity check failed: {Error}", ex.GetType().Name);
            return new PiIdentity(PiIdentityStatus.Unavailable, null);
        }
    }
}
