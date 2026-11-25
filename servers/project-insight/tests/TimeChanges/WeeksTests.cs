using System.Globalization;
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class WeeksTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("th-TH")] // Buddhist calendar: 2026 is 2569
    public void Day_key_is_the_same_and_round_trips_in_any_culture(string name)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            var date = new DateOnly(2026, 11, 2);
            var key = Weeks.DayKey("u|1", date);
            Assert.Equal("u|1|2026-11-02", key);
            Assert.Equal(("u|1", date), Weeks.ParseDayKey(key));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
