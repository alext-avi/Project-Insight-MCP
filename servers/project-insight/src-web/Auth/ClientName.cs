using System.Globalization;
using System.Text;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// A client's self-asserted name, as the consent page shows it. Control and format characters (bidi embeddings,
/// overrides, isolates and marks, zero-width spaces, tags) are dropped, so a name can't reorder or hide the text
/// around it. The two joiners are kept: emoji sequences and scripts such as Persian and Devanagari need them, and
/// they only shape the characters either side. Variation selectors are marks, not format characters, so they stay.
/// </summary>
public static class ClientName
{
    public const int MaxLength = 100;
    private const int ZeroWidthNonJoiner = 0x200C, ZeroWidthJoiner = 0x200D;

    /// <summary>The name without control or format characters other than the joiners; null if nothing else is left.</summary>
    public static string? Clean(string? name)
    {
        if (name is null)
            return null;
        var kept = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
            if (rune.Value is ZeroWidthNonJoiner or ZeroWidthJoiner
                || Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
                kept.Append(rune.ToString());
        var cleaned = kept.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
