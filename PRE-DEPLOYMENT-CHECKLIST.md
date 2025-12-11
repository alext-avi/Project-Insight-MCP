# Pre-Deployment Checklist

## ✅ Files Ready for Deployment

- [x] `deploy-params.json` - Deployment configuration
- [x] `deploy.sh` - Automated deployment script
- [x] `azure-resources.json` - ARM template for infrastructure
- [x] `test-deployment.sh` - Post-deployment validation script
- [x] `DEPLOYMENT.md` - Complete deployment guide

## ✅ Project Status

- [x] .NET 8.0 project builds successfully
- [x] Azure Functions Core Tools v4 available
- [x] Azure CLI authenticated
- [x] All required dependencies installed (jq, func, dotnet)
- [x] Project Insight API credentials configured

## ✅ Deployment Configuration

Current settings in `deploy-params.json`:
- Resource Group: `rg-projectinsight-mcp`
- Function App: `func-projectinsight-mcp`
- Storage Account: `stprojectinsightmcp`
- Location: `East US`
- Project Insight API: `https://applied-visions.projectinsight.net/api/`

## 🚨 Before Running Deployment

1. **Review deploy-params.json**:
   - Ensure `functionAppName` is globally unique
   - Ensure `storageAccountName` is globally unique
   - Verify Project Insight credentials are correct

2. **Test locally first** (optional but recommended):
   ```bash
   func start
   curl "http://localhost:7071/api/health?validate-auth=true"
   ```

3. **Run deployment**:
   ```bash
   ./deploy.sh
   ```

4. **Test deployment**:
   ```bash
   ./test-deployment.sh https://func-projectinsight-mcp.azurewebsites.net
   ```

## ⚡ Quick Deployment Commands

```bash
# 1. Customize configuration (edit deploy-params.json as needed)
# 2. Deploy to Azure
./deploy.sh

# 3. Test the deployment
./test-deployment.sh https://your-function-app-name.azurewebsites.net
```

## 🎯 Expected Deployment Time

- Resource creation: ~5-10 minutes
- Code deployment: ~3-5 minutes
- Total time: ~10-15 minutes

## 📋 Post-Deployment

After successful deployment, you'll have:
- ✅ Azure Function App running .NET 8.0
- ✅ MCP server accessible via HTTPS
- ✅ Health monitoring endpoint
- ✅ Integrated with Project Insight API
- ✅ Ready for MCP client integration

The deployment will output the final URLs you can use for integration.