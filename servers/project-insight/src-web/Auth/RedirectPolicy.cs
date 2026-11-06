using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Exact redirect URIs only (MCP: "MUST validate exact redirect URIs"), plus the RFC 8252 loopback
/// form native clients use. The allowlist is what keeps a sign-in from reaching anyone else.
/// </summary>
public sealed class RedirectPolicy(IOptions<AuthOptions> auth)
{
    // System.Uri normalises other spellings of loopback (127.1, [0:0:0:0:0:0:0:1]) to the canonical host;
    // that is deliberate, since browsers resolve them to the same loopback address.
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    public bool IsAllowed(string? candidate)
        => Parse(candidate) is { } uri
           && (IsLoopback(uri) || auth.Value.RedirectUris.Select(Parse).Any(allowed => allowed is not null && SameUri(allowed, uri)));

    /// <summary>Whether <paramref name="candidate"/> is one of the client's registered redirect URIs.</summary>
    public static bool Matches(string candidate, IEnumerable<string> registered)
    {
        if (Parse(candidate) is not { } uri)
            return false;
        return registered.Select(Parse).Any(r => r is not null &&
            (IsLoopback(uri) && IsLoopback(r) ? r.Host == uri.Host : SameUri(r, uri)));
    }

    public static bool IsLoopback(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp && LoopbackHosts.Contains(uri.Host) && uri.AbsolutePath == "/callback" && uri.Query.Length == 0;

    public static Uri? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048)
            return null;
        // Uri.TryCreate trims whitespace and control characters; matching must be exact, and CR/LF must never reach a Location header.
        if (value.Any(c => c is <= ' ' or > '~'))
            return null;
        // Encoded characters, backslashes and dot segments have no place in a callback URL.
        if (value.Contains('%') || value.Contains('\\') || value.Contains("/./") || value.Contains("/../") || value.EndsWith("/.") || value.EndsWith("/.."))
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || value.Contains('#'))
            return null;
        if (uri.Scheme == Uri.UriSchemeHttps && !uri.IsDefaultPort)
            return null;
        if (uri.Scheme == Uri.UriSchemeHttp && !LoopbackHosts.Contains(uri.Host))
            return null;
        return uri;
    }

    // Host compares case-insensitively (Uri lower-cases it); path and query exactly.
    private static bool SameUri(Uri a, Uri b)
        => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port && a.AbsolutePath == b.AbsolutePath && a.Query == b.Query;
}
