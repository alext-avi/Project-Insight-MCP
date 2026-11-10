using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>The web host with in-memory stores, a temp drafts folder and every outbound call sent to <see cref="Pi"/>.</summary>
public sealed class WebFactory : WebApplicationFactory<Program>
{
    public static readonly Uri BaseAddress = new("https://mcp.test/");
    private readonly string _drafts = Path.Combine(Path.GetTempPath(), "pi-web-drafts-" + Guid.NewGuid().ToString("N"));

    public PiStub Pi { get; } = new();

    /// <summary>Hosting environment; tests of production-only startup checks change it.</summary>
    public string Environment { get; set; } = "Testing";

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Web:PublicBaseUrl"] = "https://mcp.test",
        ["ProjectInsight:BaseUrl"] = "https://pi.test/api/",
        ["Auth:AllowedPiUserIds"] = $"{PiStub.UserA},{PiStub.UserB}",
        ["Storage:Mode"] = "Local",
        ["AllowedHosts"] = "*"
    };

    public Action<IServiceCollection>? ExtraServices { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Settings["ProjectInsight:DraftsPath"] = _drafts;
        builder.UseEnvironment(Environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            // Every HttpClient the app creates talks to the stub, never the network.
            services.ConfigureAll<HttpClientFactoryOptions>(o =>
                o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = Pi));
            ExtraServices?.Invoke(services);
        });
    }

    public HttpClient Client() => CreateDefaultClient(BaseAddress);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_drafts))
            Directory.Delete(_drafts, recursive: true);
    }
}
