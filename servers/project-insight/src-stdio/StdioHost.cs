using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectInsightMCP.Server;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.TimeChanges;
using Shared.MCP;

namespace ProjectInsightMCP.Stdio;

/// <summary>Stdio-only wiring: the single-user service graph, startup pruning and the draining transport.</summary>
public static class StdioHost
{
    /// <summary>
    /// Registers Project Insight services. Everything is a singleton: one stdio process serves
    /// one user, and device activation keeps pending state between tool calls.
    /// </summary>
    public static IServiceCollection AddProjectInsightServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CredentialStorageService>();
        services.AddSingleton(sp =>
        {
            // Env var token wins; otherwise fall back to credentials stored by device activation.
            var accessor = new ApiTokenAccessor();
            var apiToken = configuration["ProjectInsight:ApiToken"];
            if (!string.IsNullOrEmpty(apiToken))
            {
                accessor.ApiToken = apiToken;
            }
            else
            {
                var stored = sp.GetRequiredService<CredentialStorageService>().LoadCredential();
                if (stored != null)
                {
                    accessor.ApiToken = stored.ApiToken;
                    Console.Error.WriteLine($"Loaded stored credentials (expires {stored.ExpiresUtc:yyyy-MM-dd})");
                }
                else
                {
                    Console.Error.WriteLine("No API token configured. Will prompt for authentication on first use.");
                }
            }
            return accessor;
        });

        services.AddHttpClient();
        services.AddSingleton<DeviceActivationService>();
        services.AddSingleton<ProjectInsightAuthService>();
        services.AddSingleton<ProjectInsightApiService>();
        services.AddSingleton<McpServerService>();

        // Time-change drafts (plan -> review card -> confirm/cancel/undo).
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDraftStore>(sp => new FileDraftStore(
            configuration["ProjectInsight:DraftsPath"] is { Length: > 0 } path ? path : FileDraftStore.DefaultRoot,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITimeEntryGateway, ProjectInsightTimeEntryGateway>();
        services.AddSingleton<TimeChangePlanner>();
        services.AddSingleton<TimeChangeCommitter>();
        services.AddSingleton<TimeChangeService>();
        return services;
    }

    /// <summary>
    /// Deletes expired drafts. A failure must never stop the server, and stdout is the protocol
    /// stream, so it is reported on stderr only.
    /// </summary>
    public static async Task PruneDraftsAtStartupAsync(IServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<IDraftStore>().PruneAllAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not prune time-change drafts: {ex.Message}");
        }
    }

    /// <summary>
    /// Serves MCP over <paramref name="input"/>/<paramref name="output"/> like the SDK's stdio
    /// transport, except that end of input first waits for pending answers (see <see cref="DrainingStdio"/>).
    /// </summary>
    public static IMcpServerBuilder WithDrainingStreamServerTransport(this IMcpServerBuilder builder,
        Func<Stream> input, Func<Stream> output, TimeSpan? quietPeriod = null)
    {
        // Registers the single-session hosted server; the transport itself is replaced below.
        builder.WithStdioServerTransport();
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(sp =>
        {
            var gate = sp.GetRequiredService<ToolCallGate>();
            var stdio = new DrainingStdio(input(), output(), () => gate.IsBusy, quietPeriod);
            return new StreamServerTransport(stdio.Input, stdio.Output,
                sp.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInfo?.Name,
                sp.GetService<ILoggerFactory>());
        }));
        return builder;
    }
}
