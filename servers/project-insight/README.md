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
- **Azure Functions**: Serverless deployment for scalability and reliability

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

- **.NET 10** SDK (builds both the stdio host and the .NET 8 Azure Functions host)
- **Azure Functions Core Tools v4** (for local development)
- **Project Insight** API access with valid API token
- **Azure subscription** (for cloud deployment)

## ⚡ Quick Start

### 1. Setup
```bash
cd servers/project-insight/src
```

### 2. Configure API Settings
Create `local.settings.json` with your Project Insight API details:
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet",
    "ProjectInsight__BaseUrl": "https://yourdomain.projectinsight.net/api/",
    "ProjectInsight__ApiToken": "your-api-token-here"
  }
}
```

### 3. Build and Run
```bash
dotnet build
func start --port 7071
```

### 4. Test the Server
```bash
# Health check
curl http://localhost:7071/api/health

# Test MCP tools
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'
```

## 🔧 Configuration

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `ProjectInsight__BaseUrl` | Project Insight API base URL | ✅ |
| `ProjectInsight__ApiToken` | API authentication token | ✅ |
| `ProjectInsight__Timeout` | API timeout in seconds (default: 30) | ❌ |

### MCP Endpoint

- **Local Development**: `http://localhost:7071/api/mcp`
- **Production**: `https://your-function-app.azurewebsites.net/api/mcp`

## 💬 Usage Examples

Once configured with GitHub Copilot, you can use natural language commands:

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
├── src/
│   ├── Functions/           # Azure Functions endpoints
│   ├── Services/           # Core business logic and API integration
│   ├── Models/             # Data models and DTOs
│   ├── MCP/               # MCP protocol implementation
│   └── Program.cs         # Dependency injection and startup
├── docs/                  # Documentation
│   ├── AUTHENTICATION.md  # API authentication guide
│   ├── DEPLOYMENT.md      # Azure deployment guide
│   └── *.md              # Other guides
└── deploy/               # Deployment scripts
    ├── deploy.sh         # Automated deployment script
    └── *.json           # Configuration files
```

## 🚀 Deployment

### Option 1: Automated Script
```bash
cd servers/project-insight/deploy
./deploy.sh
```

### Option 2: Manual Azure Deployment
See [DEPLOYMENT.md](./docs/DEPLOYMENT.md) for detailed instructions.

## 🧪 Testing

### Manual Testing
Use the provided HTTP file:
```bash
# From src directory
code ProjectInsightMCP.http
# Use VS Code REST Client extension
```

### Integration Testing
```bash
# Test with real Project Insight API
curl -X POST http://localhost:7071/api/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "search_projects", "arguments": {}}}'
```

## 📖 Documentation

- **[Authentication Setup](./docs/AUTHENTICATION.md)** - API token configuration
- **[Deployment Guide](./docs/DEPLOYMENT.md)** - Azure Functions deployment
- **[VS Code Setup](./docs/VS-CODE-SETUP.md)** - Development environment
- **[MCP Client Setup](./docs/MCP-CLIENT-SETUP.md)** - GitHub Copilot integration

## 🔧 Development

### Local Development
1. Install prerequisites
2. Configure `local.settings.json`
3. Run `func start --port 7071`
4. Test with HTTP requests or MCP client

### Debugging
Use VS Code with the included debug configuration:
1. Open VS Code in the `src` directory
2. Set breakpoints in your code
3. Press F5 to start debugging

## 🆘 Troubleshooting

### Common Issues

**Connection Refused**
- Ensure Azure Functions Core Tools is installed
- Check if port 7071 is available
- Verify local.settings.json configuration

**Authentication Errors**
- Validate API token is correct
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