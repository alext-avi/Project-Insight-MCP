# Project Insight MCP

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server for [Project Insight](https://www.projectinsight.com/) — bring your projects, tasks, timesheets, time entries, and resources into AI assistants like **Claude** and **GitHub Copilot**.

> Independent, community-built integration. Not affiliated with or endorsed by Project Insight or its vendor; "Project Insight" is the trademark of its respective owner.

## What you can do

Ask your assistant things like *"show my active projects"*, *"log 3 hours to the Acme redesign task yesterday"*, *"submit my timesheet"*, or *"who's assigned to project X?"*. Tools span:

- **Projects** — search, retrieve, status
- **Tasks** — list, detail, assignments
- **Time entries** — create, update, delete, filter by date / user / task
- **Timesheets** — view, submit, approve, reject
- **Resources** — project & task assignments (add, update, remove)
- **Users & PTO** — directory lookups and paid-time-off management

Token-efficient field selection (`minimal` / `summary` / `standard` / `full`, or a custom field list) keeps responses small.

## Install

Pick whichever fits your client:

### 🖥️ Claude Desktop — one-click bundle (recommended)

Download the latest **`.mcpb`** from the [**Releases page**](https://github.com/alext-avi/Project-Insight-MCP/releases/latest), add it as an extension in Claude Desktop, set your `ProjectInsight__BaseUrl`, and leave the token blank to sign in via SSO. No .NET SDK required.

### 📦 .NET tool — VS Code, CLI, or any stdio MCP client

```bash
dotnet tool install -g ProjectInsight.MCP
```

Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (version 2.0.0 and later).

Then point your client's MCP config at the `projectinsight-mcp` command — see the [server guide](./servers/project-insight/README.md) for ready-to-paste config.

### ☁️ Remote server — claude.ai connector / Copilot (self-host on Azure)

Deploy the Azure Functions server with its OAuth 2.1 + PKCE connector so claude.ai and Copilot can connect over HTTPS. Infrastructure-as-code lives in [`bicep/`](./bicep/); walkthrough in the [deployment docs](./servers/project-insight/docs/).

## Authentication

| Method | When to use |
|--------|-------------|
| **SSO device activation** *(recommended)* | Normal end users — sign in through your org's SSO, no token handling. Per-user credentials are cached. |
| **Static API token** | Service accounts, CI, or where SSO isn't available. |

To link via SSO, just ask the assistant **"link my Project Insight account"**, open the sign-in URL it returns, and paste back the activation code.

## Configuration

| Setting | Required | Description |
|---------|----------|-------------|
| `ProjectInsight__BaseUrl` | ✅ | Your Project Insight API URL, e.g. `https://yourdomain.projectinsight.net/api/` |
| `ProjectInsight__ApiToken` | — | Static token for service accounts. **Leave blank to use SSO.** |

## Repository layout

```
Project-Insight-MCP/
├── servers/project-insight/
│   ├── core/         # PI API client, auth helpers, MCP tool definitions/handlers
│   ├── src/          # Azure Functions remote server (+ OAuth 2.1/PKCE bridge)
│   ├── src-stdio/    # stdio host (official MCP C# SDK) → .mcpb bundle + ProjectInsight.MCP NuGet tool
│   ├── tests/        # xUnit tests, incl. end-to-end through the MCP SDK client
│   ├── mcpb/         # bundle manifest + build staging
│   └── docs/         # setup, auth, and deployment guides
├── bicep/            # Azure infrastructure-as-code (self-host)
├── docs/             # general integration guides
└── examples/         # usage examples
```

## Documentation

- [**Server guide**](./servers/project-insight/README.md) — install, auth, and the full tool list
- [Authentication](./servers/project-insight/docs/AUTHENTICATION.md)
- [Deployment & infrastructure](./servers/project-insight/docs/)

## Contributing

Issues and pull requests are welcome. Fork, branch, and open a PR — please include documentation for any new tools.

## License

[MIT](./LICENSE) — © Applied Visions Inc.

---

Built with ❤️ by **Applied Visions Inc. (AVI)**
