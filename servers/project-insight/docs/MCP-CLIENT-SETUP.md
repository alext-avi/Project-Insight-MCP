# MCP Client Configuration

## Overview

This guide shows you how to configure the Project Insight MCP server with various MCP clients (VS Code Copilot, Claude Desktop, etc.).

The server supports **two transport modes**:
- **STDIO** (recommended for local use) - runs as a local subprocess
- **HTTP** (for cloud/shared deployment) - runs as Azure Functions

## Quick Start: Local STDIO Mode

This is the simplest setup for personal use.

### VS Code Copilot

Add to your VS Code `settings.json`:

```json
{
  "github.copilot.chat.mcpServers": {
    "project-insight": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Project-Insight-MCP/servers/project-insight/src-stdio"],
      "env": {
        "ProjectInsight__BaseUrl": "https://your-domain.projectinsight.net/api/",
        "ProjectInsight__ApiToken": "your-api-token-here"
      }
    }
  }
}
```

### Claude Desktop

Add to `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS):

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Project-Insight-MCP/servers/project-insight/src-stdio"],
      "env": {
        "ProjectInsight__BaseUrl": "https://your-domain.projectinsight.net/api/",
        "ProjectInsight__ApiToken": "your-api-token-here"
      }
    }
  }
}
```

## HTTP Mode (Cloud Deployment)

For team sharing or enterprise use, deploy to Azure Functions and use HTTP transport.

### VS Code Copilot (HTTP)

```json
{
  "github.copilot.chat.mcpServers": {
    "project-insight": {
      "transport": "http", 
      "url": "https://your-function-app.azurewebsites.net/api/mcp"
    }
  }
}
```

### Local Development (HTTP)

For testing the Azure Functions version locally:

```json
{
  "github.copilot.chat.mcpServers": {
    "project-insight-local": {
      "transport": "http",
      "url": "http://localhost:7071/api/mcp"
    }
  }
}
```

## Configuration Reference

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `ProjectInsight__BaseUrl` | Project Insight API base URL | ✅ |
| `ProjectInsight__ApiToken` | API authentication token | ✅ |

### Getting Your API Token

1. Log into Project Insight
2. Navigate to **Settings** → **API Access**
3. Generate or copy your API token
4. Keep this token secure - it has access to your project data

## Quick Test Commands

Once configured, you can test the MCP server directly:

### 1. Test MCP Tools List
```bash
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}'
```

### 2. Test Project Search
```bash
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "search_projects", "arguments": {}}}'
```

### 3. Test Health Endpoint
```bash
curl "http://localhost:7071/api/health?test-api"
```

## Testing with MCP Inspector

The MCP Inspector is the official debugging tool for MCP servers:

```bash
# Install and run MCP Inspector
npx @modelcontextprotocol/inspector dotnet run --project /path/to/src-stdio
```

This opens a web UI where you can:
- View all available tools
- Test tool execution interactively
- Debug request/response payloads

## VS Code Extension Configuration

For VS Code MCP extensions, create this configuration:

**File**: `.vscode/mcp-servers.json`

```json
{
  "servers": [
    {
      "name": "project-insight",
      "url": "http://localhost:7071/api/mcp",
      "transport": "http",
      "capabilities": {
        "tools": ["search_projects"],
        "resources": []
      },
      "description": "Project Insight project management tools"
    }
  ]
}
```

## Available MCP Tools

Once connected, you'll have access to these tools:

### Project & Task Tools
| Tool | Description |
|------|-------------|
| `search_projects` | Search projects with filters (active, open, manager, status) |
| `get_project` | Get detailed project information by ID |
| `get_project_tasks` | Get all tasks for a project |
| `create_project` | Create a new project |
| `update_project` | Update an existing project |
| `get_task` | Get task details by ID |
| `create_task` | Create a new task |
| `update_task` | Update an existing task |

### Time Tracking Tools
| Tool | Description |
|------|-------------|
| `get_project_time_entries` | Get time entries with date/user/task filters |
| `get_timesheet` | Get a timesheet by ID |
| `search_timesheets` | Search timesheets by user, dates, status |
| `get_my_time_status` | Get current user's time status |
| `create_timesheet` | Create a new timesheet |
| `update_timesheet` | Update a timesheet |
| `submit_timesheet` | Submit for approval |
| `approve_timesheet` | Approve a submitted timesheet |
| `reject_timesheet` | Reject with comments |

### User Tools
| Tool | Description |
|------|-------------|
| `get_current_user` | Get authenticated user's profile |
| `get_user` | Get user by ID |
| `get_users` | Batch get multiple users |
| `search_users` | Search users by name |

## Example Usage in Chat

After configuration, you can use the MCP server in your chat:

```
@project-insight Please show me all current projects and their status
```

or

```
Use the search_projects tool to get project information
```

## Troubleshooting

### 1. Connection Issues
- Ensure Azure Function is running: `lsof -i :7071`
- Test health endpoint: `curl http://localhost:7071/api/health`

### 2. Authentication Issues  
- Verify API token in local.settings.json
- Test auth: `curl "http://localhost:7071/api/health?validate-auth=true"`

### 3. Tool Discovery Issues
- Test tools list: `curl -X POST http://localhost:7071/api/mcp -H "Content-Type: application/json" -d '{"method": "tools/list"}'`

### 4. MCP Protocol Issues
- Verify JSON-RPC 2.0 format in requests
- Check response format matches MCP specification

## Production Configuration

For production deployment (Azure Functions), update URLs:

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

---

**Local Server**: `http://localhost:7071/api/mcp`  
**Health Check**: `http://localhost:7071/api/health`  
**Server Info**: `http://localhost:7071/api/info`