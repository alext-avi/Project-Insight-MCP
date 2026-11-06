using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.TimeChanges;
using Shared.MCP;

namespace ProjectInsightMCP.Server;

/// <summary>Which tools a host exposes, and the version it reports.</summary>
public sealed class ProjectInsightMcpOptions
{
    /// <summary>Reported in serverInfo. Hosts pass their own assembly's version.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>False hides a tool from tools/list and refuses calls to it.</summary>
    public Func<string, bool> ExposeTool { get; init; } = _ => true;
}

/// <summary>Which <see cref="ToolCallGate"/> queue a tool call joins: one queue per user. Resolved from the MCP request's service scope; a scoped implementation sees the HTTP request scope only when the transport is stateless.</summary>
public interface IToolCallKey
{
    string Key { get; }
}

internal sealed class SingleUserToolCallKey : IToolCallKey
{
    public static readonly SingleUserToolCallKey Instance = new();
    public string Key => "";
}

/// <summary>
/// The SDK dispatches requests concurrently; the handlers depend on one call at a time per user
/// (e.g. device activation's check-then-start of a pending sign-in). One queue per key.
/// </summary>
internal sealed class ToolCallGate
{
    // Bounded by the number of users allowed on a server; entries are never removed.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queues = new();
    private int _active;

    /// <summary>True while any tool call, in any queue, is queued or running.</summary>
    public bool IsBusy => Volatile.Read(ref _active) > 0;

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> call, CancellationToken whileQueued)
    {
        var queue = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        Interlocked.Increment(ref _active);
        try
        {
            await queue.WaitAsync(whileQueued);
            try
            {
                return await call();
            }
            finally
            {
                queue.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

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
    /// Registers the MCP server with the Project Insight tools. The caller chooses the transport.
    /// </summary>
    public static IMcpServerBuilder AddProjectInsightMcp(this IServiceCollection services, ProjectInsightMcpOptions? options = null)
    {
        options ??= new ProjectInsightMcpOptions();
        services.TryAddSingleton<ToolCallGate>();
        // One queue for the whole process unless the host registers a per-user key first.
        services.TryAddSingleton<IToolCallKey>(SingleUserToolCallKey.Instance);

        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = options.ServerVersion ?? VersionOf(typeof(ProjectInsightServer).Assembly),
                    Description = ServerDescription
                };
            })
            .WithListToolsHandler((ctx, ct) =>
            {
                var tools = McpServerService.InitializeTools()
                    .Where(t => options.ExposeTool(t.Name))
                    .Select(ToSdkTool)
                    .ToList();
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
                return ToSdkResult(await mcp.ExecuteToolAsync(call, CancellationToken.None));
            })
            .WithTools(TimeChangeTools.Create().Where(t => options.ExposeTool(t.ProtocolTool.Name)))
            .WithResources([TimeChangeTools.CreateCard()])
            // Every tool call, adapter or SDK-native, goes through the gate, in its caller's queue.
            // Cancellation only applies while a call is queued. Once started it runs to completion,
            // as before the SDK (the SDK then sends no response, per spec): stopping midway could
            // leave a multi-step write (e.g. add_task_resource, or a time-change commit) half-applied.
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
            {
                var name = context.Params?.Name ?? "";
                if (!options.ExposeTool(name))
                    throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
                var services = context.Services!;
                return new ValueTask<CallToolResult>(services.GetRequiredService<ToolCallGate>().RunAsync(
                    services.GetRequiredService<IToolCallKey>().Key,
                    () => next(context, CancellationToken.None).AsTask(), cancellationToken));
            }));
    }

    public static string VersionOf(Assembly assembly)
        => VersionOf(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    internal static string VersionOf(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
    }

    internal static Tool ToSdkTool(McpTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema, SchemaOptions),
        Annotations = tool.Annotations is null ? null : new ToolAnnotations { DestructiveHint = tool.Annotations.DestructiveHint }
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
}
