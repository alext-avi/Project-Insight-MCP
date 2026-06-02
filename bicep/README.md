# Project Insight MCP Server - Infrastructure as Code

This directory contains Bicep templates and GitHub Actions workflows for automated deployment of the Project Insight MCP Server to Azure.

## Architecture Overview

The infrastructure includes:
- **Azure Function App** (Consumption or Elastic Premium plan)
- **Storage Account** (for Function App runtime and content)
- **Application Insights** (telemetry and monitoring)
- **Log Analytics Workspace** (centralized logging)
- **System-assigned Managed Identity** (secure Azure service access)

## Security Features

### Client-Provided Authentication
- ✅ **No hardcoded API keys** in server configuration
- ✅ **Dynamic token validation** per request
- ✅ **Secure MCP tool parameters** requiring `apiToken` from clients
- ✅ **Flexible multi-tenant support** with client-side authentication

### Azure Security
- HTTPS-only traffic enforcement
- TLS 1.2 minimum version
- Disabled FTP/FTPS access
- System-assigned managed identity for Azure service access
- Storage account with blob contributor role assignment

## Directory Structure

```
bicep/
├── main.bicep                     # Main Bicep template
├── parameters/
│   ├── dev.parameters.json        # Development environment parameters
│   ├── staging.parameters.json    # Staging environment parameters
│   └── prod.parameters.json       # Production environment parameters
└── README.md                      # This file
```

## Environments

| Environment | Branch Trigger | App Service Plan | Manual Deploy |
|------------|---------------|------------------|---------------|
| **dev**     | `develop`     | Y1 (Consumption) | ✅ |
| **staging** | `main`        | EP1 (Elastic Premium) | ✅ |
| **prod**    | Manual only   | EP2 (Elastic Premium) | ✅ |

## Prerequisites

### Azure Setup
1. **Azure Subscription** with appropriate permissions
2. **Resource Groups** for each environment:
   - `rg-projectinsight-dev`
   - `rg-projectinsight-staging`
   - `rg-projectinsight-prod`

### Service Principal
Create a service principal with the following permissions:
```bash
az ad sp create-for-rbac --name "ProjectInsight-GitHub-Actions" \
  --role contributor \
  --scopes /subscriptions/{subscription-id} \
  --sdk-auth
```

### GitHub Secrets and Variables

#### Repository Secrets
- `AZURE_CREDENTIALS`: Service principal JSON output from above command

#### Repository Variables
- `AZURE_RESOURCE_GROUP_DEV`: Name of development resource group
- `AZURE_RESOURCE_GROUP_STAGING`: Name of staging resource group  
- `AZURE_RESOURCE_GROUP_PROD`: Name of production resource group

## Deployment

### Automated Deployment (Recommended)

#### Development Environment
- **Trigger**: Push to `develop` branch
- **Manual**: Use workflow dispatch with `dev` environment

#### Staging Environment  
- **Trigger**: Push to `main` branch
- **Manual**: Use workflow dispatch with `staging` environment

#### Production Environment
- **Trigger**: Manual workflow dispatch only with `prod` environment
- **Approval**: Requires manual approval in GitHub environments

### Manual Deployment

#### 1. Login to Azure
```bash
az login
az account set --subscription "your-subscription-id"
```

#### 2. Create Resource Group (if needed)
```bash
az group create --name rg-projectinsight-dev --location "East US"
```

#### 3. Deploy Infrastructure
```bash
# Development
az deployment group create \
  --resource-group rg-projectinsight-dev \
  --template-file bicep/main.bicep \
  --parameters @bicep/parameters/dev.parameters.json

# Staging
az deployment group create \
  --resource-group rg-projectinsight-staging \
  --template-file bicep/main.bicep \
  --parameters @bicep/parameters/staging.parameters.json

# Production
az deployment group create \
  --resource-group rg-projectinsight-prod \
  --template-file bicep/main.bicep \
  --parameters @bicep/parameters/prod.parameters.json
```

#### 4. Deploy Function App Code
```bash
# Build and publish
dotnet publish --configuration Release --output ./output

# Deploy to Azure (get function app name from deployment output)
func azure functionapp publish your-function-app
```

## Configuration

### Template Parameters

| Parameter | Description | Type | Default |
|-----------|-------------|------|---------|
| `resourcePrefix` | Prefix for all resources | string | `func-projectinsight` |
| `environment` | Environment name | string | `dev` |
| `location` | Azure region | string | Resource group location |
| `runtime` | Function runtime | string | `dotnet-isolated` |
| `runtimeVersion` | Runtime version | string | `8.0` |
| `appServicePlanSku` | App Service Plan SKU | string | `Y1` |
| `enableApplicationInsights` | Enable App Insights | bool | `true` |
| `tags` | Resource tags | object | Environment-specific |

### Environment-Specific Settings

#### Development (dev.parameters.json)
- **App Service Plan**: Y1 (Consumption)
- **Cost**: Minimal, pay-per-execution
- **Scaling**: Automatic based on demand
- **Use Case**: Development and testing

#### Staging (staging.parameters.json)
- **App Service Plan**: EP1 (Elastic Premium)
- **Cost**: Always-on premium tier
- **Scaling**: Pre-warmed instances available
- **Use Case**: Integration testing and pre-production validation

#### Production (prod.parameters.json)
- **App Service Plan**: EP2 (Elastic Premium)
- **Cost**: Higher tier for production workloads
- **Scaling**: Enhanced performance and capacity
- **Use Case**: Production workloads

## Monitoring and Observability

### Application Insights
- **Connection String**: Automatically configured via app settings
- **Telemetry**: Function execution metrics, dependencies, exceptions
- **Custom Metrics**: MCP server performance and usage analytics

### Log Analytics
- **Retention**: 30 days (configurable)
- **Integration**: Linked to Application Insights
- **Queries**: KQL-based log analysis and alerting

### Health Monitoring
- **Health Endpoint**: `GET /api/health`
- **Authentication Test**: Optional API token validation via query parameter
- **CI/CD Integration**: Automated health checks in deployment pipeline

## Security Considerations

### Authentication Architecture
The MCP server uses a **client-provided authentication model**:

1. **No Server-Side Secrets**: API tokens are never stored in Azure configuration
2. **Dynamic Validation**: Each tool call includes `apiToken` parameter
3. **Per-Request Authentication**: Tokens validated on each Project Insight API call
4. **Multi-Tenant Ready**: Different clients can use different API tokens

### Example MCP Tool Call
```json
{
  "method": "tools/call",
  "params": {
    "name": "search_projects",
    "arguments": {
      "apiToken": "client-provided-token-here",
      "isActive": true
    }
  }
}
```

### Azure Security Features
- **Managed Identity**: System-assigned identity for Azure service access
- **Storage Access**: Blob contributor role for function storage needs
- **Network Security**: HTTPS enforcement, TLS 1.2 minimum
- **Access Control**: Principle of least privilege for all services

## Troubleshooting

### Common Issues

#### 1. Deployment Fails - Resource Naming
**Error**: Resource name conflicts or invalid characters
**Solution**: Check `resourcePrefix` parameter, ensure unique names

#### 2. Function App Won't Start
**Error**: Application logs show startup failures
**Solution**: 
- Verify storage account connection string
- Check Application Insights configuration
- Review function app settings in Azure portal

#### 3. Authentication Errors
**Error**: 401 Unauthorized from Project Insight API
**Solution**:
- Verify client is providing valid `apiToken` in tool calls
- Test authentication with Project Insight API directly
- Check `PROJECT_INSIGHT_BASE_URL` app setting

#### 4. GitHub Actions Deployment Fails
**Error**: Azure CLI authentication or permission issues
**Solution**:
- Verify `AZURE_CREDENTIALS` secret is correctly configured
- Ensure service principal has contributor role
- Check resource group exists and is accessible

### Diagnostic Commands

```bash
# Check deployment status
az deployment group show \
  --resource-group rg-projectinsight-dev \
  --name main

# View function app logs
az functionapp log tail \
  --name your-function-app \
  --resource-group rg-projectinsight-dev

# Test function app endpoint
curl https://your-function-app.azurewebsites.net/api/health

# Check application insights
az monitor app-insights component show \
  --app func-projectinsight-insights-dev \
  --resource-group rg-projectinsight-dev
```

## Cost Optimization

### Development
- Uses **Consumption Plan** (Y1) - pay only for execution time
- **No fixed costs** - ideal for development and testing
- **Auto-scaling** based on demand

### Staging/Production  
- Uses **Elastic Premium** plans for consistent performance
- **Pre-warmed instances** eliminate cold start delays
- **Reserved capacity** for predictable workloads

### Monitoring Costs
- **Log Analytics**: 30-day retention (adjustable)
- **Application Insights**: Sampling can be configured to reduce ingestion costs
- **Storage**: Standard LRS for cost-effective function storage

## Next Steps

1. **Set up GitHub environments** with appropriate approval workflows
2. **Configure monitoring alerts** in Application Insights
3. **Implement blue-green deployments** for production updates
4. **Add infrastructure tests** to validate deployments
5. **Set up cost budgets** and alerts in Azure

## Support

For questions or issues:
1. Check the [troubleshooting section](#troubleshooting) above
2. Review Azure Function App logs in the portal
3. Check Application Insights for detailed telemetry
4. Review GitHub Actions workflow logs for deployment issues