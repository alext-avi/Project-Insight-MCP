using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Handles the PI device activation flow for obtaining per-user API tokens via SSO
/// </summary>
public class DeviceActivationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeviceActivationService> _logger;
    private readonly string _baseUrl;
    private readonly string _adminApiToken;
    private readonly CredentialStorageService _credentialStorage;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Pending activation state (in-memory, lives for the duration of the server process)
    private string? _pendingActivationId;
    private string? _pendingActivationUrl;

    public bool HasPendingActivation => !string.IsNullOrEmpty(_pendingActivationId);
    public string? PendingActivationUrl => _pendingActivationUrl;

    public DeviceActivationService(
        HttpClient httpClient,
        ILogger<DeviceActivationService> logger,
        IConfiguration configuration,
        CredentialStorageService credentialStorage)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["ProjectInsight:BaseUrl"]?.TrimEnd('/') ?? "";
        _adminApiToken = configuration["ProjectInsight:ApiToken"] ?? "";
        _credentialStorage = credentialStorage;
    }

    /// <summary>
    /// Stateless variant for the OAuth bridge: just create an activation request
    /// against PI and return the id+url. Does not store credentials, does not
    /// touch instance fields, does not open a browser.
    /// </summary>
    public async Task<DeviceActivationStartResult> CreateActivationRequestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                clientAgent = "ProjectInsight-MCP-OAuth",
                clientPlatform = "Claude",
                clientDevice = "Remote"
            });
            var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/device-activation/create-request");
            if (!string.IsNullOrEmpty(_adminApiToken))
                request.Headers.Add("api-token", _adminApiToken);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("CreateActivationRequest failed: {Status} - {Error}", response.StatusCode, error);
                return new DeviceActivationStartResult { Success = false, Error = $"Failed to create activation request: {response.StatusCode}" };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var activation = JsonSerializer.Deserialize<DeviceActivationResponse>(responseJson, JsonOptions);
            if (activation == null || string.IsNullOrEmpty(activation.DeviceActivationId))
                return new DeviceActivationStartResult { Success = false, Error = "Invalid activation response from PI" };

            return new DeviceActivationStartResult
            {
                Success = true,
                ActivationId = activation.DeviceActivationId,
                ActivationUrl = activation.DeviceActivationRequestUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating PI device activation");
            return new DeviceActivationStartResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Stateless variant: complete an activation given an explicit id+code.
    /// Does not write to credential storage — the bridge passes the token
    /// straight back to the OAuth client.
    /// </summary>
    public async Task<DeviceActivationCompleteResult> CompleteActivationByIdAsync(
        string activationId, string activationCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_baseUrl}/device-activation/complete-request" +
                      $"?activationRequestId={Uri.EscapeDataString(activationId)}" +
                      $"&activationCode={Uri.EscapeDataString(activationCode)}" +
                      $"&clientAgent=ProjectInsight-MCP-OAuth" +
                      $"&timeoutMinutes=525600";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_adminApiToken))
                request.Headers.Add("api-token", _adminApiToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("CompleteActivationById failed: {Status} - {Error}", response.StatusCode, responseJson);
                return new DeviceActivationCompleteResult { Success = false, Error = $"Activation failed: {response.StatusCode}" };
            }

            var result = JsonSerializer.Deserialize<DeviceActivationCompleteResponse>(responseJson, JsonOptions);
            if (result == null || string.IsNullOrEmpty(result.ApiToken))
                return new DeviceActivationCompleteResult { Success = false, Error = "Invalid activation code or activation expired." };

            return new DeviceActivationCompleteResult
            {
                Success = true,
                ApiToken = result.ApiToken,
                UserId = result.UserId,
                ExpiresUtc = result.ApiTokenExpirationDateTimeUTC
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing PI device activation by id");
            return new DeviceActivationCompleteResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Start the device activation flow: create a request and open the user's browser
    /// </summary>
    public async Task<DeviceActivationStartResult> StartActivationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                clientAgent = "ProjectInsight-MCP",
                clientPlatform = RuntimeInformation.OSDescription,
                clientDevice = Environment.MachineName
            });

            var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

            // Use admin token to create the activation request
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/device-activation/create-request");
            if (!string.IsNullOrEmpty(_adminApiToken))
                request.Headers.Add("api-token", _adminApiToken);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to create device activation: {Status} - {Error}", response.StatusCode, error);
                return new DeviceActivationStartResult { Success = false, Error = $"Failed to create activation request: {response.StatusCode}" };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            TryWriteLinkDebug($"StartActivation POST {_baseUrl}/device-activation/create-request -> {(int)response.StatusCode}\nRaw: {responseJson}");
            var activation = JsonSerializer.Deserialize<DeviceActivationResponse>(responseJson, JsonOptions);

            if (activation == null || string.IsNullOrEmpty(activation.DeviceActivationId))
            {
                _logger.LogError("Invalid activation response from PI. Raw: {Raw}", responseJson);
                return new DeviceActivationStartResult { Success = false, Error = $"Invalid activation response from PI. Raw response: {responseJson}" };
            }

            // Never hand back a silently-empty URL: surface PI's raw response so the
            // failure is observable instead of rendering a blank "Sign-in URL:".
            if (string.IsNullOrEmpty(activation.DeviceActivationRequestUrl))
            {
                _logger.LogError("PI returned activation id {Id} but an empty sign-in URL. Raw: {Raw}", activation.DeviceActivationId, responseJson);
                return new DeviceActivationStartResult { Success = false, Error = $"Project Insight returned an activation id but no sign-in URL. Raw PI response: {responseJson}" };
            }

            _pendingActivationId = activation.DeviceActivationId;
            _pendingActivationUrl = activation.DeviceActivationRequestUrl;

            // Open the browser
            OpenBrowser(activation.DeviceActivationRequestUrl!);

            _logger.LogInformation("Device activation started: {ActivationId}, URL: {Url}",
                activation.DeviceActivationId, activation.DeviceActivationRequestUrl);

            return new DeviceActivationStartResult
            {
                Success = true,
                ActivationUrl = activation.DeviceActivationRequestUrl,
                ActivationId = activation.DeviceActivationId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting device activation");
            return new DeviceActivationStartResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Complete the activation by exchanging the user-provided code for a token
    /// </summary>
    public async Task<DeviceActivationCompleteResult> CompleteActivationAsync(string activationCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_pendingActivationId))
        {
            return new DeviceActivationCompleteResult { Success = false, Error = "No pending activation request. Please try again." };
        }

        try
        {
            var url = $"{_baseUrl}/device-activation/complete-request" +
                      $"?activationRequestId={Uri.EscapeDataString(_pendingActivationId)}" +
                      $"&activationCode={Uri.EscapeDataString(activationCode)}" +
                      $"&clientAgent=ProjectInsight-MCP" +
                      $"&timeoutMinutes=525600"; // 1 year

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_adminApiToken))
                request.Headers.Add("api-token", _adminApiToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to complete device activation: {Status} - {Error}", response.StatusCode, responseJson);
                return new DeviceActivationCompleteResult { Success = false, Error = $"Activation failed: {response.StatusCode}" };
            }

            var result = JsonSerializer.Deserialize<DeviceActivationCompleteResponse>(responseJson, JsonOptions);

            if (result == null || string.IsNullOrEmpty(result.ApiToken))
            {
                return new DeviceActivationCompleteResult { Success = false, Error = "Invalid activation code or activation expired. Please try again." };
            }

            // Store the credential
            var credential = new StoredCredential
            {
                ApiToken = result.ApiToken,
                UserId = result.UserId,
                DeviceId = result.DeviceId,
                ExpiresUtc = result.ApiTokenExpirationDateTimeUTC,
                BaseUrl = _baseUrl,
                CreatedUtc = DateTime.UtcNow
            };
            _credentialStorage.SaveCredential(credential);

            // Clear pending state
            _pendingActivationId = null;
            _pendingActivationUrl = null;

            _logger.LogInformation("Device activation completed successfully for user {UserId}", result.UserId);

            return new DeviceActivationCompleteResult
            {
                Success = true,
                ApiToken = result.ApiToken,
                UserId = result.UserId,
                ExpiresUtc = result.ApiTokenExpirationDateTimeUTC
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing device activation");
            return new DeviceActivationCompleteResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Best-effort diagnostic log of the raw PI activation response, written to
    /// ~/.projectinsight/link-debug.log so we can inspect what PI returned even
    /// when the MCP host (e.g. Cowork) doesn't surface stderr.
    /// </summary>
    private static void TryWriteLinkDebug(string text)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".projectinsight");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "link-debug.log"), $"[{DateTime.UtcNow:O}] {text}\n\n");
        }
        catch { /* diagnostics are best-effort */ }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch
        {
            // Browser open is best-effort; the URL is returned in the response
        }
    }
}

#region Device Activation Models

public class DeviceActivationResponse
{
    [JsonPropertyName("DeviceActivationId")]
    public string? DeviceActivationId { get; set; }

    [JsonPropertyName("DeviceActivationRequestUrl")]
    public string? DeviceActivationRequestUrl { get; set; }

    [JsonPropertyName("ExpirationDateTimeUTC")]
    public DateTime? ExpirationDateTimeUTC { get; set; }
}

public class DeviceActivationCompleteResponse
{
    [JsonPropertyName("AuthenticationToken")]
    public string? AuthenticationToken { get; set; }

    [JsonPropertyName("ApiToken")]
    public string? ApiToken { get; set; }

    [JsonPropertyName("DeviceApiToken")]
    public string? DeviceApiToken { get; set; }

    [JsonPropertyName("DeviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("UserId")]
    public string? UserId { get; set; }

    [JsonPropertyName("ApiTokenExpirationDateTimeUTC")]
    public DateTime? ApiTokenExpirationDateTimeUTC { get; set; }
}

public class DeviceActivationStartResult
{
    public bool Success { get; set; }
    public string? ActivationUrl { get; set; }
    public string? ActivationId { get; set; }
    public string? Error { get; set; }
}

public class DeviceActivationCompleteResult
{
    public bool Success { get; set; }
    public string? ApiToken { get; set; }
    public string? UserId { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public string? Error { get; set; }
}

#endregion
