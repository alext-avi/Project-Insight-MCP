# Project Insight MCP Server - Agent Knowledge Base

## Overview

This is a **Model Context Protocol (MCP) server** that integrates with the **Project Insight API** to provide AI agents with project management capabilities. The local (stdio) server runs on **.NET 10** with the official MCP C# SDK; the remote server is an **Azure Functions** app on **.NET 8.0** (in-process).

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

### Time-change drafts (`plan_time_changes` → `commit_time_changes`)

Prefer drafts over direct `create/update/delete_time_entry` calls when changing several entries or
anyone else's time. `plan_time_changes` takes an `intent` and up to 100 `changes`, writes nothing,
and returns a draft (or a `needs_clarification` result listing candidates when a project, task or person name is
ambiguous). Summarize the draft and ask the user; commit with `commit_time_changes` only after they
agree, or let them press Confirm on the card. A stale result means the affected days changed in
Project Insight since planning: read them again and re-plan. `undo_time_changes` drafts the reverse
of a commit for 24 hours.

## 🚀 Getting Started

### Prerequisites
- .NET 10 SDK (plus the .NET 8 runtime to run the Functions host locally)
- Azure Functions Core Tools v4
- Project Insight API credentials

### Local Development

1. **Configure Authentication**:
   ```json
   // local.settings.json
   {
     "Values": {
       "ProjectInsight__BaseUrl": "https://yourdomain.projectinsight.net/api/",
       "ProjectInsight__ApiKey": "your-api-token"
     }
   }
   ```

2. **Build and Run**:
   ```bash
   dotnet build
   func start --verbose
   ```

3. **Test Health Endpoint**:
   ```bash
   curl "http://localhost:7071/api/health?validate-auth=true"
   ```

4. **Test MCP Tool**:
   ```bash
   curl -X POST http://localhost:7071/api/mcp \
     -H "Content-Type: application/json" \
     -d '{
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

## 🔐 Security Considerations

1. **API Credentials**: Store in Azure Key Vault for production
2. **HTTPS**: Always use HTTPS in production environments
3. **Token Validation**: Implement token expiration handling
4. **CORS**: Configure appropriately for client access
5. **Input Validation**: All MCP inputs are validated against schemas

## 🚀 Deployment

### Azure Functions Deployment

1. **Create Azure Function App** (Linux, .NET 8.0)
2. **Configure Application Settings**:
   ```
   ProjectInsight__BaseUrl = https://yourdomain.projectinsight.net/api/
   ProjectInsight__ApiKey = <your-api-token>
   ```
3. **Deploy**: `func azure functionapp publish <function-app-name>`

### MCP Client Integration

For Claude Desktop, add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "curl",
      "args": ["-X", "POST", "https://your-function-app.azurewebsites.net/api/mcp"],
      "env": {}
    }
  }
}
```

## 🧪 Testing Strategy

### Unit Testing
- Service layer methods
- Model validation
- Error handling scenarios

### Integration Testing
- API connectivity
- Authentication flows
- End-to-end MCP protocol

### Manual Testing
- Health endpoint validation
- MCP tool execution
- Error scenario handling

## 📈 Performance Considerations

1. **API Caching**: Consider implementing caching for project data
2. **Connection Pooling**: HttpClient is properly configured for reuse
3. **Async Operations**: All API calls are asynchronous
4. **Error Recovery**: Retry logic for transient failures

## 🎯 MCP Client Compatibility

### **✅ Tested and Working Clients:**

1. **Warp Terminal** - Full compatibility (v2025.03.26 protocol)
2. **Claude Desktop** - Full compatibility  
3. **HTTP-based MCP clients** - Full streaming support
4. **VS Code MCP extensions** - Development testing verified

### **Configuration for Warp:**
```json
{
  "mcpServers": {
    "project-insight": {
      "transport": {
        "type": "http", 
        "url": "http://localhost:7071/api/mcp"
      },
      "name": "Project Insight",
      "description": "Project management tools"
    }
  }
}
```

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

## ✅ Validation Checklist

Before making changes to this system:

- [ ] Build succeeds: `dotnet build`
- [ ] Function starts: `func start`
- [ ] Health endpoint responds: `curl http://localhost:7071/api/health`
- [ ] Authentication works: `curl "http://localhost:7071/api/health?validate-auth=true"`
- [ ] MCP tools list: `curl -X POST http://localhost:7071/api/mcp -H "Content-Type: application/json" -d '{"method": "tools/list"}'`
- [ ] Project search works: `curl -X POST http://localhost:7071/api/mcp -H "Content-Type: application/json" -d '{"method": "tools/call", "params": {"name": "search_projects", "arguments": {}}}'`

---

**Last Updated**: November 24, 2025  
**Status**: ✅ Fully Functional  
**Version**: 1.0.0