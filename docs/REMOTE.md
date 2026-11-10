# Connecting to the remote server

The remote server serves the same tools as the local `projectinsight-mcp` tool at `https://<app>/mcp`.
You sign in with your Project Insight account; the connector keeps the resulting token, which lasts
90 days. Only Project Insight users the server's owner has allowed can connect.

## Claude (claude.ai, Desktop, Cowork, mobile)

Settings → Connectors → Add custom connector → URL `https://<app>/mcp` → Connect. A sign-in page
opens: click **Sign in to Project Insight**, sign in, copy the activation code Project Insight shows,
paste it, and press Continue.

## Claude Code

    claude mcp add --transport http project-insight https://<app>/mcp

Then run `/mcp` in Claude Code and choose project-insight to sign in.

## ChatGPT

Settings → Apps & Connectors → Advanced → Developer mode, then Create: URL `https://<app>/mcp`,
authentication OAuth. Sign in as above.

## Codex

In `~/.codex/config.toml`:

    mcp_oauth_credentials_store = "keyring"

    [mcp_servers.project-insight]
    url = "https://<app>/mcp"

Then `codex mcp login project-insight`. The keyring setting keeps the token out of a plain file.

Or add the server from the command line, then sign in:

    codex mcp add project-insight --url https://<app>/mcp
    codex mcp login project-insight

## Signing in

The same steps apply in every client.

1. The consent page opens. Click **Sign in to Project Insight**.
2. The activation page shows an **Open Project Insight** link. Click it and sign in there. Project
   Insight shows you an activation code.
3. Paste the code into the activation page and press **Continue**. The button reads "Checking…"
   while the server works.

A wrong code can be retried a few times. After that the sign-in ends and you must start it again
from your client.

## Where your token lives

The connector holds a Project Insight token with your full Project Insight permissions for 90 days.
For Claude and ChatGPT it stays in the vendor's connector service, out of the model's reach. For
Claude Code and Codex it is in your OS keychain (or a credentials file on Linux and Windows), which
an agent with shell access could read, the same as the local tool's stored credentials.

## Limits

The server rate-limits each client IP address:

| Endpoint | Limit |
|----------|-------|
| `/register` | 10 per hour |
| `/authorize` | 30 per 10 minutes |
| `/authorize/complete` | 20 per 10 minutes |
| `/token` | 30 per 10 minutes |
| `/mcp` | 300 per minute |

Going over a limit returns HTTP 429 with a `Retry-After` header.

Hosted connectors (Claude, ChatGPT) call from their vendors' shared backend addresses, so many users
of the same vendor share one limit for `/mcp`, `/token` and `/register`. If users see 429 errors,
tell the server's owner. The owner watches telemetry and raises the limits.

## Revoking a token

After you revoke a token in Project Insight, tool calls fail at Project Insight at once. The server
may keep accepting the token at `/mcp` for up to 10 minutes, because it caches a successful identity
check. After that the client is asked to sign in again.

## If you are not on the allow list

If your Project Insight account is not allowed, the sign-in ends with "This server isn't open to your
Project Insight account." Project Insight has already created a token during activation. The server
discards it, but it stays valid in Project Insight until it expires on its own (90 days) or you
revoke it there.

## If a token leaks

Not yet verified. The server must not be used until the owner has filled in this section with the
Project Insight screen where a user, and an admin, revoke a device-activation token (each one is
labelled "MCP connector (<app host>)"), and confirmed that a revoked token stops working.
