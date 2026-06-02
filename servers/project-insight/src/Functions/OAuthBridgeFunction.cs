using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Functions;

/// <summary>
/// OAuth 2.1 authorization-code-with-PKCE bridge that lets remote MCP clients
/// (Claude.ai's "Custom Connectors") obtain a Project Insight API token without
/// us holding any per-user state on the server.
///
/// User sign-in is delegated to PI's hosted activation page, which itself sits
/// behind Entra SSO — so users get the AVI tenant login experience without the
/// bridge needing to integrate Entra directly.
///
/// Lives separately from McpFunction.cs to keep the MCP "vehicle" untouched.
/// The bridge is a sidecar; remove these files and the MCP server still works
/// for any client that brings its own PI token.
/// </summary>
public class OAuthBridgeFunction
{
    private readonly DeviceActivationService _piActivation;
    private readonly AuthCodeStore _store;
    private readonly ILogger<OAuthBridgeFunction> _logger;
    private readonly HashSet<string> _allowedRedirectUris;

    /// <summary>
    /// Default redirect URIs Claude.ai uses for custom MCP connectors.
    /// Override via the OAUTH_ALLOWED_REDIRECT_URIS env var (comma-separated).
    /// </summary>
    private static readonly string[] DefaultAllowedRedirectUris = new[]
    {
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
    };

    public OAuthBridgeFunction(
        DeviceActivationService piActivation,
        AuthCodeStore store,
        IConfiguration configuration,
        ILogger<OAuthBridgeFunction> logger)
    {
        _piActivation = piActivation;
        _store = store;
        _logger = logger;

        var configured = configuration["OAUTH_ALLOWED_REDIRECT_URIS"];
        var uris = string.IsNullOrWhiteSpace(configured)
            ? DefaultAllowedRedirectUris
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _allowedRedirectUris = new HashSet<string>(uris, StringComparer.Ordinal);
    }

    // ─── /oauth/authorize ────────────────────────────────────────────────────

    /// <summary>
    /// OAuth 2.0 authorization endpoint. Validates the request, creates a PI
    /// device-activation, and renders an HTML page that:
    ///   1. Tells the user to open PI's sign-in URL (opens it in a new tab),
    ///   2. Lets them paste back the activation code PI shows after sign-in,
    ///   3. POSTs that code to /oauth/complete.
    /// </summary>
    [FunctionName("OAuthAuthorize")]
    public async Task<IActionResult> Authorize(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "oauth/authorize")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var responseType = req.Query["response_type"].ToString();
        var clientId = req.Query["client_id"].ToString();
        var redirectUri = req.Query["redirect_uri"].ToString();
        var state = req.Query["state"].ToString();
        var codeChallenge = req.Query["code_challenge"].ToString();
        var codeChallengeMethod = req.Query["code_challenge_method"].ToString();

        if (responseType != "code")
            return BadRequestHtml("response_type must be 'code'");
        if (string.IsNullOrEmpty(clientId))
            return BadRequestHtml("client_id is required");
        if (string.IsNullOrEmpty(redirectUri) || !Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            return BadRequestHtml("redirect_uri is required and must be absolute");
        // RFC 6749 §4.1.2.1: when redirect_uri itself is invalid, do NOT redirect.
        // Render an error in our own domain so a phishing redirect_uri can't
        // capture an auth code.
        if (!_allowedRedirectUris.Contains(redirectUri))
        {
            _logger.LogWarning("Rejected /authorize: redirect_uri {RedirectUri} is not on the allowlist", redirectUri);
            return BadRequestHtml(
                "This redirect URI is not registered with the Project Insight MCP bridge. " +
                "If you're configuring a new client, ask an administrator to add its callback URL " +
                "to the OAUTH_ALLOWED_REDIRECT_URIS list.");
        }
        if (string.IsNullOrEmpty(codeChallenge))
            return BadRequestHtml("code_challenge is required (PKCE)");
        if (string.IsNullOrEmpty(codeChallengeMethod))
            codeChallengeMethod = "S256";
        if (codeChallengeMethod != "S256")
            return BadRequestHtml("code_challenge_method must be S256");

        var activation = await _piActivation.CreateActivationRequestAsync(cancellationToken);
        if (!activation.Success || string.IsNullOrEmpty(activation.ActivationId) || string.IsNullOrEmpty(activation.ActivationUrl))
        {
            _logger.LogError("Failed to create PI device activation for OAuth bridge: {Error}", activation.Error);
            return BadRequestHtml($"Failed to start Project Insight sign-in: {activation.Error}");
        }

        var sessionId = NewToken(32);
        await _store.SaveSessionAsync(new OAuthSession
        {
            SessionId = sessionId,
            ClientId = clientId,
            RedirectUri = redirectUri,
            State = state,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            PiActivationId = activation.ActivationId,
            PiActivationUrl = activation.ActivationUrl,
            ExpiresUtc = AuthCodeStore.NewSessionExpiry()
        }, cancellationToken);

        return RenderActivationPage(sessionId, activation.ActivationUrl, error: null);
    }

    // ─── /oauth/complete ─────────────────────────────────────────────────────

    /// <summary>
    /// Receives the user-pasted activation code, completes the PI activation,
    /// mints an OAuth auth code bound to the resulting PI token, and 302s back
    /// to the client's redirect_uri.
    /// </summary>
    [FunctionName("OAuthComplete")]
    public async Task<IActionResult> Complete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "oauth/complete")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var form = await req.ReadFormAsync(cancellationToken);
        var sessionId = form["session_id"].ToString();
        var activationCode = form["activation_code"].ToString().Trim();

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(activationCode))
            return BadRequestHtml("Missing session or activation code.");

        var session = await _store.GetSessionAsync(sessionId, cancellationToken);
        if (session == null)
            return BadRequestHtml("Your sign-in session expired. Please start over from Claude.");

        var complete = await _piActivation.CompleteActivationByIdAsync(session.PiActivationId, activationCode, cancellationToken);
        if (!complete.Success || string.IsNullOrEmpty(complete.ApiToken))
        {
            _logger.LogWarning("PI activation complete failed for session {Session}: {Error}", sessionId, complete.Error);
            // Re-render the form with an error so the user can retry.
            return RenderActivationPage(sessionId, session.PiActivationUrl,
                error: complete.Error ?? "Invalid activation code. Try copying it again.");
        }

        var code = NewToken(32);
        await _store.SaveAuthCodeAsync(new IssuedAuthCode
        {
            Code = code,
            PiToken = complete.ApiToken,
            CodeChallenge = session.CodeChallenge,
            CodeChallengeMethod = session.CodeChallengeMethod,
            RedirectUri = session.RedirectUri,
            ClientId = session.ClientId,
            ExpiresUtc = AuthCodeStore.NewCodeExpiry()
        }, cancellationToken);

        await _store.DeleteSessionAsync(sessionId, cancellationToken);

        // Redirect back to the OAuth client (Claude) with code & state.
        var separator = session.RedirectUri.Contains('?') ? '&' : '?';
        var redirect = $"{session.RedirectUri}{separator}code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrEmpty(session.State))
            redirect += $"&state={Uri.EscapeDataString(session.State)}";

        return new RedirectResult(redirect, permanent: false);
    }

    // ─── /oauth/token ────────────────────────────────────────────────────────

    /// <summary>
    /// OAuth 2.0 token endpoint. Exchanges the authorization code (plus PKCE
    /// verifier) for the PI token, which we hand back as the access_token.
    /// Single-use: ConsumeAuthCodeAsync deletes the code on read.
    /// </summary>
    [FunctionName("OAuthToken")]
    public async Task<IActionResult> Token(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "oauth/token")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var form = await req.ReadFormAsync(cancellationToken);
        var grantType = form["grant_type"].ToString();
        var code = form["code"].ToString();
        var redirectUri = form["redirect_uri"].ToString();
        var clientId = form["client_id"].ToString();
        var codeVerifier = form["code_verifier"].ToString();

        if (grantType != "authorization_code")
            return TokenError("unsupported_grant_type", "Only authorization_code is supported");
        if (string.IsNullOrEmpty(code))
            return TokenError("invalid_request", "code is required");
        if (string.IsNullOrEmpty(codeVerifier))
            return TokenError("invalid_request", "code_verifier is required (PKCE)");

        var issued = await _store.ConsumeAuthCodeAsync(code, cancellationToken);
        if (issued == null)
            return TokenError("invalid_grant", "Authorization code is invalid or expired");

        if (!string.IsNullOrEmpty(clientId) && clientId != issued.ClientId)
            return TokenError("invalid_grant", "client_id mismatch");
        if (!string.IsNullOrEmpty(redirectUri) && redirectUri != issued.RedirectUri)
            return TokenError("invalid_grant", "redirect_uri mismatch");

        if (!PkceVerifier.Verify(codeVerifier, issued.CodeChallenge, issued.CodeChallengeMethod))
            return TokenError("invalid_grant", "PKCE verification failed");

        // The "access token" we hand back IS the PI API token. The MCP server's
        // existing Bearer-token path uses it directly; no decryption, no lookup.
        var body = new
        {
            access_token = issued.PiToken,
            token_type = "Bearer",
            // PI tokens we mint with timeoutMinutes=525600 last ~1 year. We do
            // not track exact expiry per-token; clients should refresh by
            // re-running the flow if a 401 comes back from the MCP endpoint.
            expires_in = 31_536_000
        };
        return new JsonResult(body);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static IActionResult TokenError(string error, string description)
    {
        return new ObjectResult(new { error, error_description = description })
        {
            StatusCode = (int)HttpStatusCode.BadRequest
        };
    }

    private static IActionResult BadRequestHtml(string message)
    {
        var html = $"<!doctype html><html><body style=\"font-family:system-ui;max-width:480px;margin:4em auto\">" +
                   $"<h2>Sign-in error</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        return new ContentResult { Content = html, ContentType = "text/html", StatusCode = 400 };
    }

    private static IActionResult RenderActivationPage(string sessionId, string activationUrl, string? error)
    {
        var errorHtml = string.IsNullOrEmpty(error)
            ? ""
            : $"<p style=\"color:#b00\">{WebUtility.HtmlEncode(error)}</p>";

        var html = $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"">
  <title>Connect Project Insight</title>
  <style>
    body {{ font-family: system-ui, sans-serif; max-width: 520px; margin: 4em auto; line-height: 1.5; color: #222; }}
    h1 {{ font-size: 1.4em; }}
    .step {{ margin: 1.5em 0; }}
    .num {{ display: inline-block; background:#222; color:#fff; border-radius:50%; width:1.5em; height:1.5em; text-align:center; margin-right:.5em; }}
    a.button {{ display:inline-block; padding:.6em 1em; background:#0a5; color:#fff; text-decoration:none; border-radius:4px; }}
    input[type=text] {{ width:100%; padding:.6em; font-size:1em; border:1px solid #999; border-radius:4px; }}
    button {{ padding:.6em 1em; font-size:1em; background:#0a5; color:#fff; border:0; border-radius:4px; cursor:pointer; }}
  </style>
</head>
<body>
  <h1>Connect Project Insight</h1>
  <p>Claude is requesting access to your Project Insight account.</p>

  <div class=""step"">
    <p><span class=""num"">1</span> Open the Project Insight sign-in page:</p>
    <p><a class=""button"" href=""{WebUtility.HtmlEncode(activationUrl)}"" target=""_blank"" rel=""noopener"">Sign in to Project Insight</a></p>
  </div>

  <div class=""step"">
    <p><span class=""num"">2</span> Sign in with your AVI account, then copy the activation code shown there.</p>
  </div>

  <div class=""step"">
    <p><span class=""num"">3</span> Paste the activation code below and click <em>Connect</em>:</p>
    {errorHtml}
    <form method=""post"" action=""/api/oauth/complete"">
      <input type=""hidden"" name=""session_id"" value=""{WebUtility.HtmlEncode(sessionId)}"">
      <p><input type=""text"" name=""activation_code"" autocomplete=""off"" autofocus required placeholder=""Activation code""></p>
      <p><button type=""submit"">Connect</button></p>
    </form>
  </div>
</body>
</html>";

        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
    }

    private static string NewToken(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
