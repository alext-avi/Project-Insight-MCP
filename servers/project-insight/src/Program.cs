using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ProjectInsightMCP.Services;

[assembly: FunctionsStartup(typeof(ProjectInsightMCP.Startup))]

namespace ProjectInsightMCP;

/// <summary>
/// Startup class for Azure Functions
/// </summary>
public class Startup : FunctionsStartup
{
    public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
    {
        // This ensures environment variables are properly loaded
        // The base class already loads from environment variables with __ -> : replacement
        base.ConfigureAppConfiguration(builder);
    }

    public override void Configure(IFunctionsHostBuilder builder)
    {
        // Add per-request token accessor (scoped so each request gets its own instance)
        builder.Services.AddScoped<ApiTokenAccessor>();

        // Add HTTP client for Project Insight API
        builder.Services.AddHttpClient<ProjectInsightAuthService>();
        builder.Services.AddHttpClient<ProjectInsightApiService>();
        builder.Services.AddHttpClient<DeviceActivationService>();

        // Add authentication service first
        builder.Services.AddScoped<ProjectInsightAuthService>();

        // Add Key Vault token service (singleton — shared across requests)
        builder.Services.AddSingleton<KeyVaultTokenService>();

        // OAuth bridge state store (singleton — TableClient is thread-safe and
        // expensive to construct). Used only by the Claude Cowork OAuth bridge;
        // the core MCP path doesn't touch it.
        builder.Services.AddSingleton<AuthCodeStore>();

        // Add device activation service (scoped — per-request pending state)
        builder.Services.AddScoped<DeviceActivationService>();
        builder.Services.AddScoped<CredentialStorageService>();

        // Add API service
        builder.Services.AddScoped<ProjectInsightApiService>();
        builder.Services.AddScoped<McpServerService>();

        // Add configuration validation
        builder.Services.AddOptions();
    }
}
