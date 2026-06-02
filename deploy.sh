#!/bin/bash

# Project Insight MCP Server - Local Deployment Script
# This script deploys the infrastructure and function app locally

set -e

# Configuration
ENVIRONMENT="${1:-dev}"
RESOURCE_GROUP="ProjectInsight-MCP"  # Dedicated resource group
LOCATION="centralus"   # Match existing deployment location

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Helper functions
log_info() {
    echo -e "${BLUE}ℹ️  $1${NC}"
}

log_success() {
    echo -e "${GREEN}✅ $1${NC}"
}

log_warning() {
    echo -e "${YELLOW}⚠️  $1${NC}"
}

log_error() {
    echo -e "${RED}❌ $1${NC}"
}

# Validate environment parameter
if [[ ! "$ENVIRONMENT" =~ ^(dev|staging|prod)$ ]]; then
    log_error "Invalid environment. Use: dev, staging, or prod"
    echo "Usage: $0 [dev|staging|prod]"
    exit 1
fi

# Set environment-specific variables
RESOURCE_GROUP="ProjectInsight-MCP"  # Dedicated resource group

log_info "Starting deployment update for environment: $ENVIRONMENT"
log_info "Resource Group: $RESOURCE_GROUP"
log_info "Location: $LOCATION"

# Check if Azure CLI is installed and user is logged in
if ! command -v az &> /dev/null; then
    log_error "Azure CLI is not installed. Please install it first."
    exit 1
fi

if ! az account show &> /dev/null; then
    log_error "You are not logged into Azure. Please run 'az login' first."
    exit 1
fi

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    log_error ".NET SDK is not installed. Please install .NET 8.0 SDK."
    exit 1
fi

# Check if Azure Functions Core Tools is installed
if ! command -v func &> /dev/null; then
    log_warning "Azure Functions Core Tools not found. Function deployment will be skipped."
    SKIP_FUNCTION_DEPLOY=true
fi

log_info "Prerequisites check completed"

# Resource group already exists, skip creation step
log_info "Using existing resource group: $RESOURCE_GROUP"

# Validate Bicep template
log_info "Validating Bicep template..."
az bicep build --file bicep/main.bicep
log_success "Bicep template validation completed"

# Deploy infrastructure
log_info "Updating existing infrastructure..."
DEPLOYMENT_OUTPUT=$(az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --template-file bicep/main.bicep \
    --parameters "@bicep/parameters/${ENVIRONMENT}.parameters.json" \
    --output json)

if [[ $? -eq 0 ]]; then
    log_success "Infrastructure update completed"
    
    # Extract outputs
    FUNCTION_APP_NAME=$(echo "$DEPLOYMENT_OUTPUT" | jq -r '.properties.outputs.functionAppName.value')
    FUNCTION_APP_URL=$(echo "$DEPLOYMENT_OUTPUT" | jq -r '.properties.outputs.functionAppUrl.value')
    
    log_info "Function App Name: $FUNCTION_APP_NAME"
    log_info "Function App URL: $FUNCTION_APP_URL"
else
    log_error "Infrastructure update failed"
    exit 1
fi

# Build and deploy function app
if [[ "$SKIP_FUNCTION_DEPLOY" != "true" ]]; then
    log_info "Building .NET application..."
    
    # Navigate to the function app directory
    PROJECT_DIR="servers/project-insight/src"
    cd "$PROJECT_DIR" || {
        log_error "Failed to navigate to project directory: $PROJECT_DIR"
        exit 1
    }
    
    # Restore dependencies
    dotnet restore
    
    # Build application
    dotnet build --configuration Release --no-restore
    
    # Run tests
    log_info "Running tests..."
    dotnet test --configuration Release --no-build --verbosity normal
    
    # Publish application
    log_info "Publishing application..."
    dotnet publish --configuration Release --output ./output --no-build
    
    log_success "Build completed"
    
    # Deploy function app
    log_info "Deploying function app..."
    func azure functionapp publish "$FUNCTION_APP_NAME" --force
    
    log_success "Function app code updated successfully"
    
    # Return to original directory
    cd - > /dev/null
    
    # Wait for function app to be ready
    log_info "Waiting for function app to be ready..."
    sleep 30
    
    # Test health endpoint
    log_info "Testing health endpoint..."
    HEALTH_URL="${FUNCTION_APP_URL}/api/health"
    
    for i in {1..10}; do
        if curl -f -s "$HEALTH_URL" > /dev/null 2>&1; then
            log_success "Health check passed"
            break
        else
            log_warning "Health check failed, retrying in 10 seconds... (attempt $i/10)"
            sleep 10
        fi
        
        if [[ $i -eq 10 ]]; then
            log_error "Health check failed after 10 attempts"
            exit 1
        fi
    done
else
    log_warning "Function deployment skipped (Azure Functions Core Tools not found)"
fi

# Display summary
echo
log_success "🚀 Deployment Update Summary"
echo "======================="
echo "Environment: $ENVIRONMENT"
echo "Resource Group: $RESOURCE_GROUP"
echo "Function App: $FUNCTION_APP_NAME"
echo "URL: $FUNCTION_APP_URL"
echo "Health Check: $HEALTH_URL"
echo

log_info "Existing deployment has been updated successfully!"

log_info "To test the MCP server:"
echo "1. Health check: curl $HEALTH_URL"
echo "2. Health check with auth test: curl '$HEALTH_URL?api-token=your-project-insight-token'"
echo "3. Configure your MCP client to use: $FUNCTION_APP_URL"

# Clean up build artifacts
if [[ -d "./servers/project-insight/src/output" ]]; then
    rm -rf ./servers/project-insight/src/output
    log_info "Cleaned up build artifacts"
fi

log_success "Deployment update completed successfully! 🎉"