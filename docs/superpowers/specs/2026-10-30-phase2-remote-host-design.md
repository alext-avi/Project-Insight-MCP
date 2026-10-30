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
| Identity | PI is the only identity provider. PI has no OAuth, so the server runs a thin OAuth front for PI device activation, as the Functions bridge did. No Entra sign-in. |
| Client registration | Client ID Metadata Documents (CIMD) and Dynamic Client Registration (DCR). Both are needed: Claude and ChatGPT prefer CIMD; DCR remains for older clients (deprecated in MCP 2026-07-28, still supported). Neither stores anything. |
| Tokens | Pass-through: the access token the client receives **is** the PI token, valid until PI's expiry (one year). No refresh tokens. The server stores no tokens; the client's harness (Anthropic's or OpenAI's connector store, or the CLI's credential store) holds it, never the model. |
| Per-user state | Sign-in sessions and one-time codes in Table storage, for minutes; drafts in Blob storage. Both reached with the app's managed identity, no keys. No Key Vault. |
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
- PI services registered per request (scoped): `ApiTokenAccessor`, `ProjectInsightApiService`, `McpServerService`, the time-change services. `ApiTokenAccessor.ApiToken` is set from the request's bearer token before the SDK runs.
- OAuth endpoints and metadata (below), a health endpoint, and an hourly background job that prunes expired drafts, sessions and codes.
- No CORS: MCP clients call from their own servers, and the card talks to the server through its host, not directly.

## Sign-in

### Discovery

The public base URL comes from configuration (`Web:PublicBaseUrl`, e.g. `https://<app>.azurewebsites.net`), never from the request's `Host` header.

- `POST /mcp` without a valid token → `401` with
  `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource/mcp", scope="pi"`.
- `GET /.well-known/oauth-protected-resource/mcp` and `/.well-known/oauth-protected-resource`:
  `resource` = `<base>/mcp` (exactly the URL users enter), `authorization_servers` = [`<base>`], `scopes_supported` = [`pi`], `bearer_methods_supported` = [`header`].
- `GET /.well-known/oauth-authorization-server`:
  `issuer` = `<base>` (no trailing slash, identical everywhere it appears), `authorization_endpoint` `<base>/authorize`, `token_endpoint` `<base>/token`, `registration_endpoint` `<base>/register`, `response_types_supported` [`code`], `grant_types_supported` [`authorization_code`], `code_challenge_methods_supported` [`S256`], `token_endpoint_auth_methods_supported` [`none`], `client_id_metadata_document_supported` `true`, `authorization_response_iss_parameter_supported` `true`, `scopes_supported` [`pi`]. `offline_access` is not advertised, so clients don't expect a refresh token.

Without an `/api` prefix, standard path insertion and the root paths both resolve, which fixes both URL mismatches found on 2026-10-07.

### Clients and redirect URIs

A `client_id` that is an `https` URL with a path is a CIMD client; anything else must have been issued by `/register`.

**CIMD fetch:** `https` only, no redirects, 5-second timeout, at most 5 KB, JSON. The host must resolve only to public addresses (no loopback, private, link-local or metadata ranges), checked on the connected address. The document's `client_id` must equal the URL, and it must list `redirect_uris`. Valid documents are cached for 24 hours, or less if `Cache-Control` says so. A fetch failure shows an error page and never redirects.

**DCR:** `POST /register` (JSON) accepts `client_name`, `redirect_uris` and `token_endpoint_auth_method` = `none` (public clients only). Every redirect URI must pass the allowlist below, or the registration is refused (`invalid_redirect_uri`). It returns a random `client_id` and stores nothing: at `/authorize` a DCR client's redirect URI is checked against the allowlist only. The allowlist is the security control, not the registration.

**Redirect policy:** a CIMD client's `redirect_uri` must match one of the redirect URIs its document publishes exactly. Every client's redirect host must be on the allowlist `Auth:AllowedRedirectHosts`. The default is `claude.ai` and `chatgpt.com` over `https`, plus loopback (`localhost`, `127.0.0.1`, `[::1]`) over `http`, where the port is ignored when matching (RFC 8252; Claude Code and Codex). Because anyone can register a client, the allowlist is what stops a phishing client from receiving a user's sign-in. A rejected redirect URI gets an error page on our domain, never a redirect.

### Authorize

`GET /authorize` validates `response_type=code`, the client, the redirect URI, `code_challenge` with `code_challenge_method=S256` (plain is refused), `scope` (if present, must be `pi`; other scopes such as `offline_access` are ignored, not granted) and `resource` (if present, must be `<base>/mcp`). It stores a sign-in session (15 minutes) and starts a PI device activation, as the bridge does today.

The page shows:

- which app is asking (the client name and the redirect URI's host, stated plainly; an extra warning when the only redirect is loopback, as the MCP spec asks);
- a "Sign in to Project Insight" link to PI's activation page, opening in a new tab;
- a field for the activation code PI shows, and Continue.

`POST /authorize/complete` completes the activation, which yields the PI token and its expiry, stores them in a one-time code, and redirects to `redirect_uri` with `code`, `state` and `iss`. A wrong activation code re-renders the page with the error. Errors after the redirect URI has been validated are returned to the client with `error`, `state` and `iss`.

The page sends `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. It uses no inline script.

### Tokens

The access token is the PI token itself (pass-through). PI has no OAuth, so the server translates PI device activation into the authorization-code flow clients expect and then steps aside. The token is held by the client's harness, never by the model. The MCP spec discourages "token passthrough" (a server accepting tokens not issued for it). That concern is mostly about multi-service audit and confused deputies, which a single-tenant tool fronting one API doesn't have. The upgrade path, should that change, is in Out of scope.

- **Authorization code:** random 256 bits, single use (deleted on first redemption, with an ETag-conditional delete so two redemptions can't both succeed), 5 minutes. It holds the PI token and its expiry, plus the client, redirect URI, code challenge and resource it was issued for. Only the code's SHA-256 hash is the row key.
- **`/token`** accepts `application/x-www-form-urlencoded` with `grant_type=authorization_code`, `code`, `code_verifier`, `client_id`, `redirect_uri` and an optional `resource`. All must match the code. It returns `{ "access_token": <PI token>, "token_type": "Bearer", "expires_in": <seconds to PI expiry> }` and no refresh token. Errors follow RFC 6749 (`invalid_grant` for a bad, expired or reused code or a PKCE mismatch; `unsupported_grant_type` for anything else).
- **Per request:** an ASP.NET Core authentication handler takes the bearer token, sets `ApiTokenAccessor.ApiToken`, and resolves the PI user id by calling PI's current-user endpoint. The result is cached in memory for 10 minutes under the token's SHA-256 hash, never the token itself. If PI rejects the token, or there is no token, the request gets the 401 above before the SDK runs, so the client starts a fresh sign-in.
- **Expiry:** after PI's expiry (one year) the token stops working, the server answers 401, and the client signs in again. PI's API offers no way for us to revoke a token; the user can revoke it in PI if PI's UI allows.

**The token must never be echoed.** No tool result, error message, health or info output, or log line may contain the bearer token, an authorization code or an activation code. Tool results reach the model, so this is the line that keeps the token away from the agent. The token arrives in a header and is redacted by default in ASP.NET Core and App Insights request logging; that redaction is asserted in tests, not assumed. Exception messages from PI calls are scrubbed of the token before they become tool errors.

## Per-user state

| Data | Where | Key | Notes |
| - | - | - | - |
| CIMD documents | memory | client id URL | 24 hours or `Cache-Control` |
| Token → PI user id | memory | SHA-256 of the token | 10 minutes |
| Sign-in sessions | Table `OAuthSessions` | session id | 15 minutes: client, redirect URI, state, code challenge, resource, PI activation id |
| Authorization codes | Table `OAuthCodes` | SHA-256 of the code | 5 minutes, single use |
| Time-change drafts | Blob container `drafts`, `<piUserId>/<draftId>.json` | PI user id + draft id | `BlobDraftStore : IDraftStore`. `TryTransitionAsync` uses ETag `If-Match`, the same compare-and-set the file store gets from its lock. Blob, not Table, because a draft with avatars can exceed Table's 64 KB property limit. |

`ownerId` for drafts is always the PI user id resolved from the token, never a tool argument, so one user can't read or commit another's draft. Codes hold a PI token for at most 5 minutes; nothing else in storage holds one.

The in-memory caches and the per-user `ToolCallGate` assume one instance. Scaling out is out of scope; Bicep pins the plan to one instance.

## Configuration and secrets

App settings (set by Bicep, none in the repo):

| Setting | Value |
| - | - |
| `ProjectInsight__BaseUrl` | PI tenant API URL |
| `Web__PublicBaseUrl` | `https://<app>.azurewebsites.net` |
| `Storage__TableEndpoint`, `Storage__BlobEndpoint` | storage account endpoints (managed identity, no keys) |
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
- **Storage account:** shared-key access disabled, TLS 1.2, the tables `OAuthSessions` and `OAuthCodes`, and the `drafts` container.
- **Log Analytics and App Insights.**
- **App identity roles:** Storage Table Data Contributor and Storage Blob Data Contributor on the storage account.
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
- **Exact matching:** the base URL comes from configuration. Redirect URIs must be on an allowlist and match exactly. PKCE S256 is required. Codes are single use. A `resource` other than `<base>/mcp` is refused.
- **Credential handling:** pass-through. The PI token is held by the client's harness, never by the model. The server stores it only inside a 5-minute single-use code, keyed by the code's hash. It never echoes it in tool output, errors or logs, and tests assert that.
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
  - code reuse and concurrent redemption (exactly one wins);
  - code and session expiry;
  - resource mismatch;
  - `expires_in` derived from PI's expiry.
- **End to end** (in-memory test server, in-memory stores, stubbed PI):
  1. DCR client → `/authorize` → complete with an activation code → `/token` returns the PI token;
  2. `tools/list` and `get_current_user` over `/mcp` with it as the bearer token;
  3. a token PI rejects gets the 401 with `resource_metadata`;
  4. the same flow for a CIMD client;
  5. the 401 and both metadata documents match the JSON above exactly;
  6. two users' drafts stay separate.
- **No echo:** with a recognisable fake token, call every tool (including ones the stubbed PI fails), the health endpoint and a failing `/token`. Assert the token appears in no response body and no captured log line.
- **Storage:** one shared contract test suite runs against the in-memory, file and blob draft stores, and against the Table session and code stores. The Azure-backed stores run against Azurite in CI.
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
3. **Infra and deploy:** Bicep, `deploy.yml` and docs. The docs work includes rewriting `docs/AGENTS.md`, which still describes the Functions host, and a "connect a client" section with the connector URL shape for each client. Then the user runs the one-time setup, starts the first deploy by hand, and tests live.

**Cutover, by the user, after live testing:**

- point the existing connectors at `https://<new app>/mcp`;
- delete the old Function app, its storage, its Entra app registration and the old Key Vault.

## Out of scope

- Copilot Studio / Teams (stretch goal; the Entra path and `KeyVaultTokenService` stay in Core for it).
- A custom domain, a staging environment, more than one instance.
- Enterprise managed auth and a public directory listing.
- Server-issued tokens. If the server ever serves people beyond a small internal group, or needs revocation, the upgrade is to issue our own short-lived tokens with rotating refresh tokens. The PI token would then be kept server-side, encrypted with ASP.NET Core Data Protection in Table storage, with the key ring wrapped by one Key Vault key. Client-facing URLs and metadata stay the same, apart from adding `refresh_token` and `offline_access`.
- Guaranteeing the card renders in every client: it renders where the host supports MCP Apps, and the tools work as text everywhere.
