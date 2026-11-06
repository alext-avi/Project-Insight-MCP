using ProjectInsightMCP.Server;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>The signed-in PI user for this request. Set by <see cref="PiBearerHandler"/>; per-user tool queue key.</summary>
public sealed class PiCaller : IToolCallKey
{
    public string? UserId { get; set; }
    // PI's GUID casing is not guaranteed stable; one user must always share one queue.
    public string Key => UserId?.ToLowerInvariant() ?? throw new InvalidOperationException("No signed-in PI user on this request.");
}
