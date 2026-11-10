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
        // Once per result, not per string: a large structured result holds many thousands of strings.
        var forms = SecretForms.Of(secret);
        foreach (var text in result.Content.OfType<TextContentBlock>())
            text.Text = SecretForms.Redact(text.Text, forms);
        // Compared after JSON decoding, and in every JSON spelling: a decoded string can still hold an escaped one
        // (a PI error body passed through as a value), so no escaping of the token can hide it.
        if (result.StructuredContent is { } structured && Mentions(structured, forms))
            result.StructuredContent = Rewrite(structured, forms);
        return result;
    }

    private static bool Mentions(JsonElement e, IReadOnlyList<string> forms) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Any(p => SecretForms.AppearsIn(p.Name, forms) || Mentions(p.Value, forms)),
        JsonValueKind.Array => e.EnumerateArray().Any(i => Mentions(i, forms)),
        JsonValueKind.String => SecretForms.AppearsIn(e.GetString()!, forms),
        _ => false
    };

    private static JsonElement Rewrite(JsonElement e, IReadOnlyList<string> forms)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, e, forms);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement e, IReadOnlyList<string> forms)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    writer.WritePropertyName(SecretForms.Redact(p.Name, forms));
                    Write(writer, p.Value, forms);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                    Write(writer, item, forms);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SecretForms.Redact(e.GetString()!, forms));
                break;
            default:
                e.WriteTo(writer);
                break;
        }
    }
}
