# Authentication Configuration Guide

## Overview

The Project Insight MCP Server supports multiple authentication strategies to ensure secure access to your Project Insight workspace.

## Configuration Options

### Option 1: Static API Token (Recommended for MVP)

**Best for**: Simple deployments, single-tenant scenarios, development/testing

**Configuration**:
```json
{
  "Values": {
    "ProjectInsight__BaseUrl": "https://yourdomain.projectinsight.net/api/",
    "ProjectInsight__ApiToken": "your-actual-api-token-here"
  }
}
```

**Features**:
- ✅ Simple setup
- ✅ Works with all MCP clients
- ✅ Authentication validation
- ✅ Enhanced error handling
- ✅ Token masking for security

### Option 2: Azure Key Vault (Production)

**Best for**: Production deployments, enhanced security

**Configuration**:
```json
{
  "Values": {
    "ProjectInsight__BaseUrl": "https://yourdomain.projectinsight.net/api/",
    "ProjectInsight__UseKeyVault": "true",
    "ProjectInsight__KeyVaultUrl": "https://your-keyvault.vault.azure.net/",
    "ProjectInsight__TokenSecretName": "project-insight-token"
  }
}
```

### Option 3: User-Specific Authentication

**Best for**: Multi-tenant scenarios, user-specific data access

**Implementation**: Would require MCP protocol extensions for user authentication

## Security Best Practices

### 1. Environment Variables
```bash
export ProjectInsight__ApiToken="your-token-here"
export ProjectInsight__BaseUrl="https://yourdomain.projectinsight.net/api/"
```

### 2. Azure Application Settings
```bash
az functionapp config appsettings set \
  --name your-function-app \
  --resource-group your-resource-group \
  --settings \
    "ProjectInsight__BaseUrl=https://yourdomain.projectinsight.net/api/" \
    "ProjectInsight__ApiToken=@Microsoft.KeyVault(SecretUri=https://your-vault.vault.azure.net/secrets/pi-token/)"
```

### 3. Key Vault Integration
```csharp
// Add to Program.cs for Key Vault support
builder.Services.AddHttpClient<ProjectInsightAuthService>();

// Configure Key Vault
builder.Services.AddAzureKeyVault();
```

## Authentication Validation

### Health Check with Auth Validation
```bash
# Basic health check
curl http://localhost:7071/api/health

# Health check with authentication validation
curl "http://localhost:7071/api/health?validate-auth=true"
```

### Response Example
```json
{
  "status": "healthy",
  "timestamp": "2025-11-24T17:30:00.000Z",
  "version": "1.0.0",
  "service": "ProjectInsight MCP Server",
  "authentication": {
    "configured": true,
    "tokenMasked": "abc1...xyz9",
    "validated": true,
    "error": null
  }
}
```

## Error Handling

The authentication service provides detailed error information:

### Authentication Errors
- **UNAUTHORIZED**: Invalid or expired token
- **FORBIDDEN**: Insufficient permissions
- **VALIDATION_FAILED**: Unable to validate with any endpoint
- **VALIDATION_ERROR**: Technical error during validation

### Error Response Example
```json
{
  "success": false,
  "message": "Authentication failed: Invalid or expired API token",
  "errors": ["Check your ProjectInsight API token configuration"]
}
```

## Token Management

### Token Requirements
- Must be a valid Project Insight API token
- Should have read/write permissions for projects and tasks
- Token should not expire during operation

### Token Validation
The server validates tokens by attempting API calls to:
1. `/api/user/current` - User info endpoint
2. `/api/projects?$top=1` - Projects list
3. `/api/project/search?$top=1` - Project search

### Token Security
- Tokens are masked in logs: `abc1...xyz9`
- Never logged in plain text
- Validated on startup and optionally in health checks

## Troubleshooting

### Common Issues

1. **Invalid Token Format**
   - Error: `Valid ProjectInsight API token is required`
   - Solution: Ensure token is not placeholder value

2. **Authentication Failed**
   - Error: `Authentication failed: Invalid or expired API token`
   - Solution: Verify token is correct and not expired

3. **Insufficient Permissions**
   - Error: `Access forbidden: Insufficient permissions`
   - Solution: Ensure token has project management permissions

4. **Invalid Base URL**
   - Error: `Valid ProjectInsight BaseUrl is required`
   - Solution: Ensure URL format is correct and ends with `/api/`

### Debug Steps

1. **Check Configuration**:
   ```bash
   curl "http://localhost:7071/api/health?validate-auth=true"
   ```

2. **Verify Token Manually**:
   ```bash
   curl -H "Authorization: Bearer your-token" \
        "https://yourdomain.projectinsight.net/api/user/current"
   ```

3. **Check Function Logs**:
   ```bash
   func start --verbose
   ```

## Migration Guide

### From Basic to Enhanced Auth

1. Add the new `ProjectInsightAuthService` dependency
2. Update Function constructor to include auth service
3. Configure environment variables properly
4. Test with health check validation

### To Key Vault

1. Create Azure Key Vault
2. Store API token in Key Vault
3. Configure managed identity for Function App
4. Update application settings to reference Key Vault

## Future Enhancements

### Planned Features
- ✅ Token refresh support
- ✅ Multiple authentication providers
- ✅ User-specific authentication
- ✅ OAuth integration
- ✅ Certificate-based authentication