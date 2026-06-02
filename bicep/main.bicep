@description('Prefix for all resource names')
param resourcePrefix string = 'func-projectinsight'

@description('Environment name')
@allowed(['dev', 'staging', 'prod'])
param environment string = 'dev'

@description('Location for all resources')
param location string = resourceGroup().location

@description('Function App runtime stack')
@allowed(['dotnet-isolated'])
param runtime string = 'dotnet-isolated'

@description('SKU for the App Service Plan')
@allowed(['Y1', 'EP1', 'EP2', 'EP3'])
param appServicePlanSku string = 'Y1'

@description('Enable Application Insights')
param enableApplicationInsights bool = true

@description('Tags to be applied to all resources')
param tags object = {
  Environment: environment
  Project: 'ProjectInsight'
  ManagedBy: 'Bicep'
}

// Variables
var functionAppName = '${resourcePrefix}-mcp-${environment}'
var appServicePlanName = '${resourcePrefix}-plan-${environment}'
var storageAccountName = 'pistore${environment}${uniqueString(resourceGroup().id)}'
var applicationInsightsName = '${resourcePrefix}-insights-${environment}'
var logAnalyticsWorkspaceName = '${resourcePrefix}-logs-${environment}'

// Storage Account for Function App
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// Log Analytics Workspace for Application Insights
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (enableApplicationInsights) {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      searchVersion: 1
    }
  }
}

// Application Insights
resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = if (enableApplicationInsights) {
  name: applicationInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}

// App Service Plan
resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: appServicePlanSku
    tier: appServicePlanSku == 'Y1' ? 'Dynamic' : 'ElasticPremium'
  }
  properties: {
    reserved: false
  }
}

// Function App
resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      netFrameworkVersion: 'v8.0'
      use32BitWorkerProcess: false
      cors: {
        allowedOrigins: ['*']
        supportCredentials: false
      }
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: toLower(functionAppName)
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: runtime
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environment == 'prod' ? 'Production' : 'Development'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: enableApplicationInsights ? '${applicationInsights.properties.ConnectionString}' : ''
        }
        {
          name: 'PROJECT_INSIGHT_BASE_URL'
          value: 'https://yourdomain.projectinsight.net/api'
        }
      ]
    }
  }
}

// Storage Account blob contributor role assignment for Function App system identity
resource storageAccountRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, functionApp.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    ) // Storage Blob Data Contributor
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
@description('Function App name')
output functionAppName string = functionApp.name

@description('Function App default hostname')
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'

@description('Function App resource ID')
output functionAppResourceId string = functionApp.id

@description('Storage Account name')
output storageAccountName string = storageAccount.name

@description('Application Insights instrumentation key')
output applicationInsightsInstrumentationKey string = enableApplicationInsights
  ? '${applicationInsights.properties.InstrumentationKey}'
  : ''

@description('Application Insights connection string')
output applicationInsightsConnectionString string = enableApplicationInsights
  ? '${applicationInsights.properties.ConnectionString}'
  : ''

@description('Function App system-assigned managed identity principal ID')
output functionAppPrincipalId string = functionApp.identity.principalId
