namespace ProjectInsightMCP.Services;

/// <summary>
/// Scoped service to hold the per-request API token.
/// This allows the API token to be set from the request header and accessed by downstream services.
/// </summary>
public class ApiTokenAccessor
{
    /// <summary>
    /// The API token for the current request
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// The Entra object ID of the authenticated user (set by EasyAuth)
    /// </summary>
    public string? EntraOid { get; set; }

    /// <summary>
    /// Whether an API token has been provided for this request
    /// </summary>
    public bool HasToken => !string.IsNullOrEmpty(ApiToken);

    /// <summary>
    /// Whether an Entra identity is present (user authenticated via EasyAuth)
    /// </summary>
    public bool HasEntraIdentity => !string.IsNullOrEmpty(EntraOid);
}
