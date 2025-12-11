# AVI MCP Tools - Project Insight Server

A **Model Context Protocol (MCP)** server for integrating with the **Project Insight API**, enabling AI agents like GitHub Copilot to interact with project management data and tools through natural language.

**Part of Applied Visions Inc. (AVI) MCP Tools suite.**

## 🚀 Status: Production Ready

✅ **All 5 MCP Tools Working**  
✅ **Task Filtering Fixed**  
✅ **API Integration Tested**  
✅ **GitHub Copilot Compatible**

## 🛠️ Features

- **Project Management Tools**: Search, list, create, update projects
- **Task Management**: Get project-specific tasks and assignments  
- **Resource Management**: Manage project resources and allocations
- **Authentication**: Secure token-based authentication with Project Insight API
- **Type Safety**: Full C# implementation with comprehensive data models
- **Azure Functions**: Serverless deployment for scalability and reliability

## 🔧 API Integration

- **Base URL pattern**: `https://yourdomain.projectinsight.net/api/`
- **Authentication**: API token-based authentication
- **Custom property selection**: Via `modelProperties` parameter for efficient data retrieval
- **Comprehensive data models**: Fully typed Project and Task data structures
- **Error handling**: Robust error handling with detailed logging

## 📋 Available MCP Tools

| Tool | Description | Status |
|------|-------------|---------|
| `search_projects` | Search for projects with optional filters | ✅ Working |
| `get_project` | Get detailed information about a specific project | ✅ Working |
| `get_project_tasks` | Get all tasks for a specific project | ✅ Fixed & Working |
| `create_project` | Create a new project in Project Insight | ✅ Working |
| `update_project` | Update an existing project | ✅ Working |

## 🔧 Prerequisites

- **.NET 8.0** SDK or later
- **Azure Functions Core Tools v4** (for local development)
- **Project Insight** API access with valid API token
- **Azure subscription** (for cloud deployment)

## ⚡ Quick Start

### 1. Clone and Setup
```bash
git clone git@github.com:alext-avi/AVI-MCP-Tools.git
cd AVI-MCP-Tools
```

### 2. Configure API Settings
Create `local.settings.json` with your Project Insight API details:
```json
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

3. **Build the project**:
   ```bash
   dotnet build ProjectInsightMCP.csproj
   ```

4. **Start the Functions runtime**:
   ```bash
   func start
   ```

5. **Test the endpoints**:
   - Health: `GET http://localhost:7071/api/health`
   - Server Info: `GET http://localhost:7071/api/info`
   - MCP Endpoint: `POST http://localhost:7071/api/mcp`

## MCP Protocol Testing

### Initialize the MCP Server

```bash
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2024-11-05",
      "capabilities": {},
      "clientInfo": {
        "name": "test-client",
        "version": "1.0.0"
      }
    }
  }'
```

### List Available Tools

```bash
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/list",
    "params": {}
  }'
```

### Call a Tool (Search Projects)

```bash
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
      "name": "search_projects",
      "arguments": {
        "isActive": true,
        "modelProperties": "UniqueID,Name,Description,StartDate,EndDate,Status"
      }
    }
  }'
```

## Azure Deployment

### Using Azure CLI

1. **Create a Function App**:
   ```bash
   az functionapp create \
     --resource-group your-resource-group \
     --consumption-plan-location eastus \
     --runtime dotnet \
     --functions-version 4 \
     --name your-function-app-name \
     --storage-account your-storage-account
   ```

2. **Deploy the function**:
   ```bash
   func azure functionapp publish your-function-app-name
   ```

3. **Configure app settings**:
   ```bash
   az functionapp config appsettings set \
     --name your-function-app-name \
     --resource-group your-resource-group \
     --settings \
       "ProjectInsight__BaseUrl=https://yourdomain.projectinsight.net/api/" \
       "ProjectInsight__ApiToken=your-api-token-here"
   ```

### Using VS Code

1. Install the **Azure Functions** extension
2. Right-click the project and select **"Deploy to Function App..."**
3. Follow the deployment wizard
4. Configure application settings in the Azure portal

## Project Structure

```
ProjectInsight/
├── Functions/
│   └── McpFunction.cs          # Azure Function HTTP triggers
├── MCP/
│   └── McpModels.cs           # MCP protocol message structures
├── Models/
│   └── ProjectInsightModels.cs # Project Insight API data models
├── Services/
│   ├── ProjectInsightApiService.cs # HTTP client for Project Insight API
│   └── McpServerService.cs    # MCP server protocol handler
├── Program.cs                 # Application startup and DI configuration
├── host.json                 # Azure Functions configuration
├── local.settings.json       # Local development settings
└── ProjectInsightMCP.csproj  # .NET project file
```

## Configuration

### Environment Variables

- **ProjectInsight__BaseUrl**: Project Insight API base URL
- **ProjectInsight__ApiToken**: Project Insight API authentication token
- **FUNCTIONS_WORKER_RUNTIME**: Set to "dotnet" for .NET runtime
- **FUNCTIONS_INPROC_NET8_ENABLED**: Set to "1" for .NET 8 support

### Project Insight API Configuration

The server expects the following API configuration:
- Valid API token with read/write permissions
- Base URL ending with `/api/`
- Support for modelProperties parameter for property selection

## Security Considerations

- Store API credentials securely using Azure Key Vault or environment variables
- Validate all inputs with proper schemas
- Handle sensitive project data appropriately
- Implement proper CORS for HTTP transport
- Use HTTPS for all communications

## Troubleshooting

### Common Issues

1. **Build Errors**: Ensure .NET 8.0 SDK is installed
2. **Permission Errors**: Check Azure Functions Core Tools permissions
3. **API Connection**: Verify Project Insight API token and base URL
4. **Extension Bundle Error**: Remove extensionBundle from host.json for pre-compiled functions

### Debug Logs

Enable verbose logging in Azure Functions:
```bash
func start --verbose
```

Check application logs in Azure portal under **"Monitoring" > "Log Stream"**

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For issues and questions:
- Create an issue in the repository
- Check the Project Insight API documentation
- Refer to the MCP protocol specification

## Version History

- **1.0.0**: Initial release with core MCP functionality
  - Project management tools
  - Azure Functions deployment
  - Full MCP protocol compliance
  - Project Insight API integration