using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Stdio;

/// <summary>
/// STDIO transport host for Project Insight MCP Server.
/// This allows the MCP server to be run locally via "dotnet run" and communicate
/// with MCP clients (VS Code, Claude Desktop, etc.) over stdin/stdout.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

        // Environment variables use __ as separator (e.g., ProjectInsight__BaseUrl)
        // which gets converted to : for hierarchical config (ProjectInsight:BaseUrl)
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables();

        if (string.IsNullOrEmpty(builder.Configuration["ProjectInsight:BaseUrl"]))
        {
            Console.Error.WriteLine("ERROR: ProjectInsight__BaseUrl environment variable is required");
            Console.Error.WriteLine("Example: ProjectInsight__BaseUrl=https://your-tenant.projectinsight.net/api/");
            Environment.Exit(1);
        }

        // All logging goes to stderr so stdout is reserved for MCP protocol
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddProjectInsightServices(builder.Configuration);
        // Like WithStdioServerTransport (incl. its BufferedStream on stdout), but stdin EOF
        // answers the requests already read instead of dropping them.
        builder.Services.AddProjectInsightMcp().WithDrainingStreamServerTransport(
            Console.OpenStandardInput, () => new BufferedStream(Console.OpenStandardOutput()));

        var app = builder.Build();

        // Build the tool stack now so bad configuration (e.g. a malformed BaseUrl) fails at
        // startup, as before the SDK migration, rather than on every tool call.
        try
        {
            app.Services.GetRequiredService<McpServerService>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            Environment.Exit(1);
        }

        await app.RunAsync();
    }
}
