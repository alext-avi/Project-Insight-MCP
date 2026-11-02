using System.Text.RegularExpressions;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

public sealed record OAuthClient(string ClientId, string? Name, IReadOnlyList<string> RedirectUris, bool IsMetadataDocument);

/// <summary>A client is either a CIMD URL or a registration stored by /register; nothing else exists.</summary>
public sealed partial class ClientResolver(IClientStore store, CimdFetcher cimd, RedirectPolicy policy)
{
    public async Task<OAuthClient?> ResolveAsync(string clientId, CancellationToken ct)
    {
        if (clientId.StartsWith("https://", StringComparison.Ordinal))
            return await cimd.FetchAsync(clientId, ct) is { } doc
                ? new OAuthClient(clientId, doc.ClientName, doc.RedirectUris.Where(policy.IsAllowed).ToList(), IsMetadataDocument: true)
                : null;
        // Only the shape /register issues (dcr_ + 16 random bytes in base64url) can name a stored client.
        if (!IssuedId().IsMatch(clientId))
            return null;
        return await store.GetAsync(clientId, ct) is { } registered
            ? new OAuthClient(registered.ClientId, registered.ClientName, registered.RedirectUris, IsMetadataDocument: false)
            : null;
    }

    [GeneratedRegex("^dcr_[A-Za-z0-9_-]{22}$")]
    private static partial Regex IssuedId();
}
