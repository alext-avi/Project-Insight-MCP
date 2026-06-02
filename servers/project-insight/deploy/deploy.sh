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
FUNCTION_APP=$(jq -r '.functionAppName' deploy-params.json)
STORAGE_ACCOUNT=$(jq -r '.storageAccountName' deploy-params.json)
RUNTIME=$(jq -r '.runtime' deploy-params.json)
RUNTIME_VERSION=$(jq -r '.runtimeVersion' deploy-params.json)
PROJECT_INSIGHT_BASE_URL=$(jq -r '.projectInsightBaseUrl' deploy-params.json)

echo "📋 Deployment Configuration:"
echo "   Resource Group: $RESOURCE_GROUP"
echo "   Location: $LOCATION"
echo "   Function App: $FUNCTION_APP"
echo "   Storage Account: $STORAGE_ACCOUNT"
echo "   Runtime: $RUNTIME $RUNTIME_VERSION"
echo "   Mode: Consumption Plan (Serverless - No Monthly Cost)"
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

# Step 3: Create Function App (Consumption Plan)
echo "⚡ Creating Function App..."
az functionapp create \
    --name "$FUNCTION_APP" \
    --resource-group "$RESOURCE_GROUP" \
    --storage-account "$STORAGE_ACCOUNT" \
    --consumption-plan-location "$LOCATION" \
    --runtime "$RUNTIME" \
    --runtime-version "$RUNTIME_VERSION" \
    --functions-version 4 \
    --output table

# Step 4: Configure Application Settings
echo "🔧 Configuring Application Settings..."
az functionapp config appsettings set \
    --name "$FUNCTION_APP" \
    --resource-group "$RESOURCE_GROUP" \
    --settings \
    "ProjectInsight__BaseUrl=$PROJECT_INSIGHT_BASE_URL" \
    "FUNCTIONS_WORKER_RUNTIME=dotnet" \
    "FUNCTIONS_INPROC_NET8_ENABLED=1" \
    --output table

# Step 6: Build and Deploy
echo "🔨 Building Project..."
cd ../src
dotnet build --configuration Release

echo "📤 Deploying to Azure Functions..."
func azure functionapp publish "$FUNCTION_APP" --build-native-deps

cd ../deploy

# Step 5: Test Deployment
echo "🧪 Testing Deployment..."
FUNCTION_URL="https://$FUNCTION_APP.azurewebsites.net"

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
echo "1. Test the health endpoint: curl '$FUNCTION_URL/api/health'"
echo "2. Test auth validation (provide your API token): curl '$FUNCTION_URL/api/health?validate-auth=true&api-token=your-token'"
echo "3. Test MCP tools list: curl -X POST '$FUNCTION_URL/api/mcp' -H 'Content-Type: application/json' -d '{\"method\": \"tools/list\"}'"
echo "4. Configure your MCP client with API token in tool arguments"
echo ""
echo "🔐 Authentication:"
echo "   API tokens are now provided by the MCP client in tool arguments"
echo "   No server-side API token configuration required"
echo ""