# MCP Usage Examples

Examples of connecting an MCP client to the Project Insight server.

## Local (stdio)

Install the tool (`dotnet tool install -g ProjectInsight.MCP`), then point your client at it.
VS Code `mcp.json`:

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

Claude Desktop can use the `.mcpb` bundle instead; see the [server guide](../servers/project-insight/README.md).

## Remote (HTTP)

If the server's owner runs the remote web host, connect to `https://<app>/mcp`. You sign in with
your own Project Insight account through OAuth; there is no token to paste.

```bash
claude mcp add --transport http project-insight https://<app>/mcp
```

Steps for Claude, ChatGPT and Codex are in [docs/REMOTE.md](../docs/REMOTE.md).

## Example conversations

**Project management:**
```
User: "Show me all active projects in Project Insight"
Assistant: Uses search_projects to fetch and display active projects

User: "What tasks are assigned to project ABC-123?"
Assistant: Uses get_project_tasks with the project ID to show its tasks
```

**Time:**
```
User: "Show my time for last week"
Assistant: Reads your time entries for the week and summarizes them

User: "Log 3 hours to the redesign task yesterday"
Assistant: Plans the change as a draft, asks you to confirm, then commits it
```

## Multiple servers

```json
{
  "servers": {
    "project-insight": {
      "type": "stdio",
      "command": "projectinsight-mcp",
      "env": { "ProjectInsight__BaseUrl": "https://your-tenant.projectinsight.net/api/" }
    },
    "another-server": {
      "type": "http",
      "url": "https://example.com/mcp"
    }
  }
}
```

## Development

See the [implementation](../servers/project-insight/) for how tools are built:
- Tool definitions and handlers in `server/`
- API integration and models in `core/`
- The remote web host in `src-web/` and the local stdio host in `src-stdio/`
