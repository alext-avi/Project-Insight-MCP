using System.Text.Json;
using ModelContextProtocol.Protocol;
using ProjectInsightMCP.Web.Security;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class ScrubberTests
{
    [Fact]
    public void Scrub_replaces_every_secret_and_ignores_very_short_ones()
    {
        var text = PiErrorScrubbingHandler.Scrub("bad abcd-token and abcd-token, id 12", ["abcd-token", "12"]);
        Assert.Equal("bad [redacted] and [redacted], id 12", text);
    }

    [Fact]
    public async Task Handler_redacts_credentials_and_truncates_error_bodies()
    {
        var inner = new StubHandler(r => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("token tok-12345 " + new string('x', 2000))
        });
        using var client = new HttpClient(new PiErrorScrubbingHandler { InnerHandler = inner });
        client.DefaultRequestHeaders.Authorization = new("Bearer", "tok-12345");

        var body = await (await client.GetAsync("https://pi.test/api/x")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("tok-12345", body);
        Assert.StartsWith("token [redacted]", body);
        Assert.True(body.Length <= PiErrorScrubbingHandler.MaxErrorBody + 1);
    }

    [Fact]
    public void Tool_results_lose_the_secret_in_text_and_structured_content()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "has sec-9999 inside" }],
            StructuredContent = JsonDocument.Parse("{\"a\":\"sec-9999\"}").RootElement.Clone()
        };

        ToolResultScrubber.Scrub(result, "sec-9999");

        Assert.Equal("has [redacted] inside", ((TextContentBlock)result.Content[0]).Text);
        Assert.DoesNotContain("sec-9999", result.StructuredContent!.Value.GetRawText());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
