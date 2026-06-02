# Resource Assignment Tools Design

## Summary

Add MCP tools for managing user-to-project and user-to-task assignments in the Project Insight MCP server. Currently the server can only read project resources via `get_project_resources`; this adds full CRUD for project resource assignments and task resource assignments.

## Tools

### 1. `add_project_resource`

Assign a user to a project as a team member/contributor.

**Parameters:**
| Name | Type | Required | Description |
|------|------|----------|-------------|
| projectId | string (GUID) | yes | The project to add the user to |
| userId | string (GUID) | yes | The user to assign |
| isProjectManager | boolean | no | Whether the user is a project manager on this project |
| isProjectScheduler | boolean | no | Whether the user is a project scheduler |
| percentAllocationDefault | number | no | Default allocation percentage (0-100) |
| resourceTypeDefaultId | string (GUID) | no | Resource type classification (e.g., Senior Consultant) |

**API call:** `POST /api/project-resource` with `ProjectResource` body (no `id` field).

**Response:** Returns the `TaskSaveResult` with the new resource assignment ID, plus a human-readable summary.

### 2. `update_project_resource`

Update an existing project resource assignment (change role, allocation, PM status, etc.).

**Parameters:**
| Name | Type | Required | Description |
|------|------|----------|-------------|
| id | string (GUID) | yes | The project resource ID (from `get_project_resources`) |
| isProjectManager | boolean | no | Update PM status |
| isProjectScheduler | boolean | no | Update scheduler status |
| isActive | boolean | no | Activate/deactivate the assignment |
| percentAllocationDefault | number | no | Update allocation % |
| resourceTypeDefaultId | string (GUID) | no | Update resource type |

**API call:** `POST /api/project-resource` with `ProjectResource` body (with `id` field set).

**Response:** Returns `TaskSaveResult` plus a human-readable summary.

### 3. `remove_project_resource`

Remove a user from a project (delete the resource assignment).

**Parameters:**
| Name | Type | Required | Description |
|------|------|----------|-------------|
| id | string (GUID) | yes | The project resource ID to remove |

**API call:** `DELETE /api/project-resource/{id}`

**Response:** Success/failure message.

### 4. `add_task_resource`

Assign a user to a specific task within a project.

**Parameters:**
| Name | Type | Required | Description |
|------|------|----------|-------------|
| taskId | string (GUID) | yes | The task to assign the user to |
| userId | string (GUID) | yes | The user to assign |
| workHours | number | no | Planned work hours for this assignment |
| percentAllocationDefault | number | no | Allocation percentage |

**API call:** `POST /api/task-resource` with `TaskResource` body (no `id` field).

**Response:** Returns `TaskSaveResult` with the new assignment ID.

### 5. `remove_task_resource`

Remove a user from a task.

**Parameters:**
| Name | Type | Required | Description |
|------|------|----------|-------------|
| id | string (GUID) | yes | The task resource ID to remove |

**API call:** `DELETE /api/task-resource/{id}`

**Response:** Success/failure message.

## Implementation Details

### Models (`ProjectInsightModels.cs`)

Add a `TaskResource` model:

```csharp
public class TaskResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("task_Id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("user_Id")]
    public string? UserId { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }

    [JsonPropertyName("workHours")]
    public decimal? WorkHours { get; set; }

    [JsonPropertyName("percentAllocationDefault")]
    public decimal? PercentAllocationDefault { get; set; }
}
```

No changes needed to the existing `ProjectResource` model.

### API Service (`ProjectInsightApiService.cs`)

Add 5 methods following the existing save/delete patterns:

1. `SaveProjectResourceAsync(ProjectResource resource)` - POST /api/project-resource
2. `DeleteProjectResourceAsync(string resourceId)` - DELETE /api/project-resource/{id}
3. `SaveTaskResourceAsync(TaskResource resource)` - POST /api/task-resource
4. `DeleteTaskResourceAsync(string resourceId)` - DELETE /api/task-resource/{id}

Each follows the same pattern as `SaveTimeEntryAsync` / `DeleteTimeEntryAsync`.

### MCP Server (`McpServerService.cs`)

Add 5 entries to the tool switch statement and 5 tool implementations, plus 5 tool definitions in `InitializeTools()`. Each tool implementation:

1. Extracts and validates parameters from the arguments dictionary
2. Calls the appropriate API service method
3. Returns a human-readable summary + JSON data

### OpenAPI Spec (`openapi.yaml`)

Add corresponding endpoint definitions for the 5 new operations.

## File Changes

| File | Change |
|------|--------|
| `Models/ProjectInsightModels.cs` | Add `TaskResource` model |
| `Services/ProjectInsightApiService.cs` | Add 4 API methods (save/delete for project-resource and task-resource) |
| `Services/McpServerService.cs` | Add 5 tool cases, 5 implementations, 5 tool definitions |
| `copilot-agent/openapi.yaml` | Add 5 new endpoint definitions |

## Risks

- The PI API endpoint paths (`/api/project-resource`, `/api/task-resource`) are inferred from the existing search endpoint pattern. If the actual endpoints differ, the API calls will fail with 404s and need path adjustment.
- Task resource endpoints may have different required fields than assumed. The implementation should handle API errors gracefully and surface them to the user.
