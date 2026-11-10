using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using ProjectInsightMCP.Server;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.TimeChanges;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Hosting;
using ProjectInsightMCP.Web.Security;
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
        services.AddWebRateLimits();
        services.AddHostedService<PruneService>();
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60)); // let in-flight commits finish on deploy
        services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // App Service's front end is the single proxy hop.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear(); // named KnownNetworks before .NET 10
            o.KnownProxies.Clear();
        });

        services.AddMemoryCache();
        services.AddTransient<PiErrorScrubbingHandler>();
        services.AddHttpClient(PiIdentityClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
            // A redirect would carry the bearer token to wherever PI points; treat any 3xx as an outage instead.
            // No cookie jar: the pooled handler serves every user, so a cookie PI set for one would ride along on the next.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .AddHttpMessageHandler<PiErrorScrubbingHandler>();
        services.AddSingleton<PiIdentityClient>();
        services.AddSingleton<RedirectPolicy>();
        services.AddSingleton<CimdFetcher>();
        services.AddSingleton<ClientResolver>();
        services.AddHttpClient(CimdFetcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // A proxy would make ConnectCallback vet the proxy's address, not the target's.
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                UseCookies = false,
                ConnectCallback = PublicAddressOnly.ConnectAsync
            });
        services.AddHttpClient(PiActivationClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(8))
            // A redirect's Location could repeat the activation code to wherever PI points. No cookie jar, as above.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .AddHttpMessageHandler<PiErrorScrubbingHandler>()
            // Its URLs carry the activation code in the query string.
            .RemoveAllLoggers();
        services.AddSingleton<IPiActivationClient, PiActivationClient>();
        services.AddScoped<ApiTokenAccessor>();
        services.AddScoped<PiCaller>();
        services.AddAuthentication(PiBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PiBearerHandler>(PiBearerHandler.SchemeName, null);
        services.AddAuthorization();

        // Per-request PI services. Nothing here may be a singleton: each request carries its own user's token.
        services.AddHttpClient<ProjectInsightApiService>()
            // Same as the identity client: PI never redirects its API, so a 3xx is an outage, not a new destination,
            // and no cookie jar shared across users.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .AddHttpMessageHandler<PiErrorScrubbingHandler>();
        services.AddScoped(sp => new McpServerService(
            sp.GetRequiredService<ProjectInsightApiService>(),
            sp.GetRequiredService<ILogger<McpServerService>>(),
            sp.GetRequiredService<ApiTokenAccessor>(),
            deviceActivation: null,       // no device-activation sign-in, pending state or credential file on a shared server
            keyVaultTokenService: null));
        services.AddScoped<ITimeEntryGateway, ProjectInsightTimeEntryGateway>();
        services.AddScoped<TimeChangePlanner>();
        services.AddScoped<TimeChangeCommitter>();
        services.AddScoped<TimeChangeService>();

        services.AddProjectInsightMcp(new ProjectInsightMcpOptions
        {
            ServerVersion = ProjectInsightServer.VersionOf(typeof(WebHost).Assembly),
            ExposeTool = name => !HiddenTools.Contains(name)
        }).WithHttpTransport(o => o.Stateless = true)
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var token = context.Services!.GetRequiredService<ApiTokenAccessor>().ApiToken;
                return string.IsNullOrEmpty(token) ? result : ToolResultScrubber.Scrub(result, token);
            }));
        // Per-user tool queue instead of the single-user default.
        services.Replace(ServiceDescriptor.Scoped<IToolCallKey>(sp => sp.GetRequiredService<PiCaller>()));

        // Unlike the rest of the setup this reads configuration before Build(): it is an environment
        // variable on App Service, and tests never set it.
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            services.AddTelemetry();
    }

    /// <summary>The Azure Monitor export, with the redaction processors ahead of its exporters.</summary>
    public static void AddTelemetry(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddOpenTelemetry()
            // Added before UseAzureMonitor, so they run before the exporter sees the activity or log record.
            .WithTracing(t => t.AddProcessor(new QueryRedactionProcessor()))
            .WithLogging(l => l.AddProcessor(sp => new LogRedactionProcessor(sp.GetRequiredService<IHttpContextAccessor>())))
            .UseAzureMonitor();
    }

    /// <summary>Device-activation sign-in tools: the connector's OAuth sign-in replaces them.</summary>
    public static readonly IReadOnlySet<string> HiddenTools =
        new HashSet<string> { "complete_authentication", "link_pi_account", "complete_pi_link" };

    public static void UseProjectInsightWeb(this WebApplication app)
    {
        // Configuration is complete only after Build(), so validation runs here.
        StartupChecks.Validate(app.Configuration,
            app.Services.GetRequiredService<IOptions<WebOptions>>().Value,
            app.Services.GetRequiredService<IOptions<AuthOptions>>().Value,
            app.Environment);

        if (!app.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.Stateless)
            throw new InvalidOperationException("The MCP endpoint must run stateless: per-request PI services depend on it.");

        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        app.UseMiddleware<OriginGuard>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOAuthMetadata();
        app.MapClientRegistration();
        app.MapAuthorize();
        app.MapToken();
        var css = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wwwroot", "oauth.css"));
        app.MapGet("/oauth.css", () => Results.Bytes(css, "text/css")).WithOAuthHeaders();
        var js = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wwwroot", "oauth.js"));
        app.MapGet("/oauth.js", () => Results.Bytes(js, "text/javascript")).WithOAuthHeaders();
        app.MapMcp("/mcp").RequireAuthorization().RequireRateLimiting(RateLimits.Mcp);

        app.MapGet("/healthz", () => Results.Json(new { status = "ok", version = Version }));
        // App Service's Always On pings "/" every few minutes; answer it rather than log a stream of 404s.
        app.MapGet("/", () => Results.Text("Project Insight MCP server. Connect an MCP client to /mcp.", "text/plain"));
    }

    // The deploy sets InformationalVersion to the commit SHA; reported as-is so the smoke test can match it.
    private static readonly string Version =
        typeof(WebHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
