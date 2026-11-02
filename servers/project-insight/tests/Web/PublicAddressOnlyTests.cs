using System.Net;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class PublicAddressOnlyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("160.79.104.10")]
    [InlineData("2001:4860:4860::8888")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(PublicAddressOnly.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("::127.0.0.1")]
    [InlineData("::8.8.8.8")]
    [InlineData("64:ff9b:1::a00:1")]
    public void Internal_and_tunnelled_addresses_are_refused(string ip) => Assert.False(PublicAddressOnly.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Connect_callback_refuses_loopback_hosts_before_connecting(string host)
    {
        // A live loopback listener: without the guard the request would connect to it.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new HttpClient(new SocketsHttpHandler { ConnectCallback = PublicAddressOnly.ConnectAsync }) { Timeout = TimeSpan.FromSeconds(5) };
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://{host}:{port}/x"));
            Assert.Contains("does not resolve to public addresses only", ex.ToString());
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }
}
