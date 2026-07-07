using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Services;
using Shared.MCP;

namespace ProjectInsightMCP.Tests;

public static class McpServerTestHarness
{
    private static readonly JsonSerializerOptions ReadOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static (McpServerService mcp, StubHttpMessageHandler handler) CreateServer(
        params HttpResponseMessage[] responses)
    {
        var handler = new StubHttpMessageHandler(responses);
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectInsight:BaseUrl"] = "https://pi.test"
            })
            .Build();
        var token = new ApiTokenAccessor { ApiToken = "test-token" };
        var api = new ProjectInsightApiService(
            httpClient, NullLogger<ProjectInsightApiService>.Instance, config, token);
        var mcp = new McpServerService(
            api, NullLogger<McpServerService>.Instance, token);
        return (mcp, handler);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Mirrors how the MCP layer populates arguments: values are JsonElement.
    public static Dictionary<string, object> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);

    public static T ReadJson<T>(string json)
        => JsonSerializer.Deserialize<T>(json, ReadOptions)!;

    public static McpToolCall Call(string name, string argsJson)
        => new() { Name = name, Arguments = Args(argsJson) };
}
