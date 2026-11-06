using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Each scrubbing layer is pinned on its own: removing one wiring in WebHost fails one of these.</summary>
public class NoEchoWiringTests
{
    [Fact]
    public async Task Typed_client_handler_keeps_the_token_out_of_logs_when_a_write_fails_with_it_in_the_body()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<ILoggerProvider>(logs) };
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        var result = await mcp.CallToolAsync("create_project", new Dictionary<string, object?>
        {
            ["project"] = new Dictionary<string, object?> { ["name"] = "p" }
        });

        Assert.Contains(logs.Lines, l => l.Contains("Create project failed")); // the path under test ran
        Assert.DoesNotContain(logs.Lines, l => l.Contains(PiStub.TokenA));
        Assert.DoesNotContain(result.Content.OfType<TextContentBlock>(), c => c.Text.Contains(PiStub.TokenA));
    }

    [Fact]
    public async Task Call_tool_filter_strips_the_token_from_a_successful_result()
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        var result = await mcp.CallToolAsync("get_project", new Dictionary<string, object?> { ["projectId"] = "leak" });

        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("[redacted]", text); // the stub's 200 body did reach the result
        Assert.DoesNotContain(PiStub.TokenA, text);
        Assert.DoesNotContain(PiStub.TokenA, result.StructuredContent?.GetRawText() ?? "");
    }

    [Fact]
    public async Task Identity_client_redacts_the_bearer_from_error_bodies()
    {
        using var factory = new WebFactory();
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PiIdentityClient.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://pi.test/api/echo");
        request.Headers.Authorization = new("Bearer", PiStub.TokenA);

        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("[redacted]", body);
        Assert.DoesNotContain(PiStub.TokenA, body);
    }

    [Fact]
    public async Task Activation_client_redacts_the_activation_code_from_error_bodies()
    {
        using var factory = new WebFactory();
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PiActivationClient.HttpClientName);

        var body = await (await client.GetAsync("https://pi.test/api/echo?activationCode=ACT-SECRET-99")).Content.ReadAsStringAsync();

        Assert.Contains("[redacted]", body);
        Assert.DoesNotContain("ACT-SECRET-99", body);
    }
}
