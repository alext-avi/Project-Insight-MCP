using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace ProjectInsightMCP.Tests.Web;

public static class McpConnect
{
    /// <summary>An MCP client for the in-memory web host, sending <paramref name="token"/> as the bearer token.</summary>
    public static Task<McpClient> ConnectAsync(this WebFactory factory, string token)
        => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(WebFactory.BaseAddress, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        }, factory.Client(), NullLoggerFactory.Instance, ownsHttpClient: true));
}
