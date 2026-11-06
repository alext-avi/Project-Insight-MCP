using System.Text;
using System.Text.Encodings.Web;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>Server-rendered pages. Every dynamic value is HTML-encoded; no script, no inline style.</summary>
public static class AuthorizePages
{
    private static readonly string[] FormFields =
        ["response_type", "client_id", "redirect_uri", "state", "code_challenge", "code_challenge_method", "scope", "resource"];

    // Un-encoding the apostrophe is safe only for text content and double-quoted attributes. Never use E() output
    // in single-quoted or unquoted attributes, script, style or URLs.
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "").Replace("&#x27;", "'");

    public static IResult Consent(AuthorizeRequest r, IReadOnlyDictionary<string, string?> fields)
    {
        var redirect = new Uri(r.RedirectUri);
        var body = new StringBuilder();
        body.Append("<h1>Connect Project Insight</h1>");
        body.Append($"<p><strong>{E(redirect.Host)}</strong> is asking to use your Project Insight account.</p>");
        if (r.Client.IsMetadataDocument && Uri.TryCreate(r.Client.ClientId, UriKind.Absolute, out var id))
            body.Append($"<p>App identity: <strong>{E(id.Host)}</strong></p>");
        if (!string.IsNullOrWhiteSpace(r.Client.Name))
            body.Append($"<p>It calls itself “{E(r.Client.Name)}” (unverified).</p>");
        if (RedirectPolicy.IsLoopback(redirect))
            body.Append("<p class=\"warn\">This app runs on your own computer (localhost). Only continue if you started it yourself, for example Claude Code or Codex.</p>");
        body.Append("<p class=\"warn\">Only continue if you just clicked Connect in Claude or ChatGPT yourself. If someone sent you this link, close this page.</p>");
        body.Append("<form method=\"post\" action=\"/authorize/start\">");
        foreach (var name in FormFields)
            if (fields.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                body.Append($"<input type=\"hidden\" name=\"{name}\" value=\"{E(value)}\">");
        body.Append("<button>Sign in to Project Insight</button></form>");
        return Page("Connect Project Insight", body.ToString(), 200);
    }

    public static IResult Activation(string sessionId, Uri activationUrl, string redirectHost, string? error)
    {
        var body = new StringBuilder();
        body.Append("<h1>Sign in to Project Insight</h1>");
        body.Append($"<p>Connecting <strong>{E(redirectHost)}</strong> to your Project Insight account.</p><ol>");
        body.Append($"<li><a href=\"{E(activationUrl.AbsoluteUri)}\" target=\"_blank\" rel=\"noopener noreferrer\">Open Project Insight</a> and sign in</li>");
        body.Append("<li>Copy the activation code it shows and paste it here.</li></ol>");
        if (error is not null)
            body.Append($"<p class=\"error\">{E(error)}</p>");
        body.Append("<form method=\"post\" action=\"/authorize/complete\">");
        body.Append($"<input type=\"hidden\" name=\"session_id\" value=\"{E(sessionId)}\">");
        body.Append("<label>Activation code <input name=\"activation_code\" autocomplete=\"off\" required maxlength=\"64\"></label>");
        body.Append("<button>Continue</button></form>");
        return Page("Sign in to Project Insight", body.ToString(), 200);
    }

    public static IResult Error(int status, string message)
        => Page("Couldn't connect", $"<h1>Couldn't connect</h1><p>{E(message)}</p>", status);

    private static IResult Page(string title, string body, int status)
        => Results.Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            $"<title>{E(title)}</title><link rel=\"stylesheet\" href=\"/oauth.css\"><script src=\"/oauth.js\" defer></script></head><body><main>{body}</main></body></html>",
            "text/html; charset=utf-8", statusCode: status);
}
