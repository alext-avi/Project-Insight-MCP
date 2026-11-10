# Authentication

How the Project Insight MCP server signs in to Project Insight (PI). There are two hosts.

| Host | Where it runs | Sign-in |
|------|---------------|---------|
| Local stdio tool (`src-stdio`, `projectinsight-mcp`) | Your machine | SSO device activation, or a static API token |
| Remote web host (`src-web`) | Azure App Service | OAuth 2.1 in front of PI's device activation |

## Local tool

Configuration:

| Variable | Required | Description |
|----------|----------|-------------|
| `ProjectInsight__BaseUrl` | Yes | PI API URL, such as `https://your-tenant.projectinsight.net/api/` |
| `ProjectInsight__ApiToken` | Only for static-token mode | A PI API token |

### SSO device activation (recommended)

Set only the base URL. On first use, ask the assistant to "link my Project Insight account".

1. The `link_pi_account` tool opens PI's sign-in page in your browser.
2. You sign in through your organization's identity provider.
3. PI shows an activation code. Paste it back into the chat.
4. The assistant calls `complete_pi_link` with the code.
5. The per-user token is saved to `~/.projectinsight/credentials.json` and reused on later runs.

If the token expires, run the link flow again.

### Static API token

Set `ProjectInsight__ApiToken` next to the base URL. Use it for service accounts and CI. To create a
token, sign in to PI and open User Settings, then API Access.

## Remote web host

The host never holds a shared PI token. Each user signs in through the host's OAuth 2.1 flow, which
drives PI's device activation. PI issues a token to that user, and the access token the client holds
is that PI token. Every tool call then runs with the user's own PI permissions.

- Only PI users listed in `Auth:AllowedPiUserIds` can connect.
- The token lasts `Auth:PiTokenLifetimeDays` days (default 90, allowed 1 to 90).
- The host refuses to start if `ProjectInsight:ApiToken` is set, because it would override every
  user's token.
- Bearer tokens, authorization codes and activation codes are never written to logs or telemetry.

User steps, limits and revocation timing are in [docs/REMOTE.md](../../../docs/REMOTE.md). Settings are
in [CONFIGURATION.md](../../../CONFIGURATION.md).

## Token security

- A token carries your full PI permissions. Treat it like a password.
- To revoke a token, use PI. See "If a token leaks" in [docs/REMOTE.md](../../../docs/REMOTE.md).

## Troubleshooting

- **Authentication failed or token expired:** sign in again (local: run the link flow; remote:
  reconnect the connector).
- **Access forbidden:** your PI account lacks permission for that action.
- **"This server isn't open to your Project Insight account":** the owner has not allowed your PI
  user. Ask them to add it.
- **Invalid base URL:** use an absolute https URL ending in `/api/`.
