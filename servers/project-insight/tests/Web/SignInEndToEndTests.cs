using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class SignInEndToEndTests
{
    [Fact]
    public async Task Registered_client_signs_in_and_calls_tools_with_the_token()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync("http://127.0.0.1/callback");
        var code = await flow.CodeForAsync(clientId, "http://127.0.0.1:53111/callback");
        var token = await TokenAsync(flow, code, clientId, "http://127.0.0.1:53111/callback");

        await using var mcp = await factory.ConnectAsync(token);
        var me = await mcp.CallToolAsync("get_current_user", new Dictionary<string, object?>());

        Assert.NotEqual(true, me.IsError);
        Assert.Contains(PiStub.UserA, string.Concat(me.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text)));
        Assert.Contains(factory.Pi.Requests, r => r.Authorization == "Bearer " + PiStub.TokenA);
    }

    [Fact]
    public async Task Metadata_document_client_signs_in()
    {
        using var factory = new WebFactory();
        const string clientId = "https://claude.ai/oauth/claude-code-client-metadata";
        factory.Pi.Fixed[clientId] = () => PiStub.Json(new
        {
            client_id = clientId,
            client_name = "Claude Code",
            redirect_uris = new[] { "http://localhost/callback", "http://127.0.0.1/callback" }
        });
        var flow = new AuthorizeFlow(factory);
        var code = await flow.CodeForAsync(clientId, "http://localhost:3118/callback");
        var token = await TokenAsync(flow, code, clientId, "http://localhost:3118/callback");

        await using var mcp = await factory.ConnectAsync(token);
        Assert.NotEmpty(await mcp.ListToolsAsync());
    }

    private static async Task<string> TokenAsync(AuthorizeFlow flow, string code, string clientId, string redirect)
    {
        var response = await flow.Browser.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = AuthorizeFlow.Verifier,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect
        }));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
}
