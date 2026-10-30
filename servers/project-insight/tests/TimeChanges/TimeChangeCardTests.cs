using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.TimeChanges;

public class TimeChangeCardTests
{
    private static readonly string Html = TimeChangeCard.Html;

    [Fact]
    public void Card_is_embedded_and_self_contained()
    {
        Assert.StartsWith("<!doctype html>", Html, StringComparison.OrdinalIgnoreCase);
        foreach (var banned in new[]
                 {
                     "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function",
                     "fetch(", "XMLHttpRequest", "http://", "https://", "<link", "src=", "@import"
                 })
            Assert.DoesNotContain(banned, Html);
    }

    [Fact]
    public void Card_speaks_the_mcp_apps_protocol_and_calls_the_time_change_tools()
    {
        foreach (var expected in new[]
                 {
                     "ui/initialize", "ui/notifications/initialized", "ui/notifications/tool-result",
                     "ui/notifications/host-context-changed", "ui/notifications/size-changed", "ui/open-link",
                     "tools/call", "get_time_changes", "confirm_time_changes", "cancel_time_changes", "undo_time_changes",
                     "structuredContent", "confirmToken", "event.source !== window.parent"
                 })
            Assert.Contains(expected, Html);
    }

    [Fact]
    public void Card_has_light_and_dark_tokens()
    {
        Assert.Contains(":root {", Html);
        Assert.Contains("@media (prefers-color-scheme: dark)", Html);
        Assert.Contains(":root:not([data-theme=\"light\"])", Html);
        Assert.Contains(":root[data-theme=\"dark\"]", Html);
    }

    [Fact]
    public void Card_words_undo_and_committed_bylines_from_the_receipt_fields()
    {
        Assert.Contains("undoOfCommittedAt", Html);
        Assert.Contains("confirmed by you", Html);
        Assert.Contains("committed by the agent", Html);
    }

    [Fact]
    public void Card_flags_unsure_failures_links_reverse_failed_entries_and_rereads_after_errors()
    {
        Assert.Contains("Failed · check Project Insight", Html);
        Assert.Contains("Failed · rolled back", Html);
        Assert.Contains("r.outcome === \"reverse_failed\"", Html);
        Assert.Contains("failedEntry(r, w)", Html);
        Assert.Contains("r.savedId || r.timeEntryId", Html);
        Assert.Contains("if (failed && id) refresh(id);", Html);
        Assert.Contains("failed = true;", Html);
        Assert.Contains("300000", Html);
        Assert.Contains("commit_time_changes", Html);
    }

    [Fact]
    public void Card_greys_an_undone_receipt_and_offers_to_draft_it_again()
    {
        Assert.Contains("Undone · back to how it was", Html);
        Assert.Contains("app.classList.toggle(\"spent\"", Html);
        Assert.Contains(".spent .row.chg", Html);
        Assert.Contains("act(\"undo_time_changes\", { receiptId: d.revertedBy })", Html);
        Assert.Contains("Draft a redo", Html);
        Assert.Contains("startsWith(\"Redo: \") ? \"redo\" : \"undo\"", Html);
    }

    [Fact]
    public void Card_draws_pi_avatars_and_project_colours_and_checks_them_first()
    {
        Assert.Contains("w.avatar", Html);
        Assert.Contains("look.photo && PHOTO.test(look.photo)", Html);
        Assert.Contains("HEX.test(look.background)", Html);
        Assert.Contains("swatch(v.projectColor)", Html);
        Assert.Contains("swatch(c.projectColor)", Html);
    }

    [Fact]
    public void Card_treats_an_unknown_outcome_like_a_failed_reversal()
    {
        Assert.Contains("d.outcomeUnknown", Html);
        Assert.Contains("unknown: \"", Html);
        Assert.Contains("r.outcome === \"unknown\"", Html);
        Assert.Contains(".st-unknown", Html);
    }

    [Fact]
    public void Card_shows_the_year_of_a_date_outside_this_year()
    {
        Assert.Contains("y !== new Date().getFullYear()", Html);
        Assert.Contains("{ year: \"numeric\" }", Html);
        // Every date on the card goes through day(): day lines, "week of", "moved to/from".
        Assert.Contains("\" · week of \" + day(", Html);
        Assert.Contains("\"moved to \" + day(", Html);
        Assert.Contains("\"moved from \" + day(", Html);
    }

    [Fact]
    public void Card_words_the_undo_button_by_the_receipts_kind()
    {
        Assert.Contains("const redo = !!d.undoOf && String(d.intent || \"\").startsWith(\"Undo: \");", Html);
        Assert.Contains("button(redo ? \"Draft a redo\" : \"Draft an undo\"", Html);
    }

    [Fact]
    public void Card_shows_a_redone_receipt_as_back_in_effect_with_no_button()
    {
        Assert.Contains("Redone · changes put back", Html);
        Assert.Contains("Put back by a later redo; undo that one to remove them again.", Html);
        Assert.Contains("d.status === \"committed\" && !!d.revertedBy && !d.redoneBy", Html);
        Assert.Contains("d.status === \"committed\" && d.revertedBy && d.redoneBy", Html);
    }

    [Fact]
    public void Card_opens_and_shows_only_https_links()
    {
        Assert.Contains("/^https:\\/\\//.test(url)", Html);
        Assert.Contains("if (!isHttps(url)) return;", Html);
        Assert.Contains("if (isHttps(w.timesheetUrl))", Html);
        Assert.Contains("if (w && isHttps(w.timesheetUrl))", Html);
        Assert.DoesNotContain("if (w.timesheetUrl)", Html);
    }
}
