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

    /// <summary>Comma-separated PI user ids allowed to use the server.</summary>
    public string AllowedPiUserIds { get; set; } = "";
    public int PiTokenLifetimeDays { get; set; } = 90;
    // Nullable so configuration replaces the defaults instead of appending to them.
    public string[]? AllowedRedirectUris { get; set; }
    public string[]? AllowedCimdHosts { get; set; }
    public string[]? AllowedOrigins { get; set; }

    public IReadOnlySet<string> PiUserIds => AllowedPiUserIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Email domains, such as example.com, whose PI users may use the server as well as those in
    /// <see cref="AllowedPiUserIds"/>. The whole domain after the @ must match, ignoring case: list subdomains too.
    /// </summary>
    public string[]? AllowedEmailDomains { get; set; }
    public IReadOnlySet<string> EmailDomains => (AllowedEmailDomains ?? [])
        .Select(d => d.Trim()).Where(d => d.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the PI user is listed by id, or the email PI holds for them is on an allowed domain.</summary>
    public bool Allows(string userId, string? email) =>
        PiUserIds.Contains(userId) || (DomainOf(email) is { } domain && EmailDomains.Contains(domain));

    /// <summary>The part after the one @ of an address, or null when it isn't a single-@ address.</summary>
    public static string? DomainOf(string? email)
    {
        var address = email?.Trim();
        if (string.IsNullOrEmpty(address))
            return null;
        var at = address.IndexOf('@');
        return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 1 ? address[(at + 1)..] : null;
    }
    public IReadOnlyList<string> RedirectUris => AllowedRedirectUris ?? DefaultRedirectUris;
    public IReadOnlySet<string> CimdHosts => (AllowedCimdHosts ?? DefaultCimdHosts).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Origins => (AllowedOrigins ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
