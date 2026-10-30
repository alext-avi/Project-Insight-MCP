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

    /// <summary>
    /// The period containing <paramref name="date"/> when no timesheet covers it: the same shape as
    /// <paramref name="template"/> (the person's latest earlier timesheet), stepped forward or back,
    /// so a Sunday-to-Saturday company gets Sunday-to-Saturday weeks. Weekly and two-weekly periods
    /// only; anything else (or no template) falls back to Monday to Sunday.
    /// </summary>
    public static (DateOnly Start, DateOnly End) PeriodLike(PiTimeSheet? template, DateOnly date)
    {
        var length = template is null ? 0 : template.End.DayNumber - template.Start.DayNumber + 1;
        if (length is not (7 or 14))
            return (MondayOf(date), SundayOf(date));
        var periods = (int)Math.Floor((date.DayNumber - template!.Start.DayNumber) / (double)length);
        var start = template.Start.AddDays(periods * length);
        return (start, start.AddDays(length - 1));
    }

    /// <summary>The latest timesheet starting on or before <paramref name="date"/>, to shape periods after it.</summary>
    public static PiTimeSheet? LatestFrom(IEnumerable<PiTimeSheet> sheets, DateOnly date)
        => sheets.Where(s => s.Start <= date && s.End >= s.Start).MaxBy(s => s.Start);

    public static string ShortDate(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    public static string DayLabel(string personName, DateOnly date) => $"{personName} · {ShortDate(date)}";

    public static string FormatHours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    public static IEnumerable<DateOnly> Range(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
            yield return d;
    }
}
