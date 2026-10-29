using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectInsightMCP.Services;
using Shared.MCP;

namespace ProjectInsightMCP.Stdio;

/// <summary>
/// Wires the transport-agnostic <see cref="McpServerService"/> into the official MCP SDK.
/// Protocol handling (handshake, version negotiation, JSON-RPC framing) belongs to the SDK;
/// tool definitions and handlers stay in Core and are adapted at this boundary.
/// </summary>
public static class ProjectInsightServer
{
    public const string ServerName = "Project Insight MCP Server";

    private const string ServerDescription = "Project management server for Project Insight. IMPORTANT: Use the 'fields' parameter to control response size and reduce token usage. Presets: 'minimal' (Id,Name), 'summary' (key fields), 'standard', 'full'. Default is 'summary'. Always use 'minimal' or 'summary' unless you specifically need more data.";

    private static readonly JsonSerializerOptions SchemaOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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
        services.AddSingleton<ToolCallGate>();
        return services;
    }

    /// <summary>
    /// Registers the MCP server with the Project Insight tools. The caller chooses the transport.
    /// </summary>
    public static IMcpServerBuilder AddProjectInsightMcp(this IServiceCollection services)
    {
        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = ServerVersion(),
                    Description = ServerDescription
                };
            })
            .WithListToolsHandler((ctx, ct) =>
            {
                var tools = McpServerService.InitializeTools().Select(ToSdkTool).ToList();
                return ValueTask.FromResult(new ListToolsResult { Tools = tools });
            })
            .WithCallToolHandler(async (ctx, ct) =>
            {
                if (ctx.Params is null)
                    throw new McpProtocolException("Invalid params: parameters must be provided", McpErrorCode.InvalidParams);

                var mcp = ctx.Services!.GetRequiredService<McpServerService>();
                var call = new McpToolCall
                {
                    Name = ctx.Params.Name,
                    Arguments = ToLegacyArguments(ctx.Params.Arguments)
                };

                // Cancellation only applies while the call is queued. Once started it runs to
                // completion, as before the SDK (the SDK then sends no response, per spec):
                // stopping midway could leave a multi-step write (e.g. add_task_resource) half-applied.
                return await ctx.Services!.GetRequiredService<ToolCallGate>().RunAsync(
                    async () => ToSdkResult(await mcp.ExecuteToolAsync(call, CancellationToken.None)), ct);
            });
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

    /// <summary>
    /// The SDK dispatches requests concurrently; the pre-SDK loop ran one at a time and the
    /// handlers depend on it (e.g. device activation's check-then-start of a pending sign-in).
    /// </summary>
    internal sealed class ToolCallGate
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _active;

        /// <summary>True while any tool call is queued or running.</summary>
        public bool IsBusy => Volatile.Read(ref _active) > 0;

        public async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken whileQueued)
        {
            Interlocked.Increment(ref _active);
            try
            {
                await _semaphore.WaitAsync(whileQueued);
                try
                {
                    return await call();
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    internal static Tool ToSdkTool(McpTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema, SchemaOptions)
    };

    internal static CallToolResult ToSdkResult(McpToolResult result) => new()
    {
        Content = result.Content
            .Select(c => (ContentBlock)new TextContentBlock { Text = c.Text ?? string.Empty })
            .ToList(),
        IsError = result.IsError
    };

    // Handlers expect boxed JsonElement values, as the pre-SDK transport produced.
    internal static Dictionary<string, object> ToLegacyArguments(IDictionary<string, JsonElement>? arguments)
        => arguments?.ToDictionary(kv => kv.Key, kv => (object)kv.Value) ?? new Dictionary<string, object>();

    private static string ServerVersion()
    {
        var version = typeof(ProjectInsightServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
