using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Stores and retrieves PI credentials from ~/.projectinsight/credentials.json
/// </summary>
public class CredentialStorageService
{
    private readonly ILogger<CredentialStorageService> _logger;
    private static readonly string CredentialDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".projectinsight");
    private static readonly string CredentialFile = Path.Combine(CredentialDir, "credentials.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CredentialStorageService(ILogger<CredentialStorageService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Load stored credentials, or null if none exist or expired
    /// </summary>
    public StoredCredential? LoadCredential()
    {
        try
        {
            if (!File.Exists(CredentialFile))
                return null;

            var json = File.ReadAllText(CredentialFile);
            var credential = JsonSerializer.Deserialize<StoredCredential>(json, JsonOptions);

            if (credential == null || string.IsNullOrEmpty(credential.ApiToken))
                return null;

            if (credential.ExpiresUtc.HasValue && credential.ExpiresUtc.Value < DateTime.UtcNow)
            {
                _logger.LogWarning("Stored PI credential has expired");
                return null;
            }

            _logger.LogInformation("Loaded stored PI credential for user {UserId}", credential.UserId);
            return credential;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stored credentials");
            return null;
        }
    }

    /// <summary>
    /// Save a credential to disk
    /// </summary>
    public void SaveCredential(StoredCredential credential)
    {
        try
        {
            Directory.CreateDirectory(CredentialDir);
            var json = JsonSerializer.Serialize(credential, JsonOptions);
            File.WriteAllText(CredentialFile, json);
            _logger.LogInformation("Saved PI credential for user {UserId}", credential.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save credentials");
            throw;
        }
    }
}

public class StoredCredential
{
    [JsonPropertyName("apiToken")]
    public string? ApiToken { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("expiresUtc")]
    public DateTime? ExpiresUtc { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime? CreatedUtc { get; set; }
}
