# Copilot Per-User Auth via Device Activation

## Summary

Add per-user authentication to the Azure Function MCP endpoint so each Copilot user gets their own PI identity. Uses Entra ID (from Copilot) to identify the user and PI's device activation flow for one-time account linking. PI tokens stored in Azure Key Vault.

## Architecture

### Authentication Flow

1. Copilot sends request to Azure Function with Entra token
2. EasyAuth validates the token, populates `X-MS-CLIENT-PRINCIPAL-ID` (user's Entra oid)
3. Azure Function extracts oid, looks up `pi-token-{oid}` in Key Vault
4. If found → sets ApiTokenAccessor, processes tool call with user's PI token
5. If not found → returns "link your account" message with device activation URL

### Account Linking Flow (one-time per user)

1. User asks Copilot anything → gets "link your account" prompt
2. User says "link my account" → `link_pi_account` tool fires
3. Azure Function creates PI device activation request, returns URL
4. User clicks URL, authenticates via PI SSO (Entra ID), gets activation code
5. User tells Copilot the code → `complete_pi_link` tool fires
6. Azure Function exchanges code for PI token, stores in Key Vault as `pi-token-{oid}`
7. All subsequent requests resolve the user's PI token automatically

## Token Storage

Azure Key Vault secret per user:
- **Secret name**: `pi-token-{entra-oid}` (e.g., `pi-token-e2b6aa27-48bd-43ec-8093-49d71373c756`)
- **Secret value**: PI API token (JWT)
- **Tags**: `piUserId`, `linkedUtc`, `expiresUtc`, `userEmail`
- **Uses the existing resource group's Key Vault** (or creates one if none exists)
- Cost: ~$0.03/10k operations, negligible at team scale

## Azure Function Changes

### Infrastructure

- Enable App Service Authentication (EasyAuth) with Entra ID on the Azure Function
- Create or reference an Azure Key Vault in the same resource group
- Grant the Function App's managed identity `Key Vault Secrets User` role

### New Endpoints

**`POST /api/auth/link-start`** (Entra-authenticated)
- Extracts Entra oid from EasyAuth headers
- Creates PI device activation request via `POST /api/device-activation/create-request`
- Returns `{ activationUrl, activationId, expiresUtc }`

**`POST /api/auth/link-complete`** (Entra-authenticated)
- Body: `{ activationCode }`
- Extracts Entra oid from EasyAuth headers
- Calls PI `GET /api/device-activation/complete-request` with the code
- Stores resulting PI token in Key Vault as `pi-token-{oid}`
- Returns success with expiration date

### Modified MCP Endpoint

**`POST /api/mcp`** (existing)
- Extract Entra oid from `X-MS-CLIENT-PRINCIPAL-ID` header (set by EasyAuth)
- If oid present → look up `pi-token-{oid}` in Key Vault
  - Found → set ApiTokenAccessor, process normally
  - Not found → return link prompt
- If no oid (unauthenticated call, e.g., from local client with X-PI-Token header) → use existing header-based auth (backwards compatible)

### New MCP Tools (Copilot only)

**`link_pi_account`**
- No parameters (identity from Entra)
- Creates device activation, returns URL and instructions
- Description: "Link your Project Insight account to enable all tools"

**`complete_pi_link`**
- Parameter: `activationCode` (string, required)
- Exchanges code for token, stores in Key Vault
- Description: "Complete Project Insight account linking with your activation code"

### Backwards Compatibility

- Local stdio server continues using env var or stored credentials — no changes
- Azure Function still accepts `X-PI-Token` header for non-Copilot clients
- If EasyAuth is not configured (e.g., local dev), falls back to existing behavior

## File Changes

| File | Change |
|------|--------|
| `src/Functions/McpFunction.cs` | Extract Entra oid, Key Vault lookup, link endpoints |
| `src/Services/McpServerService.cs` | Add `link_pi_account` and `complete_pi_link` tool cases + definitions |
| `src/Services/KeyVaultTokenService.cs` | New service: get/set PI tokens in Key Vault by Entra oid |
| `src/Services/DeviceActivationService.cs` | Already exists, reuse for Azure Function context |
| `src/Program.cs` | Register Key Vault client, DeviceActivationService |
| `bicep/` or deploy script | Add Key Vault resource, managed identity role assignment, EasyAuth config |

## Copilot UX

**First use:**
> **User**: "Show me my projects"
> **Agent**: "I need to connect your Project Insight account first. Click here to sign in: [link]. Then tell me the activation code."
> **User**: *clicks, authenticates via AD SSO, gets code* "My code is AHT6UG"
> **Agent**: "Your Project Insight account is linked! Here are your projects: ..."

**Subsequent use:**
> **User**: "Show me my projects"
> **Agent**: "Here are your projects: ..."

## Risks

- EasyAuth adds latency to every request (~50ms). Acceptable for this use case.
- Key Vault lookups add ~100ms per request. Could cache in-memory with short TTL if needed.
- PI device activation codes expire in ~10 minutes. User needs to complete the flow promptly.
- If PI token expires (1 year), user needs to re-link. The error handling should detect 401s from PI and prompt re-linking.
