using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using ProjectInsightMCP.Web;
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

    [Fact]
    public async Task Root_answers_the_platform_ping_with_a_plain_page()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
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

    [Theory]
    [InlineData("http://example.com/callback")]
    [InlineData("https://claude.ai/a/../b")]
    [InlineData("https://claude.ai:8443/x")]
    [InlineData("not a url")]
    public void Startup_refuses_a_bad_redirect_allowlist_entry(string entry)
    {
        using var factory = new WebFactory();
        factory.Settings["Auth:AllowedRedirectUris:0"] = "https://claude.ai/api/mcp/auth_callback";
        factory.Settings["Auth:AllowedRedirectUris:1"] = entry;

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains("Auth:AllowedRedirectUris", ex!.ToString());
    }

    [Theory]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "True")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "1")]
    [InlineData("ForwardedHeaders_Enabled", "true")]
    public void Startup_refuses_the_frameworks_own_forwarded_headers_switch(string key, string value)
    {
        using var factory = new WebFactory();
        factory.Settings[key] = value;

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", ex!.ToString());
    }

    // Outside Development and Testing, storage settings must be usable at startup, not at the first request.
    private static WebFactory Production(Action<Dictionary<string, string?>> settings)
    {
        var factory = new WebFactory { Environment = "Production" };
        factory.Settings.Remove("Storage:Mode");
        settings(factory.Settings);
        return factory;
    }

    [Fact]
    public void Startup_refuses_local_storage_in_production()
    {
        using var factory = Production(s => s["Storage:Mode"] = "Local");
        var ex = Record.Exception(() => factory.Client());
        Assert.NotNull(ex);
        Assert.Contains("Storage:Mode", ex!.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://acct.table.core.windows.net", null)]
    [InlineData(null, "https://acct.blob.core.windows.net")]
    [InlineData("http://acct.table.core.windows.net", "https://acct.blob.core.windows.net")]
    [InlineData("acct.table.core.windows.net", "https://acct.blob.core.windows.net")]
    public void Startup_refuses_azure_storage_without_a_connection_string_or_both_https_endpoints(string? table, string? blob)
    {
        using var factory = Production(s =>
        {
            s["Storage:TableEndpoint"] = table;
            s["Storage:BlobEndpoint"] = blob;
        });
        var ex = Record.Exception(() => factory.Client());
        Assert.NotNull(ex);
        Assert.Contains("Storage needs either", ex!.ToString());
    }

    [Theory]
    [InlineData("UseDevelopmentStorage=true", null, null)]
    [InlineData(null, "https://acct.table.core.windows.net", "https://acct.blob.core.windows.net")]
    public void Startup_accepts_azure_storage_with_a_connection_string_or_both_https_endpoints(string? conn, string? table, string? blob)
    {
        // Direct call: starting a Production host would connect to the stores in the background.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectInsight:BaseUrl"] = "https://pi.test/api/",
            ["Storage:ConnectionString"] = conn,
            ["Storage:TableEndpoint"] = table,
            ["Storage:BlobEndpoint"] = blob
        }).Build();

        StartupChecks.Validate(config, new WebOptions { PublicBaseUrl = "https://mcp.test" },
            new AuthOptions { AllowedPiUserIds = "u" }, new HostingEnvironment { EnvironmentName = "Production" });
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
