using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectInsightMCP.Web.Security;

/// <summary>
/// The spellings a secret can take in text: as-is, and as JSON encoders write it. System.Text.Json's default
/// encoder writes '+' as \u002B; other serializers use lower-case hex or write '/' as \/.
/// </summary>
public static partial class SecretForms
{
    public const string Redacted = "[redacted]";

    public static IReadOnlyList<string> Of(string secret)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal) { secret };
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var encoded = JsonEncodedText.Encode(secret, encoder).ToString();
            forms.Add(encoded);
            forms.Add(EscapeHex().Replace(encoded, m => m.Value.ToLowerInvariant()));
        }
        foreach (var form in forms.ToList())
            forms.Add(form.Replace("/", "\\/", StringComparison.Ordinal));
        // Longest first, so a shorter spelling never cuts into a longer one.
        return forms.OrderByDescending(f => f.Length).ToList();
    }

    public static string Redact(string text, string secret) => Redact(text, Of(secret));

    /// <summary>
    /// With <paramref name="forms"/> from <see cref="Of"/>. Computing them takes two encodes and two regex passes, so a
    /// caller scrubbing many strings for one secret computes them once and passes them to each call.
    /// </summary>
    public static string Redact(string text, IReadOnlyList<string> forms)
        => forms.Aggregate(text, (t, form) => t.Replace(form, Redacted, StringComparison.Ordinal));

    public static bool AppearsIn(string text, string secret) => AppearsIn(text, Of(secret));

    /// <summary>With <paramref name="forms"/> from <see cref="Of"/>, computed once per secret.</summary>
    public static bool AppearsIn(string text, IReadOnlyList<string> forms)
        => forms.Any(form => text.Contains(form, StringComparison.Ordinal));

    [GeneratedRegex(@"\\u[0-9A-F]{4}")]
    private static partial Regex EscapeHex();
}
