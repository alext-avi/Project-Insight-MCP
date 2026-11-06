namespace ProjectInsightMCP.Web.Auth;

/// <summary>What an authorization code stands for. Stored only sealed under the code (CodeProtector).</summary>
public sealed record CodePayload(string PiToken, DateTimeOffset ExpiresAt, string ClientId, string RedirectUri,
    string CodeChallenge, string? Resource, string PiUserId);
