# Connecting to the remote server

The remote server serves the same tools as the local `projectinsight-mcp` tool at `https://<app>/mcp`.
You sign in with your Project Insight account; the connector keeps the resulting token, which lasts
90 days by default. The server's owner can set a shorter lifetime (`Auth__PiTokenLifetimeDays`), and
then you are asked to sign in again sooner. Only Project Insight users the server's owner has allowed
can connect.

## Claude (claude.ai, Desktop, Cowork, mobile)

Settings → Connectors → Add custom connector → URL `https://<app>/mcp` → Connect. A sign-in page
opens: click **Sign in to Project Insight**, sign in, copy the activation code Project Insight shows,
paste it, and press Continue.

## Claude Code

    claude mcp add --transport http project-insight https://<app>/mcp

Then run `/mcp` in Claude Code and choose project-insight to sign in.

## ChatGPT

Add a custom MCP server (connector) with URL `https://<app>/mcp` and OAuth authentication, then sign
in as above. Where that lives in ChatGPT's settings, and which plans and workspace settings allow it,
changes over time: follow OpenAI's help article "Connecting custom MCP servers in ChatGPT".

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

The connector holds a Project Insight token with your full Project Insight permissions for the token's
lifetime (90 days by default; see the top of this page).
For Claude and ChatGPT it stays in the vendor's connector service, out of the model's reach. For
Claude Code and Codex it is in your OS keychain (or a credentials file on Linux and Windows), which
an agent with shell access could read, the same as the local tool's stored credentials.

## Dates

Dates such as "today" in time-change drafts, and the warnings about dates far in the past or future,
use the server's configured time zone, not your device's.

## Limits

The server rate-limits each client IP address:

| Endpoint | Limit |
|----------|-------|
| `/register` | 10 per hour |
| `/authorize` and `/authorize/start`, together | 30 per 10 minutes |
| `/authorize/complete` | 20 per 10 minutes |
| `/token` | 30 per 10 minutes |
| `/mcp` | 300 per minute |

Going over a limit returns HTTP 429 with a `Retry-After` header. Separately, `/mcp` accepts at most 30 new
tokens per minute from one address (each costs the server a check with Project Insight); a token the
server checked in the last 10 minutes doesn't count.

At most 3 sign-ins can be in progress from one network (an IPv4 address, or an IPv6 /64) at a time,
and 200 across the server. A sign-in is in progress from **Sign in to Project Insight** until you
finish it, it ends after too many wrong codes, or 15 minutes pass. Clicking **Sign in to Project
Insight** again in the same browser replaces that browser's earlier sign-in, so retrying never uses
up the limit; only sign-ins started in other browsers or apps on your network count against it. Past
the limit, a start shows "Too many sign-ins are in progress from your network" (HTTP 429, with a
`Retry-After` of 15 minutes, by which time the others have ended): finish one of
those sign-ins in the browser that started it, or wait and start again. Past the server-wide limit,
a start shows "Too many sign-ins in progress" (HTTP 503, with `Retry-After` of 5 minutes).

To count sign-ins per network, the server stores a keyed hash of the network address with each
sign-in. The key is random, made when the server starts and never stored, so the stored value can't
be turned back into an address.

Hosted connectors (Claude, ChatGPT) call from their vendors' shared backend addresses, so many users
of the same vendor share one limit for `/mcp`, `/token` and `/register`. If users see 429 errors,
tell the server's owner. The owner watches telemetry and raises the limits.

## Revoking a token

After you revoke a token in Project Insight, tool calls fail at Project Insight at once. The server
may keep accepting the token at `/mcp` for up to 10 minutes, because it caches a successful identity
check. After that the client is asked to sign in again.

## If a token leaks

Not yet verified. The server must not be used until the owner has filled in this section with the
Project Insight screen where a user, and an admin, revoke a device-activation token (each one is
labelled "MCP connector (<client host>)", e.g. "MCP connector (claude.ai)", or 127.0.0.1/localhost
for Claude Code and Codex), and confirmed that a revoked token stops working.
