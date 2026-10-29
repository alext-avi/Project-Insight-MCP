using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class TimeChangeResolverTests
{
    private readonly FakeGateway _pi = new();
    private TimeChangeResolver Resolver() => new(_pi);

    [Fact]
    public async Task No_user_means_the_caller()
    {
        var r = await Resolver().UserAsync(0, null, _pi.Caller, default);
        Assert.Same(_pi.Caller, r.Value);
    }

    [Theory]
    [InlineData("jane")]
    [InlineData("Jane Doe")]
    [InlineData("u-2")]
    public async Task User_resolves_by_unique_name_or_id(string query)
        => Assert.Equal("u-2", (await Resolver().UserAsync(0, query, _pi.Caller, default)).Value!.Id);

    [Fact]
    public async Task Ambiguous_user_returns_candidates()
    {
        _pi.Users.Add(new PiUser("u-3", "Jane Smith"));
        var r = await Resolver().UserAsync(2, "Jane", _pi.Caller, default);

        Assert.Null(r.Value);
        Assert.Equal(2, r.Clarification!.ChangeIndex);
        Assert.Equal("user", r.Clarification.Field);
        Assert.Equal(["u-2", "u-3"], r.Clarification.Candidates.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Task_resolves_from_project_and_task_names_ignoring_case()
    {
        var r = await Resolver().TaskAsync(0, null, "acme portal", "development", default);
        Assert.Equal(new PiTask("t-1", "Development", "p-1", "Acme Portal"), r.Value);
    }

    [Fact]
    public async Task Exact_name_wins_over_partial_matches()
    {
        _pi.Projects.Add(new PiNamed("p-4", "Internal Tools"));
        var r = await Resolver().TaskAsync(0, null, "Internal", "Staff meeting", default);
        Assert.Equal("p-3", r.Value!.ProjectId);
    }

    [Fact]
    public async Task Ambiguous_project_lists_the_matching_projects()
    {
        var r = await Resolver().TaskAsync(1, null, "Acme", "Development", default);

        Assert.Null(r.Value);
        Assert.Equal("project", r.Clarification!.Field);
        Assert.Equal(["Acme Portal", "Acme Support"], r.Clarification.Candidates.Select(c => c.Name));
    }

    [Fact]
    public async Task Unknown_task_and_missing_names_are_clarifications()
    {
        var unknown = await Resolver().TaskAsync(0, null, "Internal", "Gardening", default);
        var missing = await Resolver().TaskAsync(0, null, "Internal", null, default);
        var badId = await Resolver().TaskAsync(0, "t-404", null, null, default);

        Assert.Contains("No task in Internal matches", unknown.Clarification!.Message);
        Assert.Contains("Give taskId, or both project and task names", missing.Clarification!.Message);
        Assert.Contains("No task with id t-404", badId.Clarification!.Message);
    }

    [Fact]
    public async Task Task_id_resolves_with_its_project_name()
        => Assert.Equal("Internal", (await Resolver().TaskAsync(0, "t-5", null, null, default)).Value!.ProjectName);
}
