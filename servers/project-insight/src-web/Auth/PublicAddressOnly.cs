using System.Net;
using System.Net.Sockets;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Second line of defence for client-metadata fetches (the host allowlist is the first): connect only
/// to public addresses, so a DNS answer can't steer our server at internal endpoints.
/// </summary>
public static class PublicAddressOnly
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8), (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8), (IPAddress.Parse("169.254.0.0"), 16), (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24), (IPAddress.Parse("192.168.0.0"), 16), (IPAddress.Parse("198.18.0.0"), 15),
        (IPAddress.Parse("224.0.0.0"), 4), (IPAddress.Parse("240.0.0.0"), 4),
        (IPAddress.Parse("168.63.129.16"), 32) // Azure platform endpoint: public range, internal service
    ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !BlockedV4.Any(b => InNetwork(address, b.Network, b.Prefix));

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes[..12].All(b => b == 0))
            return false; // IPv4-compatible (::/96): some stacks route ::a.b.c.d to the IPv4 address
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return false; // 6to4 embeds an IPv4 address
        if (bytes is [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, ..])
            return IsPublic(new IPAddress(bytes[12..])); // NAT64 well-known prefix
        if (bytes is [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01, ..])
            return false; // local-use NAT64 prefix (64:ff9b:1::/48)
        return true;
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
            throw new HttpRequestException("Client metadata host does not resolve to public addresses only.");
        // Every address was vetted above, so any of them may be tried; keep the last failure if none connects.
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last!;
    }

    private static bool InNetwork(IPAddress address, IPAddress network, int prefix)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = (byte)(0x80 >> (bit % 8));
            if ((a[bit / 8] & mask) != (n[bit / 8] & mask))
                return false;
        }
        return true;
    }
}
