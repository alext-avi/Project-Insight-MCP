using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ProjectInsightMCP.Web.Hosting;

public static class RateLimits
{
    public const string Register = "register", Authorize = "authorize", Complete = "complete", Token = "token", Mcp = "mcp";

    public static IServiceCollection AddWebRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Register, ctx => Fixed(ctx, 10, TimeSpan.FromHours(1)));
            o.AddPolicy(Authorize, ctx => Fixed(ctx, 30, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Complete, ctx => Fixed(ctx, 20, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Token, ctx => Fixed(ctx, 30, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Mcp, ctx => Fixed(ctx, 300, TimeSpan.FromMinutes(1)));
        });
        // Tokens PI hasn't vouched for yet, each of which costs a PI call.
        services.AddSingleton(PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            })));
        return services;
    }

    /// <summary>The caller's address, with IPv6 grouped by /64 (one subscriber's block).</summary>
    public static string ClientKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
            return "unknown";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static RateLimitPartition<string> Fixed(HttpContext ctx, int permits, TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = window, QueueLimit = 0
        });
}
