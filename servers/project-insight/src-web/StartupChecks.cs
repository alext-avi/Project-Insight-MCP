using ProjectInsightMCP.Web.Auth;

namespace ProjectInsightMCP.Web;

/// <summary>Refuses to start with configuration that would be unsafe on a shared server.</summary>
public static class StartupChecks
{
    private static readonly string[] ListSettings =
        [nameof(AuthOptions.AllowedRedirectUris), nameof(AuthOptions.AllowedCimdHosts), nameof(AuthOptions.AllowedOrigins)];

    public static void Validate(IConfiguration config, WebOptions web, AuthOptions auth, IHostEnvironment environment)
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
        // The binder can't turn a single value into a list, and would leave the defaults in place without a word.
        foreach (var list in ListSettings)
            if (config[$"Auth:{list}"] is not null)
                throw new InvalidOperationException(
                    $"Auth:{list} is a list: set each entry as its own setting (Auth__{list}__0, Auth__{list}__1, …), not as one value.");
        // A bad entry would otherwise be dropped without a word, leaving a client unable to sign in.
        foreach (var entry in auth.AllowedRedirectUris ?? [])
            if (RedirectPolicy.Parse(entry) is null)
                throw new InvalidOperationException($"Auth:AllowedRedirectUris has an entry that is not a valid callback URL: '{entry}'.");
        // UseForwardedHeaders runs in the app with the proxy lists cleared; a second pass by the framework would trust client-sent headers.
        // The framework reads ForwardedHeaders_Enabled, which the ASPNETCORE_-prefixed env var also maps to.
        if (IsTrue(config["ASPNETCORE_FORWARDEDHEADERS_ENABLED"]) || IsTrue(config["ForwardedHeaders_Enabled"]))
            throw new InvalidOperationException("ASPNETCORE_FORWARDEDHEADERS_ENABLED must be false or unset: the app handles forwarded headers itself, and a second pass would let clients spoof their address.");
        CheckStorage(config, environment);
    }

    // Everything that would otherwise fail only on the first request.
    private static void CheckStorage(IConfiguration config, IHostEnvironment environment)
    {
        var local = string.Equals(config["Storage:Mode"], "Local", StringComparison.OrdinalIgnoreCase);
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return;
        if (local)
            throw new InvalidOperationException("Storage:Mode Local keeps everything in memory and on local disk; it is only for Development.");
        if (!string.IsNullOrEmpty(config["Storage:ConnectionString"]))
            return;
        if (!IsHttpsUrl(config["Storage:TableEndpoint"]) || !IsHttpsUrl(config["Storage:BlobEndpoint"]))
            throw new InvalidOperationException("Storage needs either Storage:ConnectionString or absolute https Storage:TableEndpoint and Storage:BlobEndpoint.");
    }

    private static bool IsTrue(string? value) => bool.TryParse(value, out var b) ? b : value == "1";

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsHttpsOrigin(string? value, bool allowPath)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
           && (allowPath || uri.AbsolutePath == "/");
}
