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
        var original = response.Content;
        var body = await ReadBoundedAsync(original, ct);
        var scrubbed = Scrub(body, SecretsIn(request));
        if (scrubbed.Length > MaxErrorBody)
            scrubbed = scrubbed[..MaxErrorBody] + "…";
        response.Content = new StringContent(scrubbed, Encoding.UTF8, original.Headers.ContentType?.MediaType ?? "text/plain");
        original.Dispose();
        return response;
    }

    // A secret can only straddle the cut if it sits within the last few bytes; the cap is generous enough
    // that anything surviving the truncation below was scrubbed whole first.
    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxErrorBody * 4];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    // Every JSON spelling too: PI's own serializer may escape characters of a token it echoes.
    public static string Scrub(string text, IEnumerable<string> secrets)
        => secrets.Where(s => s.Length >= 4).Aggregate(text, SecretForms.Redact);

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
