using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class NoEchoTests
{
    private const string WrongCode = "WRONG-CODE-7731";

    [Fact]
    public async Task Tokens_and_codes_appear_in_no_response_and_no_log()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<ILoggerProvider>(logs) };
        var bodies = new List<string>();

        // Sign-in with a wrong code first, then the right one.
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var session = await flow.StartAsync(AuthorizeFlow.Query(clientId));
        bodies.Add(await (await flow.CompleteAsync(session, WrongCode)).Content.ReadAsStringAsync());
        var redirect = await flow.CompleteAsync(session, PiStub.ActivationCode);
        bodies.Add(redirect.Headers.Location!.ToString());

        // A failing /token and the health endpoint.
        bodies.Add(await (await factory.Client().PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = "nope", ["code_verifier"] = AuthorizeFlow.Verifier,
            ["client_id"] = clientId, ["redirect_uri"] = AuthorizeFlow.ClaudeCallback
        }))).Content.ReadAsStringAsync());
        bodies.Add(await factory.Client().GetStringAsync("/healthz"));

        // Tools whose PI calls echo the token, fail with it in the body, or time out.
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);
        foreach (var id in new[] { "echo", "boom", "slow" })
        {
            var result = await mcp.CallToolAsync("get_project", new Dictionary<string, object?> { ["projectId"] = id });
            bodies.AddRange(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            if (result.StructuredContent is { } structured)
                bodies.Add(structured.ToString());
        }

        foreach (var secret in new[] { PiStub.TokenA, PiStub.ActivationCode, WrongCode })
        {
            Assert.DoesNotContain(bodies, b => b.Contains(secret));
            Assert.DoesNotContain(logs.Lines, l => l.Contains(secret));
        }
    }
}
