using System.Diagnostics;
using ProjectInsightMCP.Web.Security;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class QueryRedactionProcessorTests
{
    [Theory]
    [InlineData("url.full")]
    [InlineData("http.url")]
    public void Query_strings_are_removed_from_outbound_urls(string tag)
    {
        using var activity = new Activity("HTTP GET").Start();
        activity.SetTag(tag, "https://pi.test/api/device-activation/complete-request?activationRequestId=a&activationCode=SECRET");

        new QueryRedactionProcessor().OnEnd(activity);

        Assert.Equal("https://pi.test/api/device-activation/complete-request", activity.GetTagItem(tag));
    }
}
