# AVI MCP Tools

A collection of **Model Context Protocol (MCP)** servers for integrating various Applied Visions Inc. (AVI) systems with AI agents like GitHub Copilot.

## 🚀 Available MCP Servers

| Server | Description | Status | Documentation |
|--------|-------------|---------|---------------|
| [Project Insight](./servers/project-insight/) | Project management and task tracking | ✅ Production Ready | [📖 Docs](./servers/project-insight/README.md) |

## 🏗️ Repository Structure

```
Project-Insight-MCP/
├── README.md                    # This file - main repository overview
├── docs/                        # Shared documentation and guides
├── shared/                      # Common infrastructure and utilities
├── servers/                     # Individual MCP servers
│   └── project-insight/         # Project Insight MCP server
│       ├── src/                 # Source code
│       ├── docs/                # Server-specific documentation
│       └── deploy/              # Deployment scripts and config
└── examples/                    # Usage examples and integration guides
```

## ⚡ Quick Start

### Option 1: Use Specific Server
Navigate to the server you want to use:
```bash
cd servers/project-insight/
# Follow the README in that directory
```

### Option 2: Clone and Explore
```bash
git clone git@github.com:alext-avi/Project-Insight-MCP.git
cd Project-Insight-MCP
# Browse the available servers in ./servers/
```

## 🛠️ Development

Each MCP server is self-contained with its own:
- Source code and dependencies
- Documentation and setup guides  
- Deployment configurations
- Testing and debugging setup

### Prerequisites
- **.NET 8.0** SDK (for .NET-based servers)
- **Azure Functions Core Tools v4** (for Azure Functions deployment)
- **Node.js 18+** (for Node.js-based servers, when added)
- **Docker** (optional, for containerized deployment)

## 📋 Shared Infrastructure

The `shared/` directory contains:
- Common MCP protocol implementations
- Shared authentication utilities
- Common data models and types
- Deployment utilities and scripts

## 🔐 Security

Each server manages its own:
- API keys and authentication tokens
- Environment configuration
- Access control and permissions

See individual server documentation for security setup.

## 🤝 Contributing

1. **Fork** the repository
2. **Create** a feature branch for your server or improvements
3. **Follow** the established directory structure
4. **Add** comprehensive documentation
5. **Test** your implementation thoroughly
6. **Submit** a pull request

### Adding a New MCP Server

1. Create a new directory: `servers/your-server-name/`
2. Follow the structure of existing servers
3. Include comprehensive README and documentation
4. Add your server to the table above

## 📖 Documentation

- **[General MCP Integration Guide](./docs/COPILOT-INTEGRATION.md)** - GitHub Copilot setup
- **[Development Guide](./docs/agents.md)** - Technical implementation details
- **Individual Server Docs** - See each server's directory

## 🏷️ License

MIT License - see LICENSE file for details

## 🆘 Support

- **Issues**: Use GitHub Issues for bug reports and feature requests
- **Discussions**: Use GitHub Discussions for questions and ideas
- **Internal**: Contact the AVI Development Team

---

**Built with ❤️ by Applied Visions Inc. (AVI)**
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