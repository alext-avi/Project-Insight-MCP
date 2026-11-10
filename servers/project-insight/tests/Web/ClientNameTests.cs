using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Self-asserted client names can't reorder or hide the consent page's text.</summary>
public class ClientNameTests
{
    private static readonly string RightToLeftOverride = char.ConvertFromUtf32(0x202E);
    private static readonly string ZeroWidthSpace = char.ConvertFromUtf32(0x200B);
    private static readonly string Bell = char.ConvertFromUtf32(0x07);
    private static readonly string TagLetterA = char.ConvertFromUtf32(0xE0041); // a format character outside the BMP
    private static readonly string Dirty = $"Clau{ZeroWidthSpace}de{RightToLeftOverride} Code{Bell}{TagLetterA}";

    [Theory]
    [InlineData("Claude Code", "Claude Code")]
    [InlineData("  Codex ", "Codex")]
    [InlineData("Café 東京", "Café 東京")]
    public void Ordinary_names_are_kept(string given, string expected) => Assert.Equal(expected, ClientName.Clean(given));

    [Fact]
    public void Control_and_format_characters_are_dropped()
    {
        Assert.Equal("Claude Code", ClientName.Clean(Dirty));
        Assert.Null(ClientName.Clean(RightToLeftOverride + ZeroWidthSpace));
    }

    [Fact]
    public async Task Registration_stores_and_returns_the_cleaned_name()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new
        {
            client_name = Dirty,
            redirect_uris = new[] { "http://127.0.0.1/callback" }
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Claude Code", body.GetProperty("client_name").GetString());
        var stored = await factory.Services.GetRequiredService<IClientStore>().GetAsync(body.GetProperty("client_id").GetString()!, default);
        Assert.Equal("Claude Code", stored!.ClientName);
    }

    [Fact]
    public async Task Registration_still_refuses_an_overlong_name()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new
        {
            client_name = new string('a', ClientName.MaxLength + 1),
            redirect_uris = new[] { "http://127.0.0.1/callback" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Consent_page_cleans_a_name_stored_before_names_were_cleaned()
    {
        using var factory = new WebFactory();
        _ = factory.Client();
        const string clientId = "dcr_AAAAAAAAAAAAAAAAAAAAAA";
        await factory.Services.GetRequiredService<IClientStore>().TryAddAsync(
            new RegisteredClient(clientId, Dirty, [AuthorizeFlow.ClaudeCallback], DateTimeOffset.UtcNow), 10, default);

        var html = await (await new AuthorizeFlow(factory).GetAuthorizeAsync(AuthorizeFlow.Query(clientId))).Content.ReadAsStringAsync();

        Assert.Contains("It calls itself “Claude Code” (unverified).", html);
        Assert.DoesNotContain("&#x202E;", html);
        Assert.DoesNotContain(RightToLeftOverride, html, StringComparison.Ordinal); // culture comparison ignores format characters
    }

    [Fact]
    public async Task Cimd_document_name_is_cleaned()
    {
        using var factory = new WebFactory();
        _ = factory.Client();
        const string url = "https://claude.ai/oauth/claude-code-client-metadata";
        factory.Pi.Fixed[url] = () => PiStub.Json(new { client_id = url, client_name = Dirty, redirect_uris = new[] { AuthorizeFlow.ClaudeCallback } });

        var doc = await factory.Services.GetRequiredService<CimdFetcher>().FetchAsync(url, default);

        Assert.Equal("Claude Code", doc!.ClientName);
    }
}
