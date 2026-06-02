# Connecting Project Insight MCP Server to Microsoft Copilot Studio

This guide explains how to connect your Project Insight MCP server to Microsoft Copilot Studio, enabling AI agents to interact with your project management data using the Model Context Protocol (MCP).

## Overview

Microsoft Copilot Studio has native MCP support, allowing you to:
- Connect directly to your MCP server URL
- Use API key authentication (per-user credentials)
- Automatically discover all available tools
- Have tools update dynamically when your MCP server changes

## Prerequisites

1. **Project Insight MCP Server** deployed to Azure Functions
   - Endpoint: `https://your-function-app.azurewebsites.net/api/mcp`
   - Health check: `https://your-function-app.azurewebsites.net/api/health`
   - Server info: `https://your-function-app.azurewebsites.net/api/info`
   
2. **Microsoft Copilot Studio access**
   - [Free trial available](https://go.microsoft.com/fwlink/?LinkId=2107702)
   - Generative Orchestration must be enabled

3. **Project Insight API Token**
   - Each user needs their own API token from Project Insight

## Connection Methods

### Method 1: MCP Onboarding Wizard (Recommended)

1. **Open your agent in Copilot Studio**
   - Navigate to [Microsoft Copilot Studio](https://web.powerva.microsoft.com)
   - Open or create an agent

2. **Add MCP Server**
   - Go to **Tools** page for your agent
   - Select **Add a tool** → **New tool**
   - Select **Model Context Protocol**

3. **Configure Server Details**
   - **Server name**: `Project Insight`
   - **Server description**: `Project management server for accessing projects, tasks, time entries, timesheets, and resources from Project Insight`
   - **Server URL**: `https://your-function-app.azurewebsites.net/api/mcp`

4. **Configure Authentication**
   - Select **API key** as authentication type
   - **Type**: `Header`
   - **Header name**: `X-API-Token`
   
5. **Create Connection**
   - Select **Create**
   - On the Add tool dialog, select **Create a new connection**
   - Enter your personal Project Insight API token
   - Select **Add to agent**

### Method 2: Custom Connector via Power Apps

If you need more control over the connector configuration:

1. **Prepare OpenAPI Schema**
   
   Create a schema file (see [MCP Schema Example](#mcp-schema-example) below)

2. **Create Custom Connector**
   - Go to **Tools** → **Add a tool** → **New tool**
   - Select **Custom connector**
   - In Power Apps, select **New custom connector** → **Import OpenAPI file**
   - Import your schema file

3. **Configure and Test**
   - Complete the setup in Power Apps
   - Test the connector with your credentials

## MCP Schema Example

```yaml
swagger: '2.0'
info:
  title: Project Insight MCP
  description: MCP server for Project Insight project management
  version: 1.4.0
host: your-function-app.azurewebsites.net
basePath: /api
schemes:
  - https
securityDefinitions:
  apiKey:
    type: apiKey
    in: header
    name: X-API-Token
security:
  - apiKey: []
paths:
  /mcp:
    post:
      summary: Project Insight MCP Server
      description: Access projects, tasks, time entries, timesheets, and resources
      x-ms-agentic-protocol: mcp-streamable-1.0
      operationId: InvokeMCP
      consumes:
        - application/json
      produces:
        - application/json
      responses:
        '200':
          description: Success
        '202':
          description: Accepted (for notifications)
        '400':
          description: Bad Request
```

## Available Tools

Once connected, Copilot Studio will automatically discover these tools:

### Project Management
| Tool | Description |
|------|-------------|
| `search_projects` | Search and filter projects by status, manager, dates |
| `get_project` | Get detailed project information by ID |
| `create_project` | Create a new project |
| `update_project` | Update project properties |

### Task Management
| Tool | Description |
|------|-------------|
| `get_project_tasks` | Get all tasks for a project |

### Time Tracking
| Tool | Description |
|------|-------------|
| `get_project_time_entries` | Get time entries with date/user/task filtering |
| `get_my_timesheets` | Get current user's timesheets |
| `get_timesheet_time_entries` | Get entries for a specific timesheet |
| `submit_timesheet` | Submit timesheet for approval |
| `approve_timesheet` | Approve a submitted timesheet |
| `reject_timesheet` | Reject a timesheet with comments |

### Resource Management
| Tool | Description |
|------|-------------|
| `get_resource_by_id` | Get resource details |
| `get_project_resources` | Get project assignments |
| `get_resource_projects` | Get projects for a resource |

### Lookups & Configuration
| Tool | Description |
|------|-------------|
| `get_project_statuses` | Get available project statuses |
| `get_project_types` | Get available project types |
| `get_time_entry_types` | Get time entry type options |
| `get_custom_fields` | Get custom field definitions |

## Authentication Details

### API Key Authentication Flow

1. User configures their connection with their personal API token
2. Each request from Copilot Studio includes the token in the `X-API-Token` header
3. The MCP server validates the token and makes API calls on behalf of the user

### Getting Your API Token

1. Log into Project Insight
2. Navigate to your profile settings
3. Generate or copy your API token
4. Keep this token secure - it provides access to your account

## Testing Your Connection

After adding the MCP server to your agent:

1. **Test in Copilot Studio**
   - Open the test pane
   - Ask: "What projects do I have access to?"
   - The agent should use the `search_projects` tool

2. **Verify Tool Discovery**
   - Go to **Tools** in your agent
   - You should see all Project Insight tools listed
   - Each tool shows its description and parameters

## Troubleshooting

### Connection Fails
- Verify the server URL is correct and accessible
- Check that your API token is valid
- Ensure the MCP server is running (test with health endpoint)

### Tools Not Discovered
- Verify Generative Orchestration is enabled
- Check server description is clear and descriptive
- Restart the connection

### Authentication Errors
- Regenerate your API token
- Verify the header name is exactly `X-API-Token`
- Check token hasn't expired

## Example Prompts

Once configured, users can interact naturally:

- "Show me all active projects"
- "What tasks are in the Website Redesign project?"
- "Get time entries for the ERP Implementation project from January 2026"
- "Submit my timesheet for this week"
- "Who is assigned to the Mobile App project?"

## Related Documentation

- [MCP Protocol Documentation](https://modelcontextprotocol.io/)
- [Copilot Studio MCP Overview](https://learn.microsoft.com/en-us/microsoft-copilot-studio/agent-extend-action-mcp)
- [Connect to MCP Server](https://learn.microsoft.com/en-us/microsoft-copilot-studio/mcp-add-existing-server-to-agent)
- [Project Insight API Documentation](https://yourdomain.projectinsight.net/api/)

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.4.0 | 2026-02-10 | Initial Copilot Studio documentation |
