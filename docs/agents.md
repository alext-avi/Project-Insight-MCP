# Project Insight MCP Server - Agent Knowledge Base

## Overview

This is a **Model Context Protocol (MCP) server** that integrates with the **Project Insight API** to provide AI agents with project management capabilities. Both hosts run on **.NET 10** with the official MCP C# SDK: a local stdio tool (`projectinsight-mcp`, also shipped as a Claude Desktop bundle) and a remote ASP.NET Core web host on Azure App Service. The web host fronts Project Insight's device activation with OAuth 2.1 and passes each user's own Project Insight token through.

## 🏗️ Architecture

### Projects (all under `servers/project-insight/`)

1. **`core/`**: Project Insight API client and auth helpers
   - `Services/ProjectInsightApiService.cs`: Project Insight API integration and data transformation
   - `Services/DeviceActivationService.cs`, `CredentialStorageService.cs`: SSO device activation and the stored credential
   - `Models/`, `TimeChanges/`: API DTOs and the time-change draft logic

2. **`server/`**: shared MCP wiring
   - `ProjectInsightServer.cs`: tool definitions and handlers used by both hosts
   - `TimeChangeTools.cs`: the `plan_time_changes` / `commit_time_changes` family

3. **`src-stdio/`**: the local stdio host (`projectinsight-mcp` dotnet tool and the `.mcpb` bundle)
   - Signs in with SSO device activation or a static token

4. **`src-web/`**: the remote host on App Service
   - `/mcp` (stateless streamable HTTP), the OAuth endpoints (`/register`, `/authorize`, `/token`) and `/healthz`
   - The access token a client holds is the user's own PI token; `Auth:AllowedPiUserIds` limits who may connect
   - Per-IP rate limits, Table and Blob storage for OAuth state and drafts
   - The sign-in tools (`complete_authentication`, `link_pi_account`, `complete_pi_link`) are hidden here

5. **`tests/`**: xUnit tests, including end-to-end through the MCP SDK client and the web host

Outside that folder: `bicep/` (infrastructure and the owner's one-time setup), `docs/REMOTE.md`
(connecting a client) and `.github/workflows/deploy.yml` (deploy from `main`).

## 🛠️ Available MCP Tools

### `search_projects`
- **Purpose**: Retrieves all projects from Project Insight
- **Parameters**: None (returns all projects)
- **Response**: Array of project objects with comprehensive metadata
- **Fields Returned**: ID, Name, Status, Manager, Dates, Budget, etc.

### Time-change drafts (`plan_time_changes` → `commit_time_changes`)

Prefer drafts over direct `create/update/delete_time_entry` calls when changing several entries or
anyone else's time. `plan_time_changes` takes an `intent` and up to 100 `changes`, writes nothing,
and returns a draft (or a `needs_clarification` result listing candidates when a project, task or person name is
ambiguous). Summarize the draft and ask the user; commit with `commit_time_changes` only after they
agree, or let them press Confirm on the card. A stale result means the affected days changed in
Project Insight since planning: read them again and re-plan. `undo_time_changes` drafts the reverse
of a commit for 24 hours.

## 🚀 Getting Started

### Prerequisites
- .NET 10 SDK
- Project Insight access (SSO, or an API token for the stdio tool)

### Build and test

```bash
dotnet build servers/project-insight/src-stdio
dotnet build servers/project-insight/src-web/ProjectInsightMCP.Web.csproj
dotnet test servers/project-insight/tests -c Release
```

Every project builds with 0 warnings in Release.

### Run the local stdio tool

```bash
ProjectInsight__BaseUrl=https://your-tenant.projectinsight.net/api/ \
  dotnet run --project servers/project-insight/src-stdio
```

Then ask the assistant to "link my Project Insight account", or set `ProjectInsight__ApiToken`.
Or use the MCP Inspector: `npx @modelcontextprotocol/inspector projectinsight-mcp`.

### Run the web host locally

`Storage__Mode=Local` keeps OAuth state in memory and drafts on local disk. It is accepted only when
`ASPNETCORE_ENVIRONMENT=Development`.

```bash
export ASPNETCORE_ENVIRONMENT=Development
export Storage__Mode=Local
export ProjectInsight__BaseUrl=https://your-tenant.projectinsight.net/api/
export ProjectInsight__DraftsPath=/tmp/pi-drafts
export Web__PublicBaseUrl=https://localhost:5001
export Auth__AllowedPiUserIds=<your-pi-user-id>
dotnet run --project servers/project-insight/src-web/ProjectInsightMCP.Web.csproj --urls https://localhost:5001
```

`Web__PublicBaseUrl` must be an https origin with no path, so trust the dev certificate first
(`dotnet dev-certs https --trust`). Check `https://localhost:5001/healthz`, then connect a client
to `https://localhost:5001/mcp`. Do not set `ProjectInsight__ApiToken` on the web host; it refuses
to start with it. Settings are listed in [CONFIGURATION.md](../CONFIGURATION.md).

## 🔧 Key Implementation Details

### API Service Configuration

The `ProjectInsightApiService` uses **POST method** (not GET) for project searches:

```csharp
// Correct implementation - uses POST with JSON body
var requestBody = new
{
    modelProperties = GetDefaultModelProperties(),
    // Additional filtering can be added here
};

var response = await _httpClient.PostAsJsonAsync(
    "Projects/Search", 
    requestBody, 
    cancellationToken
);
```

### Model Properties Selection

Project Insight API requires explicit field selection via `modelProperties`. We use a comprehensive default set:

```csharp
private static string[] GetDefaultModelProperties() => new[]
{
    "ProjectId", "ProjectName", "ProjectStatus", "ProjectManager",
    "StartDate", "EndDate", "Budget", "ActualCost", "Description",
    "Priority", "ClientName", "CreatedDate", "ModifiedDate",
    "PercentComplete", "ProjectType", "ProjectCode"
};
```

### Error Handling Strategy

1. **API Level**: HTTP status validation, JSON parsing errors
2. **Service Level**: Business logic validation, data transformation
3. **Host Level**: Request validation, response formatting
4. **Logging**: Comprehensive logging at all levels using ILogger

## 🔍 Troubleshooting

### Common Issues

1. **"Method not allowed" errors**:
   - Ensure using POST method for Project Insight API calls
   - Verify JSON body structure includes `modelProperties`

2. **Authentication failures**:
   - Stdio: run the link flow again, or check `ProjectInsight__ApiToken`
   - Web: reconnect the connector to sign in again
   - Validate the account has project read permissions

3. **Web host startup issues**:
   - The host refuses to start on missing or unsafe settings; read the exception message
   - Ensure the .NET 10 SDK is installed
   - Build first: `dotnet build`

4. **Empty project results**:
   - Verify API token permissions
   - Check Project Insight instance accessibility
   - Review host logs for API errors

### Debugging Commands

```bash
# Web host liveness
curl https://localhost:5001/healthz

# Run the tests
dotnet test servers/project-insight/tests -c Release

# Inspect the local tool
npx @modelcontextprotocol/inspector projectinsight-mcp
```

## 📁 File Structure

```
ProjectInsight/
├── servers/project-insight/
│   ├── core/        # PI API client, auth helpers, models, time-change logic
│   ├── server/      # shared MCP wiring and tool handlers
│   ├── src-stdio/   # local stdio host
│   ├── src-web/     # remote web host (OAuth 2.1, App Service)
│   ├── tests/       # xUnit tests
│   ├── mcpb/        # Claude Desktop bundle staging
│   └── docs/        # setup and auth guides
├── bicep/           # App Service infrastructure and one-time setup
├── docs/            # REMOTE.md, agents.md, integration guides
└── .github/workflows/deploy.yml
```

## 🔐 Security Considerations

1. **No shared token**: the web host passes each user's own PI token through and refuses to start with `ProjectInsight:ApiToken` set
2. **HTTPS**: the web host is HTTPS only
3. **Allow list**: `Auth:AllowedPiUserIds` limits who can connect
4. **Token handling**: bearer tokens, authorization codes and activation codes never appear in logs or telemetry
5. **Input Validation**: All MCP inputs are validated against schemas

## 🚀 Deployment

The web host runs on Azure App Service. The owner does the one-time setup from
[`bicep/README.md`](../bicep/README.md) (infrastructure, OIDC deploy identity, GitHub secrets).
After that, a push to `main` that touches the web host runs `.github/workflows/deploy.yml`.
Settings are in [CONFIGURATION.md](../CONFIGURATION.md).

### MCP Client Integration

- **Remote**: connect Claude, ChatGPT, Claude Code or Codex to `https://<app>/mcp` and sign in with your Project Insight account. Steps are in [REMOTE.md](./REMOTE.md).
- **Local**: install `ProjectInsight.MCP` and point the client at `projectinsight-mcp`:

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

## 🧪 Testing Strategy

### Unit Testing
- Service layer methods
- Model validation
- Error handling scenarios

### Integration Testing
- API connectivity
- Authentication flows
- End-to-end MCP protocol

### Manual Testing
- Health endpoint validation
- MCP tool execution
- Error scenario handling

## 📈 Performance Considerations

1. **API Caching**: Consider implementing caching for project data
2. **Connection Pooling**: HttpClient is properly configured for reuse
3. **Async Operations**: All API calls are asynchronous
4. **Error Recovery**: Retry logic for transient failures

## 🎯 MCP Client Compatibility

### **✅ Tested and Working Clients:**

1. **Warp Terminal** - Full compatibility (v2025.03.26 protocol)
2. **Claude Desktop** - Full compatibility  
3. **HTTP-based MCP clients** - Full streaming support
4. **VS Code MCP extensions** - Development testing verified

### **Configuration for Warp (remote):**
```json
{
  "mcpServers": {
    "project-insight": {
      "transport": {
        "type": "http",
        "url": "https://<app>/mcp"
      },
      "name": "Project Insight",
      "description": "Project management tools"
    }
  }
}
```

The remote server requires an OAuth sign-in, so the client must support MCP authorization.

## 🔄 Future Enhancements

### Potential MCP Tools to Add

1. **`get_project`** - ✅ IMPLEMENTED - Retrieve specific project details
2. **`create_project`** - ✅ IMPLEMENTED - Create new projects  
3. **`update_project`** - ✅ IMPLEMENTED - Modify existing projects
4. **`get_project_tasks`** - ✅ IMPLEMENTED - Get project tasks
5. **`get_resources`** - Retrieve project resources
6. **`get_timesheets`** - Access time tracking data

### Advanced Features

1. **Real-time Updates**: WebSocket integration for live data
2. **Batch Operations**: Multiple project operations in single request
3. **Advanced Filtering**: Complex search criteria
4. **Data Validation**: Schema-based validation for all operations
5. **Audit Logging**: Track all API operations for compliance

## 📚 Additional Resources

- [Model Context Protocol Specification](https://modelcontextprotocol.io/docs)
- [Azure App Service Documentation](https://learn.microsoft.com/en-us/azure/app-service/)
- [Project Insight API Documentation](https://api.projectinsight.net/help)
- [.NET 10 Documentation](https://learn.microsoft.com/en-us/dotnet/)

## ✅ Validation Checklist

Before making changes to this system:

- [ ] Builds with 0 warnings: `dotnet build servers/project-insight/src-web/ProjectInsightMCP.Web.csproj -c Release` and the same for `src-stdio`
- [ ] Tests pass: `dotnet test servers/project-insight/tests -c Release`
- [ ] Tool list snapshot still matches (part of the test run)
- [ ] Web host starts locally with `Storage__Mode=Local` and `/healthz` responds
- [ ] No tokens, tenant hosts or Azure ids in any file (the repo is public)

---

**Last Updated**: November 9, 2026  
**Status**: ✅ Fully Functional