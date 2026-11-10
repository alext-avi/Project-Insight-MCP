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
| `Auth__AllowedPiUserIds` | Comma-separated Project Insight user ids allowed to connect. At least one. Matching ignores case. |
| `Storage__Mode` | `Azure` in production |
| `Storage__TableEndpoint`, `Storage__BlobEndpoint` | Storage account endpoints (https). The app's managed identity reads and writes them. `Storage__ConnectionString` can replace both. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Application Insights connection string |

The app refuses to start when a required setting is missing or unsafe.

## Optional settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Auth__PiTokenLifetimeDays` | `90` | Lifetime of the Project Insight token, 1 to 90 days |
| `Auth__AllowedRedirectUris` | Claude and ChatGPT callbacks | Replaces the default OAuth redirect list |
| `Auth__AllowedCimdHosts` | `claude.ai`, `chatgpt.com` | Replaces the default list of hosts that may publish client metadata |
| `Auth__AllowedOrigins` | none | Browser origins allowed to call the server |

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

- The server stores no Project Insight API token. Each user signs in, and the token Project Insight
  issues to them is the one used for their requests.
- The web app's system-assigned managed identity accesses storage. No storage keys are used.
- Bearer tokens, authorization codes and activation codes never appear in logs or telemetry.
