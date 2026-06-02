using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Services;

/// <summary>
/// Short-lived persistence for the OAuth bridge.
///
/// Two logical record kinds, sharing one Azure Table:
///  - "session"  → in-flight /authorize state, keyed by a session_id we mint
///                 when the user first arrives. Holds PKCE challenge, the
///                 client's redirect_uri, the PI device-activation id, etc.
///  - "code"     → an issued authorization code, keyed by the code itself.
///                 Holds the PI API token to be returned at /token. Single-use.
///
/// Stored in the AzureWebJobsStorage account already provisioned for the
/// Function App — no extra infrastructure.
/// </summary>
public class AuthCodeStore
{
    private const string TableName = "PiOAuthBridge";
    private const string SessionPartition = "session";
    private const string CodePartition = "code";

    // Sessions and codes are short-lived; we let cleanup happen on read.
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(2);

    private readonly TableClient _table;
    private readonly ILogger<AuthCodeStore> _logger;

    public AuthCodeStore(IConfiguration configuration, ILogger<AuthCodeStore> logger)
    {
        _logger = logger;
        var connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured");

        var serviceClient = new TableServiceClient(connectionString);
        serviceClient.CreateTableIfNotExists(TableName);
        _table = serviceClient.GetTableClient(TableName);
    }

    public async Task SaveSessionAsync(OAuthSession session, CancellationToken cancellationToken = default)
    {
        var entity = new TableEntity(SessionPartition, session.SessionId)
        {
            ["ClientId"] = session.ClientId,
            ["RedirectUri"] = session.RedirectUri,
            ["State"] = session.State,
            ["CodeChallenge"] = session.CodeChallenge,
            ["CodeChallengeMethod"] = session.CodeChallengeMethod,
            ["PiActivationId"] = session.PiActivationId,
            ["PiActivationUrl"] = session.PiActivationUrl,
            ["ExpiresUtc"] = session.ExpiresUtc
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task<OAuthSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(SessionPartition, sessionId, cancellationToken: cancellationToken);
            var entity = response.Value;
            var expires = entity.GetDateTime("ExpiresUtc") ?? DateTime.MinValue;
            if (expires < DateTime.UtcNow)
            {
                await _table.DeleteEntityAsync(SessionPartition, sessionId, cancellationToken: cancellationToken);
                return null;
            }
            return new OAuthSession
            {
                SessionId = sessionId,
                ClientId = entity.GetString("ClientId") ?? "",
                RedirectUri = entity.GetString("RedirectUri") ?? "",
                State = entity.GetString("State") ?? "",
                CodeChallenge = entity.GetString("CodeChallenge") ?? "",
                CodeChallengeMethod = entity.GetString("CodeChallengeMethod") ?? "",
                PiActivationId = entity.GetString("PiActivationId") ?? "",
                PiActivationUrl = entity.GetString("PiActivationUrl") ?? "",
                ExpiresUtc = expires
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _table.DeleteEntityAsync(SessionPartition, sessionId, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // already gone, fine
        }
    }

    public async Task SaveAuthCodeAsync(IssuedAuthCode code, CancellationToken cancellationToken = default)
    {
        var entity = new TableEntity(CodePartition, code.Code)
        {
            ["PiToken"] = code.PiToken,
            ["CodeChallenge"] = code.CodeChallenge,
            ["CodeChallengeMethod"] = code.CodeChallengeMethod,
            ["RedirectUri"] = code.RedirectUri,
            ["ClientId"] = code.ClientId,
            ["ExpiresUtc"] = code.ExpiresUtc
        };
        await _table.AddEntityAsync(entity, cancellationToken);
    }

    /// <summary>Read and delete an auth code in one go. Returns null if missing or expired.</summary>
    public async Task<IssuedAuthCode?> ConsumeAuthCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(CodePartition, code, cancellationToken: cancellationToken);
            var entity = response.Value;
            // Delete first so a token-endpoint replay can't get a second use even on partial failure.
            await _table.DeleteEntityAsync(CodePartition, code, cancellationToken: cancellationToken);

            var expires = entity.GetDateTime("ExpiresUtc") ?? DateTime.MinValue;
            if (expires < DateTime.UtcNow) return null;

            return new IssuedAuthCode
            {
                Code = code,
                PiToken = entity.GetString("PiToken") ?? "",
                CodeChallenge = entity.GetString("CodeChallenge") ?? "",
                CodeChallengeMethod = entity.GetString("CodeChallengeMethod") ?? "",
                RedirectUri = entity.GetString("RedirectUri") ?? "",
                ClientId = entity.GetString("ClientId") ?? "",
                ExpiresUtc = expires
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public static DateTime NewSessionExpiry() => DateTime.UtcNow.Add(SessionTtl);
    public static DateTime NewCodeExpiry() => DateTime.UtcNow.Add(CodeTtl);
}

public class OAuthSession
{
    public string SessionId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
    public string CodeChallengeMethod { get; set; } = "";
    public string PiActivationId { get; set; } = "";
    public string PiActivationUrl { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}

public class IssuedAuthCode
{
    public string Code { get; set; } = "";
    public string PiToken { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
    public string CodeChallengeMethod { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string ClientId { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}
