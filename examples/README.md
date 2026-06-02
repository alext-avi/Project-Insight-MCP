# MCP Usage Examples

This directory contains examples of how to integrate and use the AVI MCP Tools servers.

## GitHub Copilot Integration

### 1. Basic Setup
Add to your VS Code settings.json:
```json
{
  "mcp.servers": {
    "project-insight": {
      "name": "Project Insight",
      "transport": {
        "type": "http", 
        "url": "http://localhost:7071/api/mcp"
      }
    }
  }
}
```

### 2. Example Conversations

**Project Management:**
```
User: "Show me all active projects in Project Insight"
Copilot: Uses search_projects tool to fetch and display active projects

User: "What tasks are assigned to project ABC-123?"
Copilot: Uses get_project_tasks with the project ID to show specific tasks

User: "Create a new project for website redesign"
Copilot: Uses create_project tool to create the project with appropriate details
```

**Analysis and Reporting:**
```
User: "Which projects are behind schedule?"
Copilot: Searches projects and analyzes completion percentages and dates

User: "Show me projects managed by John Smith"
Copilot: Filters projects by project manager name/ID
```

## MCP Client Integration

### Direct API Usage
```bash
# Test connection
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'

# Search projects
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0", 
    "id": 1, 
    "method": "tools/call", 
    "params": {
      "name": "search_projects", 
      "arguments": {"isActive": true}
    }
  }'
```

### Claude Desktop Integration
Add to your Claude configuration:
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

## Multiple Server Setup

When using multiple MCP servers:
```json
{
  "mcp.servers": {
    "project-insight": {
      "name": "Project Insight",
      "transport": {
        "type": "http", 
        "url": "http://localhost:7071/api/mcp"
      }
    },
    "future-server": {
      "name": "Future AVI Server",
      "transport": {
        "type": "http", 
        "url": "http://localhost:7072/api/mcp"
      }
    }
  }
}
```

## Development Examples

### Adding a New MCP Tool
See [Project Insight implementation](../servers/project-insight/src/) for examples of:
- Tool definition in `McpServerService.cs`
- API integration in `ProjectInsightApiService.cs`
- Model definitions in `Models/`

### Testing Your Integration
```bash
# Start the server
cd servers/project-insight/src
func start --port 7071

# In another terminal, test tools
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'
```