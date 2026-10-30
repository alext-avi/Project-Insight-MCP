# Phase 2: remote host on .NET 10, connector sign-in, and deploy from CI

**Status:** design · **Issue:** #4 · **Branch:** `phase2-remote-host`

## Goal

Replace the in-process Azure Functions host (end of support 2026-11-10) with an ASP.NET Core app on .NET 10 that serves the same tools and card as the local stdio server. Make it work as a remote connector in claude.ai, Cowork, Claude Code, ChatGPT and Codex. Deploy it automatically from CI on every green push to `main`, as `publish-nuget.yml` does for the NuGet tool. Keep every secret and identifying value (tenant, subscription, client IDs, app names, the PI tenant URL) out of this public repo and out of public Actions logs.

## Decisions

| Topic | Decision |
| - | - |
| Clients | claude.ai, Cowork, Claude Desktop/mobile, Claude Code, ChatGPT, Codex. Copilot Studio / Teams is a stretch goal and is not built here. |
| Hosting | Azure App Service, Linux, B1, Always On. One environment, in the existing Dev/Test subscription and resource group. |
| Shared wiring | New project `ProjectInsightMCP.Server` holds the SDK registration (tools, card, adapters) used by both hosts. Core stays SDK-free. |
| Identity | PI is the only identity provider. The server is its own OAuth 2.1 authorization server and signs users in through PI device activation, as the Functions bridge did. No Entra sign-in. |
| Client registration | Client ID Metadata Documents (CIMD) and Dynamic Client Registration (DCR). Both are needed: Claude and ChatGPT prefer CIMD; DCR remains for older clients (deprecated in MCP 2026-07-28, still supported). |
| Tokens | Opaque access tokens (1 hour) and rotating refresh tokens (30 days), stored hashed. The PI token never leaves the server. |
| Per-user state | PI tokens in Key Vault; OAuth records in Table storage; drafts in Blob storage. All reached with the app's managed identity, no keys. |
| Deploy | `deploy.yml` runs on pushes to `main`: test, publish, deploy with GitHub OIDC (no stored Azure credential), then a smoke test. |
| Infrastructure | Bicep in the repo with no identifying values. Applied by hand, rarely, from a gitignored parameters file. CI's Azure identity can only deploy code to the one web app. |

## Layout

```
servers/project-insight/
  core/       ProjectInsightMCP.Core     net10.0   PI client, tools, time changes (net8.0 target dropped)
  server/     ProjectInsightMCP.Server   net10.0   NEW: AddProjectInsightMcp, tool/card adapters
  src-stdio/  projectinsight-mcp         net10.0   stdio transport, singletons, file drafts, device-activation tools
  src-web/    ProjectInsightMCP.Web      net10.0   NEW: HTTP transport, OAuth server, per-user stores
  tests/                                 net10.0   existing tests + web tests
  src/                                             DELETED (Functions host, deployment.zip, .sln.backup2)
  deploy/                                          DELETED (deploy.sh, untracked params)
bicep/                                             rewritten for App Service
```

### `ProjectInsightMCP.Server`

`ProjectInsightServer.AddProjectInsightMcp`, `ToSdkTool`, `ToSdkResult`, `ToLegacyArguments`, `ToolCallGate`, `TimeChangeTools` and the card resource move here from `src-stdio` unchanged, apart from namespaces. No tool handler changes. `src-stdio` keeps `Program`, `DrainingStdio`, `WithDrainingStreamServerTransport`, `AddProjectInsightServices` (its singleton registrations) and `PruneDraftsAtStartupAsync`.

`AddProjectInsightMcp` gains one option, a predicate for which tools a host exposes. The web host uses it to hide the three device-activation sign-in tools (`complete_authentication`, `link_pi_account`, `complete_pi_link`): they disappear from `tools/list` and a call to one returns "unknown tool". The stdio host exposes all tools, as today.

`ToolCallGate` becomes keyed: stdio uses a single key; the web host keys it by PI user id, so one user's calls run one at a time while different users run in parallel. This keeps the handlers' one-call-at-a-time assumption per user.

The move ships no behaviour change for the local tool. The existing 237 tests, including the tool-list snapshot, must pass unchanged after it, before any web work lands.

### `ProjectInsightMCP.Web`

ASP.NET Core minimal app:

- `MapMcp("/mcp")` with the SDK's HTTP transport, `Stateless = true`, behind `RequireAuthorization()`.
- PI services registered per request (scoped): `ApiTokenAccessor`, `ProjectInsightApiService`, `McpServerService`, the time-change services. `ApiTokenAccessor.ApiToken` is set from the signed-in user's PI token before the SDK runs.
- OAuth endpoints and metadata (below), a health endpoint, and an hourly background job that prunes expired drafts, sessions, codes and tokens.
- No CORS: MCP clients call from their own servers, and the card talks to the server through its host, not directly.

## Sign-in

### Discovery

The public base URL comes from configuration (`Web:PublicBaseUrl`, e.g. `https://<app>.azurewebsites.net`), never from the request's `Host` header.

- `POST /mcp` without a valid token → `401` with
  `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource/mcp", scope="pi"`.
- `GET /.well-known/oauth-protected-resource/mcp` and `/.well-known/oauth-protected-resource`:
  `resource` = `<base>/mcp` (exactly the URL users enter), `authorization_servers` = [`<base>`], `scopes_supported` = [`pi`], `bearer_methods_supported` = [`header`].
- `GET /.well-known/oauth-authorization-server`:
  `issuer` = `<base>` (no trailing slash, identical everywhere it appears), `authorization_endpoint` `<base>/authorize`, `token_endpoint` `<base>/token`, `registration_endpoint` `<base>/register`, `revocation_endpoint` `<base>/revoke`, `response_types_supported` [`code`], `grant_types_supported` [`authorization_code`, `refresh_token`], `code_challenge_methods_supported` [`S256`], `token_endpoint_auth_methods_supported` [`none`], `client_id_metadata_document_supported` `true`, `authorization_response_iss_parameter_supported` `true`, `scopes_supported` [`pi`, `offline_access`].

Without an `/api` prefix, standard path insertion and the root paths both resolve, which fixes both URL mismatches found on 2026-10-07.

### Clients and redirect URIs

A `client_id` that is an `https` URL with a path is a CIMD client; anything else must have been issued by `/register`.

**CIMD fetch:** `https` only, no redirects, 5-second timeout, at most 5 KB, JSON. The host must resolve only to public addresses (no loopback, private, link-local or metadata ranges), checked on the connected address. The document's `client_id` must equal the URL, and it must list `redirect_uris`. Valid documents are cached for 24 hours, or less if `Cache-Control` says so. A fetch failure shows an error page and never redirects.

**DCR:** `POST /register` (JSON) accepts `client_name`, `redirect_uris` and `token_endpoint_auth_method` = `none` (public clients only), and returns a random `client_id`. Registrations unused for 90 days are pruned.

**Redirect policy:** a `redirect_uri` must match one of the client's registered or published redirect URIs exactly, and its host must be on the allowlist `Auth:AllowedRedirectHosts`. The default is `claude.ai` and `chatgpt.com` over `https`, plus loopback (`localhost`, `127.0.0.1`, `[::1]`) over `http`, where the port is ignored when matching (RFC 8252; Claude Code and Codex). Because anyone can register a client, the allowlist is what stops a phishing client from receiving a user's sign-in. A rejected redirect URI gets an error page on our domain, never a redirect.

### Authorize

`GET /authorize` validates `response_type=code`, the client, the redirect URI, `code_challenge` with `code_challenge_method=S256` (plain is refused), `scope` (must include `pi`; `offline_access` optional) and `resource` (if present, must be `<base>/mcp`). It stores a sign-in session (15 minutes) and starts a PI device activation, as the bridge does today.

The page shows:

- which app is asking (the client name and the redirect URI's host, stated plainly; an extra warning when the only redirect is loopback, as the MCP spec asks);
- a "Sign in to Project Insight" link to PI's activation page, opening in a new tab;
- a field for the activation code PI shows, and Continue.

`POST /authorize/complete` completes the activation, reads the PI user (id, name) with the new PI token, stores the PI token in Key Vault, and redirects to `redirect_uri` with `code`, `state` and `iss`. A wrong activation code re-renders the page with the error. Errors after the redirect URI has been validated are returned to the client with `error`, `state` and `iss`.

The page sends `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. It uses no inline script.

### Tokens

- **Authorization code:** random 256 bits, single use, 5 minutes. Bound to client, redirect URI, code challenge, resource, scope and PI user. A second use revokes every token issued from it.
- **Access token:** random 256 bits, 1 hour. Bound to the PI user, client and resource (`<base>/mcp`).
- **Refresh token:** issued when `offline_access` was granted. Random 256 bits, 30 days. Rotated on every use: the response carries a new refresh token and the old one stops working. Reusing an old refresh token revokes the whole grant. A grant never outlives the PI token behind it.
- **Storage:** only SHA-256 hashes of codes and tokens are stored. Tokens and codes are never logged.
- **`/token`** accepts `application/x-www-form-urlencoded` and answers errors per RFC 6749 (`invalid_grant` for a bad, expired or reused code or refresh token). **`/revoke`** (RFC 7009) revokes a grant from either token.
- **Per request:** an ASP.NET Core authentication handler hashes the bearer token, looks it up (cached in memory for 60 seconds), checks expiry and resource, and loads the user's PI token from Key Vault (cached for 10 minutes).
- **PI token rejected:** if PI answers 401, the user's grants are revoked. The next request gets our 401, the client's refresh gets `invalid_grant`, and the client asks the user to reconnect.

Each sign-in creates a new PI token, which replaces the user's previous one in Key Vault. PI's API offers no way to revoke the old one; it expires on its own (one year).

## Per-user state

| Data | Where | Key | Notes |
| - | - | - | - |
| PI token | Key Vault secret `pi-user-<piUserId>` | PI user id | tags: expiry. Key Vault keeps it encrypted and audited. |
| OAuth clients (DCR) | Table `OAuthClients` | client id | |
| CIMD cache | memory | client id URL | |
| Sign-in sessions | Table `OAuthSessions` | session id | 15 minutes |
| Codes, access and refresh tokens, grants | Table `OAuthTokens` | token hash | grant id links a family for revocation |
| Time-change drafts | Blob container `drafts`, `<piUserId>/<draftId>.json` | PI user id + draft id | `BlobDraftStore : IDraftStore`. `TryTransitionAsync` uses ETag `If-Match`, the same compare-and-set the file store gets from its lock. Blob, not Table, because a draft with avatars can exceed Table's 64 KB property limit. |

`ownerId` for drafts is always the PI user id from the token, never a tool argument, so one user can't read or commit another's draft.

The in-memory caches and the per-user `ToolCallGate` assume one instance. Scaling out is out of scope; Bicep pins the plan to one instance.

## Configuration and secrets

App settings (set by Bicep, none in the repo):

| Setting | Value |
| - | - |
| `ProjectInsight__BaseUrl` | PI tenant API URL |
| `Web__PublicBaseUrl` | `https://<app>.azurewebsites.net` |
| `Storage__TableEndpoint`, `Storage__BlobEndpoint` | storage account endpoints (managed identity, no keys) |
| `KeyVault__Url` | vault URI |
| `Auth__AllowedRedirectHosts` | optional override of the default allowlist |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights |

GitHub environment `production` (deployment branches: `main` only) holds `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and `AZURE_WEBAPP_NAME` as **secrets**, not variables. They are identifiers, not credentials, but this repo's Actions logs are public, and only secrets are masked there.

## Infrastructure (`bicep/`)

`main.bicep` declares:

- **App Service plan:** Linux, B1, one instance.
- **Web app:**
  - runtime `DOTNETCORE|10.0`, Always On, HTTPS only, TLS 1.2 minimum;
  - FTP and basic publishing credentials disabled;
  - health check `/healthz`, system-assigned identity, the app settings above.
- **Storage account:** shared-key access disabled, TLS 1.2, the three tables, and the `drafts` container.
- **Key Vault:** RBAC authorisation, soft delete, purge protection.
- **Log Analytics and App Insights.**
- **App identity roles:** Storage Table Data Contributor, Storage Blob Data Contributor, Key Vault Secrets Officer, each scoped to its resource.
- **Deployer identity:** a user-assigned managed identity with a federated credential for subject `repo:alext-avi/Project-Insight-MCP:environment:production`, given Website Contributor on the web app only.

Parameters: `namePrefix`, `location`, `piBaseUrl`. `bicep/main.bicepparam.example` shows them with placeholders. The real file (`*.bicepparam`, not `.example`) is gitignored. `main.json` (stale compiled ARM) and `parameters/*.json` are deleted.

**One-time setup, run by the user** (documented in `bicep/README.md`):

1. `az deployment group create` with the local parameters file. The user needs rights to create role assignments in the resource group.
2. Copy the outputs (deployer client id, web app name) and the tenant and subscription ids into the `production` environment's secrets.

## Deploy (`.github/workflows/deploy.yml`)

```yaml
on:
  push:
    branches: [main]
    paths: [servers/project-insight/core/**, servers/project-insight/server/**,
            servers/project-insight/src-web/**, .github/workflows/deploy.yml]
  workflow_dispatch:
concurrency: { group: deploy-production, cancel-in-progress: false }
```

1. **test:** restore, build (warnings as errors, as CI does), run all tests.
2. **deploy** (`needs: test`, `environment: production`, `permissions: { id-token: write, contents: read }`):
   1. Publish `src-web` in Release, with the commit SHA as the informational version.
   2. Sign in with `azure/login` using OIDC: the client, tenant and subscription ids, and no secret.
   3. Deploy with `azure/webapps-deploy`.
3. **Smoke test, failing the run on any miss:**
   - `/healthz` reports the deployed SHA within 3 minutes;
   - an unauthenticated `POST /mcp` gets a 401 whose `WWW-Authenticate` has `resource_metadata`;
   - the protected-resource metadata's `resource` equals `<base>/mcp`.

**Rollback:** run the workflow by hand from an earlier commit.

Pull requests, including forks, never reach the `production` environment or its secrets. `ci.yml` also builds `server` and `src-web` and runs their tests.

`/healthz` returns `{ "status": "ok", "version": "<sha>" }` and nothing else. It reveals no configuration.

## Security summary

- **No secrets in the repo:** no secrets or identifying values in the repo or in public logs. gitleaks (full history and pull requests) and CodeRabbit's secret rule back this up.
- **No stored Azure credential:** OIDC only, with a deploy identity limited to one web app.
- **Exact matching:** the base URL comes from configuration. Redirect URIs must be on an allowlist and match exactly. PKCE S256 is required. Codes are single use. Tokens are bound to `<base>/mcp`.
- **Credential storage:** PI tokens live only in Key Vault. Tokens and codes are stored only as hashes and are never logged.
- **Rate limits:** per-IP rate limits on `/register`, `/token`, `/authorize` and `/authorize/complete`.
- **Draft isolation:** drafts are keyed by the signed-in user.
- **Sign-in page:** CSP, no framing, no inline script.

## Testing

- **Refactor gate:** the existing suite and tool snapshot pass unchanged after the move to `server/`.
- **Tool parity:** the web host's `tools/list` equals stdio's minus the three sign-in tools, and both expose the card resource.
- **OAuth unit tests**, using fakes and `TimeProvider`:
  - PKCE;
  - redirect matching (exact, loopback any port, host not on the allowlist refused without a redirect);
  - CIMD validation (non-https, redirect, oversize, private address, mismatched `client_id`);
  - code reuse;
  - refresh rotation, and reuse revoking the grant;
  - expiry;
  - resource mismatch.
- **End to end** (in-memory test server, in-memory stores, stubbed PI):
  1. DCR client → `/authorize` → complete with an activation code → `/token`;
  2. `tools/list` and `get_current_user` over `/mcp`;
  3. refresh, then the old refresh token refused;
  4. the same flow for a CIMD client;
  5. the 401 and both metadata documents match the JSON above exactly.
- **Storage:** one shared contract test suite runs against the in-memory, file and blob draft stores, and against the Table OAuth stores. The Azure-backed stores run against Azurite in CI.
- **After deploy:** the smoke test above.
- **Live, by the user:**
  - claude.ai and Cowork, ChatGPT developer mode, Claude Code and Codex: add the connector, sign in, list tools, run a read, plan a time change and see the card;
  - committing a time change only in the user's own test.

## Delivery

Three pull requests, each reviewed by CodeRabbit and gitleaks:

1. **Shared wiring:** add `server/`, move the code, drop Core's net8.0 target. No behaviour change, no release.
2. **Web host:**
   - add `src-web` with sign-in, the stores and the tests;
   - delete `src/` and `deploy/`;
   - extend `ci.yml`.
3. **Infra and deploy:** Bicep, `deploy.yml` and docs. Then the user runs the one-time setup, starts the first deploy by hand, and tests live.

**Cutover, by the user, after live testing:**

- point the existing connectors at `https://<new app>/mcp`;
- delete the old Function app, its storage, its Entra app registration and the old Key Vault.

## Out of scope

- Copilot Studio / Teams (stretch goal; the Entra path and `KeyVaultTokenService` stay in Core for it).
- A custom domain, a staging environment, more than one instance.
- Enterprise managed auth and a public directory listing.
- Guaranteeing the card renders in every client: it renders where the host supports MCP Apps, and the tools work as text everywhere.
