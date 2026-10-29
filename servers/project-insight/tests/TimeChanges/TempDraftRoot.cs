namespace ProjectInsightMCP.Tests.TimeChanges;

public sealed class TempDraftRoot : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pi-drafts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
