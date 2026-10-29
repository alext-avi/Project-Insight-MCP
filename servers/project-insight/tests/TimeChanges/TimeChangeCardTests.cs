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
}
