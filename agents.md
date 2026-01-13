# Project Insight MCP Server - Agent Knowledge Base

## Overview

This is a **Model Context Protocol (MCP) server** that integrates with the **Project Insight API** to provide AI agents with project management capabilities. The server is built using **.NET 8.0** and **Azure Functions** for cloud deployment and local development.

## 🏗️ Architecture

### Core Components

1. **Azure Functions Layer** (`Functions/McpFunction.cs`)
   - HTTP triggered functions for MCP protocol handling
   - Health monitoring with API validation
   - Entry point for all MCP requests

2. **MCP Protocol Layer** (`MCP/McpServerService.cs`)
   - Handles JSON-RPC 2.0 protocol implementation
   - Tool discovery and execution
   - Request/response parsing

3. **API Service Layer** (`Services/ProjectInsightApiService.cs`)
   - Project Insight API integration
   - Authentication handling
   - Data transformation

4. **Models** (`Models/`)
   - API request/response DTOs
   - MCP protocol models
   - Project Insight domain models

## 🛠️ Available MCP Tools

### `search_projects`
- **Purpose**: Retrieves all projects from Project Insight
- **Parameters**: None (returns all projects)
- **Response**: Array of project objects with comprehensive metadata
- **Fields Returned**: ID, Name, Status, Manager, Dates, Budget, etc.

## 🚀 Getting Started

### Prerequisites
- .NET 8.0 SDK
- Azure Functions Core Tools v4
- Project Insight API credentials
- jq (for deployment scripts)

### Local Development

1. **Configure Authentication**:
   ```json
   // local.settings.json
   {
     "IsEncrypted": false,
     "Values": {
       "AzureWebJobsStorage": "UseDevelopmentStorage=true",
       "FUNCTIONS_WORKER_RUNTIME": "dotnet",
       "FUNCTIONS_INPROC_NET8_ENABLED": "1",
       "ProjectInsight__BaseUrl": "https://yourdomain.projectinsight.net/api/",
       "ProjectInsight__ApiToken": "your-api-token-here"
     }
   }
   ```

2. **Build and Run**:
   ```bash
   # Build the project
   dotnet build ProjectInsightMCP.csproj
   
   # Start Azure Functions
   func start --verbose
   ```

3. **Test Health Endpoint**:
   ```bash
   # Basic health check
   curl "http://localhost:7071/api/health"
   
   # With authentication validation
   curl "http://localhost:7071/api/health?validate-auth=true"
   ```

4. **Test MCP Protocol**:
   ```bash
   # Initialize
   curl -X POST http://localhost:7071/api/mcp \
     -H "Content-Type: application/json" \
     -d '{
       "jsonrpc": "2.0",
       "id": 1,
       "method": "initialize",
       "params": {
         "protocolVersion": "2024-11-05",
         "capabilities": {},
         "clientInfo": {"name": "test-client", "version": "1.0.0"}
       }
     }'
   
   # List tools
   curl -X POST http://localhost:7071/api/mcp \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc": "2.0", "id": 2, "method": "tools/list"}'
   
   # Call a tool
   curl -X POST http://localhost:7071/api/mcp \
     -H "Content-Type: application/json" \
     -d '{
       "jsonrpc": "2.0",
       "id": 3,
       "method": "tools/call",
       "params": {
         "name": "search_projects",
         "arguments": {}
       }
     }'
   ```

## 🔧 Key Implementation Details

### API Service Configuration

The `ProjectInsightApiService` uses **POST method** (not GET) for project searches:

```csharp
// Correct implementation - uses POST with JSON body
var requestBody = new
{
    modelProperties = GetDefaultModelProperties(),
    // Additional filtering can be added here
};

var response = await _httpClient.PostAsJsonAsync(
    "Projects/Search", 
    requestBody, 
    cancellationToken
);
```

### Model Properties Selection

Project Insight API requires explicit field selection via `modelProperties`. We use a comprehensive default set:

```csharp
private static string[] GetDefaultModelProperties() => new[]
{
    "ProjectId", "ProjectName", "ProjectStatus", "ProjectManager",
    "StartDate", "EndDate", "Budget", "ActualCost", "Description",
    "Priority", "ClientName", "CreatedDate", "ModifiedDate",
    "PercentComplete", "ProjectType", "ProjectCode"
};
```

### Error Handling Strategy

1. **API Level**: HTTP status validation, JSON parsing errors
2. **Service Level**: Business logic validation, data transformation
3. **Function Level**: Request validation, response formatting
4. **Logging**: Comprehensive logging at all levels using ILogger

## 🔍 Troubleshooting

### Common Issues

1. **"Method not allowed" errors**:
   - Ensure using POST method for Project Insight API calls
   - Verify JSON body structure includes `modelProperties`

2. **Authentication failures**:
   - Check API token in `local.settings.json`
   - Validate token has project read permissions
   - Test with health endpoint: `?validate-auth=true`

3. **Function startup issues**:
   - Ensure .NET 8.0 runtime is installed
   - Check Azure Functions Core Tools version (v4 required)
   - Build project first: `dotnet build`

4. **Empty project results**:
   - Verify API token permissions
   - Check Project Insight instance accessibility
   - Review function logs for API errors

### Debugging Commands

```bash
# Check function logs
cat function.log

# Test API connectivity
curl "http://localhost:7071/api/health?test-api=true&validate-auth=true"

# Validate MCP protocol
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"method": "tools/list"}'

# Check running processes
lsof -i :7071
```

## 📁 File Structure

```
ProjectInsight/
├── Functions/
│   └── McpFunction.cs           # Azure Function entry points
├── MCP/
│   ├── McpServerService.cs      # Core MCP protocol handling
│   └── Models/                  # MCP protocol models
├── Services/
│   ├── ProjectInsightApiService.cs  # API integration
│   └── AuthenticationService.cs     # Auth handling
├── Models/
│   ├── ProjectInsight/          # API models
│   └── MCP/                     # MCP protocol models
├── local.settings.json          # Local configuration
├── host.json                    # Azure Functions config
└── Program.cs                   # DI configuration
```

## 🔐 Security and Authentication

### Authentication Configuration

**Static API Token (Recommended for getting started)**:
```json
{
  "Values": {
    "ProjectInsight__BaseUrl": "https://yourdomain.projectinsight.net/api/",
    "ProjectInsight__ApiToken": "your-actual-api-token-here"
  }
}
```

**Azure Key Vault (Production)**:
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

### Security Best Practices

1. **API Credentials**: Store in Azure Key Vault or environment variables for production
2. **HTTPS**: Always use HTTPS in production environments
3. **Token Validation**: 
   - Tokens are validated on startup and optionally in health checks
   - Tokens are masked in logs: `abc1...xyz9`
   - Never logged in plain text
4. **CORS**: Configure appropriately for client access (currently set to allow all origins)
5. **Input Validation**: All MCP inputs are validated against schemas
6. **Authorization**: Function uses Anonymous authorization level for MCP compatibility

### Token Requirements

- Must be a valid Project Insight API token
- Should have read/write permissions for projects and tasks
- Token should not expire during operation
- Validated by attempting calls to `/api/user/current`, `/api/projects`, or `/api/project/search`

## 🚀 Deployment

### Azure Functions Deployment

1. **Prerequisites**:
   - Azure CLI authenticated: `az login`
   - .NET 8.0 SDK
   - Azure Functions Core Tools v4
   - jq (JSON processor)

2. **Configuration**:
   Edit `deploy-params.json`:
   ```json
   {
     "resourceGroupName": "rg-projectinsight-mcp",
     "location": "East US",
     "functionAppName": "func-projectinsight-mcp-[unique-suffix]",
     "storageAccountName": "st[yourname]mcp",
     "appServicePlanName": "asp-projectinsight-mcp",
     "projectInsightBaseUrl": "https://your-domain.projectinsight.net/api/",
     "projectInsightApiToken": "your-api-token-here"
   }
   ```
   **Note**: `functionAppName` and `storageAccountName` must be globally unique

3. **Deploy**:
   ```bash
   # Automated deployment (creates all Azure resources)
   ./deploy.sh
   
   # Manual deployment (if resources already exist)
   func azure functionapp publish [function-app-name]
   ```

4. **Configure Application Settings**:
   ```bash
   az functionapp config appsettings set \
     --name your-function-app-name \
     --resource-group your-resource-group \
     --settings \
       "ProjectInsight__BaseUrl=https://yourdomain.projectinsight.net/api/" \
       "ProjectInsight__ApiToken=your-api-token-here"
   ```

5. **Test Deployment**:
   ```bash
   # Replace with your actual function app URL
   FUNCTION_URL="https://your-function-app.azurewebsites.net"
   
   # Health check
   curl "$FUNCTION_URL/api/health?validate-auth=true"
   
   # Test MCP
   curl -X POST "$FUNCTION_URL/api/mcp" \
     -H "Content-Type: application/json" \
     -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'
   ```

### Monitoring and Logs

```bash
# Stream live logs
az webapp log tail --name func-projectinsight-mcp --resource-group rg-projectinsight-mcp

# Download log files
az webapp log download --name func-projectinsight-mcp --resource-group rg-projectinsight-mcp
```

### MCP Client Integration

**For Claude Desktop**, add to `claude_desktop_config.json`:

**Location**: `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS)

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "curl",
      "args": [
        "-s", "-X", "POST",
        "http://localhost:7071/api/mcp",
        "-H", "Content-Type: application/json",
        "--data-binary", "@-"
      ],
      "env": {}
    }
  }
}
```

**For Warp Terminal**, use `~/.warp/mcp_config.json`:

```json
{
  "mcpServers": {
    "project-insight": {
      "transport": {
        "type": "http",
        "url": "http://localhost:7071/api/mcp"
      },
      "name": "Project Insight",
      "description": "Project management tools via Project Insight API"
    }
  }
}
```

**For VS Code MCP Extensions**, create `.vscode/mcp-servers.json`:

```json
{
  "servers": [
    {
      "name": "project-insight",
      "url": "http://localhost:7071/api/mcp",
      "transport": "http",
      "capabilities": {
        "tools": ["search_projects", "get_project", "get_project_tasks", "create_project", "update_project"],
        "resources": []
      },
      "description": "Project Insight project management tools"
    }
  ]
}
```

**For Production** (replace localhost with your Azure Function URL):
```json
{
  "mcpServers": {
    "project-insight-prod": {
      "transport": "http",
      "url": "https://your-function-app.azurewebsites.net/api/mcp",
      "name": "Project Insight MCP Server (Production)"
    }
  }
}
```

## 🔧 Development Environment Setup

### VS Code Configuration

The project includes complete VS Code integration:

**Available Tasks** (Access via `Cmd+Shift+P` → "Tasks: Run Task"):

**Build Tasks:**
- `build` - Build the project
- `clean` - Clean build artifacts
- `restore` - Restore NuGet packages

**Development Tasks:**
- `func: start` - Start Azure Functions runtime
- `func: stop` - Stop running functions
- `build and start` - Build then start function

**Test Tasks:**
- `test: health` - Quick health check
- `test: health with auth` - Health check with auth validation
- `test: mcp tools list` - List available MCP tools
- `test: search projects` - Test project search
- `test: full suite` - Run complete test script

**Deployment:**
- `deploy to azure` - Deploy to Azure Functions

**Debug Configurations:**
1. `Launch Azure Functions` - Start with debugger attached (press F5)
2. `Attach to Azure Functions` - Attach to running instance
3. `Debug .NET Core` - Direct .NET debugging

**Quick Workflow:**
1. Open workspace: `code ProjectInsightMCP.code-workspace`
2. Run task: "build and start"
3. Test: Open `test-requests.http` and click "Send Request"
4. Debug: Set breakpoints and press F5

**Recommended Extensions:**
- C# Dev Kit - Essential for .NET development
- Azure Functions - For development and debugging
- REST Client - For testing HTTP endpoints in VS Code

## 🧪 Testing Strategy

### Local Testing Commands

```bash
# 1. Start the function
func start --verbose

# 2. Basic health check
curl "http://localhost:7071/api/health"

# 3. Health with authentication validation
curl "http://localhost:7071/api/health?validate-auth=true"

# 4. Initialize MCP
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0", "id": 1, "method": "initialize",
    "params": {
      "protocolVersion": "2024-11-05",
      "capabilities": {},
      "clientInfo": {"name": "test-client", "version": "1.0.0"}
    }
  }'

# 5. List available tools
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 2, "method": "tools/list"}'

# 6. Search projects
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0", "id": 3, "method": "tools/call",
    "params": {"name": "search_projects", "arguments": {}}
  }'
```

### Unit Testing
- Service layer methods
- Model validation
- Error handling scenarios

### Integration Testing
- API connectivity with Project Insight
- Authentication flows
- End-to-end MCP protocol
- Tool execution with real data

### Manual Testing
- Health endpoint validation
- MCP tool execution via curl or REST Client
- Error scenario handling
- Performance under load

## 🎯 MCP Client Compatibility

### **✅ Tested and Working Clients:**

1. **Warp Terminal** - Full compatibility (v2025.03.26 protocol)
   - Uses HTTP transport with streaming support
   - Protocol version: 2025-03-26
   - CORS headers properly configured
   
2. **Claude Desktop** - Full compatibility  
   - Command-based execution works
   - HTTP transport supported
   
3. **HTTP-based MCP clients** - Full streaming support
   - CORS enabled for cross-origin requests
   - Connection keep-alive: 30 seconds
   - OPTIONS preflight handling
   
4. **VS Code MCP extensions** - Development testing verified
   - REST Client integration via `test-requests.http`
   - Direct HTTP endpoint testing

### **Key Compatibility Features:**

- **Protocol Version**: 2025-03-26 (matches latest Warp expectation)
- **CORS Headers**: `Access-Control-Allow-Origin: *` (all origins allowed)
- **Authorization**: Anonymous level (no auth required for MCP endpoint)
- **Connection Management**: Keep-alive with 30-second timeout
- **Notifications Support**: Proper handling of `notifications/initialized`
- **Response Format**: Standard JSON-RPC 2.0 with proper content-type headers

## 🔄 Future Enhancements

### Potential MCP Tools to Add

1. **`get_project`** - ✅ IMPLEMENTED - Retrieve specific project details
2. **`create_project`** - ✅ IMPLEMENTED - Create new projects  
3. **`update_project`** - ✅ IMPLEMENTED - Modify existing projects
4. **`get_project_tasks`** - ✅ IMPLEMENTED - Get project tasks
5. **`get_resources`** - Retrieve project resources
6. **`get_timesheets`** - Access time tracking data

### Advanced Features

1. **Real-time Updates**: WebSocket integration for live data
2. **Batch Operations**: Multiple project operations in single request
3. **Advanced Filtering**: Complex search criteria
4. **Data Validation**: Schema-based validation for all operations
5. **Audit Logging**: Track all API operations for compliance

## 📚 Additional Resources

- [Model Context Protocol Specification](https://modelcontextprotocol.io/docs)
- [Azure Functions Documentation](https://docs.microsoft.com/en-us/azure/azure-functions/)
- [Project Insight API Documentation](https://api.projectinsight.net/help)
- [.NET 8.0 Documentation](https://docs.microsoft.com/en-us/dotnet/)

## 📈 Performance and Operational Considerations

1. **API Caching**: Consider implementing caching for project data to reduce API calls
2. **Connection Pooling**: HttpClient is properly configured for reuse
3. **Async Operations**: All API calls are asynchronous for better throughput
4. **Error Recovery**: Implement retry logic for transient failures
5. **Rate Limiting**: Be aware of Project Insight API rate limits
6. **Monitoring**: Use Application Insights for Azure deployments
7. **Scaling**: Azure Functions consumption plan scales automatically
8. **Connection Management**: Keep-alive headers for efficient HTTP connections

## ✅ Pre-Flight Validation Checklist

Before making changes to this system, verify:

- [ ] **Build succeeds**: `dotnet build ProjectInsightMCP.csproj`
- [ ] **Function starts**: `func start --verbose`
- [ ] **Health responds**: `curl http://localhost:7071/api/health`
- [ ] **Authentication works**: `curl "http://localhost:7071/api/health?validate-auth=true"`
- [ ] **MCP initializes**: Test initialize method via curl
- [ ] **MCP tools list**: `curl -X POST http://localhost:7071/api/mcp -H "Content-Type: application/json" -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'`
- [ ] **Project search works**: Test search_projects tool
- [ ] **All 5 tools respond**: Verify all tools are functional

## 🔄 Maintenance and Updates

### Updating Dependencies
```bash
# Check for outdated packages
dotnet list package --outdated

# Update specific package
dotnet add package <PackageName> --version <VersionNumber>

# Restore packages
dotnet restore
```

### Common Maintenance Tasks
1. **Update Protocol Version**: Modify `McpServerService.cs` if MCP spec changes
2. **Add New Tools**: Implement in `McpServerService.cs` and update tool list
3. **API Changes**: Update `ProjectInsightApiService.cs` and models
4. **Security Updates**: Keep .NET and Azure Functions runtime up to date

### Troubleshooting After Updates
- Clear build artifacts: `dotnet clean`
- Remove bin/obj folders if issues persist
- Verify Azure Functions Core Tools version: `func --version`
- Check for breaking changes in dependencies

---

**Last Updated**: November 24, 2025  
**Status**: ✅ Fully Functional  
**Version**: 1.0.0