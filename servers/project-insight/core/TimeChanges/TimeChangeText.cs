using System.Text;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// The text block of every time-change result. It must read on its own (clients without cards
/// show only this) and tell the model what to do next.
/// </summary>
public static class TimeChangeText
{
    public static string For(TimeChangesView view) => view.Kind switch
    {
        "needs_clarification" => Clarify(view.Clarifications ?? new()),
        "draft" when view.Draft is { } d => Draft(d),
        _ => view.Message ?? "Something went wrong."
    };

    private static string Clarify(List<Clarification> items)
    {
        var sb = new StringBuilder("No draft was created. Resolve these and call plan_time_changes again:");
        foreach (var c in items)
        {
            sb.Append($"\n- Change {c.ChangeIndex + 1}: {c.Message}");
            if (c.Candidates.Count > 0)
                sb.Append(" Candidates: ").Append(string.Join("; ", c.Candidates.Select(x => $"{x.Name} ({x.Id})")));
        }
        return sb.ToString();
    }

    private static string Draft(DraftView d)
    {
        var t = d.Totals;
        var sb = new StringBuilder();
        sb.Append(d.Status switch
        {
            DraftStatus.Draft =>
                $"{(d.UndoOf is null ? "Draft" : $"Undo draft (reverses {d.UndoOf})")} {d.Id}: {Count(t.Changes, "change", "changes")} " +
                $"across {Count(t.Days, "day", "days")} for {Count(t.People, "person", "people")}, net {Signed(t.NetHours)}h " +
                $"(billable {Signed(t.BillableDelta)}h). Nothing has changed yet. Summarize the draft for the user and ask them " +
                "to confirm. They can press Confirm on the card, or tell you to commit it with commit_time_changes.",
            DraftStatus.Committing => $"Draft {d.Id} is being committed right now. Check again with get_time_changes.",
            DraftStatus.Committed =>
                $"Committed {d.Id}: {Count(t.Changes, "change", "changes")} applied " +
                $"({(d.Channel == CommitChannel.Agent ? "committed by the agent" : "confirmed by the user in chat")})." +
                (d.RevertedBy is not null ? $" It has since been undone by {d.RevertedBy}."
                 : d.UndoUntil is { } until ? $" Undo is available until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC with undo_time_changes."
                 : ""),
            DraftStatus.Stale =>
                $"Nothing was changed: Project Insight changed after draft {d.Id} was made ({string.Join(", ", d.StaleDays ?? new())}). " +
                "Read the current entries and draft the changes again.",
            DraftStatus.Failed => $"Nothing was kept from draft {d.Id}. {d.Error}",
            DraftStatus.Cancelled => $"Draft {d.Id} was cancelled. Nothing was changed.",
            DraftStatus.Expired => $"Draft {d.Id} expired without being confirmed. Nothing was changed. Draft the changes again if they are still wanted.",
            _ => $"Draft {d.Id} is {d.Status}."
        });
        sb.Append($"\nIntent: {d.Intent}");
        foreach (var r in d.Rows)
            sb.Append('\n').Append(Row(r, d));
        var reverseFailed = d.Rows.Where(r => r.Outcome == RowOutcome.ReverseFailed).ToList();
        if (reverseFailed.Count > 0)
            sb.Append("\nThese could not be reversed and need checking in Project Insight: ")
              .Append(string.Join("; ", reverseFailed.Select(r => $"change {r.Index + 1} ({r.TimeEntryId ?? r.SavedId})")));
        return sb.ToString();
    }

    private static string Row(DraftRow r, DraftView d)
    {
        var v = (r.After ?? r.Before)!;
        var who = d.Weeks.Any(w => w.UserId == r.UserId && w.IsCaller) ? "You" : r.UserName;
        var hours = r.Action switch
        {
            ChangeAction.Create => $"+{Weeks.FormatHours(r.After!.Hours)}h",
            ChangeAction.Delete => $"−{Weeks.FormatHours(r.Before!.Hours)}h",
            _ => $"{Weeks.FormatHours(r.Before!.Hours)}h → {Weeks.FormatHours(r.After!.Hours)}h"
        };
        var line = new StringBuilder(
            $"{r.Index + 1}. {r.Action} · {who} · {Weeks.ShortDate(v.Date)} · {v.ProjectName} › {v.TaskName} · {hours}");
        if (v.Billable) line.Append(" (billable)");
        if (v.Description is { } desc) line.Append($" — \"{desc}\"");
        if (r is { Action: ChangeAction.Update, Before: { } b, After: { } a })
        {
            if (b.Date != a.Date) line.Append($"; moved from {Weeks.ShortDate(b.Date)}");
            if (b.TaskId != a.TaskId) line.Append($"; was {b.ProjectName} › {b.TaskName}");
        }
        foreach (var w in r.Warnings) line.Append($" [warning: {w.Message}]");
        if (r.Outcome is { } outcome && outcome != RowOutcome.Applied)
            line.Append($" [{outcome.Replace('_', ' ')}{(r.Error is null ? "" : ": " + r.Error)}]");
        return line.ToString();
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Signed(decimal h) => h > 0 ? "+" + Weeks.FormatHours(h) : h < 0 ? "−" + Weeks.FormatHours(-h) : "±0";
}
