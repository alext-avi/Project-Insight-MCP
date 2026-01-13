# AVI MCP Tools - Project Insight Server

A **Model Context Protocol (MCP)** server for integrating with the **Project Insight API**, enabling AI agents like GitHub Copilot, Claude, and Warp to interact with project management data through natural language.

**Part of Applied Visions Inc. (AVI) MCP Tools suite.**

## 🚀 Quick Start

### Prerequisites
- .NET 8.0 SDK or later
- Azure Functions Core Tools v4
- Project Insight API access with valid API token

### Setup in 3 Steps

1. **Clone and configure**:
   ```bash
   git clone git@github.com:alext-avi/AVI-MCP-Tools.git
   cd AVI-MCP-Tools
   ```

2. **Create `local.settings.json`**:
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

3. **Build and run**:
   ```bash
   dotnet build ProjectInsightMCP.csproj
   func start
   ```

Test it: `curl http://localhost:7071/api/health`

## 🛠️ Features

### Available MCP Tools

| Tool | Description | Status |
|------|-------------|---------|
| `search_projects` | Search for projects with optional filters | ✅ Working |
| `get_project` | Get detailed information about a specific project | ✅ Working |
| `get_project_tasks` | Get all tasks for a specific project | ✅ Working |
| `create_project` | Create a new project in Project Insight | ✅ Working |
| `update_project` | Update an existing project | ✅ Working |

### Capabilities

- **Natural Language Interface**: AI agents can search, create, and update projects
- **Full Type Safety**: Comprehensive C# data models for all API interactions
- **Secure Authentication**: Token-based auth with Azure Key Vault support
- **Azure Functions**: Serverless deployment for scalability
- **MCP Compatible**: Works with Claude Desktop, Warp Terminal, VS Code, and more

## 🔌 MCP Client Integration

### Claude Desktop

Add to `~/Library/Application Support/Claude/claude_desktop_config.json`:

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
      ]
    }
  }
}
```

### Warp Terminal

Add to `~/.warp/mcp_config.json`:

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

## 🚀 Azure Deployment

1. **Configure**: Edit `deploy-params.json` with your Azure settings
2. **Deploy**: Run `./deploy.sh`
3. **Test**: Run `./test-deployment.sh https://your-function-app.azurewebsites.net`

See `agents.md` for detailed deployment instructions.

## 📁 Project Structure

```
├── Functions/          # Azure Function HTTP triggers
│   └── McpFunction.cs
├── MCP/               # MCP protocol implementation
│   └── McpModels.cs
├── Models/            # Project Insight data models
│   └── ProjectInsightModels.cs
├── Services/          # API and MCP services
│   ├── ProjectInsightApiService.cs
│   └── McpServerService.cs
├── Program.cs         # DI configuration
└── agents.md          # Complete agent knowledge base
```

## 📚 Documentation

- **`agents.md`** - Complete knowledge base for AI agents (architecture, troubleshooting, development)
- **`README.md`** - This file (getting started, quick reference)
- **`.github/copilot-instructions.md`** - GitHub Copilot configuration

## 🔐 Security

- Store credentials in Azure Key Vault for production
- Use HTTPS for all production endpoints
- Tokens are validated and masked in logs
- CORS configured for MCP client compatibility
- Input validation on all API calls

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## 📝 License

MIT License - See LICENSE file for details

## 💬 Support

- **Issues**: Create an issue in the repository
- **Documentation**: See `agents.md` for detailed information
- **API Docs**: [Project Insight API Documentation](https://api.projectinsight.net/help)
- **MCP Spec**: [Model Context Protocol](https://modelcontextprotocol.io/docs)

---

**Status**: ✅ Production Ready | **Version**: 1.0.0