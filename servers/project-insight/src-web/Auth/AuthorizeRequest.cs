using Microsoft.AspNetCore.WebUtilities;

namespace ProjectInsightMCP.Web.Auth;

public sealed record AuthorizeRequest(OAuthClient Client, string RedirectUri, string? State, string CodeChallenge, string? Resource)
{
    public static async Task<(AuthorizeRequest? Request, IResult? Failure)> ValidateAsync(
        IReadOnlyDictionary<string, string?> p, ClientResolver clients, RedirectPolicy policy, WebOptions web, CancellationToken ct)
    {
        string? Get(string key) => p.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

        var clientId = Get("client_id");
        var client = clientId is null ? null : await clients.ResolveAsync(clientId, ct);
        if (client is null)
            return (null, AuthorizePages.Error(400, "This app isn't known to this server."));

        var redirect = Get("redirect_uri");
        if (redirect is null || !policy.IsAllowed(redirect) || !RedirectPolicy.Matches(redirect, client.RedirectUris))
            return (null, AuthorizePages.Error(400, "This app's return address isn't allowed."));

        var state = Get("state");
        IResult Back(string error, string description)
        {
            var query = new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description };
            if (state is not null)
                query["state"] = state;
            query["iss"] = web.Issuer;
            // redirect has passed Parse: visible ASCII only, so no header injection.
            return Results.Redirect(QueryHelpers.AddQueryString(redirect, query));
        }

        if (Get("response_type") != "code")
            return (null, Back("unsupported_response_type", "Only response_type=code is supported."));
        var challenge = Get("code_challenge");
        if (Get("code_challenge_method") != "S256" || challenge is null || !Pkce.IsValidChallenge(challenge))
            return (null, Back("invalid_request", "PKCE with code_challenge_method=S256 is required."));
        if (Get("scope") is { } scope
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(s => s is not ("pi" or "offline_access")))
            return (null, Back("invalid_scope", "Only the scopes pi and offline_access are supported."));
        var resource = Get("resource");
        if (resource is not null && !web.IsResource(resource))
            return (null, Back("invalid_target", "The resource parameter doesn't name this server."));

        return (new AuthorizeRequest(client, redirect, state, challenge, resource), null);
    }
}
