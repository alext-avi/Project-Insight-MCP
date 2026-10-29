using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Tests.TimeChanges;

public static class Drafts
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    public static Draft New(string owner = "u-1", string? id = null, string status = DraftStatus.Draft, DateTimeOffset? at = null)
    {
        var when = at ?? T0;
        return new Draft
        {
            Id = id ?? DraftIds.NewDraftId(),
            Status = status,
            Intent = "Test draft",
            ProposedBy = owner,
            ProposedByName = "Alex Example",
            ProposedAt = when,
            ExpiresAt = when.AddHours(24),
            StatusChangedAt = when,
            ConfirmTokenHash = DraftIds.Hash("token")
        };
    }
}
