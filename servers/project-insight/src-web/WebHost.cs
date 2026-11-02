using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ProjectInsightMCP.Server;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web;

public static class WebHost
{
    public static void AddProjectInsightWeb(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<WebOptions>().BindConfiguration("Web");
        services.AddOptions<AuthOptions>().BindConfiguration("Auth");
        services.AddSingleton(TimeProvider.System);
        services.AddWebStorage();

        services.AddMemoryCache();
        services.AddHttpClient(PiIdentityClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<PiIdentityClient>();
        services.AddScoped<ApiTokenAccessor>();
        services.AddScoped<PiCaller>();
        services.AddAuthentication(PiBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PiBearerHandler>(PiBearerHandler.SchemeName, null);
        services.AddAuthorization();

        services.AddProjectInsightMcp(new ProjectInsightMcpOptions
        {
            ServerVersion = ProjectInsightServer.VersionOf(typeof(WebHost).Assembly),
            ExposeTool = name => !HiddenTools.Contains(name)
        }).WithHttpTransport(o => o.Stateless = true);
        // Per-user tool queue instead of the single-user default.
        services.Replace(ServiceDescriptor.Scoped<IToolCallKey>(sp => sp.GetRequiredService<PiCaller>()));
    }

    /// <summary>Device-activation sign-in tools: the connector's OAuth sign-in replaces them.</summary>
    public static readonly IReadOnlySet<string> HiddenTools =
        new HashSet<string> { "complete_authentication", "link_pi_account", "complete_pi_link" };

    public static void UseProjectInsightWeb(this WebApplication app)
    {
        // Configuration is complete only after Build(), so validation runs here.
        StartupChecks.Validate(app.Configuration,
            app.Services.GetRequiredService<IOptions<WebOptions>>().Value,
            app.Services.GetRequiredService<IOptions<AuthOptions>>().Value);

        if (!app.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.Stateless)
            throw new InvalidOperationException("The MCP endpoint must run stateless: per-request PI services depend on it.");

        app.UseMiddleware<OriginGuard>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOAuthMetadata();
        app.MapMcp("/mcp").RequireAuthorization();

        app.MapGet("/healthz", () => Results.Json(new { status = "ok", version = Version }));
    }

    // The deploy sets InformationalVersion to the commit SHA; reported as-is so the smoke test can match it.
    private static readonly string Version =
        typeof(WebHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
