# Project Insight MCP Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server for integrating [Project Insight](https://www.projectinsight.com/) with AI assistants like GitHub Copilot, Claude Desktop, and VS Code.

## Installation

### As a .NET Tool (Recommended)

```bash
# Install globally from nuget.org
dotnet tool install -g ProjectInsight.MCP

# Or install locally in a project
dotnet new tool-manifest
dotnet tool install ProjectInsight.MCP

# Update to latest version
dotnet tool update -g ProjectInsight.MCP
```

### As a Claude Desktop extension (.mcpb bundle)

For one-click setup in Claude Desktop (no .NET SDK required), download the
latest `project-insight-<version>.mcpb` from the
[**Releases page**](https://github.com/alext-avi/Project-Insight-MCP/releases)
and add it as an extension, then set your `ProjectInsight__BaseUrl`.

## Authentication

You have two options. **SSO (recommended)** ties each MCP session to the user running the client — no shared tokens, no copy-paste, and the per-user credential is cached for future sessions.

| Option | When to use |
|--------|-------------|
| **SSO device activation** (recommended) | Normal end-user setup. Sign in through your org's SSO; no token handling. |
| **Static API token** | Service accounts, CI, or when SSO is unavailable. |

### First-time setup (60 seconds)

1. Copy the SSO config block from [Option 1](#option-1-sso-device-activation-recommended) into your client's MCP config — only `ProjectInsight__BaseUrl` is required.
2. Restart your client and ask the assistant: **"link my Project Insight account"**.
3. Sign in via your browser when prompted, paste the activation code back into the chat, and you're done — credentials are cached for one year.

### Configuration variables

| Variable | Required | Description |
|----------|----------|-------------|
| `ProjectInsight__BaseUrl` | Yes | Your Project Insight API URL (e.g., `https://your-tenant.projectinsight.net/api/`) |
| `ProjectInsight__ApiToken` | Only for static-token mode | A Project Insight API token |

## Option 1: SSO Device Activation (Recommended)

Configure only the base URL — no token needed:

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

On first use, ask your AI assistant to **"link my Project Insight account"**. The server will:

1. Call the `link_pi_account` tool, which opens Project Insight's sign-in page in your browser.
2. Walk you through SSO (your organization's identity provider).
3. Display an activation code in the browser.
4. You paste the code back into the chat; the assistant calls `complete_pi_link` with it.
5. The per-user API token is saved to `~/.projectinsight/credentials.json` and reused on future runs.

The token is valid for one year. If it expires, just run the link flow again.

## Option 2: Static API Token

Add the token as an environment variable:

```json
{
  "mcpServers": {
    "project-insight": {
      "command": "projectinsight-mcp",
      "env": {
        "ProjectInsight__BaseUrl": "https://your-tenant.projectinsight.net/api/",
        "ProjectInsight__ApiToken": "your-api-token"
      }
    }
  }
}
```

To generate a token: log into Project Insight → **User Settings** → **API Access**.

## Client-specific config locations

The JSON above goes in whichever file your client reads:

| Client | Config file |
|--------|-------------|
| VS Code / GitHub Copilot | `.vscode/mcp.json` (workspace) or `~/.vscode/mcp.json` (user) |
| Claude Desktop (macOS) | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Claude Desktop (Windows) | `%APPDATA%\Claude\claude_desktop_config.json` |
| Claude Code | `~/.claude.json` |

> **Tip:** If `projectinsight-mcp` is not on your PATH, use the full path (e.g., `~/.dotnet/tools/projectinsight-mcp`).

## Field Selection (Token Efficiency)

**IMPORTANT:** All tools support a `fields` parameter to control response size and reduce token usage. This is critical for staying within context limits.

### Options

| Option | Description | Example |
|--------|-------------|---------|
| `minimal` | `Id,Name` only | `"fields": "minimal"` |
| `summary` | Key fields (default) | `"fields": "summary"` |
| `standard` | Common fields | `"fields": "standard"` |
| `full` | All fields (use sparingly) | `"fields": "full"` |
| **custom** | Specific fields you need | `"fields": "Id,Name,WorkPercentComplete"` |

### Custom Field Selection

Pass any comma-separated list of field names to get exactly what you need:

```json
// Get only completion status for projects
{ "fields": "Id,Name,WorkPercentComplete,IsOpen" }

// Get task assignments with dates
{ "fields": "Id,Name,AssignedTo_Id,StartDateTimeUTC,EndDateTimeUTC" }

// Get user contact info
{ "fields": "Id,Name,Email,PhoneNumber,Department" }
```

### Best Practices

1. **Use custom fields** when you only need specific data - more efficient than presets
2. **Start with `minimal`** for discovery, then request specific fields
3. **Avoid `full`** - returns all fields and quickly exhausts token limits

## Available Tools

### Authentication

| Tool | Description |
|------|-------------|
| `link_pi_account` | Start SSO device activation — opens the PI sign-in page in your browser |
| `complete_pi_link` | Complete linking by providing the activation code shown after sign-in |

### Project Management

| Tool | Description |
|------|-------------|
| `search_projects` | Search/filter projects by status, manager, dates |
| `get_project` | Get project details by ID |
| `create_project` | Create a new project |
| `update_project` | Update project properties |
| `get_project_tasks` | Get tasks for a project |
| `get_project_resources` | Get team members assigned to a project |

### Task Management

| Tool | Description |
|------|-------------|
| `get_task` | Get task details by ID |
| `create_task` | Create a new task in a project |
| `update_task` | Update task properties |

### Time Management

| Tool | Description |
|------|-------------|
| `get_timesheet` | Get timesheet by ID |
| `search_timesheets` | Search timesheets by user, date, status |
| `get_my_time_status` | Get current user's time status |
| `create_timesheet` | Create a new timesheet |
| `submit_timesheet` | Submit for approval |
| `approve_timesheet` | Approve a timesheet |
| `reject_timesheet` | Reject a timesheet |
| `get_project_time_entries` | Get time entries for a project |
| `search_time_entries` | Search time entries across projects with filters |
| `create_time_entry` | Create a time entry against a task |

### Rate Cards & Billing

| Tool | Description |
|------|-------------|
| `get_project_rate_cards` | Get rate cards for a project |
| `get_rate_card` | Get rate card details by ID |
| `save_rate_card_rate` | Create or update a rate on a rate card |
| `delete_rate_card_rate` | Delete a rate from a rate card |
| `get_resource_types` | Get available resource types for rate cards |

### User Management

| Tool | Description |
|------|-------------|
| `get_current_user` | Get authenticated user's profile |
| `get_user` | Get user profile by ID |
| `get_users` | Get multiple users by IDs |
| `search_users` | Search users by name |

## Available Fields Reference

### Project Fields
`Id`, `Name`, `Description`, `ItemNumber`, `IsActive`, `IsOpen`, `IsBillable`, `WorkPercentComplete`, `StartDateTimeUTC`, `EndDateTimeUTC`, `ActualStartDateTimeUTC`, `ActualEndDateTimeUTC`, `BaselineStartDateTimeUTC`, `BaselineEndDateTimeUTC`, `PrimaryProjectManager_Id`, `ProjectStatus_Id`, `Priority_Id`, `Client_Id`, `WorkHours`, `ActualWorkHours`, `BudgetHours`, `RemainingWorkHours`, `UrlFull`, `CreatedByUser_Id`, `CreatedDateTimeUTC`, `ModifiedDateTimeUTC`

### Task Fields
`Id`, `Name`, `Description`, `ItemNumber`, `Project_Id`, `Parent_Id`, `IsActive`, `IsMilestone`, `IsSummary`, `WorkPercentComplete`, `StartDateTimeUTC`, `EndDateTimeUTC`, `ActualStartDateTimeUTC`, `ActualEndDateTimeUTC`, `WorkHours`, `ActualWorkHours`, `RemainingWorkHours`, `AssignedTo_Id`, `TaskStatus_Id`, `Priority_Id`, `UrlFull`

### Project Resource Fields
`Id`, `ProjectId`, `UserId`, `User` (nested: `Id`, `Name`, `Email`, `FirstName`, `LastName`), `IsActive`, `IsProjectManager`, `IsPrimaryProjectManager`, `IsProjectScheduler`, `PercentAllocationDefault`, `ProjectResourceRoleDescription`, `ActualHours`, `TaskCount`, `OpenTaskCount`, `TaskBehindScheduleCount`

### Timesheet Fields
`Id`, `Name`, `User_Id`, `StartDate`, `EndDate`, `TotalHours`, `TotalBillableHours`, `TotalNonBillableHours`, `IsSubmitted`, `IsApproved`, `IsRejected`, `SubmittedDateTimeUTC`, `ApprovedDateTimeUTC`, `RejectedDateTimeUTC`, `ApprovedBy_Id`, `TimeSheetEntries` (nested)

### User Fields
`Id`, `Name`, `FirstName`, `LastName`, `Email`, `IsActive`, `Title`, `Department`, `PhoneNumber`, `MobileNumber`, `EmployeeNumber`, `HireDate`, `Address`, `City`, `State`, `PostalCode`, `Country`, `TimeZone_Id`, `Manager_Id`, `UrlFull`

## Requirements

- .NET 10 Runtime (if not using self-contained build)
- Valid Project Insight account with API access

## License

MIT License - See [LICENSE](https://github.com/alext-avi/Project-Insight-MCP/blob/main/LICENSE) for details.

## Support

- [GitHub Issues](https://github.com/alext-avi/Project-Insight-MCP/issues)
- [Project Insight Documentation](https://www.projectinsight.com/support)
