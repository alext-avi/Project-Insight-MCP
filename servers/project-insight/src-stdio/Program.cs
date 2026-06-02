using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.Services;
using Shared.MCP;

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
        // All logging goes to stderr so stdout is reserved for MCP protocol
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(LogLevel.Warning); // Only show warnings+ in stderr
        });

        var logger = loggerFactory.CreateLogger<Program>();
        logger.LogInformation("Starting Project Insight MCP Server (STDIO mode)");

        try
        {
            // Build configuration from environment variables and config files
            // Environment variables use __ as separator (e.g., ProjectInsight__BaseUrl)
            // which gets converted to : for hierarchical config (ProjectInsight:BaseUrl)
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            
            // Validate required configuration
            var baseUrl = configuration["ProjectInsight:BaseUrl"];
            var apiToken = configuration["ProjectInsight:ApiToken"];

            if (string.IsNullOrEmpty(baseUrl))
            {
                Console.Error.WriteLine("ERROR: ProjectInsight__BaseUrl environment variable is required");
                Console.Error.WriteLine("Example: ProjectInsight__BaseUrl=https://your-tenant.projectinsight.net/api/");
                Environment.Exit(1);
            }

            // Set up dependency injection
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging(builder =>
            {
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.SetMinimumLevel(LogLevel.Warning);
            });

            // Register credential storage
            services.AddSingleton<CredentialStorageService>();

            // Register ApiTokenAccessor — try env var first, then stored credentials
            var credStorage = new CredentialStorageService(
                loggerFactory.CreateLogger<CredentialStorageService>());
            var tokenAccessor = new ApiTokenAccessor();
            if (!string.IsNullOrEmpty(apiToken))
            {
                tokenAccessor.ApiToken = apiToken;
            }
            else
            {
                var stored = credStorage.LoadCredential();
                if (stored != null)
                {
                    tokenAccessor.ApiToken = stored.ApiToken;
                    Console.Error.WriteLine($"Loaded stored credentials (expires {stored.ExpiresUtc:yyyy-MM-dd})");
                }
                else
                {
                    Console.Error.WriteLine("No API token configured. Will prompt for authentication on first use.");
                }
            }
            services.AddSingleton(tokenAccessor);
            services.AddSingleton(credStorage);

            // Register device activation service with its own HttpClient
            var deviceActivation = new DeviceActivationService(
                new HttpClient(),
                loggerFactory.CreateLogger<DeviceActivationService>(),
                configuration,
                credStorage);
            services.AddSingleton(deviceActivation);

            services.AddHttpClient<ProjectInsightApiService>();
            services.AddHttpClient<ProjectInsightAuthService>();
            services.AddSingleton<ProjectInsightAuthService>();
            services.AddSingleton<ProjectInsightApiService>();
            services.AddSingleton<McpServerService>();

            var serviceProvider = services.BuildServiceProvider();

            // Create the MCP server
            var mcpServer = serviceProvider.GetRequiredService<McpServerService>();

            // Run the STDIO message loop
            await RunStdioLoop(mcpServer, logger);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error in MCP server");
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static async Task RunStdioLoop(McpServerService mcpServer, ILogger logger)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        using var reader = new StreamReader(Console.OpenStandardInput());
        using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        logger.LogInformation("MCP STDIO server ready, waiting for messages...");

        while (true)
        {
            try
            {
                // Read a line from stdin (JSON-RPC message)
                var line = await reader.ReadLineAsync();
                
                if (line == null)
                {
                    // EOF - client disconnected
                    logger.LogInformation("Client disconnected (EOF)");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                logger.LogDebug("Received: {Message}", line);

                // Parse the JSON-RPC message
                McpMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<McpMessage>(line, jsonOptions);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Failed to parse message: {Line}", line);
                    var parseError = new McpResponse
                    {
                        Id = null,
                        Error = new McpError
                        {
                            Code = -32700,
                            Message = "Parse error",
                            Data = ex.Message
                        }
                    };
                    await WriteResponse(writer, parseError, jsonOptions);
                    continue;
                }

                if (message == null)
                {
                    continue;
                }

                // Notifications (no id) should be silently handled - no response
                bool isNotification = message.Id == null || 
                                      message.Method?.StartsWith("notifications/") == true;
                
                if (isNotification)
                {
                    // Handle known notifications silently
                    if (message.Method == "initialized" || message.Method == "notifications/initialized")
                    {
                        // Client is confirming initialization - no action needed
                        continue;
                    }
                    // Unknown notifications are silently ignored per MCP spec
                    continue;
                }

                // Handle the message (requests that require responses)
                McpResponse response = message.Method switch
                {
                    "initialize" => mcpServer.HandleInitialize(message),
                    "tools/list" => mcpServer.HandleListTools(message),
                    "tools/call" => await mcpServer.HandleToolCallAsync(message),
                    "ping" => new McpResponse { Id = message.Id, Result = new { } },
                    _ => new McpResponse
                    {
                        Id = message.Id,
                        Error = new McpError
                        {
                            Code = -32601,
                            Message = $"Method not found: {message.Method}"
                        }
                    }
                };

                // Write response to stdout
                await WriteResponse(writer, response, jsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing message");
                
                var errorResponse = new McpResponse
                {
                    Id = null,
                    Error = new McpError
                    {
                        Code = -32603,
                        Message = "Internal error",
                        Data = ex.Message
                    }
                };
                
                try
                {
                    await WriteResponse(writer, errorResponse, jsonOptions);
                }
                catch
                {
                    // If we can't write, we should exit
                    break;
                }
            }
        }
    }

    private static async Task WriteResponse(StreamWriter writer, McpResponse response, JsonSerializerOptions jsonOptions)
    {
        var responseJson = JsonSerializer.Serialize(response, jsonOptions);
        await writer.WriteLineAsync(responseJson);
    }
}
