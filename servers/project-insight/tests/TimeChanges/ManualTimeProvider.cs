namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    // UTC, so "today" (GetLocalNow) is the same date on every machine that runs the tests.
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
