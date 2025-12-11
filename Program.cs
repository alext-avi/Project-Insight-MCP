using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.Services;

[assembly: FunctionsStartup(typeof(ProjectInsightMCP.Startup))]

namespace ProjectInsightMCP;

/// <summary>
/// Startup class for Azure Functions
/// </summary>
public class Startup : FunctionsStartup
{
    public override void Configure(IFunctionsHostBuilder builder)
    {
        // Add HTTP client for Project Insight API
        builder.Services.AddHttpClient<ProjectInsightAuthService>();
        builder.Services.AddHttpClient<ProjectInsightApiService>();
        
        // Add authentication service first
        builder.Services.AddScoped<ProjectInsightAuthService>();
        
        // Add API service
        builder.Services.AddScoped<ProjectInsightApiService>();
        builder.Services.AddScoped<McpServerService>();
        
        // Add configuration validation
        builder.Services.AddOptions();
    }
}
