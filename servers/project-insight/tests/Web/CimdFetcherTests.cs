using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class CimdFetcherTests
{
    private const string Url = "https://claude.ai/oauth/claude-code-client-metadata";

    private static HttpResponseMessage Doc(string json, string type = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, type) };

    private static (WebFactory, CimdFetcher) Create()
    {
        var factory = new WebFactory();
        _ = factory.Client();
        return (factory, factory.Services.GetRequiredService<CimdFetcher>());
    }

    [Fact]
    public async Task Valid_document_is_read_and_cached()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc($$"""{"client_id":"{{Url}}","client_name":"Claude Code","redirect_uris":["http://localhost/callback","http://127.0.0.1/callback"]}""");

        var doc = await cimd.FetchAsync(Url, default);
        await cimd.FetchAsync(Url, default);

        Assert.Equal("Claude Code", doc!.ClientName);
        Assert.Equal(2, doc.RedirectUris.Count);
        Assert.Single(factory.Pi.Requests, r => r.Uri.AbsoluteUri == Url);
    }

    [Theory]
    [InlineData("https://evil.example/client.json")]       // host not on the allowlist
    [InlineData("http://claude.ai/client.json")]           // not https
    [InlineData("https://claude.ai:8443/client.json")]     // not port 443
    [InlineData("https://claude.ai/")]                     // no path
    [InlineData("https://user@claude.ai/client.json")]     // userinfo
    [InlineData("https://claude.ai/client.json?v=1")]      // query
    [InlineData("https://claude.ai/client.json?")]         // empty query
    [InlineData("https://claude.ai/client.json#")]         // empty fragment
    public async Task Refused_urls_are_never_fetched(string url)
    {
        var (factory, cimd) = Create();
        using var _ = factory;

        Assert.Null(await cimd.FetchAsync(url, default));
        Assert.Empty(factory.Pi.Requests);
    }

    [Fact]
    public async Task Overlong_client_ids_are_never_fetched()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        var url = "https://claude.ai/" + new string('a', CimdFetcher.MaxClientIdLength - "https://claude.ai/".Length + 1);

        Assert.Null(await cimd.FetchAsync(url, default));
        Assert.Empty(factory.Pi.Requests);
    }

    [Fact]
    public async Task The_cache_holds_a_bounded_number_of_client_ids()
    {
        var (factory, cimd) = Create();
        using var _ = factory;

        // Every path on an allowed host is a distinct client id; each failed fetch would be remembered.
        for (var i = 0; i < CimdFetcher.MaxCached + 50; i++)
            await cimd.FetchAsync($"https://claude.ai/random/{i}", default);

        Assert.InRange(cimd.CachedCount, 1, CimdFetcher.MaxCached);
    }

    [Fact]
    public async Task Document_naming_another_client_id_is_refused()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc("""{"client_id":"https://claude.ai/other","redirect_uris":["http://localhost/callback"]}""");
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Oversized_document_is_refused_even_without_content_length()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        var padding = new string(' ', 6000);
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""{"client_id":"{{Url}}","redirect_uris":["http://localhost/callback"]{{padding}}}""")))
            { Headers = { ContentType = new("application/json") } }
        };
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Wrong_content_type_is_refused()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc($$"""{"client_id":"{{Url}}","redirect_uris":["http://localhost/callback"]}""", "text/html");
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://claude.ai/elsewhere") } };
        Assert.Null(await cimd.FetchAsync(Url, default));
        Assert.DoesNotContain(factory.Pi.Requests, r => r.Uri.AbsolutePath == "/elsewhere");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Document_with_zero_max_age_still_resolves(int seconds)
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () =>
        {
            var r = Doc($$"""{"client_id":"{{Url}}","redirect_uris":["http://localhost/callback"]}""");
            r.Headers.CacheControl = seconds == 0 ? new() { MaxAge = TimeSpan.Zero } : new() { NoStore = true };
            return r;
        };

        Assert.NotNull(await cimd.FetchAsync(Url, default));
        await cimd.FetchAsync(Url, default);
        Assert.Single(factory.Pi.Requests, r => r.Uri.AbsoluteUri == Url); // floored, not refetched every time
    }

    [Fact]
    public async Task Body_that_fails_mid_read_is_a_failed_fetch()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingStream()) { Headers = { ContentType = new("application/json") } }
        };
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Document_with_a_lone_surrogate_is_a_failed_fetch()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc($$"""{"client_id":"{{Url}}","client_name":"\uD800","redirect_uris":["http://localhost/callback"]}""");
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("reset");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Failures_are_remembered_for_a_while()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        await cimd.FetchAsync(Url, default);
        await cimd.FetchAsync(Url, default);

        Assert.Single(factory.Pi.Requests, r => r.Uri.AbsoluteUri == Url);
    }

    [Fact]
    public void Configured_handler_never_follows_redirects_uses_a_proxy_or_skips_the_address_check()
    {
        using var factory = new WebFactory();
        using var configured = ConfiguredHandler.Of<SocketsHttpHandler>(factory, CimdFetcher.HttpClientName);

        Assert.NotNull(configured);
        Assert.False(configured!.UseProxy);
        Assert.False(configured.AllowAutoRedirect);
        Assert.NotNull(configured.ConnectCallback);
    }
}
