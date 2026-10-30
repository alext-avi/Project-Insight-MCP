using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class FingerprintTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);

    private static PiEntry Entry(string id, decimal hours = 1m, string task = "t-1", bool billable = true, string? desc = "x")
        => new(id, "u-1", Day, hours, billable, desc, task, "Task", "p-1", "Project", null);

    [Fact]
    public void Same_entries_in_any_order_give_the_same_fingerprint()
        => Assert.Equal(
            Fingerprint.Compute([Entry("a"), Entry("b")], null),
            Fingerprint.Compute([Entry("b"), Entry("a")], null));

    [Fact]
    public void Equal_hours_written_differently_give_the_same_fingerprint()
        => Assert.Equal(Fingerprint.Compute([Entry("a", 1.5m)], null), Fingerprint.Compute([Entry("a", 1.50m)], null));

    [Theory]
    [InlineData("hours")]
    [InlineData("task")]
    [InlineData("billable")]
    [InlineData("description")]
    [InlineData("added")]
    [InlineData("removed")]
    public void Any_change_to_the_day_changes_the_fingerprint(string change)
    {
        var before = Fingerprint.Compute([Entry("a"), Entry("b")], null);
        PiEntry[] after = change switch
        {
            "hours" => [Entry("a", 2m), Entry("b")],
            "task" => [Entry("a", task: "t-2"), Entry("b")],
            "billable" => [Entry("a", billable: false), Entry("b")],
            "description" => [Entry("a", desc: "y"), Entry("b")],
            "added" => [Entry("a"), Entry("b"), Entry("c")],
            _ => [Entry("a")]
        };
        Assert.NotEqual(before, Fingerprint.Compute(after, null));
    }

    [Fact]
    public void Timesheet_status_is_part_of_the_fingerprint()
    {
        var open = new PiTimeSheet("ts", Day, Day, false, false, false, null);
        Assert.NotEqual(Fingerprint.Compute([], open), Fingerprint.Compute([], open with { Submitted = true }));
        Assert.NotEqual(Fingerprint.Compute([], open), Fingerprint.Compute([], open with { Approved = true }));
        Assert.NotEqual(Fingerprint.Compute([], open), Fingerprint.Compute([], open with { Locked = true }));
        Assert.Equal(Fingerprint.Compute([], null), Fingerprint.Compute([], open));
    }

    [Fact]
    public void Week_helpers_use_monday_to_sunday_and_prefer_a_covering_timesheet()
    {
        Assert.Equal(new DateOnly(2026, 10, 26), Weeks.MondayOf(Day));
        Assert.Equal(new DateOnly(2026, 11, 1), Weeks.SundayOf(Day));
        Assert.Equal(new DateOnly(2026, 10, 26), Weeks.MondayOf(new DateOnly(2026, 11, 1)));
        var sheet = new PiTimeSheet("ts", new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 30), false, false, false, null);
        Assert.Same(sheet, Weeks.Covering([sheet], Day));
        Assert.Null(Weeks.Covering([sheet], new DateOnly(2026, 10, 31)));
        Assert.Equal("u-1|2026-10-27", Weeks.DayKey("u-1", Day));
        Assert.Equal("Jane Doe · Tue 27 Oct", Weeks.DayLabel("Jane Doe", Day));
    }

    [Fact]
    public void Format_helpers_round_trip_day_keys_and_format_dates_and_hours()
    {
        Assert.Equal("Tue 27 Oct", Weeks.ShortDate(Day));
        Assert.Equal(("u-1", Day), Weeks.ParseDayKey(Weeks.DayKey("u-1", Day)));
        Assert.Equal(("a|b", Day), Weeks.ParseDayKey(Weeks.DayKey("a|b", Day)));
        Assert.Equal("1.5", Weeks.FormatHours(1.50m));
        Assert.Equal("4", Weeks.FormatHours(4m));
        Assert.Equal("0.25", Weeks.FormatHours(0.25m));
    }

    [Fact]
    public void Draft_ids_and_tokens_are_random_and_tokens_verify_against_their_hash()
    {
        var id = DraftIds.NewDraftId();
        Assert.Matches("^d_[a-z2-9]{10}$", id);
        Assert.NotEqual(id, DraftIds.NewDraftId());

        var token = DraftIds.NewConfirmToken();
        Assert.Matches("^[0-9a-f]{32}$", token);
        var hash = DraftIds.Hash(token);
        Assert.True(DraftIds.TokenMatches(token, hash));
        Assert.False(DraftIds.TokenMatches(DraftIds.NewConfirmToken(), hash));
        Assert.False(DraftIds.TokenMatches("", hash));
    }

    [Theory]
    [InlineData("2026-09-20", "2026-09-26", "2026-10-27", "2026-10-25", "2026-10-31")] // weekly, Sun-Sat, stepped forward
    [InlineData("2026-09-20", "2026-09-26", "2026-10-25", "2026-10-25", "2026-10-31")] // the Sunday itself starts the week
    [InlineData("2026-09-20", "2026-09-26", "2026-10-24", "2026-10-18", "2026-10-24")] // Saturday ends the one before
    [InlineData("2026-10-04", "2026-10-17", "2026-10-27", "2026-10-18", "2026-10-31")] // two-weekly
    [InlineData("2026-11-08", "2026-11-14", "2026-10-27", "2026-10-25", "2026-10-31")] // template after the date: stepped back
    [InlineData("2026-10-01", "2026-10-15", "2026-10-27", "2026-10-26", "2026-11-01")] // semi-monthly: Monday-Sunday fallback
    public void A_week_without_a_timesheet_copies_the_last_timesheets_shape(string tStart, string tEnd, string date, string start, string end)
    {
        var template = new PiTimeSheet("ts", DateOnly.Parse(tStart), DateOnly.Parse(tEnd), false, false, false, null);

        Assert.Equal((DateOnly.Parse(start), DateOnly.Parse(end)), Weeks.PeriodLike(template, DateOnly.Parse(date)));
    }

    [Fact]
    public void With_no_timesheet_history_the_week_is_monday_to_sunday()
        => Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)), Weeks.PeriodLike(null, new DateOnly(2026, 10, 27)));
}
