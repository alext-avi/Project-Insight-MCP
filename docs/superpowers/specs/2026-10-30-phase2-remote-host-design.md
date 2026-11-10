# Phase 2: remote host on .NET 10, connector sign-in, and deploy from CI

**Status:** design, revised after security review (2026-10-30) · **Issue:** #4 · **Branch:** `phase2-remote-host`

## Goal

Replace the in-process Azure Functions host (end of support 2026-11-10) with an ASP.NET Core app on .NET 10 that serves the same tools and card as the local stdio server. Make it work as a remote connector in claude.ai, Cowork, Claude Code, ChatGPT and Codex. Deploy it automatically from CI on every green push to `main`, as `publish-nuget.yml` does for the NuGet tool. Keep every secret and identifying value (tenant, subscription, client IDs, app names, the PI tenant URL) out of this public repo and out of public Actions logs.

## Decisions

| Topic | Decision |
| - | - |
| Clients | claude.ai, Cowork, Claude Desktop/mobile, Claude Code, ChatGPT, Codex. Copilot Studio / Teams is a stretch goal and is not built here. |
| Hosting | Azure App Service, Linux, B1, Always On, one instance. One environment, in the existing shared resource group (see Infrastructure for who has access and why that's accepted). |
| Shared wiring | New project `ProjectInsightMCP.Server` holds the SDK registration (tools, card, adapters) used by both hosts. Core stays SDK-free. |
| Identity | PI is the only identity provider. PI has no OAuth, so the server runs a thin OAuth front for PI device activation, as the Functions bridge did. No Entra sign-in. |
| Who may use it | Only PI users listed in `Auth:AllowedPiUserIds`. To start with, that's the owner alone. |
| Client registration | Client ID Metadata Documents (CIMD) from allowlisted hosts, and Dynamic Client Registration (DCR) for older clients (deprecated in MCP 2026-07-28, still supported). Redirect URIs must match exactly. |
| Tokens | Pass-through: the access token the client receives **is** a PI token. Our server requests it from PI with a **90-day** lifetime (`Auth:PiTokenLifetimeDays`, at most 90). No refresh tokens. The server stores no long-lived credential. |
| Per-user state | Sign-in sessions, one-time codes and DCR registrations in Table storage; drafts in Blob storage. All reached with the app's managed identity, no keys. No Key Vault. |
| Deploy | `deploy.yml` on pushes to `main`. An unprivileged job tests and builds; a separate job, with no build step, deploys with GitHub OIDC (no stored Azure credential) and smoke-tests. |
| Infrastructure | Bicep in the repo with no identifying values. The owner applies it by hand, rarely, from a gitignored parameters file. CI's Azure identity can only publish code to the one web app. |

### Pass-through, stated plainly

The MCP authorization spec says a server **MUST NOT** accept or pass through tokens not issued for it. This design knowingly does both. That rule exists to stop confused deputies across services. Here PI is the only downstream resource, and the token gives its holder nothing beyond what they already have in PI, so that harm doesn't arise.

What pass-through costs:

- **No kill switch on our side:** a leaked token works directly against PI. Our server can't see, block or revoke it. The 90-day lifetime bounds that, and PI's own revocation (below) is the kill switch.
- **The token has the user's full PI permissions.** PI tokens can't be scoped down.
- **It sits with third parties.** Anthropic's or OpenAI's connector backend holds it for hosted clients, out of the model's reach. For **Claude Code and Codex** it's in the OS keychain, or a credentials file on Linux and Windows, which an agent with shell access *can* read. That's the same exposure as the stdio tool's `~/.projectinsight/credentials.json` today. The connect-a-client doc recommends `mcp_oauth_credentials_store = "keyring"` for Codex.
- **Each sign-in mints a new PI token.** The old one stays valid until it expires or is revoked in PI.
- **Any PI token from the tenant is accepted,** not just ones issued through our sign-in. `Auth:AllowedPiUserIds` limits whose.

**Go-live gate:** before the first connector is pointed at production, the owner verifies and writes down, in `docs/REMOTE.md` under "If a token leaks", how a user and a PI admin list and revoke device-activation tokens in PI. If PI turns out to have no revocation for them, pass-through doesn't go live: the server-issued-token design in Out of scope moves into scope.

**Triggers that end pass-through:** any of these moves the server to issuing its own tokens:

- a second PI tenant;
- a public directory listing;
- more than a small internal group of users;
- a need to revoke centrally.

## Layout

```
servers/project-insight/
  core/       ProjectInsightMCP.Core     net10.0   PI client, tools, time changes (net8.0 target dropped)
  server/     ProjectInsightMCP.Server   net10.0   NEW: AddProjectInsightMcp, tool/card adapters
  src-stdio/  projectinsight-mcp         net10.0   stdio transport, singletons, file drafts, device-activation tools
  src-web/    ProjectInsightMCP.Web      net10.0   NEW: HTTP transport, OAuth front, per-user stores
  tests/                                 net10.0   existing tests + web tests
  src/                                             DELETED (Functions host, deployment.zip, .sln.backup2)
  deploy/                                          DELETED (deploy.sh, untracked params)
bicep/                                             rewritten for App Service
```

### `ProjectInsightMCP.Server`

The following move here from `src-stdio` unchanged, apart from namespaces:

- `ProjectInsightServer.AddProjectInsightMcp`
- `ToSdkTool`, `ToSdkResult`, `ToLegacyArguments`
- `ToolCallGate`
- `TimeChangeTools` and the card resource

No tool handler changes. `src-stdio` keeps:

- `Program` and `DrainingStdio`
- `WithDrainingStreamServerTransport`
- `AddProjectInsightServices` (its singleton registrations)
- `PruneDraftsAtStartupAsync`

`AddProjectInsightMcp` gains one option: a predicate for which tools a host exposes. The web host uses it to hide the three device-activation sign-in tools (`complete_authentication`, `link_pi_account`, `complete_pi_link`). The predicate filters `tools/list` and is enforced again in the call-tool filter, so calling a hidden tool returns "unknown tool". The stdio host exposes all tools, as today.

`ToolCallGate` becomes keyed: stdio uses a single key; the web host keys it by PI user id, so one user's calls run one at a time while different users run in parallel. This keeps the handlers' one-call-at-a-time assumption per user.

The move ships no behaviour change for the local tool. The existing 237 tests, including the tool-list snapshot, must pass unchanged after it, before any web work lands.

### `ProjectInsightMCP.Web`

ASP.NET Core minimal app:

- `MapMcp("/mcp")` with the SDK's HTTP transport, behind `RequireAuthorization()`. Startup fails unless `Stateless == true`, because per-request scoping of the PI services depends on it.
- **Per-request (scoped) services:** `ApiTokenAccessor`, `ProjectInsightApiService`, `ProjectInsightTimeEntryGateway`, the time-change services, and `McpServerService`.
  - `McpServerService` comes from an explicit factory that passes `deviceActivation: null` and `keyVaultTokenService: null`, so no tool path can start a device activation, use shared pending sign-in state, or write a credential to disk.
  - `CredentialStorageService` and `DeviceActivationService` are not registered. The OAuth endpoints use a separate `IPiActivationClient` with only `CreateActivationRequestAsync` and `CompleteActivationByIdAsync`.
- **Configuration checks:** startup fails if `ProjectInsight:ApiToken` is set. A static token would override every user's.
- **Bearer only:** the bearer token is read only from `Authorization: Bearer`. The old `X-PI-Token` header and any `access_token` query parameter are refused.
- **`Origin` header:** a request to `/mcp` that carries one not listed in `Auth:AllowedOrigins` (empty by default; connector backends send none) gets `403`, as the MCP transport spec requires.
- **Hosts and headers:**
  - `AllowedHosts` is the app's hostname.
  - Forwarded headers trust App Service's single proxy hop (`ForwardLimit = 1`).
  - HSTS is on (max-age one year).
- **Background job:** every 5 minutes it prunes expired sessions and codes; hourly it prunes drafts and stale DCR registrations.
- **Shutdown:** `HostOptions.ShutdownTimeout` is 60 seconds, so a deploy's restart lets an in-flight `commit_time_changes` finish, as the handlers are written to.
- **No CORS:** MCP clients call from their own servers, and the card talks to the server through its host, not directly.

## Sign-in

### Discovery

The public base URL comes from configuration (`Web:PublicBaseUrl`, e.g. `https://<app>.azurewebsites.net`), never from the request's `Host` header.

- **Unauthenticated `POST /mcp`** → `401` with
  `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource/mcp", scope="pi"`.
- **`GET /.well-known/oauth-protected-resource/mcp`** and **`/.well-known/oauth-protected-resource`**:
  - `resource`: `<base>/mcp` (exactly the URL users enter)
  - `authorization_servers`: [`<base>`]
  - `scopes_supported`: [`pi`]
  - `bearer_methods_supported`: [`header`]
- **`GET /.well-known/oauth-authorization-server`:**
  - `issuer`: `<base>`. No trailing slash, and byte-identical everywhere it appears.
  - `authorization_endpoint`: `<base>/authorize`
  - `token_endpoint`: `<base>/token`
  - `registration_endpoint`: `<base>/register`
  - `response_types_supported`: [`code`]
  - `grant_types_supported`: [`authorization_code`]
  - `code_challenge_methods_supported`: [`S256`]
  - `token_endpoint_auth_methods_supported`: [`none`]
  - `client_id_metadata_document_supported`: `true`
  - `authorization_response_iss_parameter_supported`: `true`
  - `scopes_supported`: [`pi`]. `offline_access` is not advertised, so clients don't expect a refresh token.

Without an `/api` prefix, standard path insertion and the root paths both resolve, which fixes both URL mismatches found on 2026-10-07. A `resource` parameter is compared case-insensitively on scheme and host, ignoring a trailing slash. `iss` is always emitted byte-exact.

### Clients and redirect URIs

**Redirect URIs must match exactly.** `Auth:AllowedRedirectUris` is a list of exact URIs. The default:

- `https://claude.ai/api/mcp/auth_callback`
- `https://chatgpt.com/connector_platform_oauth_redirect`. ChatGPT's stable callback, used because we emit `iss`. The per-connector `https://chatgpt.com/connector/oauth/{id}` form is not accepted unless live testing shows ChatGPT needs it. It would then be added as one exact entry from the connector's settings page.

**Loopback** (Claude Code, Codex):

- `http` only, with host exactly `127.0.0.1`, `localhost` or `[::1]`;
- any port, path exactly `/callback`;
- no userinfo, query or fragment.

Codex's callback path is checked during the plan, and added as a second exact path if it differs.

**Parsing:** URIs are parsed with `System.Uri`. Userinfo (`https://claude.ai@evil/…`), fragments, non-default https ports, encoded hosts and dot-segments are refused. Hosts compare case-insensitively; paths exactly.

A rejected redirect URI gets an error page on our domain, never a redirect.

**CIMD:** a `client_id` that is an `https` URL is a CIMD client, and is accepted only if its host is on `Auth:AllowedCimdHosts` (default `claude.ai`, `chatgpt.com`). Any other host is refused **before** anything is fetched. The fetch:

- is `https` on port 443, with no redirects and automatic decompression off;
- has a 5-second timeout;
- reads at most 5 KB from the stream, whatever `Content-Length` says;
- requires `Content-Type: application/json`.

As a second layer, a `ConnectCallback` refuses non-public addresses, normalising IPv4-mapped and embedded IPv6 and refusing `168.63.129.16`. The document's `client_id` must equal the URL, and its `redirect_uris` are filtered through the allowlist. The `redirect_uri` must exactly match one that survives.

Caching: valid documents for 24 hours, or less if `Cache-Control` says so; failures for 5 minutes.

**DCR:** `POST /register` (JSON) accepts `client_name`, `redirect_uris` and `token_endpoint_auth_method` = `none` (public clients only).

- **Validation:** every redirect URI must be on the allowlist, or the registration is refused (`invalid_redirect_uri`).
- **Storage:** the registration is stored in Table `OAuthClients`, keyed by a random `client_id`, and pruned once unused for the token lifetime plus 30 days, or after 24 hours if no sign-in ever finished with it. At most 500 registrations are kept; when full, the oldest never-used one makes room, so unused registrations can't lock out new clients.
- **At `/authorize`:** a DCR client must exist, and the `redirect_uri` must exactly match one it registered.

### Authorize

**`GET /authorize` has no side effects.** It validates:

- `response_type=code`;
- the client and the redirect URI;
- `code_challenge`, with `code_challenge_method=S256` (plain is refused);
- `scope`: if present, must be `pi`; others such as `offline_access` are ignored, not granted;
- `resource`: if present, must be `<base>/mcp`.

It then renders the consent page. Link unfurlers, Safe Links and crawlers that fetch the URL start nothing at PI.

**The consent page shows:**

- **The identity of the asking app, in bold:** the redirect URI's host (for CIMD clients, the CIMD host too). A `client_name` appears only as "calls itself '…' (unverified)".
- **A warning when the only redirect is loopback,** as the MCP spec asks.
- **The line:** "Only continue if you just clicked Connect in Claude or ChatGPT yourself. If someone sent you this link, close this page."
- **A "Sign in to Project Insight" button** that POSTs the validated request to `/authorize/start`.

**`POST /authorize/start`:**

1. Re-validates the request.
2. Creates the sign-in session (15 minutes) and binds it to the browser with a `__Host-pi_auth` cookie: Secure, HttpOnly, SameSite=Lax, a random value whose hash is stored in the session.
3. Starts a PI device activation.
   - It uses `clientDevice` = "MCP connector (<redirect host>)", so the user can recognise the token in PI later.
   - It uses `timeoutMinutes` from `Auth:PiTokenLifetimeDays`.
4. Shows the activation page: PI's activation link (checked to be `https` on the configured PI host before it's rendered), and a field for the activation code.

**`POST /authorize/complete`:**

1. Requires the session's cookie. A cross-site POST doesn't carry a `SameSite=Lax` cookie, so it fails.
2. Moves the session from Pending to Completing with an ETag-conditional update, so a session completes exactly once.
3. Completes the activation with PI. The activation code goes in the POST body if PI accepts that; otherwise the query is stripped from telemetry (see No echo).
4. Reads the PI user and refuses anyone not in `Auth:AllowedPiUserIds`, with a plain "this server isn't open to your account".
5. Stores the one-time code, and redirects to `redirect_uri` with `code`, `state` and `iss`.

**Retries and errors:**

- A wrong activation code re-renders the page, up to 5 attempts per session. After that the session is deleted ("start again"), and each failure is logged at Warning with the session id's hash, never the code.
- Once the redirect URI is validated, errors go back to the client with `error`, `state` and `iss`.

**Response headers** on `/authorize`, `/authorize/*`, `/token` and `/register`:

- `Cache-Control: no-store` and `Pragma: no-cache`;
- `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'` (no `form-action`, which would block the redirect after the POST);
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: same-origin`. Not `no-referrer`: under it, browsers send `Origin: null` on the consent page's own form posts, and the cross-site check refuses them (found in the first live sign-in).

No inline script.

### Tokens

The access token is a PI token (see "Pass-through, stated plainly"). PI has no OAuth, so the server translates PI device activation into the authorization-code flow clients expect, and then steps aside.

**Authorization code:**

- random 256 bits, single use, 5 minutes;
- the row key is `SHA-256(code)`;
- the payload holds the PI token and its expiry, plus the client, redirect URI, code challenge and resource it was issued for;
- the payload is AES-GCM-encrypted with a key derived from the code (HKDF-SHA256 with a fixed label), so storage alone never holds a usable token;
- deleted on redemption with an ETag-conditional delete, so two redemptions can't both succeed;
- expired rows are deleted when read and by the 5-minute prune.

**`/token`:**

- **Request:** `application/x-www-form-urlencoded`, with `grant_type=authorization_code`, `code`, `code_verifier`, `client_id`, `redirect_uri` and an optional `resource`. All must match the code. The code is consumed before PKCE is checked, so a verifier can't be guessed.
- **Response:** `{ "access_token": <PI token>, "token_type": "Bearer", "expires_in": <seconds to PI's expiry> }`, with no refresh token.
  - PI's expiry is read as UTC.
  - If PI returns no expiry, `expires_in` is the configured lifetime.
- **Errors** follow RFC 6749:
  - `invalid_grant` for a bad, expired or reused code, or a PKCE mismatch;
  - `unsupported_grant_type` for anything else.

**Per request:** an ASP.NET Core authentication handler checks the bearer token before anything else.

1. It rejects values that aren't token68 (`^[A-Za-z0-9\-._~+/]+=*$`), so a malformed value can't reach a header parser that would put it in an exception.
2. It sets `ApiTokenAccessor.ApiToken`.
3. It resolves the PI user by calling PI's current-user endpoint, cached for 10 minutes under the token's SHA-256 hash (never the token itself). Outcomes:
   - user in `Auth:AllowedPiUserIds` → the request proceeds;
   - PI answers **401 or 403** → our `401` with the `WWW-Authenticate` above, so the client signs in again. The rejection is cached for 60 seconds under the hash.
   - user not in `Auth:AllowedPiUserIds` → `403`.
   - PI answers **5xx, times out or is unreachable** → `503` with `Retry-After` and no `WWW-Authenticate`, so an outage doesn't push users into signing in again and minting another token.
4. Outbound identity calls are capped at 8 concurrent. `GetCurrentUserAsync` gets a typed result carrying PI's status code, so the handler can tell these cases apart.

**Expiry:** after 90 days PI rejects the token, the server answers 401, and the client signs in again.

### No echo

No tool result (text or `structuredContent`), error message, health output, log line or telemetry item may contain a bearer token, authorization code or activation code. Tool results reach the model, so for hosted clients this is what keeps the token away from the agent.

- **One central scrubber** replaces the current request's token with `[redacted]`. It runs as an MCP call-tool filter on every result and as a logging and telemetry processor.
- **PI error bodies** are shortened to 500 characters and scrubbed before they reach a tool result or a log.
- **Telemetry:**
  - App Insights uses the Azure Monitor OpenTelemetry distro, with a processor that strips the query string from PI dependency URLs.
  - `ModelContextProtocol` and `System.Net.Http.HttpClient` log at Warning in production.
  - Request-body logging is never enabled on the OAuth endpoints.
- **Tests assert all of this** (see Testing). Nothing is assumed.

## Per-user state

| Data | Where | Key | Notes |
| - | - | - | - |
| CIMD documents | memory | client id URL | 24 hours or `Cache-Control`; failures for 5 minutes |
| Token → PI user id | memory | SHA-256 of the token | 10 minutes; rejections for 60 seconds |
| DCR registrations | Table `OAuthClients` | client id | redirect URIs, client name; pruned after token lifetime + 30 days unused (24 hours if never used); at most 500, oldest never-used evicted when full |
| Sign-in sessions | Table `OAuthSessions` | session id | 15 minutes: client, redirect URI, state, code challenge, resource, PI activation id, cookie hash, attempts, status; at most 200 outstanding |
| Authorization codes | Table `OAuthCodes` | SHA-256 of the code | 5 minutes, single use, payload encrypted with a key derived from the code |
| Time-change drafts | Blob container `drafts`, `<piUserId>/<draftId>.json` | PI user id + draft id | `BlobDraftStore : IDraftStore`. Owner and id are checked with the same patterns as `FileDraftStore`. `TryTransitionAsync` uses ETag `If-Match`, the same compare-and-set the file store gets from its lock. Blob, not Table, because a draft with avatars can exceed Table's 64 KB property limit. |

`ownerId` for drafts is always the PI user id resolved from the token, never a tool argument, so one user can't read or commit another's draft. No row or blob holds a usable PI token.

The in-memory caches and the per-user `ToolCallGate` assume one instance. Bicep pins the plan to one instance.

## Rate limits

Partitioned per client IP, with IPv6 by /64, taken from the right-most forwarded address:

| Endpoint | Limit |
| - | - |
| `/register` | 10 per hour |
| `/authorize`, `/authorize/start` | 30 per 10 minutes |
| `/authorize/complete` | 20 per 10 minutes, plus the 5-per-session cap |
| `/token` | 30 per 10 minutes |
| `/mcp` | 300 per minute; requests without a valid token limited separately, 30 per minute |

## Configuration and secrets

App settings (set by Bicep, none in the repo):

| Setting | Value |
| - | - |
| `ProjectInsight__BaseUrl` | PI tenant API URL |
| `Web__PublicBaseUrl` | the app's `https://` base URL |
| `Storage__TableEndpoint`, `Storage__BlobEndpoint` | storage account endpoints (managed identity, no keys) |
| `Auth__AllowedPiUserIds` | comma-separated PI user ids allowed to use the server |
| `Auth__PiTokenLifetimeDays` | `90` (at most 90) |
| `Auth__AllowedRedirectUris`, `Auth__AllowedCimdHosts`, `Auth__AllowedOrigins` | optional overrides of the defaults |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights |

The GitHub environment `production` (deployment branches: `main` only) holds these as **secrets**, not variables, because this repo's Actions logs are public and only secrets are masked there:

- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
- `AZURE_WEBAPP_NAME`
- `APP_BASE_URL`, stored whole rather than derived: App Service can give new apps a unique default hostname that isn't just the app name.

Masking covers only literal values, so workflow steps:

- print no URLs;
- report only status codes;
- `::add-mask::` any derived value before using it.

The connector URL, and with it the app name, is semi-public anyway, and anyone who opens `/authorize` learns the PI tenant hostname from the activation link. Keeping both out of the repo stops casual discovery, not a determined person.

gitleaks gets two custom rules: `[a-z0-9-]+\.projectinsight\.net` (allowlisting the `yourdomain` and `your-tenant` placeholders) and `[a-z0-9-]+\.azurewebsites\.net` (allowlisting `<app>` and `your-function-app`).

It also flags the Azure identifiers the history rewrite removed: `/subscriptions/<guid>` resource ids; a GUID given as a tenant, subscription, client, app, object or principal id (`tenantId`, `client_id`, `AZURE_*_ID`, `--subscription` and similar); an App Insights `InstrumentationKey` or `ApplicationId`; and storage account hostnames (`<account>.blob|table|queue|file|dfs.core.windows.net`). The all-zero GUID, the built-in role ids in `bicep/`, the fake test ids, Azurite's `devstoreaccount1` and the `acct` placeholder are allowlisted, each anchored to the whole value. On pull requests the scan uses `.gitleaks.toml` and `.gitleaksignore` from the base branch, so a PR can't weaken the rules in the same change that would trip them.

## Infrastructure (`bicep/`)

**The resource group:** the app goes in the existing shared group. Its Contributors, plus the Owners, Contributors and access administrators inherited from the subscription and the tenant root, could change the app or deploy code that reads tokens in flight. Any design would allow that, a proxy included. The owner reviewed the list on 2026-10-30 and accepted it: they're trusted colleagues and admins.

So the infrastructure adds visibility rather than a lockout:

- a `CanNotDelete` lock on this app's resources: the web app, storage account, deployer identity, Log Analytics workspace, App Insights, action group and both alert rules;
- a diagnostic setting sending the web app's `AppServiceAuditLogs` and `AppServicePlatformLogs` to the workspace, since a Kudu publish with an Entra token may leave no Activity Log event;
- **Activity Log alerts** to the owner, any status (denied and failed attempts email too, and so does the owner's own Bicep redeploy):
  - on the web app, storage account, deployer identity and the group-wide alert:
    - `Microsoft.Storage/storageAccounts/write`
    - `Microsoft.Web/sites/write`
    - `Microsoft.Web/sites/config/write`
    - `Microsoft.Web/sites/basicPublishingCredentialsPolicies/write`
    - `Microsoft.Web/sites/sitecontainers/write`
    - `Microsoft.Web/sites/extensions/write`
    - `Microsoft.Insights/diagnosticSettings/write` and `/delete`
    - `Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials/write`
    - `Microsoft.Insights/activityLogAlerts/write` and `/delete`
  - on the whole resource group:
    - `Microsoft.Authorization/roleAssignments/write` and `/delete`
    - `Microsoft.Authorization/roleDefinitions/write` (a subscription-level resource: an edit made at subscription scope may not be seen)
    - `Microsoft.Authorization/locks/delete`
    - `Microsoft.Insights/activityLogAlerts/write` and `/delete`
    - `Microsoft.Insights/actionGroups/write` and `/delete`

If the server ever holds tokens for more than the owner and a few colleagues, the next step is a dedicated resource group or subscription where only the owner holds roles.

`main.bicep` declares:

- **App Service plan:** Linux, B1, one instance.
- **Web app:**
  - runtime `DOTNETCORE|10.0`, Always On, HTTPS only, TLS 1.2 minimum;
  - FTP and basic publishing credentials disabled;
  - health check `/healthz`, system-assigned identity, the app settings above.
- **Storage account:** `allowSharedKeyAccess: false`, TLS 1.2, the tables `OAuthClients`, `OAuthSessions` and `OAuthCodes`, and the `drafts` container. No group-wide policy: the group is shared, and a policy there would affect other people's storage accounts.
- **Log Analytics and App Insights.**
- **App identity roles:** Storage Table Data Contributor and Storage Blob Data Contributor on the storage account.
- **Deployer identity:** a user-assigned managed identity with a federated credential.
  - **Subject:** `repo:alext-avi/Project-Insight-MCP:environment:production:job_workflow_ref:alext-avi/Project-Insight-MCP/.github/workflows/deploy.yml@refs/heads/main`. The repository's OIDC subject template is customised to include `job_workflow_ref`, so only `deploy.yml` on `main` can sign in, not any workflow that uses the environment.
  - **Role:** a custom role on the web app only: `Microsoft.Web/sites/read`, `Microsoft.Web/sites/publish/Action`, `Microsoft.Web/sites/restart/Action`, `Microsoft.Web/sites/config/list/Action`, and `Microsoft.Web/sites/publishxml/Action` (`webapps-deploy` fetches the publishing profile before deploying; its basic-auth credentials are unusable while basic publishing is off). It falls back to Website Contributor, scoped to the app, only if the custom role can't deploy. The plan records which was needed.

Parameters: `namePrefix`, `location`, `piBaseUrl`, `allowedPiUserIds`, `ownerObjectId`, `alertEmail`. `bicep/main.bicepparam.example` shows them with placeholders. The real file (`*.bicepparam`, not `.example`) is gitignored. `main.json` (stale compiled ARM) and `parameters/*.json` are deleted.

**One-time setup, run by the owner** (documented in `bicep/README.md`):

1. `az deployment group create` with the local parameters file. The owner, as Owner of the group, can create the role assignments.
2. Set the repository's OIDC subject template to include `job_workflow_ref` (one `gh api` call, shown in the README).
3. Copy the outputs into the `production` environment's secrets: deployer client id, web app name, base URL, and the tenant and subscription ids.
4. Turn on branch protection for `main`: no force-pushes, pull requests required.

## Deploy (`.github/workflows/deploy.yml`)

```yaml
on:
  push:
    branches: [main]
    paths: [servers/project-insight/core/**, servers/project-insight/server/**,
            servers/project-insight/src-web/**, .github/workflows/deploy.yml]
  workflow_dispatch:
permissions: { contents: read }
concurrency: { group: deploy-production, cancel-in-progress: false }
```

1. **build** (no `id-token`, no environment):
   - restore with `--locked-mode` through the tests project, so every project (core, server, both hosts, tests) is checked against its NuGet lock file and nothing later restores unlocked;
   - build, with warnings as errors as CI does;
   - run all tests;
   - publish `src-web` in Release, with the commit SHA as the informational version;
   - upload the output as an artifact, kept 1 day.
2. **deploy** (`needs: build`, `environment: production`, `permissions: { id-token: write, contents: read }`). There is **no checkout, restore or build** here, so no third-party build code runs while the Azure token exists:
   1. Download the artifact.
   2. `azure/login` with OIDC.
   3. `azure/webapps-deploy`.
   4. Smoke test, printing only status codes, failing the run on any miss:
      - `/healthz` reports the deployed SHA within 3 minutes;
      - an unauthenticated `POST /mcp` gets a 401 whose `WWW-Authenticate` has `resource_metadata`;
      - the protected-resource metadata's `resource` equals `<base>/mcp`.

Every action in `deploy.yml` is pinned to a full commit SHA, with the tag as a comment. Dependabot's actions group keeps the pins current. The gitleaks image in `secret-scan.yml` is pinned by digest. `dependabot.yml` gains `server` and `src-web`.

**Rollback:** `git revert` on `main`, which deploys automatically. `workflow_dispatch` takes no ref or SHA input. No workflow in the repo uses `pull_request_target`, and pull requests, including forks, never reach the `production` environment or its secrets. `ci.yml` also builds `server` and `src-web` and runs their tests.

`/healthz` returns `{ "status": "ok", "version": "<sha>" }` and nothing else. Showing the SHA of a public repo is accepted.

## Accepted risks

- **The pass-through costs** listed under Decisions.
- **PI device-activation phishing:** PI's create-request needs no authentication, so an attacker who knows the tenant can phish a user into approving an activation directly at PI, without our server. Our server adds nothing to that beyond what the consent page, the attempt cap and `Auth:AllowedPiUserIds` already limit. The PI admin should restrict or monitor device activations if PI allows it.
- **Model-chosen ids in PI paths:** some PI path segments built from model-supplied ids (e.g. `projectId`) aren't escaped, so a crafted id reaches other PI paths with the same user's token. It gains no privilege, and every URL stays relative to the configured PI host. Fixed opportunistically with `Uri.EscapeDataString`.
- **Restarts on deploy:** every push to `main` restarts the app; the shutdown timeout covers in-flight commits.
- **Activation by users who aren't allowed:** a user not on `Auth:AllowedPiUserIds` who completes activation leaves a 90-day PI token that PI minted and the server discarded (PI returns the user id only together with the token). They can revoke it in PI. Best-effort server-side revocation is revisited after the go-live gate shows PI's revocation API.

## Testing

- **Refactor gate:** the existing suite and tool snapshot pass unchanged after the move to `server/`.
- **Tool parity:** the web host's `tools/list` equals stdio's minus the three sign-in tools; calling a hidden tool returns "unknown tool"; both expose the card resource.
- **OAuth unit tests** (fakes and `TimeProvider`):
  - PKCE.
  - **Redirect matching:**
    - exact allowed;
    - loopback on any port with `/callback`;
    - refused, without a redirect: `https://claude.ai/other`, `https://claude.ai.evil.com/…`, `https://claude.ai@evil.com/…`, `https://chatgpt.com/connector_platform_oauth_redirect/../x`, a fragment, a non-default https port;
    - accepted: `https://CLAUDE.AI/api/mcp/auth_callback`.
  - **DCR:** an unknown `client_id` is refused; a redirect the client didn't register is refused; registration caps.
  - **CIMD:**
    - a host not on the allowlist is refused before any fetch;
    - non-https, redirect, oversize stream, gzip bomb, wrong content type, port 8443;
    - `::ffff:127.0.0.1`, `168.63.129.16`;
    - a mismatched `client_id`.
  - **Codes:** reuse; concurrent redemption (exactly one wins); the payload can't be decrypted without the code; session and code expiry.
  - **Activation attempts:** the 5-attempt cap; double completion of one session (exactly one wins); completion without the session cookie refused.
  - **Requests:** resource mismatch; `expires_in` from PI's expiry and from the fallback.
- **Authentication handler:**
  - PI 401 → our 401 with `resource_metadata`;
  - PI 500 and a PI timeout → 503 with `Retry-After`;
  - a non-token68 bearer is refused;
  - a user not on the allowlist → 403;
  - a rejected token is cached, and a second request makes no PI call;
  - an `Origin` that isn't allowed → 403.
- **Startup guards:** startup fails with `ProjectInsight:ApiToken` set, and fails with `Stateless` off.
- **End to end** (in-memory test server, in-memory stores, stubbed PI):
  1. DCR client → `GET /authorize` (no PI call) → `/authorize/start` → complete with the activation code → `/token` returns the PI token;
  2. `tools/list` and `get_current_user` over `/mcp` with it as the bearer token;
  3. the same flow for a CIMD client;
  4. the 401 and both metadata documents match the JSON above exactly.
- **Isolation:** two concurrent `/mcp` calls with tokens A and B through a slow PI stub. Each outbound PI request carries its own caller's token; each draft lands under its own owner; and no token-less tool path is reachable (`SignInIfNeededAsync` is never entered).
- **No echo:** with a recognisable fake token, call every tool against PI stubs that:
  - echo the token in their error body;
  - return 500;
  - time out.

  Also hit the health endpoint, a failing `/token`, and `/authorize/complete` with a wrong code. Assert that the token and the activation code appear in no response body, no captured `ILogger` line, and no captured telemetry item (requests, dependencies, exceptions).
- **Storage:** one shared contract suite runs against the in-memory, file and blob draft stores, and against the Table client, session and code stores. The Azure-backed stores run against Azurite in CI.
- **After deploy:** the smoke test above.
- **Live, by the owner:**
  - claude.ai and Cowork, ChatGPT developer mode, Claude Code and Codex: add the connector, sign in, list tools, run a read, plan a time change and see the card;
  - commit a time change only in the owner's own test;
  - confirm the token appears in PI under its "MCP connector (…)" label, and that revoking it there makes the next call get 401 (the go-live gate).

## Delivery

Three pull requests, each reviewed by CodeRabbit and gitleaks:

1. **Shared wiring:** add `server/`, move the code, drop Core's net8.0 target. No behaviour change, no release.
2. **Web host:**
   - add `src-web` with sign-in, the stores and the tests;
   - delete `src/` and `deploy/`;
   - extend `ci.yml` and `dependabot.yml`;
   - add NuGet lock files.
3. **Infra and deploy:** Bicep, `deploy.yml` (SHA-pinned), the gitleaks rules and digest pin, and docs:
   - rewrite `docs/AGENTS.md`, which still describes the Functions host;
   - add `docs/REMOTE.md`: connecting each client, the Codex keyring setting, and "If a token leaks".

   Then the owner runs the one-time setup, starts the first deploy by hand, works through the go-live gate, and tests live.

**Cutover, by the owner, after live testing:**

- point the existing connectors at the new `/mcp` URL;
- delete the old Function app, its storage, its Entra app registration and the old Key Vault;
- revoke any PI tokens the old bridge issued.

## Out of scope

- Copilot Studio / Teams (stretch goal; the Entra path and `KeyVaultTokenService` stay in Core for it).
- A custom domain, a staging environment, more than one instance.
- Enterprise managed auth and a public directory listing.
- Guaranteeing the card renders in every client: it renders where the host supports MCP Apps, and the tools work as text everywhere.
- **Server-issued tokens.** This moves into scope if PI can't revoke device-activation tokens, or when a trigger listed under Decisions is reached. The design:
  - our own short-lived access tokens and rotating refresh tokens;
  - the PI token kept server-side, encrypted with ASP.NET Core Data Protection in Table storage, with the key ring wrapped by one Key Vault key.

  Client-facing URLs and metadata stay the same, apart from adding `refresh_token` and `offline_access`.
