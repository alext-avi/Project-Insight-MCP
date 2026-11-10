using System.Globalization;
using System.Text;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// A client's self-asserted name, as the consent page shows it. Control and format characters (bidi overrides
/// and isolates, zero-width characters, tags) are dropped, so a name can't reorder or hide the text around it.
/// </summary>
public static class ClientName
{
    public const int MaxLength = 100;

    /// <summary>The name without control or format characters; null if nothing else is left.</summary>
    public static string? Clean(string? name)
    {
        if (name is null)
            return null;
        var kept = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
                kept.Append(rune.ToString());
        var cleaned = kept.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
