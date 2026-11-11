// Remote MCP server for Project Insight on App Service (see docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md).
// No identifying values live here: the owner supplies them in a gitignored .bicepparam file.
targetScope = 'resourceGroup'

@description('Short prefix for resource names. The web app is <prefix>-web, which must be unique across Azure, so pick something distinctive.')
@minLength(3)
@maxLength(20)
param namePrefix string

param location string = resourceGroup().location

@description('Project Insight API base URL, e.g. https://your-tenant.projectinsight.net/api/')
param piBaseUrl string

@description('Comma-separated PI user ids allowed to use the server. May be empty when allowedEmailDomains is set.')
param allowedPiUserIds string = ''

@description('Email domains (e.g. example.com) whose PI users may use the server, matched against the email on each user\'s PI profile.')
param allowedEmailDomains array = []

@description('Where Activity Log alerts about risky changes are emailed.')
param alertEmail string

@description('GitHub repository (owner/name) whose deploy.yml on main may publish code.')
param githubRepository string

@description('custom: read, publish, restart, fetch the publishing profile and list settings on the app; no writes to settings. websiteContributor: fallback if the custom role cannot deploy.')
@allowed([ 'custom', 'websiteContributor' ])
param deployerRole string = 'custom'

@description('IANA time zone id for the server, e.g. America/New_York. Sets TZ, so "today" in the time-change tools is the team\'s day, not UTC\'s.')
param timeZone string = 'America/New_York'

param tags object = {}

var storageName = take('st${uniqueString(resourceGroup().id, namePrefix)}', 24)
var tableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var blobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var websiteContributor = 'de139f84-1756-47ae-9be6-808fbbe84772'
var customRoleName = guid(resourceGroup().id, namePrefix, 'code-deployer')

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  tags: tags
  properties: {
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [for name in [ 'OAuthClients', 'OAuthSessions', 'OAuthCodes' ]: {
  parent: tableService
  name: name
}]

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource drafts 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'drafts'
  properties: { publicAccess: 'None' }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-plan'
  location: location
  kind: 'linux'
  tags: tags
  sku: {
    name: 'B1'
    capacity: 1 // in-memory caches and per-user queues assume one instance
  }
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: '${namePrefix}-web'
  location: location
  kind: 'app,linux'
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/healthz'
    }
  }
}

resource ftpCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: site
  name: 'ftp'
  properties: { allow: false }
}

resource scmCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: site
  name: 'scm'
  properties: { allow: false }
}

var host = site.properties.defaultHostName

// This replaces the app's whole settings set on every deploy of this template: a setting added in the portal is
// removed by the next infrastructure deploy. Put every setting here.
resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'appsettings'
  // List settings take one numbered setting per entry (Auth__AllowedEmailDomains__0, __1, …).
  properties: union({
    ProjectInsight__BaseUrl: piBaseUrl
    Web__PublicBaseUrl: 'https://${host}'
    AllowedHosts: '${host};localhost'
    Storage__Mode: 'Azure'
    Storage__TableEndpoint: storage.properties.primaryEndpoints.table
    Storage__BlobEndpoint: storage.properties.primaryEndpoints.blob
    Auth__AllowedPiUserIds: allowedPiUserIds
    Auth__PiTokenLifetimeDays: '90'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    // The app runs its own forwarded-headers pass (ForwardLimit 1, right-most X-Forwarded-For) and StartupChecks
    // refuses to start if this is true: a second framework pass would let clients spoof the rate-limit partition.
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'false'
    // Seconds the platform waits for graceful shutdown (default 5, max 120). The host's ShutdownTimeout is 60 s.
    WEBSITES_CONTAINER_STOP_TIME_LIMIT: '90'
    // The time-change tools compute "today" (far-date warnings, the year in dates) in the server's local zone.
    TZ: timeZone
  }, toObject(range(0, length(allowedEmailDomains)), i => 'Auth__AllowedEmailDomains__${i}', i => string(allowedEmailDomains[i])))
}

resource appTables 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, tableDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', tableDataContributor)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appBlobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, blobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributor)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-deployer'
  location: location
  tags: tags
}

// Only deploy.yml on main, in the production environment, can sign in as the deployer. The repository's
// OIDC subject template must include job_workflow_ref (bicep/README.md, step 4).
resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployer
  name: 'github-deploy'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:production:job_workflow_ref:${githubRepository}/.github/workflows/deploy.yml@refs/heads/main'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

resource codeDeployerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (deployerRole == 'custom') {
  name: customRoleName
  properties: {
    roleName: '${namePrefix} code deployer'
    description: 'Publish code to one web app and restart it. No settings writes, no credentials policy.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Web/sites/read'
          'Microsoft.Web/sites/publish/Action'
          // webapps-deploy fetches the publishing profile first; its basic-auth credentials stay unusable while both
          // basicPublishingCredentialsPolicies are off, which this role can't change.
          'Microsoft.Web/sites/publishxml/Action'
          'Microsoft.Web/sites/restart/Action'
          'Microsoft.Web/sites/config/list/Action'
        ]
      }
    ]
    assignableScopes: [ resourceGroup().id ]
  }
}

resource deployerOnSite 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: site
  name: guid(site.id, deployer.id, deployerRole)
  dependsOn: [ codeDeployerRole ]
  properties: {
    roleDefinitionId: deployerRole == 'custom'
      ? subscriptionResourceId('Microsoft.Authorization/roleDefinitions', customRoleName)
      : subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributor)
    principalId: deployer.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource siteLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: site
  name: 'do-not-delete'
  properties: {
    level: 'CanNotDelete'
    notes: 'Remote MCP server for Project Insight.'
  }
}

resource storageLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: storage
  name: 'do-not-delete'
  properties: {
    level: 'CanNotDelete'
    notes: 'Sign-in records and drafts for the remote MCP server.'
  }
}

resource ownerAlerts 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${namePrefix}-owner'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'pimcp'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Changes that could expose tokens in flight or weaken the deploy trust, made outside this template. Role
// assignments are watched group-wide by accessChanges below. The alert operations only occur on accessChanges,
// which this rule also watches, so neither alert can be edited without the other emailing. Source control
// attaches an external git deployment, a route to new code; a plan write can scale out past the one instance the
// in-memory caches and per-user queues assume.
var watchedOperations = [
  'Microsoft.Storage/storageAccounts/write'
  'Microsoft.Web/serverfarms/write'
  'Microsoft.Web/sites/write'
  'Microsoft.Web/sites/config/write'
  'Microsoft.Web/sites/basicPublishingCredentialsPolicies/write'
  'Microsoft.Web/sites/sitecontainers/write'
  'Microsoft.Web/sites/extensions/write'
  'Microsoft.Web/sites/sourcecontrols/write'
  'Microsoft.Insights/diagnosticSettings/write'
  'Microsoft.Insights/diagnosticSettings/delete'
  'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials/write'
  'Microsoft.Insights/activityLogAlerts/write'
  'Microsoft.Insights/activityLogAlerts/delete'
]

resource riskyChanges 'Microsoft.Insights/activityLogAlerts@2020-10-01' = {
  name: '${namePrefix}-risky-changes'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    scopes: [ site.id, plan.id, storage.id, deployer.id, accessChanges.id ]
    condition: {
      allOf: [
        { field: 'category', equals: 'Administrative' }
        { anyOf: [for op in watchedOperations: { field: 'operationName', equals: op }] }
      ]
    }
    actions: { actionGroups: [ { actionGroupId: ownerAlerts.id } ] }
  }
}

// Changes anywhere in the shared resource group that could grant access to this app or silence these alerts:
// role assignments at any scope in the group (the group itself included), the custom role, lock removal, and
// edits to alert rules and action groups. Other people's resources share the group; these operations are rare.
var watchedGroupOperations = [
  'Microsoft.Authorization/roleAssignments/write'
  'Microsoft.Authorization/roleAssignments/delete'
  'Microsoft.Authorization/roleDefinitions/write'
  'Microsoft.Authorization/locks/delete'
  'Microsoft.Insights/activityLogAlerts/write'
  'Microsoft.Insights/activityLogAlerts/delete'
  'Microsoft.Insights/actionGroups/write'
  'Microsoft.Insights/actionGroups/delete'
]

resource accessChanges 'Microsoft.Insights/activityLogAlerts@2020-10-01' = {
  name: '${namePrefix}-access-changes'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    scopes: [ resourceGroup().id ]
    condition: {
      allOf: [
        { field: 'category', equals: 'Administrative' }
        { anyOf: [for op in watchedGroupOperations: { field: 'operationName', equals: op }] }
      ]
    }
    actions: { actionGroups: [ { actionGroupId: ownerAlerts.id } ] }
  }
}

// Kudu/SCM sign-ins and publishes (AppServiceAuditLogs) and platform events (AppServicePlatformLogs). A publish
// through Kudu with an Entra token may leave no Administrative Activity Log event, so this is where it shows up.
resource siteDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: site
  name: 'audit-to-logs'
  properties: {
    workspaceId: logs.id
    logs: [
      { category: 'AppServiceAuditLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
  }
}

// The rest of this app's resources: the deploy identity, the evidence, and the alerting itself. Contributors can't
// remove locks (that takes Microsoft.Authorization/locks/delete), and accessChanges emails if someone who can does.
var lockProperties = {
  level: 'CanNotDelete'
  notes: 'Remote MCP server for Project Insight.'
}

resource deployerLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: deployer
  name: 'do-not-delete'
  properties: lockProperties
}

resource logsLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: logs
  name: 'do-not-delete'
  properties: lockProperties
}

resource insightsLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: insights
  name: 'do-not-delete'
  properties: lockProperties
}

resource ownerAlertsLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: ownerAlerts
  name: 'do-not-delete'
  properties: lockProperties
}

resource riskyChangesLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: riskyChanges
  name: 'do-not-delete'
  properties: lockProperties
}

resource accessChangesLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: accessChanges
  name: 'do-not-delete'
  properties: lockProperties
}

output webAppName string = site.name
output baseUrl string = 'https://${host}'
output deployerClientId string = deployer.properties.clientId
