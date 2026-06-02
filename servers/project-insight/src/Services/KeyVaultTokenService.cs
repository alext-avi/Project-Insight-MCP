using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Manages per-user PI API tokens in Azure Key Vault, keyed by Entra object ID.
/// </summary>
public class KeyVaultTokenService
{
    private readonly SecretClient? _secretClient;
    private readonly ILogger<KeyVaultTokenService> _logger;
    private readonly bool _isConfigured;

    public KeyVaultTokenService(ILogger<KeyVaultTokenService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var vaultUrl = configuration["KeyVault:Url"];
        if (!string.IsNullOrEmpty(vaultUrl))
        {
            _secretClient = new SecretClient(new Uri(vaultUrl), new DefaultAzureCredential());
            _isConfigured = true;
            _logger.LogInformation("KeyVaultTokenService initialized with vault: {VaultUrl}", vaultUrl);
        }
        else
        {
            _logger.LogWarning("KeyVaultTokenService not configured — no KeyVault:Url setting");
        }
    }

    public bool IsConfigured => _isConfigured;

    /// <summary>
    /// Get the PI API token for a user by their Entra object ID. Returns null if not found.
    /// </summary>
    public async Task<UserPiToken?> GetTokenAsync(string entraOid, CancellationToken cancellationToken = default)
    {
        if (!_isConfigured || _secretClient == null)
            return null;

        try
        {
            var secretName = $"pi-token-{entraOid}";
            var response = await _secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = response.Value;

            if (secret == null || string.IsNullOrEmpty(secret.Value))
                return null;

            secret.Properties.Tags.TryGetValue("piUserId", out var piUserId);
            secret.Properties.Tags.TryGetValue("userEmail", out var userEmail);

            return new UserPiToken
            {
                ApiToken = secret.Value,
                PiUserId = piUserId,
                UserEmail = userEmail,
                ExpiresUtc = secret.Properties.ExpiresOn?.UtcDateTime
            };
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving PI token for Entra user {EntraOid}", entraOid);
            return null;
        }
    }

    /// <summary>
    /// Store a PI API token for a user, keyed by their Entra object ID.
    /// </summary>
    public async Task SaveTokenAsync(string entraOid, string apiToken, string? piUserId = null,
        string? userEmail = null, DateTime? expiresUtc = null, CancellationToken cancellationToken = default)
    {
        if (!_isConfigured || _secretClient == null)
            throw new InvalidOperationException("Key Vault is not configured");

        var secretName = $"pi-token-{entraOid}";
        var secret = new KeyVaultSecret(secretName, apiToken);

        if (expiresUtc.HasValue)
            secret.Properties.ExpiresOn = new DateTimeOffset(expiresUtc.Value, TimeSpan.Zero);

        secret.Properties.Tags["piUserId"] = piUserId ?? "";
        secret.Properties.Tags["userEmail"] = userEmail ?? "";
        secret.Properties.Tags["linkedUtc"] = DateTime.UtcNow.ToString("o");

        await _secretClient.SetSecretAsync(secret, cancellationToken);

        _logger.LogInformation("Stored PI token for Entra user {EntraOid} (PI user {PiUserId})", entraOid, piUserId);
    }

    /// <summary>
    /// Delete a user's PI token (for re-linking or revocation).
    /// </summary>
    public async Task DeleteTokenAsync(string entraOid, CancellationToken cancellationToken = default)
    {
        if (!_isConfigured || _secretClient == null)
            return;

        try
        {
            var secretName = $"pi-token-{entraOid}";
            await _secretClient.StartDeleteSecretAsync(secretName, cancellationToken);
            _logger.LogInformation("Deleted PI token for Entra user {EntraOid}", entraOid);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }
}

public class UserPiToken
{
    public string? ApiToken { get; set; }
    public string? PiUserId { get; set; }
    public string? UserEmail { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}
