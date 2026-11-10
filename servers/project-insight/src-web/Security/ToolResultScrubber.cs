using System.Buffers;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ProjectInsightMCP.Web.Security;

/// <summary>Last line of defence: the caller's token never reaches the model through a tool result.</summary>
public static class ToolResultScrubber
{
    // Only text blocks and structured content are scrubbed: no tool returns other block types today.
    public static CallToolResult Scrub(CallToolResult result, string secret)
    {
        foreach (var text in result.Content.OfType<TextContentBlock>())
            text.Text = SecretForms.Redact(text.Text, secret);
        // Compared after JSON decoding, and in every JSON spelling: a decoded string can still hold an escaped one
        // (a PI error body passed through as a value), so no escaping of the token can hide it.
        if (result.StructuredContent is { } structured && Mentions(structured, secret))
            result.StructuredContent = Rewrite(structured, secret);
        return result;
    }

    private static bool Mentions(JsonElement e, string secret) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Any(p => SecretForms.AppearsIn(p.Name, secret) || Mentions(p.Value, secret)),
        JsonValueKind.Array => e.EnumerateArray().Any(i => Mentions(i, secret)),
        JsonValueKind.String => SecretForms.AppearsIn(e.GetString()!, secret),
        _ => false
    };

    private static JsonElement Rewrite(JsonElement e, string secret)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, e, secret);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement e, string secret)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    writer.WritePropertyName(SecretForms.Redact(p.Name, secret));
                    Write(writer, p.Value, secret);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                    Write(writer, item, secret);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SecretForms.Redact(e.GetString()!, secret));
                break;
            default:
                e.WriteTo(writer);
                break;
        }
    }
}
