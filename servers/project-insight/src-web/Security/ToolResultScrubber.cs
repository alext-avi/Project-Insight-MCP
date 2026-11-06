using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ProjectInsightMCP.Web.Security;

/// <summary>Last line of defence: the caller's token never reaches the model through a tool result.</summary>
public static class ToolResultScrubber
{
    public static CallToolResult Scrub(CallToolResult result, string secret)
    {
        foreach (var text in result.Content.OfType<TextContentBlock>())
            text.Text = text.Text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (result.StructuredContent is { } structured && structured.GetRawText().Contains(secret, StringComparison.Ordinal))
            result.StructuredContent = JsonDocument.Parse(structured.GetRawText().Replace(secret, "[redacted]", StringComparison.Ordinal)).RootElement.Clone();
        return result;
    }
}
