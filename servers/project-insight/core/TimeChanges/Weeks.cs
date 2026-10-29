using System.Globalization;

namespace ProjectInsightMCP.TimeChanges;

public static class Weeks
{
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly SundayOf(DateOnly date) => MondayOf(date).AddDays(6);

    public static string DayKey(string userId, DateOnly date) => $"{userId}|{date:yyyy-MM-dd}";

    /// <summary>Inverse of <see cref="DayKey"/>. Splits at the last '|' so user ids may contain one.</summary>
    public static (string UserId, DateOnly Date) ParseDayKey(string key)
    {
        var i = key.LastIndexOf('|');
        return (key[..i], DateOnly.ParseExact(key[(i + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public static PiTimeSheet? Covering(IEnumerable<PiTimeSheet> sheets, DateOnly date)
        => sheets.FirstOrDefault(s => s.Start <= date && date <= s.End);

    public static string ShortDate(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    public static string DayLabel(string personName, DateOnly date) => $"{personName} · {ShortDate(date)}";

    public static string FormatHours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    public static IEnumerable<DateOnly> Range(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
            yield return d;
    }
}
