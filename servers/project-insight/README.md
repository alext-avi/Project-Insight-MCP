# Project Insight MCP Server

A **Model Context Protocol (MCP)** server for integrating with the **Project Insight API**, enabling AI agents like GitHub Copilot to interact with project management data and tools through natural language.

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
- **Two hosts**: a local stdio tool (`projectinsight-mcp`) and a remote web host on Azure App Service that signs each user in with OAuth 2.1

## 🔧 API Integration

- **Base URL pattern**: `https://yourdomain.projectinsight.net/api/`
- **Authentication**: API token-based authentication
- **Custom property selection**: Via `modelProperties` parameter for efficient data retrieval
- **Comprehensive data models**: Fully typed Project and Task data structures
- **Error handling**: Robust error handling with detailed logging

## 📋 Available MCP Tools

### Project Tools
| Tool | Description | Status |
|------|-------------|--------|
| `search_projects` | Search for projects with filters (active, open, manager, status) | ✅ Working |
| `get_project` | Get detailed information about a specific project | ✅ Working |
| `get_project_tasks` | Get all tasks for a specific project | ✅ Working |
| `create_project` | Create a new project in Project Insight | ✅ Working |
| `update_project` | Update an existing project | ✅ Working |

### Task Tools
| Tool | Description | Status |
|------|-------------|--------|
| `get_task` | Get detailed information about a specific task | ✅ Working |
| `create_task` | Create a new task within a project | ✅ Working |
| `update_task` | Update an existing task | ✅ Working |

### Timesheet Tools
| Tool | Description | Status |
|------|-------------|--------|
| `get_timesheet` | Get a timesheet by ID with status info | ✅ Working |
| `search_timesheets` | Search timesheets by user, approver, dates, status | ✅ Working |
| `get_my_time_status` | Get current user's time tracking status | ✅ Working |
| `create_timesheet` | Create a new timesheet for time tracking | ✅ Working |
| `update_timesheet` | Update an existing timesheet | ✅ Working |
| `submit_timesheet` | Submit a timesheet for manager approval | ✅ Working |
| `approve_timesheet` | Approve a submitted timesheet (manager only) | ✅ Working |
| `reject_timesheet` | Reject a timesheet with comments (manager only) | ✅ Working |

### Time Entry Tools
| Tool | Description | Status |
|------|-------------|--------|
| `get_project_time_entries` | Get time entries for a project with date/user/task filters | ✅ Working |

### User Tools
| Tool | Description | Status |
|------|-------------|--------|
| `get_current_user` | Get the authenticated user's profile | ✅ Working |
| `get_user` | Get a user's profile by ID | ✅ Working |
| `get_users` | Get multiple users by IDs (batch) | ✅ Working |
| `search_users` | Search for users by name | ✅ Working |

## 🔧 Prerequisites

- **.NET 10** SDK (builds every project here)
- **Project Insight** access: SSO sign-in or an API token
- **Azure subscription** (only to self-host the remote web host)

## ⚡ Quick Start

### Local stdio tool

```bash
dotnet tool install -g ProjectInsight.MCP
```

Point your MCP client at `projectinsight-mcp` with `ProjectInsight__BaseUrl` set, then ask the
assistant to "link my Project Insight account". See [src-stdio/README.md](./src-stdio/README.md) and
[MCP Client Setup](./docs/MCP-CLIENT-SETUP.md).

### Remote server

Connect a client to `https://<app>/mcp` and sign in with your Project Insight account. See
[docs/REMOTE.md](../../docs/REMOTE.md).

### Run from source

```bash
dotnet build servers/project-insight/src-stdio
dotnet test servers/project-insight/tests -c Release
```

To run the web host locally, see [docs/agents.md](../../docs/agents.md#getting-started).

## 🔧 Configuration

### Local tool

| Variable | Description | Required |
|----------|-------------|----------|
| `ProjectInsight__BaseUrl` | Project Insight API base URL | ✅ |
| `ProjectInsight__ApiToken` | Static API token (leave out to use SSO) | ❌ |

### Remote web host

See [CONFIGURATION.md](../../CONFIGURATION.md). Endpoint: `https://<app>/mcp`.

## 💬 Usage Examples

Once connected, you can use natural language commands:

### Basic Project Search
```
"Show me all current projects from Project Insight"
```

### Specific Project Details  
```
"Get details for project ID 12345"
```

### Project Creation
```
"Create a new project called 'Website Redesign' with description 'Modernize company website'"
```

### Task Analysis
```
"Show me all tasks for project 'Website Redesign' and identify any that are behind schedule"
```

### Time Entry Analysis
```
"Show me all time entries for project OLX from February 1-10, 2026"
"How many billable hours did the team log on project X last week?"
"Get time entries for user John on project ABC"
```

### Timesheet Management
```
"Search for all timesheets pending approval"
"Show me Bill's timesheets from January"
"Submit my timesheet for this week"
```

## 🔍 Filter Parameters

Many tools support filtering to get exactly the data you need:

### get_project_time_entries
| Parameter | Type | Description |
|-----------|------|-------------|
| `projectId` | string | **Required**. Project ID (GUID) |
| `startDate` | string | Filter entries on/after this date (yyyy-MM-dd) |
| `endDate` | string | Filter entries on/before this date (yyyy-MM-dd) |
| `userId` | string | Filter to specific user's entries (GUID) |
| `taskId` | string | Filter to specific task's entries (GUID) |

**Output includes:**
- Summary stats: entry count, total hours, billable hours, billable amount
- Entry details: id, date, description, hours, billable hours, isBillable, userId, taskId

### search_timesheets
| Parameter | Type | Description |
|-----------|------|-------------|
| `userId` | string | Filter by timesheet owner (GUID) |
| `approverUserId` | string | Filter by designated approver (GUID) |
| `startDate` | string | Timesheets starting on/after this date |
| `endDate` | string | Timesheets ending on/before this date |
| `excludeUnsubmitted` | boolean | Exclude draft timesheets |
| `excludeApproved` | boolean | Exclude approved timesheets |
| `excludeRejected` | boolean | Exclude rejected timesheets |
| `excludePendingApproval` | boolean | Exclude timesheets awaiting approval |

### search_projects
| Parameter | Type | Description |
|-----------|------|-------------|
| `isActive` | boolean | Filter by active status |
| `isOpen` | boolean | Filter by open/closed status |
| `projectManagerId` | string | Filter by project manager (GUID) |
| `projectStatusId` | string | Filter by project status (GUID) |

### fields Parameter
Most "get" and "search" tools support a `fields` parameter to control response size:
- **Presets**: `minimal`, `summary`, `standard` (default), `full`
- **Custom**: Comma-separated field names like `Id,Name,Description`

## 🏗️ Architecture

```
project-insight/
├── core/          # PI API client, auth helpers, models, time-change logic
├── server/        # shared MCP wiring and tool handlers
├── src-stdio/     # local stdio host: .mcpb bundle and ProjectInsight.MCP tool
├── src-web/       # remote web host (ASP.NET Core, OAuth 2.1) for App Service
├── tests/         # xUnit tests, incl. end-to-end through the MCP SDK client
├── mcpb/          # bundle manifest and build staging
└── docs/          # setup and auth guides
```

## 🚀 Deployment

The remote web host deploys to Azure App Service. One-time setup is in
[bicep/README.md](../../bicep/README.md); later deploys run from `main` through
`.github/workflows/deploy.yml`. See [DEPLOYMENT.md](./docs/DEPLOYMENT.md).

## 🧪 Testing

```bash
dotnet test servers/project-insight/tests -c Release
```

To inspect the local tool's tools interactively:

```bash
npx @modelcontextprotocol/inspector projectinsight-mcp
```

## 📖 Documentation

- **[Authentication](./docs/AUTHENTICATION.md)** - SSO, API token and the remote sign-in
- **[Connecting to the remote server](../../docs/REMOTE.md)** - Claude, ChatGPT, Codex
- **[Deployment Guide](./docs/DEPLOYMENT.md)** - Azure App Service
- **[VS Code Setup](./docs/VS-CODE-SETUP.md)** - Development environment
- **[MCP Client Setup](./docs/MCP-CLIENT-SETUP.md)** - Local client configuration

## 🔧 Development

1. Install the .NET 10 SDK.
2. Run `dotnet test servers/project-insight/tests -c Release`.
3. For the web host, follow [docs/agents.md](../../docs/agents.md#getting-started).

### Debugging
Open the repository in VS Code and press F5 to run the "Debug web host" configuration.

## 🆘 Troubleshooting

### Common Issues

**Client Can't Connect**
- Local tool: check `projectinsight-mcp` is on your PATH and `ProjectInsight__BaseUrl` is set
- Remote server: see [docs/REMOTE.md](../../docs/REMOTE.md)

**Authentication Errors**
- Run the sign-in again, or check your static token is correct
- Check Project Insight API permissions
- Verify base URL format

**Time Entry Queries Return Too Much Data**
- Always specify `startDate` and `endDate` parameters
- Use `userId` or `taskId` filters to narrow results
- The API returns ALL project entries without date filters

## 🏷️ Version History

- **v1.0.0** - Initial release with 5 MCP tools
- **v1.0.1** - Fixed task filtering bug
- **v1.1.0** - Repository restructuring for multi-server support
- **v1.2.0** - Added timesheet tools (get, search, create, update, submit, approve, reject)
- **v1.3.0** - Added user tools (get_current_user, get_user, get_users, search_users)
- **v1.4.0** - Added `get_project_time_entries` with date/user/task filtering, improved tool documentation

---

**Part of [AVI MCP Tools](../../README.md)**