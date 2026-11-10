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
    public async Task Handler_redacts_activation_values_taken_from_the_request_query()
    {
        var inner = new StubHandler(r => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad code CODE-8842 for request REQ-5521")
        });
        using var client = new HttpClient(new PiErrorScrubbingHandler { InnerHandler = inner });

        var body = await (await client.GetAsync("https://pi.test/api/x?activationRequestId=REQ-5521&activationCode=CODE-8842"))
            .Content.ReadAsStringAsync();

        Assert.Equal("bad code [redacted] for request [redacted]", body);
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

    // Base64 tokens carry '+' and '/', which JSON encoders escape.
    private const string Base64Sample = "tok+en/abc+def=";

    public static readonly TheoryData<string> EscapedForms = new()
    {
        Base64Sample,
        "tok\\u002Ben/abc\\u002Bdef=", // System.Text.Json's default encoder
        "tok\\u002ben/abc\\u002bdef=", // lower-case hex
        "tok+en\\/abc+def=",           // '/' escaped
        "tok\\u002Ben\\/abc\\u002Bdef="
    };

    [Theory]
    [MemberData(nameof(EscapedForms))]
    public void Tool_result_text_loses_every_json_spelling_of_the_secret(string form)
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = $"{{\"error\":\"bad {form} here\"}}" }] };

        ToolResultScrubber.Scrub(result, Base64Sample);

        Assert.Equal("{\"error\":\"bad [redacted] here\"}", ((TextContentBlock)result.Content[0]).Text);
    }

    [Theory]
    [MemberData(nameof(EscapedForms))]
    public void Forms_computed_once_match_and_redact_every_spelling(string form)
    {
        // The scrubbers compute the forms once per call and reuse them for every string they check.
        var forms = SecretForms.Of(Base64Sample);
        var text = $"bad {form} here";

        Assert.True(SecretForms.AppearsIn(text, forms));
        Assert.Equal("bad [redacted] here", SecretForms.Redact(text, forms));
        Assert.Equal(SecretForms.Redact(text, Base64Sample), SecretForms.Redact(text, forms));
        Assert.False(SecretForms.AppearsIn("nothing here", forms));
    }

    [Fact]
    public void Structured_content_is_scrubbed_after_json_decoding()
    {
        // What TimeChangeTools does: the default encoder writes the '+' as +.
        var result = new CallToolResult
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new { message = $"failed for {Base64Sample}", list = new[] { Base64Sample } }, JsonSerializerOptions.Web)
        };
        Assert.DoesNotContain(Base64Sample, result.StructuredContent!.Value.GetRawText()); // the escaped form a raw match would miss

        ToolResultScrubber.Scrub(result, Base64Sample);

        var structured = result.StructuredContent!.Value;
        Assert.Equal("failed for [redacted]", structured.GetProperty("message").GetString());
        Assert.Equal("[redacted]", structured.GetProperty("list")[0].GetString());
    }

    [Theory]
    [MemberData(nameof(EscapedForms))]
    public void Structured_content_loses_every_json_spelling_held_inside_a_decoded_value_or_name(string form)
    {
        // A string field that carries a PI error body: decoding the structured content leaves the escape in place.
        var result = new CallToolResult
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["error"] = $"{{\"message\":\"bad {form} here\"}}",
                [$"key {form}"] = "v"
            })
        };

        ToolResultScrubber.Scrub(result, Base64Sample);

        var structured = result.StructuredContent!.Value;
        Assert.Equal("{\"message\":\"bad [redacted] here\"}", structured.GetProperty("error").GetString());
        Assert.Equal("v", structured.GetProperty("key [redacted]").GetString());
    }

    [Theory]
    [MemberData(nameof(EscapedForms))]
    public async Task Handler_redacts_every_json_spelling_of_the_bearer(string form)
    {
        var inner = new StubHandler(r => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"{{\"message\":\"token {form} is invalid\"}}")
        });
        using var client = new HttpClient(new PiErrorScrubbingHandler { InnerHandler = inner });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Base64Sample);

        var body = await (await client.GetAsync("https://pi.test/api/x")).Content.ReadAsStringAsync();

        Assert.Equal("{\"message\":\"token [redacted] is invalid\"}", body);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
