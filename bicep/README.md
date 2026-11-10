# Remote server infrastructure

One-time setup for the remote MCP server on Azure App Service. Design: `docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md`. Every value below in angle brackets is yours to fill in; nothing identifying belongs in this repository.

## 1. What this creates

- Log Analytics workspace and Application Insights, for logs and telemetry.
- Storage account (no shared keys) with the tables `OAuthClients`, `OAuthSessions`, `OAuthCodes` and the blob container `drafts`.
- Linux App Service plan (B1, one instance) and the web app `<prefix>-web` with a system-assigned identity, HTTPS only, FTP and SCM basic credentials off.
- A diagnostic setting sending the web app's `AppServiceAuditLogs` (Kudu/SCM sign-ins and publishes) and `AppServicePlatformLogs` to the workspace.
- Role assignments giving the web app's identity table and blob data access to the storage account.
- A user-assigned `deployer` identity, federated to `deploy.yml` on `main` in the `production` environment, with a custom role that can only publish code and restart the app.
- `CanNotDelete` locks on the web app, the storage account, the deployer identity, the workspace, App Insights, the action group and both alert rules.
- An email action group and two Activity Log alerts (section 9).

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

- **App settings live in `main.bicep` only.** The template writes the app's whole settings set, so a setting added
  in the portal or with `az webapp config appsettings set` is removed by the next run of this step. Add any
  setting you need (for example the optional `Auth__*` lists) to the `appSettings` resource, or as a parameter.
- **Time zone.** `timeZone` (an IANA id, default `America/New_York`) sets `TZ` on the app, so the time-change
  tools' "today" is the team's day rather than UTC's.

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

## 9. Alerts and audit logs

Both alerts email the action group (`alertEmail`):

- **`<prefix>-risky-changes`**, on the web app, storage account, deployer identity and the other alert: writes to
  the storage account, the app, its config, its basic-credentials policies, sidecar containers
  (`sites/sitecontainers`), site extensions (`sites/extensions`, which ARM OneDeploy uses), its diagnostic setting,
  the deployer's federated credential, and changes to `<prefix>-access-changes`.
- **`<prefix>-access-changes`**, on the whole resource group: role assignments created or deleted at any scope in
  the group (the group itself included), custom role definition writes, lock deletions, and changes to any alert
  rule or action group. The group is shared, so other people's changes of these kinds email you too; they're rare.

What to expect:

- **Every matching event emails, whatever its outcome.** A denied or failed attempt emails too (for example a
  deploy action trying a write its role doesn't allow), and so does each watched operation a successful one
  performs.
- **Re-running step 3 emails.** A Bicep deploy writes the app, its config, the credentials policies, the
  diagnostic setting, the federated credential, role assignments, the alerts and the action group, so expect a
  burst of emails each time. Check that they line up with your deploy.
- **Custom role edits: needs verification.** Role definitions are subscription-level resources. This template
  writes the custom role through the resource group's scope, which the group-wide alert should see; an edit made at
  subscription scope (the default for `az role definition update`) probably isn't seen. Edit the role once after
  setup and check for the email. If none arrives and you can, add a subscription-scoped Activity Log alert for
  `Microsoft.Authorization/roleDefinitions/write`.
- **The action group is its own weak point.** Someone who changes its email address or disables it gets the
  resulting alert routed through the changed group. Glance at the action group now and then.
- **Kudu publishes may not reach the Activity Log.** A publish through Kudu with an Entra token may leave no
  Administrative event; `AppServiceAuditLogs` records it instead.

Owner follow-up, not yet in the template: after the first deploy, run `AppServiceAuditLogs | take 20` in the
workspace (if the table is missing, check that the category is available for this plan), learn which columns hold
the caller's identity and the protocol, and add a log search alert (`Microsoft.Insights/scheduledQueryRules`) for
publishes and SCM sign-ins by anyone other than the deployer identity. It isn't in the template because the
column names haven't been checked against real rows.

## 10. Who has access

The app lives in a shared resource group whose contributors the owner reviewed and accepted. Alerts email the owner on risky changes (section 9).
