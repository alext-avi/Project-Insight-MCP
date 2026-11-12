namespace ProjectInsightMCP.Web;

/// <summary>Section "Web". The public base URL is configuration, never the request's Host header.</summary>
public sealed class WebOptions
{
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>The OAuth issuer: the base URL without a trailing slash, byte-identical everywhere.</summary>
    public string Issuer => PublicBaseUrl.TrimEnd('/');
    public string Resource => Issuer + "/mcp";
    public string ResourceMetadataUrl => Issuer + "/.well-known/oauth-protected-resource/mcp";

    /// <summary>
    /// Whether a client's resource parameter names this server. Scheme and host compare
    /// case-insensitively and a trailing slash is ignored, as the MCP spec asks.
    /// </summary>
    public bool IsResource(string? candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var given) || !Uri.TryCreate(Resource, UriKind.Absolute, out var ours))
            return false;
        return string.Equals(given.Scheme, ours.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(given.Host, ours.Host, StringComparison.OrdinalIgnoreCase)
            && given.Port == ours.Port
            && given.AbsolutePath.TrimEnd('/') == ours.AbsolutePath
            && given.Query.Length == 0 && given.Fragment.Length == 0;
    }
}

/// <summary>Section "Auth".</summary>
public sealed class AuthOptions
{
    public static readonly string[] DefaultRedirectUris =
        ["https://claude.ai/api/mcp/auth_callback", "https://chatgpt.com/connector_platform_oauth_redirect"];
    public static readonly string[] DefaultCimdHosts = ["claude.ai", "chatgpt.com"];

    public int PiTokenLifetimeDays { get; set; } = 90;
    // Nullable so configuration replaces the defaults instead of appending to them.
    public string[]? AllowedRedirectUris { get; set; }
    public string[]? AllowedCimdHosts { get; set; }
    public string[]? AllowedOrigins { get; set; }

    public IReadOnlyList<string> RedirectUris => AllowedRedirectUris ?? DefaultRedirectUris;
    public IReadOnlySet<string> CimdHosts => (AllowedCimdHosts ?? DefaultCimdHosts).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Origins => (AllowedOrigins ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
