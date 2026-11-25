using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// PI time entries have no "last updated" field, so staleness is detected by hashing everything
/// a draft depends on for one person-day: the day's entries, the timesheet's status flags and the day's PTO records
/// (approval included, since the card warns about approved PTO).
/// </summary>
public static class Fingerprint
{
    /// <param name="dayPto">The day's PTO records. Null or empty leaves the hash exactly as it was before PTO existed,
    /// so drafts stored then don't all go stale.</param>
    public static string Compute(IEnumerable<PiEntry> dayEntries, PiTimeSheet? sheet, IEnumerable<PiPto>? dayPto = null)
    {
        var sb = new StringBuilder();
        foreach (var e in dayEntries.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            sb.Append(e.Id).Append('|')
              .Append(e.TaskId).Append('|')
              .Append(e.Hours.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
              .Append(e.Billable ? '1' : '0').Append('|')
              .Append(e.Description ?? "").Append('\n');
        }
        sb.Append("ts|")
          .Append(sheet?.Submitted == true ? '1' : '0')
          .Append(sheet?.Approved == true ? '1' : '0')
          .Append(sheet?.Locked == true ? '1' : '0');
        foreach (var p in (dayPto ?? []).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            sb.Append("\npto|").Append(p.Id).Append('|')
              .Append(p.TimeCodeId).Append('|')
              .Append(p.Hours.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
              .Append(p.Description ?? "").Append('|')
              .Append(p.Approved ? '1' : '0');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
