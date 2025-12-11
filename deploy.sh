#!/bin/bash

# Project Insight MCP Server Deployment Script
# This script deploys the MCP server to Azure Functions

set -e  # Exit on any error

echo "🚀 Starting Project Insight MCP Server Deployment..."

# Load configuration
if [ ! -f "deploy-params.json" ]; then
    echo "❌ Error: deploy-params.json not found!"
    echo "Please create deploy-params.json with your deployment parameters."
    exit 1
fi

# Extract parameters
RESOURCE_GROUP=$(jq -r '.resourceGroupName' deploy-params.json)
LOCATION=$(jq -r '.location' deploy-params.json)
FUNCTION_APP_NAME=$(jq -r '.functionAppName' deploy-params.json)
STORAGE_ACCOUNT=$(jq -r '.storageAccountName' deploy-params.json)
APP_SERVICE_PLAN=$(jq -r '.appServicePlanName' deploy-params.json)
PROJECT_INSIGHT_BASE_URL=$(jq -r '.projectInsightBaseUrl' deploy-params.json)
PROJECT_INSIGHT_API_TOKEN=$(jq -r '.projectInsightApiToken' deploy-params.json)

echo "📋 Deployment Configuration:"
echo "   Resource Group: $RESOURCE_GROUP"
echo "   Location: $LOCATION"
echo "   Function App: $FUNCTION_APP_NAME"
echo "   Storage Account: $STORAGE_ACCOUNT"
echo "   App Service Plan: $APP_SERVICE_PLAN"
echo ""

# Step 1: Create Resource Group
echo "📦 Creating Resource Group..."
az group create \
    --name "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --output table

# Step 2: Create Storage Account
echo "💾 Creating Storage Account..."
az storage account create \
    --name "$STORAGE_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku Standard_LRS \
    --output table

# Step 3: Create App Service Plan
echo "📋 Creating App Service Plan..."
az appservice plan create \
    --name "$APP_SERVICE_PLAN" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku Y1 \
    --is-linux \
    --output table

# Step 4: Create Function App
echo "⚡ Creating Function App..."
az functionapp create \
    --name "$FUNCTION_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --plan "$APP_SERVICE_PLAN" \
    --storage-account "$STORAGE_ACCOUNT" \
    --runtime dotnet \
    --runtime-version 8 \
    --functions-version 4 \
    --os-type Linux \
    --output table

# Step 5: Configure Application Settings
echo "🔧 Configuring Application Settings..."
az functionapp config appsettings set \
    --name "$FUNCTION_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --settings \
    "ProjectInsight__BaseUrl=$PROJECT_INSIGHT_BASE_URL" \
    "ProjectInsight__ApiToken=$PROJECT_INSIGHT_API_TOKEN" \
    "FUNCTIONS_WORKER_RUNTIME=dotnet" \
    "FUNCTIONS_INPROC_NET8_ENABLED=1" \
    --output table

# Step 6: Build and Deploy
echo "🔨 Building Project..."
dotnet build --configuration Release

echo "📤 Deploying to Azure Functions..."
func azure functionapp publish "$FUNCTION_APP_NAME" --build-native-deps

# Step 7: Test Deployment
echo "🧪 Testing Deployment..."
FUNCTION_URL="https://$FUNCTION_APP_NAME.azurewebsites.net"

echo "Waiting for function app to be ready..."
sleep 30

echo "Testing health endpoint..."
curl -f "$FUNCTION_URL/api/health" || echo "❌ Health check failed"

echo ""
echo "✅ Deployment Complete!"
echo ""
echo "🌐 Function App URL: $FUNCTION_URL"
echo "🔍 Health Endpoint: $FUNCTION_URL/api/health"
echo "🛠️ MCP Endpoint: $FUNCTION_URL/api/mcp"
echo ""
echo "📋 Next Steps:"
echo "1. Test the health endpoint: curl '$FUNCTION_URL/api/health?validate-auth=true'"
echo "2. Test MCP functionality: curl -X POST '$FUNCTION_URL/api/mcp' -H 'Content-Type: application/json' -d '{\"method\": \"tools/list\"}'"
echo "3. Configure your MCP client to use: $FUNCTION_URL/api/mcp"
echo ""