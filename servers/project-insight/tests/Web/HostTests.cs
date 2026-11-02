using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class HostTests
{
    [Fact]
    public async Task Health_reports_status_and_version_only()
    {
        using var factory = new WebFactory();
        var json = await factory.Client().GetFromJsonAsync<JsonElement>("/healthz");

        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("ProjectInsight:ApiToken", "static-token", "ProjectInsight:ApiToken")]
    [InlineData("Web:PublicBaseUrl", "http://mcp.test", "Web:PublicBaseUrl")]
    [InlineData("Web:PublicBaseUrl", "https://mcp.test/sub", "Web:PublicBaseUrl")]
    [InlineData("ProjectInsight:BaseUrl", "", "ProjectInsight:BaseUrl")]
    [InlineData("Auth:AllowedPiUserIds", "", "Auth:AllowedPiUserIds")]
    [InlineData("Auth:PiTokenLifetimeDays", "91", "Auth:PiTokenLifetimeDays")]
    public void Startup_refuses_unsafe_configuration(string key, string value, string named)
    {
        using var factory = new WebFactory();
        factory.Settings[key] = value;

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains(named, ex!.ToString());
    }

    [Fact]
    public void Startup_refuses_a_stateful_mcp_endpoint()
    {
        using var factory = new WebFactory();
        factory.ExtraServices = services => services.Configure<HttpServerTransportOptions>(o => o.Stateless = false);

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains("stateless", ex!.ToString());
    }
}
