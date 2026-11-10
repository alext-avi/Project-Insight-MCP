# Remote server infrastructure

One-time setup for the remote MCP server on Azure App Service. Design: `docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md`. Every value below in angle brackets is yours to fill in; nothing identifying belongs in this repository.

## 1. What this creates

- Log Analytics workspace and Application Insights, for logs and telemetry.
- Storage account (no shared keys) with the tables `OAuthClients`, `OAuthSessions`, `OAuthCodes` and the blob container `drafts`.
- Linux App Service plan (B1, one instance) and the web app `<prefix>-web` with a system-assigned identity, HTTPS only, FTP and SCM basic credentials off.
- Role assignments giving the web app's identity table and blob data access to the storage account.
- A user-assigned `deployer` identity, federated to `deploy.yml` on `main` in the `production` environment, with a custom role that can only publish code and restart the app.
- `CanNotDelete` locks on the web app and the storage account.
- An email action group and an Activity Log alert for risky changes (storage, app config, credentials policy, federation, role assignments).

## 2. Before you start

- Be Owner of the resource group.
- `az login` to the right tenant.
- `az account set --subscription <subscription-id>`.

## 3. Deploy the infrastructure

Steps 3–6 can be done before PR 3 merges; the merge itself then becomes the first deploy. If you merge
first, `deploy.yml` runs at once and fails at `azure/login` (one expected red run), and GitHub creates
the `production` environment without a branch policy, which step 5 then sets.

Pick a distinctive `namePrefix`: the web app `<prefix>-web` needs a name unique across all of Azure.

```bash
cp bicep/main.bicepparam.example bicep/main.bicepparam   # fill it in; it is gitignored
az deployment group create --resource-group <resource-group> --parameters bicep/main.bicepparam
```

Note the outputs `webAppName`, `baseUrl` and `deployerClientId`.

## 4. Restrict the deploy identity to `deploy.yml` on `main`

```bash
gh api -X PUT repos/<owner>/<repo>/actions/oidc/customization/sub --input - <<'JSON'
{"use_default": false, "include_claim_keys": ["repo", "context", "job_workflow_ref"]}
JSON
```

## 5. GitHub environment and secrets

- Create the environment `production` (Settings, Environments), or edit it if GitHub already created it, and limit deployment branches to `main`.
- Add the secrets with the outputs and ids:

```bash
gh secret set AZURE_CLIENT_ID --env production        # deployerClientId output
gh secret set AZURE_TENANT_ID --env production
gh secret set AZURE_SUBSCRIPTION_ID --env production
gh secret set AZURE_WEBAPP_NAME --env production      # webAppName output
gh secret set APP_BASE_URL --env production           # baseUrl output, exactly: no trailing slash
```

Each prompts for its value, so nothing lands in shell history.

## 6. Protect `main`

Branch protection with pull requests required and force-pushes blocked.

## 7. First deploy

Actions, "Deploy remote server", Run workflow on `main`.

## 8. If the deploy step gets 403

- The custom role is too narrow for `azure/webapps-deploy`. Re-run step 3 with `param deployerRole = 'websiteContributor'` in your parameters file, and note it here.
- If `azure/login` can't find the subscription, add `allow-no-subscriptions: true` to the login step.
- If step 3 fails with `RoleDefinitionDoesNotExist`, the new custom role hasn't replicated yet; run step 3 again.
- If you do switch roles, remove the old custom-role assignment from the web app (deployments are incremental and leave it in place).

## 9. Who has access

The app lives in a shared resource group whose contributors the owner reviewed and accepted. Alerts email the owner on risky changes.
