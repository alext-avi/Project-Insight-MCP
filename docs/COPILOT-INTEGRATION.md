# Using Project Insight MCP Server with GitHub Copilot

GitHub Copilot in VS Code talks to the local stdio tool, `projectinsight-mcp`. The remote web host
needs an OAuth sign-in, which is covered in [REMOTE.md](./REMOTE.md).

## Set up

Install the tool:

```bash
dotnet tool install -g ProjectInsight.MCP
```

Add it to `.vscode/mcp.json`:

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

Restart VS Code, then ask Copilot Chat to "link my Project Insight account". Open the sign-in URL it
returns, sign in, and paste the activation code back into the chat. The credential is cached for
later sessions. See [Authentication](../servers/project-insight/docs/AUTHENTICATION.md) for the
static-token option.

## Example chat interactions

```
"Show me all current projects from Project Insight"
"Get details for project ID 12345"
"Show my time entries for last week"
"Show me all projects and identify any that are behind schedule"
```

## Testing your setup

1. Open Copilot Chat in agent mode and check that the project-insight tools are listed.
2. Ask "What projects do I have access to?". The assistant should call `search_projects`.
3. To inspect the tools directly, run `npx @modelcontextprotocol/inspector projectinsight-mcp`.

## Remote server

If the server's owner runs the remote web host, connect to `https://<app>/mcp` instead. Steps for
Claude, Claude Code, ChatGPT and Codex are in [REMOTE.md](./REMOTE.md). Copilot Studio is not
supported by the current web host (stretch goal); see
[COPILOT-STUDIO-MCP.md](../servers/project-insight/docs/COPILOT-STUDIO-MCP.md).
