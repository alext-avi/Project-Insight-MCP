namespace ProjectInsightMCP.TimeChanges;

/// <summary>The MCP Apps review card, embedded in Core so any host can serve it.</summary>
public static class TimeChangeCard
{
    private const string ResourceName = "ProjectInsightMCP.TimeChanges.Card.time-changes.html";

    private static readonly Lazy<string> Content = new(() =>
    {
        using var stream = typeof(TimeChangeCard).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Html => Content.Value;
}
