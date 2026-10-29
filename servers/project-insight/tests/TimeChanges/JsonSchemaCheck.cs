using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectInsightMCP.Tests.TimeChanges;

/// <summary>
/// Just enough JSON Schema to check structuredContent against a tool's outputSchema: type
/// (including type arrays and integer/number), required, properties (unknown properties fail),
/// items, additionalProperties and local $ref.
/// </summary>
public static class JsonSchemaCheck
{
    public static List<string> Validate(JsonElement schema, JsonNode? value)
    {
        var errors = new List<string>();
        Check(schema, schema, value, "$", errors);
        return errors;
    }

    private static void Check(JsonElement root, JsonElement schema, JsonNode? value, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True)
            return;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Check(root, Resolve(root, reference.GetString()!), value, path, errors);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()!).ToList()
                : [type.GetString()!];
            var actual = KindOf(value);
            if (!allowed.Contains(actual) && !(actual == "integer" && allowed.Contains("number")))
            {
                errors.Add($"{path}: expected {string.Join("|", allowed)}, got {actual}");
                return;
            }
        }
        if (value is JsonObject obj)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
                    if (!obj.ContainsKey(name))
                        errors.Add($"{path}: missing required '{name}'");
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
            foreach (var (name, child) in obj)
            {
                if (hasProperties && properties.TryGetProperty(name, out var childSchema))
                    Check(root, childSchema, child, $"{path}.{name}", errors);
                else if (hasAdditional && additional.ValueKind == JsonValueKind.Object)
                    Check(root, additional, child, $"{path}.{name}", errors);
                else if (hasProperties)
                    errors.Add($"{path}: '{name}' is not in the schema");
            }
        }
        if (value is JsonArray array && schema.TryGetProperty("items", out var items))
            for (var i = 0; i < array.Count; i++)
                Check(root, items, array[i], $"{path}[{i}]", errors);
    }

    private static string KindOf(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => v.TryGetValue<decimal>(out var d) && d == decimal.Truncate(d) ? "integer" : "number",
            _ => "null"
        },
        _ => "unknown"
    };

    private static JsonElement Resolve(JsonElement root, string pointer)
    {
        var node = root;
        foreach (var part in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
        return node;
    }
}
