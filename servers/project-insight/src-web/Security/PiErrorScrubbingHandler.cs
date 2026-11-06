using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ProjectInsightMCP.Web.Security;

/// <summary>
/// On every outbound PI call: an error body is shortened and stripped of the credentials the
/// request carried, before Core copies it into a tool result or a log line.
/// </summary>
public sealed class PiErrorScrubbingHandler : DelegatingHandler
{
    public const int MaxErrorBody = 500;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;
        var body = await response.Content.ReadAsStringAsync(ct);
        var scrubbed = Scrub(body, SecretsIn(request));
        if (scrubbed.Length > MaxErrorBody)
            scrubbed = scrubbed[..MaxErrorBody] + "…";
        response.Content = new StringContent(scrubbed, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "text/plain");
        return response;
    }

    public static string Scrub(string text, IEnumerable<string> secrets)
        => secrets.Where(s => s.Length >= 4).Aggregate(text, (t, s) => t.Replace(s, "[redacted]", StringComparison.Ordinal));

    private static IEnumerable<string> SecretsIn(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter is { } bearer)
            yield return bearer;
        if (request.Headers.TryGetValues("api-token", out var apiTokens))
            foreach (var t in apiTokens)
                yield return t;
        if (request.RequestUri?.Query is { Length: > 1 } query)
            foreach (var (key, value) in QueryHelpers.ParseQuery(query))
                if (key is "activationCode" or "activationRequestId")
                    yield return value.ToString();
    }
}
