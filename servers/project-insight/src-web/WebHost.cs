using System.Reflection;
using Microsoft.Extensions.Options;
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
    }

    public static void UseProjectInsightWeb(this WebApplication app)
    {
        // Configuration is complete only after Build(), so validation runs here.
        StartupChecks.Validate(app.Configuration,
            app.Services.GetRequiredService<IOptions<WebOptions>>().Value,
            app.Services.GetRequiredService<IOptions<AuthOptions>>().Value);

        app.MapGet("/healthz", () => Results.Json(new { status = "ok", version = Version }));
    }

    // The deploy sets InformationalVersion to the commit SHA; reported as-is so the smoke test can match it.
    private static readonly string Version =
        typeof(WebHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
