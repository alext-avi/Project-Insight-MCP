using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting.Internal;
using ProjectInsightMCP.Web;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Auth:AllowedEmailDomains admits PI users by the email on their PI profile, alongside the id allow-list.</summary>
public class EmailDomainTests
{
    private const string Expiry = "2027-01-28T12:00:00";

    private static WebFactory WithDomains(params string[] domains)
    {
        var factory = new WebFactory();
        for (var i = 0; i < domains.Length; i++)
            factory.Settings[$"Auth:AllowedEmailDomains:{i}"] = domains[i];
        return factory;
    }

    private static HttpRequestMessage McpPost(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }

    [Theory]
    [InlineData("someone@team.test")]
    [InlineData("Someone@TEAM.Test")]
    [InlineData("  someone@team.test ")]
    public async Task A_user_on_an_allowed_domain_can_call_the_server(string email)
    {
        using var factory = WithDomains("team.test");
        var response = await factory.Client().SendAsync(McpPost(PiStub.WithEmail(email)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("someone@elsewhere.test")]
    [InlineData("someone@team.test.evil.test")]   // the allowed domain as a prefix
    [InlineData("someone@evilteam.test")]          // the allowed domain as a suffix
    [InlineData("someone@sub.team.test")]          // subdomains must be listed themselves
    [InlineData("someone@team.test@elsewhere.test")]
    [InlineData("team.test")]
    [InlineData("someone@")]
    [InlineData("")]                                // no email on the PI profile
    public async Task Anyone_else_is_refused(string email)
    {
        using var factory = WithDomains("team.test");
        var response = await factory.Client().SendAsync(McpPost(PiStub.WithEmail(email)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_domains_the_email_counts_for_nothing()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.WithEmail("someone@team.test")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ids_still_admit_their_users_when_domains_are_set()
    {
        using var factory = WithDomains("team.test");
        var response = await factory.Client().SendAsync(McpPost(PiStub.TokenA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_admits_a_user_on_an_allowed_domain()
    {
        using var factory = WithDomains("team.test");
        factory.Pi.ActivationResult = (PiStub.WithEmail("someone@team.test"), PiStub.EmailUser, Expiry);
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_refuses_a_user_on_another_domain_and_logs_their_id()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = WithDomains("team.test");
        factory.ExtraServices = s => s.AddSingleton<ILoggerProvider>(logs);
        factory.Pi.ActivationResult = (PiStub.WithEmail("someone@elsewhere.test"), PiStub.EmailUser, Expiry);
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("isn't open to your Project Insight account", await response.Content.ReadAsStringAsync());
        Assert.Contains(logs.Lines, l => l.Contains(PiStub.EmailUser) && l.Contains("not allowed"));
    }

    [Fact]
    public async Task Sign_in_refuses_when_pi_says_the_new_token_is_someone_else()
    {
        // Activation names one user; PI's identity check for the token it just issued names another.
        using var factory = WithDomains("team.test");
        factory.Pi.ActivationResult = (PiStub.WithEmail("someone@team.test"), "0f0e2a4c-4444-4a4a-9b9b-eeeeeeeeeeee", Expiry);
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_says_try_again_when_pi_cannot_say_who_the_user_is()
    {
        using var factory = WithDomains("team.test");
        factory.Pi.ActivationResult = (PiStub.BrokenToken, PiStub.EmailUser, Expiry);
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("*.team.test")]
    [InlineData("@team.test")]
    [InlineData("someone@team.test")]
    [InlineData("team")]
    [InlineData("team.test.")]
    [InlineData("https://team.test")]
    public void Startup_refuses_an_entry_that_is_not_a_plain_domain(string entry)
    {
        using var factory = WithDomains("team.test", entry);

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains("Auth:AllowedEmailDomains", ex!.ToString());
    }

    [Fact]
    public void Startup_refuses_domains_given_as_one_value()
    {
        using var factory = new WebFactory();
        factory.Settings["Auth:AllowedEmailDomains"] = "team.test";

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains("Auth__AllowedEmailDomains__0", ex!.ToString());
    }

    [Fact]
    public void Domains_alone_satisfy_the_allow_list_requirement()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectInsight:BaseUrl"] = "https://pi.test/api/",
            ["Storage:ConnectionString"] = "UseDevelopmentStorage=true"
        }).Build();

        StartupChecks.Validate(config, new WebOptions { PublicBaseUrl = "https://mcp.test" },
            new AuthOptions { AllowedEmailDomains = ["team.test"] }, new HostingEnvironment { EnvironmentName = "Production" });
    }
}
