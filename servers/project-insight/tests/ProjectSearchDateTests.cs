using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Models;
using ProjectInsightMCP.Services;
using Xunit;
using static ProjectInsightMCP.Tests.McpServerTestHarness;

namespace ProjectInsightMCP.Tests;

/// <summary>Project-search dates go to PI as Gregorian yyyy-MM-dd whatever the server's culture.</summary>
public class ProjectSearchDateTests
{
    private static ProjectSearchRequest Request() => new()
    {
        StartDateFrom = new DateTime(2026, 10, 26), StartDateTo = new DateTime(2026, 11, 1),
        EndDateFrom = new DateTime(2026, 12, 1), EndDateTo = new DateTime(2026, 12, 31)
    };

    private static async Task InCulture(string name, Func<Task> body)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            await body();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData("th-TH")] // Buddhist calendar: 2026 is 2569
    [InlineData("de-DE")]
    public async Task Search_body_dates_are_gregorian_whatever_the_servers_culture(string culture)
    {
        var stub = new StubHttpMessageHandler([Json(HttpStatusCode.OK, "[]")]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectInsight:BaseUrl"] = "https://pi.test" }).Build();
        var token = new ApiTokenAccessor { ApiToken = "t" };
        var api = new ProjectInsightApiService(new HttpClient(stub), NullLogger<ProjectInsightApiService>.Instance, config, token);

        await InCulture(culture, () => api.SearchProjectsAsync(Request()));

        var body = JsonDocument.Parse(Assert.Single(stub.Requests).Body).RootElement;
        Assert.Equal(("2026-10-26", "2026-11-01", "2026-12-01", "2026-12-31"),
            (body.GetProperty("startDateFrom").GetString(), body.GetProperty("startDateTo").GetString(),
             body.GetProperty("endDateFrom").GetString(), body.GetProperty("endDateTo").GetString()));
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("de-DE")]
    public async Task Search_query_string_dates_are_gregorian_whatever_the_servers_culture(string culture)
    {
        // BuildQueryString is private (and not called today); checked directly so it can't drift if it is used again.
        var build = typeof(ProjectInsightApiService).GetMethod("BuildQueryString", BindingFlags.NonPublic | BindingFlags.Static)!;
        string query = "";

        await InCulture(culture, () => { query = (string)build.Invoke(null, [Request()])!; return Task.CompletedTask; });

        Assert.Contains("startDateFrom=2026-10-26", query);
        Assert.Contains("startDateTo=2026-11-01", query);
        Assert.Contains("endDateFrom=2026-12-01", query);
        Assert.Contains("endDateTo=2026-12-31", query);
    }
}
