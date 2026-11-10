using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ProjectInsightMCP.Web.Security;

/// <summary>
/// The central scrubber for exported logs: the current request's bearer token is replaced in the message, its
/// arguments and any exception before the exporter sees the record. Log lines are written token-free by
/// construction; this catches one that isn't. Runs on the logging thread, so the request is still current.
/// </summary>
public sealed class LogRedactionProcessor(IHttpContextAccessor http) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        if (BearerOf(http.HttpContext) is not { } token)
            return;
        if (record.FormattedMessage is { } message)
            record.FormattedMessage = SecretForms.Redact(message, token);
        if (record.Body is { } body)
            record.Body = SecretForms.Redact(body, token);
        if (record.Attributes is { Count: > 0 } attributes
            && attributes.Any(a => a.Value?.ToString() is { } s && SecretForms.AppearsIn(s, token)))
            record.Attributes = attributes
                .Select(a => a.Value?.ToString() is { } s && SecretForms.AppearsIn(s, token)
                    ? new KeyValuePair<string, object?>(a.Key, SecretForms.Redact(s, token))
                    : a)
                .ToList();
        // An exception's text can't be edited; replace it with a redacted copy that keeps the type name.
        if (record.Exception is { } ex && SecretForms.AppearsIn(ex.ToString(), token))
            record.Exception = new RedactedException(ex.GetType().FullName + ": " + SecretForms.Redact(ex.Message, token));
    }

    private static string? BearerOf(HttpContext? context)
    {
        var header = context?.Request.Headers.Authorization.ToString();
        return header is { Length: > 7 } && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header[7..].Trim() is { Length: >= 4 } token
            ? token
            : null;
    }

    public sealed class RedactedException(string message) : Exception(message);
}
