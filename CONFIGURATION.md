# Azure Function App Configuration Guide

This document outlines the application settings and configuration for the Project Insight MCP Server Azure Function App.

## Required Application Settings

The following settings are automatically configured by the Bicep template:

### Core Function App Settings
- `AzureWebJobsStorage`: Connection string to Azure Storage Account
- `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING`: File share connection for content
- `WEBSITE_CONTENTSHARE`: File share name for function content
- `FUNCTIONS_EXTENSION_VERSION`: Azure Functions runtime version (~4)
- `FUNCTIONS_WORKER_RUNTIME`: Runtime stack (dotnet-isolated)

### Application-Specific Settings
- `ASPNETCORE_ENVIRONMENT`: Environment name (Development/Production)
- `APPLICATIONINSIGHTS_CONNECTION_STRING`: Application Insights connection string
- `PROJECT_INSIGHT_BASE_URL`: Base URL for Project Insight API (configure this!)

## Manual Configuration Required

### Project Insight API Base URL
You **must** update the `PROJECT_INSIGHT_BASE_URL` setting to point to your actual Project Insight instance:

```bash
# Update via Azure CLI
az functionapp config appsettings set \
  --name your-function-app \
  --resource-group rg-projectinsight-dev \
  --settings "PROJECT_INSIGHT_BASE_URL=https://yourdomain.projectinsight.net/api"
```

Or via Azure Portal:
1. Navigate to your Function App
2. Go to Configuration → Application settings
3. Edit `PROJECT_INSIGHT_BASE_URL`
4. Set value to: `https://yourdomain.projectinsight.net/api`
5. Save changes

## Optional Settings

### Performance Tuning
- `WEBSITE_RUN_FROM_PACKAGE`: Set to `1` for better cold start performance
- `WEBSITE_ENABLE_SYNC_UPDATE_SITE`: Set to `true` for synchronous updates

### Security Settings
- `WEBSITE_HTTPLOGGING_RETENTION_DAYS`: Log retention (default: 3)
- `WEBSITE_DISABLE_SCM_ENDPOINT`: Set to `true` to disable Kudu endpoint

### Monitoring
- `APPINSIGHTS_SAMPLING_PERCENTAGE`: Telemetry sampling (default: 100)
- `APPLICATIONINSIGHTS_ENABLE_AGENT`: Set to `true` for enhanced monitoring

## Environment Variables for Local Development

Create a `local.settings.json` file for local development:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "PROJECT_INSIGHT_BASE_URL": "https://yourdomain.projectinsight.net/api",
    "ASPNETCORE_ENVIRONMENT": "Development"
  },
  "Host": {
    "CORS": "*",
    "CORSCredentials": false
  }
}
```

## Security Notes

### API Token Handling
- ✅ **No API tokens stored in application settings**
- ✅ **Client-provided authentication** via MCP tool parameters
- ✅ **Dynamic token validation** per request
- ✅ **Multi-tenant support** with different tokens per client

### Managed Identity
The Function App uses system-assigned managed identity for:
- Azure Storage access
- Application Insights telemetry
- Other Azure service integrations

## CORS Configuration

CORS is configured in the Bicep template to allow all origins (`*`) for MCP client access. For production environments, consider restricting to specific domains:

```json
{
  "allowedOrigins": [
    "https://yourdomain.com",
    "https://app.yourdomain.com"
  ],
  "supportCredentials": false
}
```

## Health Check Configuration

The health endpoint supports optional authentication testing:
- `GET /api/health` - Basic health check
- `GET /api/health?api-token=your-token` - Health check with Project Insight API validation

## Troubleshooting Configuration

### Common Issues

1. **Function won't start**
   - Check `AzureWebJobsStorage` connection string
   - Verify storage account exists and is accessible

2. **Project Insight API calls fail**
   - Verify `PROJECT_INSIGHT_BASE_URL` is correct
   - Test API connectivity from Azure
   - Check client is providing valid API tokens

3. **Cold start performance**
   - Consider upgrading to Elastic Premium plan
   - Enable `WEBSITE_RUN_FROM_PACKAGE`
   - Configure pre-warmed instances

### Configuration Validation

Use this PowerShell script to validate configuration:

```powershell
# Validate Function App configuration
$functionAppName = "your-function-app"
$resourceGroup = "rg-projectinsight-dev"

# Get app settings
$settings = az functionapp config appsettings list `
  --name $functionAppName `
  --resource-group $resourceGroup `
  --output json | ConvertFrom-Json

# Check required settings
$requiredSettings = @(
  "AzureWebJobsStorage",
  "FUNCTIONS_EXTENSION_VERSION", 
  "FUNCTIONS_WORKER_RUNTIME",
  "PROJECT_INSIGHT_BASE_URL"
)

foreach ($setting in $requiredSettings) {
  $found = $settings | Where-Object { $_.name -eq $setting }
  if ($found) {
    Write-Host "✅ $setting is configured" -ForegroundColor Green
  } else {
    Write-Host "❌ $setting is missing" -ForegroundColor Red
  }
}
```