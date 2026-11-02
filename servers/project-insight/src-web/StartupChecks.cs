namespace ProjectInsightMCP.Web;

/// <summary>Refuses to start with configuration that would be unsafe on a shared server.</summary>
public static class StartupChecks
{
    public static void Validate(IConfiguration config, WebOptions web, AuthOptions auth)
    {
        if (!string.IsNullOrEmpty(config["ProjectInsight:ApiToken"]))
            throw new InvalidOperationException("ProjectInsight:ApiToken must not be set on the web host: it would override every user's own token.");
        if (!IsHttpsOrigin(config["ProjectInsight:BaseUrl"], allowPath: true))
            throw new InvalidOperationException("ProjectInsight:BaseUrl must be an absolute https URL.");
        if (!IsHttpsOrigin(web.PublicBaseUrl, allowPath: false))
            throw new InvalidOperationException("Web:PublicBaseUrl must be an https origin with no path, such as https://host.");
        if (auth.PiTokenLifetimeDays is < 1 or > 90)
            throw new InvalidOperationException("Auth:PiTokenLifetimeDays must be between 1 and 90.");
        if (auth.PiUserIds.Count == 0)
            throw new InvalidOperationException("Auth:AllowedPiUserIds must list at least one PI user id.");
    }

    private static bool IsHttpsOrigin(string? value, bool allowPath)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
           && (allowPath || uri.AbsolutePath == "/");
}
