# Remote Server Configuration

Settings for the remote web host (`servers/project-insight/src-web`) on Azure App Service. The
Bicep template in [`bicep/`](./bicep/README.md) sets the required ones. Connecting a client is
covered in [docs/REMOTE.md](./docs/REMOTE.md).

Settings use the double-underscore form in App Service and environment variables
(`Auth__AllowedPiUserIds`), and the colon form in `appsettings` files and error messages
(`Auth:AllowedPiUserIds`).

## Required settings

| Setting | Description |
|---------|-------------|
| `ProjectInsight__BaseUrl` | Your Project Insight API URL, an absolute https URL such as `https://your-tenant.projectinsight.net/api/` |
| `Web__PublicBaseUrl` | The server's public https origin with no path, such as `https://<app>` |
| `Auth__AllowedPiUserIds` | Comma-separated Project Insight user ids allowed to connect. Matching ignores case. Required unless `Auth__AllowedEmailDomains` is set; with both, a user matching either is allowed. |
| `Storage__TableEndpoint`, `Storage__BlobEndpoint` | Storage account endpoints (https). The app's managed identity reads and writes them. |

The app refuses to start when `ProjectInsight__BaseUrl` or `Web__PublicBaseUrl` is missing or
invalid, when neither `Auth__AllowedPiUserIds` nor `Auth__AllowedEmailDomains` lists anyone, when an
email domain entry isn't a plain domain, when storage has neither both endpoints nor
`Storage__ConnectionString` (outside Development), or when a setting is unsafe (see below). It does
not check that storage is reachable or that its credentials work: those fail on the first request.

## Optional settings

| Setting | Default | Description |
|---------|---------|-------------|
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | none | Application Insights connection string. Recommended: without it the app runs without telemetry, and says nothing about it. The Bicep template sets it. |
| `Storage__Mode` | `Azure` | `Azure` uses the storage settings above. `Local` (in-memory OAuth state, drafts on disk) is refused outside Development. |
| `Storage__ConnectionString` | none | Replaces both storage endpoints, for Azurite or local development only. The Bicep storage account has shared-key access turned off, so an account-key connection string fails in Azure. |
| `Auth__AllowedEmailDomains__0`, `__1`, … | none | Email domains, such as `example.com`, whose Project Insight users may connect: the server checks the email on the user's PI profile at sign-in and on every request. The whole domain must match, ignoring case, so list subdomains separately. Only use this if users can't change the email on their own PI profile. |
| `Auth__PiTokenLifetimeDays` | `90` | Lifetime of the Project Insight token, 1 to 90 days |
| `Auth__AllowedRedirectUris__0`, `__1`, … | Claude and ChatGPT callbacks | Replaces the default OAuth redirect list |
| `Auth__AllowedCimdHosts__0`, `__1`, … | `claude.ai`, `chatgpt.com` | Replaces the default list of hosts that may publish client metadata |
| `Auth__AllowedOrigins__0`, `__1`, … | none | Browser origins allowed to call the server |
| `TZ` | `America/New_York` from the Bicep template; UTC without it | Time zone (an IANA name) for dates such as "today" in time-change drafts and their warnings. Set it to the team's zone. |

The four `Allowed…` settings in this table are lists: give each entry its own numbered setting, such as
`Auth__AllowedRedirectUris__0=https://claude.ai/api/mcp/auth_callback`. The app refuses to start if
one of them is set as a single value (`Auth__AllowedRedirectUris=…`), which would otherwise be
ignored and leave the defaults in place.

## Settings that must not be set

- `ProjectInsight__ApiToken`: the host refuses to start with it. It would override every user's own
  token.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: leave it false or unset. The app handles forwarded headers
  itself.

## Local development

Run the web host with in-memory storage, as shown in
[docs/agents.md](./docs/agents.md#getting-started). `Storage__Mode=Local` works only when
`ASPNETCORE_ENVIRONMENT=Development`.

## Security notes

- The server holds no shared Project Insight API token. Each user signs in, and the token Project Insight
  issues to them is the one used for their requests. During sign-in that per-user token is held in the
  OAuth code store until the client redeems the authorization code, which expires after five minutes. It is
  encrypted under a key derived from the code, which the server doesn't keep. Redeeming the code deletes the
  entry; an expired one is unusable and is deleted by the hourly prune.
- The web app's system-assigned managed identity accesses storage. No storage keys are used.
- Bearer tokens, authorization codes and activation codes never appear in logs or telemetry.
