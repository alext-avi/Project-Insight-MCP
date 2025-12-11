# Project Insight MCP Server - Azure Deployment Guide

## 🚀 Quick Deployment

This guide will help you deploy the Project Insight MCP server to Azure Functions using the provided automation scripts.

### Prerequisites

1. **Azure CLI** installed and configured
2. **Azure Functions Core Tools v4** installed
3. **.NET 8.0 SDK** installed
4. **jq** (JSON processor) - Install with `brew install jq` on macOS

### Verify Prerequisites

```bash
# Check Azure CLI
az --version

# Check Azure login
az account show

# Check Functions Core Tools
func --version

# Check .NET SDK
dotnet --version

# Check jq
jq --version
```

## 🛠️ Deployment Steps

### 1. Configure Deployment Parameters

Edit `deploy-params.json` to customize your deployment:

```json
{
  "resourceGroupName": "rg-projectinsight-mcp",
  "location": "East US",
  "functionAppName": "func-projectinsight-mcp-[your-suffix]",
  "storageAccountName": "st[yourname]mcp",
  "appServicePlanName": "asp-projectinsight-mcp",
  "projectInsightBaseUrl": "https://your-domain.projectinsight.net/api/",
  "projectInsightApiToken": "your-api-token-here"
}
```

**Important Notes:**
- `functionAppName` must be globally unique across Azure
- `storageAccountName` must be globally unique and lowercase alphanumeric only
- Update `projectInsightBaseUrl` and `projectInsightApiToken` with your actual values

### 2. Run Deployment Script

```bash
# Make sure you're in the project directory
cd /Users/alext/Projects/ProjectInsight

# Run the deployment
./deploy.sh
```

The script will:
1. ✅ Create Azure Resource Group
2. ✅ Create Storage Account
3. ✅ Create App Service Plan (Consumption tier)
4. ✅ Create Function App
5. ✅ Configure Application Settings
6. ✅ Build and Deploy Code
7. ✅ Test Deployment

### 3. Verify Deployment

After deployment, test your endpoints:

```bash
# Replace with your actual function app name
FUNCTION_URL="https://func-projectinsight-mcp.azurewebsites.net"

# Test health endpoint
curl "$FUNCTION_URL/api/health"

# Test with authentication validation
curl "$FUNCTION_URL/api/health?validate-auth=true"

# Test MCP tools list
curl -X POST "$FUNCTION_URL/api/mcp" \
  -H "Content-Type: application/json" \
  -d '{"method": "tools/list"}'

# Test project search
curl -X POST "$FUNCTION_URL/api/mcp" \
  -H "Content-Type: application/json" \
  -d '{"method": "tools/call", "params": {"name": "search_projects", "arguments": {}}}'
```

## 🔐 Security Configuration

### Environment Variables Set by Deployment

The deployment script automatically configures these application settings:

- `ProjectInsight__BaseUrl`: Your Project Insight API base URL
- `ProjectInsight__ApiToken`: Your Project Insight API token
- `FUNCTIONS_WORKER_RUNTIME`: Set to "dotnet"
- `FUNCTIONS_INPROC_NET8_ENABLED`: Set to "1"

### Post-Deployment Security (Recommended)

1. **Enable Application Insights** for monitoring
2. **Configure CORS** if needed for specific origins
3. **Set up custom domain** with SSL certificate
4. **Enable Azure Key Vault** for sensitive configuration
5. **Configure authentication** if required

## 📊 Monitoring and Troubleshooting

### View Logs

```bash
# Stream live logs
az webapp log tail --name func-projectinsight-mcp --resource-group rg-projectinsight-mcp

# Download log files
az webapp log download --name func-projectinsight-mcp --resource-group rg-projectinsight-mcp
```

### Check Application Insights

1. Go to Azure Portal
2. Navigate to your Function App
3. Click "Application Insights" in the left menu
4. View live metrics, failures, and performance data

### Common Issues

1. **Function App Name Not Available**
   - Solution: Change `functionAppName` in `deploy-params.json` to something unique

2. **Storage Account Name Not Available**
   - Solution: Change `storageAccountName` to something unique (lowercase, alphanumeric only)

3. **API Authentication Errors**
   - Solution: Verify `projectInsightApiToken` in `deploy-params.json`
   - Test token with: `curl "https://your-domain.projectinsight.net/api/Projects/Search" -H "Authorization: Bearer YOUR_TOKEN"`

4. **Deployment Timeout**
   - Solution: Run `func azure functionapp publish [function-app-name]` manually

## 🔄 Updating Deployment

### Code Updates

```bash
# Build and redeploy code changes
dotnet build --configuration Release
func azure functionapp publish func-projectinsight-mcp
```

### Configuration Updates

```bash
# Update application settings
az functionapp config appsettings set \
  --name func-projectinsight-mcp \
  --resource-group rg-projectinsight-mcp \
  --settings "ProjectInsight__ApiToken=new-token-here"
```

## 🧹 Cleanup

To remove all deployed resources:

```bash
# Delete the entire resource group (removes all resources)
az group delete --name rg-projectinsight-mcp --yes --no-wait
```

## 📋 Deployment Checklist

- [ ] Azure CLI installed and authenticated
- [ ] Prerequisites verified (func, dotnet, jq)
- [ ] `deploy-params.json` configured with unique names
- [ ] Project Insight API token is valid
- [ ] Deployment script executed successfully
- [ ] Health endpoint returns 200 OK
- [ ] MCP tools/list returns available tools
- [ ] Project search returns actual project data
- [ ] Application Insights configured (optional)
- [ ] Custom domain configured (optional)

## 🆘 Support

If you encounter issues:

1. Check the deployment logs in the terminal
2. Verify all prerequisites are met
3. Test API connectivity locally first
4. Review Azure Function logs in the portal
5. Consult the `agents.md` file for troubleshooting guidance

---

**Last Updated**: November 25, 2025  
**Deployment Status**: Ready for Production