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

    [Fact]
    public async Task Search_time_sheets_throws_when_pi_fails_instead_of_reporting_none()
    {
        var (gw, _) = Create(Json(HttpStatusCode.InternalServerError, "boom"));

        var ex = await Assert.ThrowsAsync<TimeChangeException>(
            () => gw.SearchTimeSheetsAsync("u-1", new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), default));

        Assert.Equal("pi_unavailable", ex.Code);
    }

    [Fact]
    public async Task Get_entry_throws_on_server_error_but_returns_null_on_not_found()
    {
        var (gw, _) = Create(Json(HttpStatusCode.InternalServerError, "boom"), Json(HttpStatusCode.NotFound, "{}"));

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => gw.GetEntryAsync("te-1", default));
        Assert.Equal("pi_unavailable", ex.Code);
        Assert.Null(await gw.GetEntryAsync("te-9", default));
    }

    private static ProjectInsightTimeEntryGateway Unanswered(Exception failure, string? apiToken = "t")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectInsight:BaseUrl"] = "https://pi.test" })
            .Build();
        var token = new ApiTokenAccessor { ApiToken = apiToken };
        var api = new ProjectInsightApiService(new HttpClient(new FailingHandler(failure)), NullLogger<ProjectInsightApiService>.Instance, config, token);
        return new ProjectInsightTimeEntryGateway(api, token);
    }

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(failure);
    }

    [Fact]
    public async Task A_write_pi_never_answered_is_uncertain()
    {
        var gw = Unanswered(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        var values = new EntryWrite("u-1", "t-1", new DateOnly(2026, 10, 27), 4m, true, null, null);

        var results = new[]
        {
            await gw.CreateEntryAsync(values, default),
            await gw.UpdateEntryAsync("te-1", values, default),
            await gw.DeleteEntryAsync("te-1", default)
        };

        Assert.All(results, r =>
        {
            Assert.False(r.Success);
            Assert.True(r.Uncertain);
            Assert.Contains("Timeout", r.Error);
        });
    }

    [Fact]
    public async Task A_write_pi_refused_is_not_uncertain()
    {
        var (gw, _) = Create(Json(HttpStatusCode.BadRequest, "Task is closed to time entry."),
            Json(HttpStatusCode.OK, """{"success":false,"errors":[{"errorMessage":"Hours exceed the limit."}]}"""));

        var deleted = await gw.DeleteEntryAsync("te-1", default);
        var saved = await gw.CreateEntryAsync(new EntryWrite("u-1", "t-1", new DateOnly(2026, 10, 27), 4m, true, null, null), default);

        Assert.False(deleted.Success);
        Assert.False(deleted.Uncertain);
        Assert.False(saved.Success);
        Assert.False(saved.Uncertain);
        Assert.Contains("Hours exceed the limit.", saved.Error);
    }

    [Fact]
    public async Task Long_pi_error_text_is_cut_short()
    {
        var page = "<html><body>" + new string('x', 5000) + "</body></html>";
        var (gw, _) = Create(Json(HttpStatusCode.InternalServerError, page), Json(HttpStatusCode.InternalServerError, page));

        var write = await gw.DeleteEntryAsync("te-1", default);
        var read = await Assert.ThrowsAsync<TimeChangeException>(() => gw.SearchEntriesAsync("u-1", new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), default));

        Assert.True(write.Error!.Length <= 301, $"write error was {write.Error.Length} characters");
        Assert.EndsWith("…", write.Error);
        Assert.StartsWith("API returned InternalServerError", write.Error);
        Assert.True(read.Message.Length < 400, $"read error was {read.Message.Length} characters");
        Assert.EndsWith("…", read.Message);
    }

    [Fact]
    public async Task Without_a_token_the_current_user_is_not_signed_in_and_no_header_is_mentioned()
    {
        var gw = Unanswered(new InvalidOperationException("should not be sent"), apiToken: null);

        var ex = await Assert.ThrowsAsync<TimeChangeException>(() => gw.GetCurrentUserAsync(default));

        Assert.Equal("not_signed_in", ex.Code);
        Assert.Contains("not signed in to Project Insight", ex.Message);
        Assert.DoesNotContain("X-PI-Token", ex.Message);
    }

    [Fact]
    public async Task Search_reads_each_entrys_project_colour_and_drops_anything_but_a_hex_colour()
    {
        var (gw, _) = Create(Json(HttpStatusCode.OK, $"[{EntryJson.Replace("\"Name\":\"Acme Portal\"", "\"Name\":\"Acme Portal\",\"Color\":\"#BE2EDD\"")}," +
            $"{EntryJson.Replace("te-1", "te-2").Replace("\"Name\":\"Acme Portal\"", "\"Name\":\"Acme Portal\",\"Color\":\"red;background:url(x)\"")}]"));

        var entries = await gw.SearchEntriesAsync("u-1", new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), default);

        Assert.Equal(["#be2edd", null], entries.Select(e => e.ProjectColor));
        Assert.Contains("Project:Id,Name,Color", TimeEntryWithNames.ModelProperties);
    }

    [Fact]
    public async Task User_looks_come_from_one_user_list_call()
    {
        var (gw, pi) = Create(Json(HttpStatusCode.OK, """
            [{"Id":"u-1","Initials":"AT","PhotoUrl":"/Sites/Files/a/UserPhotos/b/at.jpeg",
              "ColorSettings":{"BackgroundColor":"#17a7c9","FontColor":"#ffffff","FontIsWhite":true,"HasValue":true}},
             {"Id":"u-2","Initials":"JD","PhotoUrl":"","ColorSettings":{}}]
            """));

        var looks = await gw.GetUserLooksAsync(["u-1", "u-2"], default);

        Assert.Equal(new PiLook("AT", "#17a7c9", "#ffffff", "/Sites/Files/a/UserPhotos/b/at.jpeg"), looks["u-1"]);
        Assert.Equal(new PiLook("JD", null, null, null), looks["u-2"]);
        var url = Uri.UnescapeDataString(Assert.Single(pi.Requests).Uri.PathAndQuery);
        Assert.StartsWith("/api/user/list?ids=u-1,u-2", url);
        Assert.Contains("modelProperties=Id,Initials,ColorSettings,PhotoUrl", url);
    }

    [Fact]
    public async Task A_photo_is_inlined_as_a_data_uri_from_the_pi_site()
    {
        var (gw, pi) = Create(Image([1, 2, 3], "image/jpeg"));

        var photo = await gw.GetPhotoAsync("/Sites/Files/a/UserPhotos/b/at.jpeg", default);

        Assert.Equal("data:image/jpeg;base64,AQID", photo);
        Assert.Equal("https://pi.test/Sites/Files/a/UserPhotos/b/at.jpeg", Assert.Single(pi.Requests).Uri.ToString());
    }

    [Theory]
    [InlineData("https://elsewhere.test/a.jpeg")]
    [InlineData("//elsewhere.test/a.jpeg")]
    [InlineData("/Sites/../secret.jpeg")]
    [InlineData("Sites/Files/a.jpeg")]
    [InlineData("/other/a.jpeg")]
    [InlineData("/Sites/Files/a.jpeg?x=1")]
    [InlineData("/Sites/Files/a.jpeg#x")]
    [InlineData("/Sites/Files/%2e%2e/a.jpeg")]
    [InlineData("/Sites/Files/..\\a.jpeg")]
    [InlineData("/Sites/Files//elsewhere.test/a.jpeg")]
    [InlineData("/sites/files/a.jpeg")]
    public async Task A_photo_url_that_leaves_the_pi_site_is_never_fetched(string url)
    {
        var (gw, pi) = Create();

        Assert.Null(await gw.GetPhotoAsync(url, default));
        Assert.Empty(pi.Requests);
    }

    [Fact]
    public async Task A_photo_that_is_not_an_image_or_too_large_is_left_out()
    {
        var (gw, pi) = Create(Image([1], "text/html"), Image([1], "image/svg+xml"),
            Image(new byte[ProjectInsightTimeEntryGateway.MaxPhotoBytes + 1], "image/png"), Json(HttpStatusCode.NotFound, "{}"),
            Image(new byte[40 * 1024], "image/png"));

        Assert.Null(await gw.GetPhotoAsync("/Sites/Files/a.jpeg", default));
        Assert.Null(await gw.GetPhotoAsync("/Sites/Files/b.svg", default));
        Assert.Null(await gw.GetPhotoAsync("/Sites/Files/c.png", default));
        Assert.Null(await gw.GetPhotoAsync("/Sites/Files/d.jpeg", default));
        Assert.Null(await gw.GetPhotoAsync("/Sites/Files/e.png", default));
        Assert.Equal(5, pi.Requests.Count);
    }

    [Fact]
    public async Task A_slow_photo_is_left_out_but_the_callers_cancellation_still_throws()
    {
        var pi = new StubHttpMessageHandler([Image([1], "image/png")]) { Delay = TimeSpan.FromSeconds(30) };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectInsight:BaseUrl"] = "https://pi.test" })
            .Build();
        var token = new ApiTokenAccessor { ApiToken = "t" };
        var api = new ProjectInsightApiService(new HttpClient(pi), NullLogger<ProjectInsightApiService>.Instance, config, token);
        var gw = new ProjectInsightTimeEntryGateway(api, token) { PhotoTimeout = TimeSpan.FromMilliseconds(50) };

        var started = DateTime.UtcNow;
        Assert.Null(await gw.GetPhotoAsync("/Sites/Files/a.png", default));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gw.GetPhotoAsync("/Sites/Files/a.png", cancelled.Token));
    }

    [Fact]
    public void A_photo_fetch_has_a_three_second_budget()
        => Assert.Equal(TimeSpan.FromSeconds(3), new ProjectInsightTimeEntryGateway(null!, new ApiTokenAccessor()).PhotoTimeout);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.NotImplemented, true)]
    [InlineData((HttpStatusCode)524, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public async Task A_server_error_or_timeout_status_on_a_write_is_uncertain(HttpStatusCode status, bool uncertain)
    {
        var (gw, _) = Create(Json(status, "x"), Json(status, "x"), Json(status, "x"));
        var values = new EntryWrite("u-1", "t-1", new DateOnly(2026, 10, 27), 4m, true, null, null);

        var results = new[]
        {
            await gw.CreateEntryAsync(values, default),
            await gw.UpdateEntryAsync("te-1", values, default),
            await gw.DeleteEntryAsync("te-1", default)
        };

        Assert.All(results, r => Assert.Equal((false, uncertain), (r.Success, r.Uncertain)));
    }

    private static HttpResponseMessage Image(byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
