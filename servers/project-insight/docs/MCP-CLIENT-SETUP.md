# MCP Client Configuration

## Overview

This guide shows how to configure the Project Insight MCP server with MCP clients (VS Code Copilot, Claude Desktop, and others).

There are two ways to connect:
- **Local stdio** (`projectinsight-mcp`): runs as a subprocess on your machine. Described below.
- **Remote** (`https://<app>/mcp`): the web host on Azure App Service, if your owner runs one. You sign in with your own Project Insight account. See [docs/REMOTE.md](../../../docs/REMOTE.md) for Claude, Claude Code, ChatGPT and Codex.

## Local stdio

Install the tool once:

```bash
dotnet tool install -g ProjectInsight.MCP
```

### VS Code Copilot

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "project-insight": {
      "type": "stdio",
      "command": "projectinsight-mcp",
      "env": {
        "ProjectInsight__BaseUrl": "https://your-tenant.projectinsight.net/api/"
      }
    }
  }
}
```

### Claude Desktop

Use the `.mcpb` bundle from the Releases page, or add to `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS):

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "projectinsight-mcp",
      "env": {
        "ProjectInsight__BaseUrl": "https://your-tenant.projectinsight.net/api/"
      }
    }
  }
}
```

Leave the token out to sign in with SSO: ask the assistant to "link my Project Insight account".
To use a static token instead, add `ProjectInsight__ApiToken`. See [AUTHENTICATION.md](./AUTHENTICATION.md).

### Running from source

Replace the command with `dotnet` and the arguments `["run", "--project", "/path/to/Project-Insight-MCP/servers/project-insight/src-stdio"]`.

## Configuration Reference

| Variable | Description | Required |
|----------|-------------|----------|
| `ProjectInsight__BaseUrl` | Project Insight API base URL | Yes |
| `ProjectInsight__ApiToken` | Static API token | Only without SSO |

## Testing with MCP Inspector

The MCP Inspector is the official debugging tool for MCP servers:

```bash
npx @modelcontextprotocol/inspector projectinsight-mcp
```

It opens a web UI where you can list tools, call them and read the request and response payloads.

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

### 1. Connection issues
- Check that `projectinsight-mcp` is on your PATH, or use its full path (such as `~/.dotnet/tools/projectinsight-mcp`).
- Restart the client after changing its config.

### 2. Authentication issues
- Check `ProjectInsight__BaseUrl` is an absolute https URL ending in `/api/`.
- For SSO, run the link flow again. For a static token, check it has not expired.

### 3. Remote server issues
- See [docs/REMOTE.md](../../../docs/REMOTE.md) for sign-in steps, limits and revocation.
