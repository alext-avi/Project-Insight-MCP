using System.Diagnostics;
using OpenTelemetry;

namespace ProjectInsightMCP.Web.Security;

/// <summary>
/// Outbound URLs lose their query strings before export: PI's device-activation endpoint takes the
/// activation code in the query, and nothing in a query is needed for diagnosis.
/// </summary>
public sealed class QueryRedactionProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlTags = ["url.full", "http.url"];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in UrlTags)
            if (activity.GetTagItem(tag) is string url && url.IndexOf('?') is var q and >= 0)
                activity.SetTag(tag, url[..q]);
    }
}
