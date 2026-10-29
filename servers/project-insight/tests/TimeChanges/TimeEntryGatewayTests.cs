using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectInsightMCP.Models;
using ProjectInsightMCP.Services;
using ProjectInsightMCP.TimeChanges;
using Xunit;
using static ProjectInsightMCP.Tests.McpServerTestHarness;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class TimeEntryGatewayTests
{
    private static (ProjectInsightTimeEntryGateway gw, StubHttpMessageHandler pi) Create(params HttpResponseMessage[] responses)
    {
        var pi = new StubHttpMessageHandler(responses);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectInsight:BaseUrl"] = "https://pi.test" })
            .Build();
        var token = new ApiTokenAccessor { ApiToken = "t" };
        var api = new ProjectInsightApiService(new HttpClient(pi), NullLogger<ProjectInsightApiService>.Instance, config, token);
        return (new ProjectInsightTimeEntryGateway(api, token), pi);
    }

    private const string EntryJson = """
        {"Id":"te-1","Date":"2026-10-27T00:00:00","ActualHours":1.5,"IsBillable":true,"Description":"API work",
         "Task_Id":"t-1","Project_Id":"p-1","User_Id":"u-1","TimeSheet_Id":null,
         "Task":{"Id":"t-1","Name":"Development"},"Project":{"Id":"p-1","Name":"Acme Portal"},
         "RateBill":150.0,"BillableTotal":225.0}
        """;

    [Fact]
    public async Task Search_requests_names_and_maps_dates_without_shifting_days()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, $"[{EntryJson}]"));

        var entries = await gw.SearchEntriesAsync("u-1", new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), default);

        var e = Assert.Single(entries);
        Assert.Equal(new PiEntry("te-1", "u-1", new DateOnly(2026, 10, 27), 1.5m, true, "API work",
            "t-1", "Development", "p-1", "Acme Portal", null), e);
        var url = Uri.UnescapeDataString(Assert.Single(pi.Requests).Uri.PathAndQuery);
        Assert.StartsWith("/api/time-entry/search?", url);
        Assert.Contains("userList=u-1", url);
        Assert.Contains("startDate=2026-10-26", url);
        Assert.Contains("endDate=2026-11-01", url);
        Assert.Contains(TimeEntryWithNames.ModelProperties, url);
        Assert.DoesNotContain("RateBill", url);
    }

    [Fact]
    public async Task Get_entry_reads_one_entry_by_id_and_returns_null_when_missing()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, EntryJson), Json(HttpStatusCode.NotFound, "{}"));

        var found = await gw.GetEntryAsync("te-1", default);
        var missing = await gw.GetEntryAsync("te-9", default);

        Assert.Equal("Development", found!.TaskName);
        Assert.Null(missing);
        Assert.StartsWith("/api/time-entry/te-1?modelProperties=", pi.Requests[0].Uri.PathAndQuery);
    }

    [Fact]
    public async Task Create_posts_the_entry_and_returns_the_saved_id()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """{"SavedId":"te-new","Success":true}"""));

        var result = await gw.CreateEntryAsync(
            new EntryWrite("u-1", "t-1", new DateOnly(2026, 10, 27), 4m, true, "Sync", "ts-1"), default);

        Assert.Equal(new PiWrite(true, "te-new", null), result);
        var body = JsonDocument.Parse(Assert.Single(pi.Requests).Body).RootElement;
        Assert.Equal("u-1", body.GetProperty("User_Id").GetString());
        Assert.Equal("t-1", body.GetProperty("Task_Id").GetString());
        Assert.Equal(4m, body.GetProperty("ActualHours").GetDecimal());
        Assert.True(body.GetProperty("IsBillable").GetBoolean());
        Assert.Equal("ts-1", body.GetProperty("TimeSheet_Id").GetString());
        Assert.StartsWith("2026-10-27", body.GetProperty("Date").GetString());
        Assert.False(body.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task Update_sends_every_field_and_clears_a_null_description()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """{"SavedId":"te-1","Success":true}"""));

        await gw.UpdateEntryAsync("te-1", new EntryWrite("u-1", "t-2", new DateOnly(2026, 10, 28), 2m, false, null, null), default);

        var body = JsonDocument.Parse(Assert.Single(pi.Requests).Body).RootElement;
        Assert.Equal("te-1", body.GetProperty("Id").GetString());
        Assert.Equal("t-2", body.GetProperty("Task_Id").GetString());
        Assert.Equal("", body.GetProperty("Description").GetString());
        Assert.False(body.GetProperty("IsBillable").GetBoolean());
    }

    [Fact]
    public async Task Failed_write_reports_pi_error_text()
    {
        var (gw, _) = Create(Json(HttpStatusCode.BadRequest, "Task is closed to time entry."));

        var result = await gw.DeleteEntryAsync("te-1", default);

        Assert.False(result.Success);
        Assert.Contains("Task is closed to time entry.", result.Error);
    }

    [Fact]
    public async Task Current_user_is_cached_per_token()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """{"Id":"u-1","Name":"Alex Example"}"""));

        var a = await gw.GetCurrentUserAsync(default);
        var b = await gw.GetCurrentUserAsync(default);

        Assert.Equal(new PiUser("u-1", "Alex Example"), a);
        Assert.Same(a, b);
        Assert.Single(pi.Requests);
    }

    [Fact]
    public async Task Find_users_returns_active_matches_by_name()
    {
        var (gw, pi) = Create(
            Json(HttpStatusCode.OK, """[{"Id":"p-1","PrimaryProjectManager_Id":"u-1"}]"""),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, """
                [{"Id":"u-1","Name":"Alex Example","FirstName":"Alex","LastName":"Example","IsActive":true},
                 {"Id":"u-2","Name":"Alex Gone","FirstName":"Alex","LastName":"Gone","IsActive":false}]
                """));

        var users = await gw.FindUsersAsync("alex", default);

        Assert.Equal(new[] { new PiUser("u-1", "Alex Example") }, users);
        // ListAllUsersAsync filters on IsActive client-side, so the field must be requested.
        var listUrl = Uri.UnescapeDataString(pi.Requests[2].Uri.PathAndQuery);
        Assert.Contains("/api/user/list", listUrl);
        Assert.Contains("IsActive", listUrl);
    }
}
