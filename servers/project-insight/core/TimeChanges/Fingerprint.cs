using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.TimeChanges;

/// <summary>
/// PI time entries have no "last updated" field, so staleness is detected by hashing everything
/// a draft depends on for one person-day: the day's entries plus the timesheet's status flags.
/// </summary>
public static class Fingerprint
{
    public static string Compute(IEnumerable<PiEntry> dayEntries, PiTimeSheet? sheet)
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
