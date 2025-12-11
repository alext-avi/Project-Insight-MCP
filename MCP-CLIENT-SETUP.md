# MCP Client Configuration for GitHub Copilot

## Overview

This guide shows you how to configure the Project Insight MCP server as an MCP client in GitHub Copilot (VS Code) for testing and development.

## Configuration Methods

### Method 1: HTTP-based MCP Client (Recommended for testing)

Create or update your MCP client configuration file:

**Location**: `~/.vscode/mcp-client-config.json` (or your preferred location)

```json
{
  "mcpServers": {
    "project-insight-local": {
      "transport": "http",
      "url": "http://localhost:7071/api/mcp",
      "name": "Project Insight MCP Server (Local)",
      "description": "Local development instance of Project Insight MCP server"
    }
  }
}
```

### Method 2: Command-based MCP Client 

For environments that prefer command-based execution:

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "curl",
      "args": [
        "-s",
        "-X", "POST",
        "http://localhost:7071/api/mcp",
        "-H", "Content-Type: application/json",
        "-H", "Accept: application/json",
        "--data-binary", "@-"
      ],
      "name": "Project Insight MCP Server",
      "description": "Project management tools via Project Insight API"
    }
  }
}
```

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
curl "http://localhost:7071/api/health?validate-auth=true"
```

## Integration with Claude Desktop

If you're using Claude Desktop, add this to your `claude_desktop_config.json`:

**Location**: `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS)

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "curl",
      "args": [
        "-s",
        "-X", "POST", 
        "http://localhost:7071/api/mcp",
        "-H", "Content-Type: application/json",
        "--data-binary", "@-"
      ],
      "env": {}
    }
  }
}
```

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

Once connected, you'll have access to:

| Tool | Description | Parameters |
|------|-------------|------------|
| `search_projects` | Retrieve all projects from Project Insight | None required |

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