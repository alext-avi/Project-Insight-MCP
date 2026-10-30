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
    /// only; anything else (or no template) falls back to Monday to Sunday. The period is then cut
    /// short where it would overlap any of <paramref name="sheets"/> (none of which covers the date).
    /// </summary>
    public static (DateOnly Start, DateOnly End) PeriodLike(PiTimeSheet? template, DateOnly date, IEnumerable<PiTimeSheet>? sheets = null)
    {
        var length = template is null ? 0 : template.End.DayNumber - template.Start.DayNumber + 1;
        var (start, end) = (MondayOf(date), SundayOf(date));
        if (length is 7 or 14)
        {
            var periods = (int)Math.Floor((date.DayNumber - template!.Start.DayNumber) / (double)length);
            start = template.Start.AddDays(periods * length);
            end = start.AddDays(length - 1);
        }
        foreach (var s in sheets ?? [])
        {
            if (s.Start > date && s.Start <= end)
                end = s.Start.AddDays(-1);
            else if (s.End < date && s.End >= start)
                start = s.End.AddDays(1);
        }
        return (start, end);
    }

    /// <summary>The latest timesheet starting on or before <paramref name="date"/>, to shape periods after it.</summary>
    public static PiTimeSheet? LatestFrom(IEnumerable<PiTimeSheet> sheets, DateOnly date)
        => sheets.Where(s => s.Start <= date && s.End >= s.Start).MaxBy(s => s.Start);

    /// <summary>"Tue 27 Oct", with the year when it isn't <paramref name="today"/>'s, so a wrong year shows.</summary>
    public static string ShortDate(DateOnly date, DateOnly today)
        => date.ToString(date.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);

    public static string DayLabel(string personName, DateOnly date, DateOnly today) => $"{personName} · {ShortDate(date, today)}";

    /// <summary>Today's date where the server runs, which is where the user is for the stdio host.</summary>
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public static string FormatHours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    public static IEnumerable<DateOnly> Range(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
            yield return d;
    }
}
