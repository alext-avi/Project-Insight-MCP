// Remote MCP server for Project Insight on App Service (see docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md).
// No identifying values live here: the owner supplies them in a gitignored .bicepparam file.
targetScope = 'resourceGroup'

@description('Short prefix for resource names, e.g. pi-mcp. The web app is <prefix>-web.')
@minLength(3)
@maxLength(20)
param namePrefix string

param location string = resourceGroup().location

@description('Project Insight API base URL, e.g. https://your-tenant.projectinsight.net/api/')
param piBaseUrl string

@description('Comma-separated PI user ids allowed to use the server.')
param allowedPiUserIds string

@description('Where Activity Log alerts about risky changes are emailed.')
param alertEmail string

@description('GitHub repository (owner/name) whose deploy.yml on main may publish code.')
param githubRepository string

@description('custom: publish and restart only. websiteContributor: fallback if the custom role cannot deploy.')
@allowed([ 'custom', 'websiteContributor' ])
param deployerRole string = 'custom'

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

resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'appsettings'
  properties: {
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
  }
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
    description: 'Publish code to one web app and restart it. No settings writes, no credentials policy, no Kudu console.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Web/sites/read'
          'Microsoft.Web/sites/publish/Action'
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
      ? resourceId('Microsoft.Authorization/roleDefinitions', customRoleName)
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

// Changes that could expose tokens in flight or weaken the deploy trust, made outside this template.
var watchedOperations = [
  'Microsoft.Storage/storageAccounts/write'
  'Microsoft.Web/sites/config/write'
  'Microsoft.Web/sites/basicPublishingCredentialsPolicies/write'
  'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials/write'
  'Microsoft.Authorization/roleAssignments/write'
]

resource riskyChanges 'Microsoft.Insights/activityLogAlerts@2020-10-01' = {
  name: '${namePrefix}-risky-changes'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    scopes: [ site.id, storage.id, deployer.id ]
    condition: {
      allOf: [
        { field: 'category', equals: 'Administrative' }
        { anyOf: [for op in watchedOperations: { field: 'operationName', equals: op }] }
      ]
    }
    actions: { actionGroups: [ { actionGroupId: ownerAlerts.id } ] }
  }
}

output webAppName string = site.name
output baseUrl string = 'https://${host}'
output deployerClientId string = deployer.properties.clientId
