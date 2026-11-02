using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class MetadataTests
{
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task Protected_resource_metadata_matches_the_spec(string path)
    {
        using var factory = new WebFactory();
        var json = await factory.Client().GetFromJsonAsync<JsonNode>(path);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "resource": "https://mcp.test/mcp",
              "authorization_servers": ["https://mcp.test"],
              "scopes_supported": ["pi"],
              "bearer_methods_supported": ["header"]
            }
            """), json), json!.ToJsonString());
    }

    [Fact]
    public async Task Authorization_server_metadata_matches_the_spec()
    {
        using var factory = new WebFactory();
        var json = await factory.Client().GetFromJsonAsync<JsonNode>("/.well-known/oauth-authorization-server");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "issuer": "https://mcp.test",
              "authorization_endpoint": "https://mcp.test/authorize",
              "token_endpoint": "https://mcp.test/token",
              "registration_endpoint": "https://mcp.test/register",
              "response_types_supported": ["code"],
              "grant_types_supported": ["authorization_code"],
              "code_challenge_methods_supported": ["S256"],
              "token_endpoint_auth_methods_supported": ["none"],
              "client_id_metadata_document_supported": true,
              "authorization_response_iss_parameter_supported": true,
              "scopes_supported": ["pi"]
            }
            """), json), json!.ToJsonString());
    }
}
