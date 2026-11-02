using System.Text;
using System.Text.Json;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Web.Auth;

public sealed record PiActivationStart(string ActivationId, Uri ActivationUrl);
public enum PiActivationOutcome { Ok, WrongCode, Unavailable }
public sealed record PiActivationResult(PiActivationOutcome Outcome, string? ApiToken = null, string? UserId = null, DateTimeOffset? ExpiresAt = null);

public interface IPiActivationClient
{
    Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken ct);
    Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken ct);
}

/// <summary>
/// PI device activation for the OAuth front. Stateless, unlike Core's DeviceActivationService: no
/// pending state, no browser, no credential file.
/// </summary>
public sealed class PiActivationClient(IHttpClientFactory http, IConfiguration config, ILogger<PiActivationClient> logger) : IPiActivationClient
{
    public const string HttpClientName = "pi-activation";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private string BaseUrl => config["ProjectInsight:BaseUrl"]!.TrimEnd('/');

    public async Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/device-activation/create-request")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    clientAgent = "ProjectInsight-MCP-OAuth",
                    clientPlatform = "MCP",
                    clientDevice
                }), Encoding.UTF8, "application/json")
            };
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PI create-request failed with HTTP {Status}", (int)response.StatusCode);
                return null;
            }
            var body = JsonSerializer.Deserialize<DeviceActivationResponse>(await response.Content.ReadAsStringAsync(ct), Json);
            return body is { DeviceActivationId: { Length: > 0 } id, DeviceActivationRequestUrl: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? new PiActivationStart(id, uri)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PI create-request failed: {Error}", ex.GetType().Name);
            return null;
        }
    }

    public async Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken ct)
    {
        try
        {
            // PI takes these only as a query string; telemetry strips query strings (QueryRedactionProcessor).
            var url = $"{BaseUrl}/device-activation/complete-request?activationRequestId={Uri.EscapeDataString(activationId)}" +
                      $"&activationCode={Uri.EscapeDataString(code)}&clientAgent=ProjectInsight-MCP-OAuth&timeoutMinutes={lifetimeMinutes}";
            using var response = await http.CreateClient(HttpClientName).GetAsync(url, ct);
            if ((int)response.StatusCode >= 500 || response.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests)
                return new PiActivationResult(PiActivationOutcome.Unavailable);
            if (!response.IsSuccessStatusCode)
                return new PiActivationResult(PiActivationOutcome.WrongCode);
            var body = JsonSerializer.Deserialize<DeviceActivationCompleteResponse>(await response.Content.ReadAsStringAsync(ct), Json);
            if (body is not { ApiToken: { Length: > 0 } token, UserId: { Length: > 0 } userId })
                return new PiActivationResult(PiActivationOutcome.WrongCode);
            // PI's expiry comes without an offset; it is UTC.
            DateTimeOffset? expires = body.ApiTokenExpirationDateTimeUTC is { } at
                ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
                : null;
            return new PiActivationResult(PiActivationOutcome.Ok, token, userId, expires);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PI complete-request failed: {Error}", ex.GetType().Name);
            return new PiActivationResult(PiActivationOutcome.Unavailable);
        }
    }
}
