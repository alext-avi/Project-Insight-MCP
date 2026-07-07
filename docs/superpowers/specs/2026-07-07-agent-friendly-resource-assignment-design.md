# Agent-Friendly Resource Assignment — Design

## Context & Problem

AI agents using the Project Insight MCP server report that they **cannot assign
resources to projects**. Investigation shows the capability exists
(`add_project_resource` and `add_task_resource`), but agents **decline to attempt
it** — they conclude from the tool metadata that no such capability is available
and never call the tool.

This is a **discoverability** problem, not a runtime failure. An agent decides
"can I do this?" purely from the tool **name + description**. The current metadata
reads as "no such capability" because:

- **Vocabulary gap.** The user/agent thinks "assign / staff / allocate a resource";
  the tool is named `add_project_resource` and described as *"Add a user as a team
  member/contributor to a project."* Neither the verb "assign" nor role words
  (project manager, scheduler) nor "allocate" appear where the agent looks.
- **Under-sold capability.** The schema supports PM/scheduler roles and allocation %,
  but the description never mentions them, so an agent won't attempt a role-based
  assignment through it.
- **Prerequisites read as blockers.** `add_task_resource` says *"The user should
  already be a project resource"* — an agent reads this as a precondition it cannot
  satisfy and bails.

Separately, a latent **honesty bug** compounds the problem: even if an agent *did*
call `add_project_resource` with `isProjectManager` / `percentAllocationDefault` /
etc., those params are **silently dropped** (see below). So improving the
description alone would advertise behavior the tool doesn't perform.

This design builds on the original tool design
([2026-03-27-resource-assignment-tools-design.md](./2026-03-27-resource-assignment-tools-design.md)).

## Scope

The resource-assignment tool family in `servers/project-insight/src`:
`add_project_resource`, `update_project_resource`, `remove_project_resource`,
`get_project_resources`, `add_task_resource`, `remove_task_resource`.

**In scope:** tool descriptions (metadata), the `add_project_resource` honesty fix,
`add_task_resource` project auto-ensure, actionable error messages, and a first
unit-test project.

**Out of scope (non-goals):** renaming tools; accepting human identifiers
(email/project name) instead of GUIDs; a new consolidated `assign_*` entry tool;
any change to the PI REST API. These were considered and deferred to keep the change
focused on the reported symptom.

## Design

### 1. Metadata rewrite — discoverability (all 6 tools)

Keep the **names** as-is — renaming breaks existing clients/prompts and the name is
not the core problem. Rewrite each **description** to:

1. Lead with the verbs agents match on: *assign / staff / allocate / add to*.
2. State the full capability inline (roles + allocation) so the tool is trusted for
   role-based assignment.
3. Reframe prerequisites as guidance, not blockers.

Target descriptions:

- **`add_project_resource`** —
  *"Assign (staff / allocate) a user to a project — as a team member, project
  manager, or scheduler, with an optional default allocation %. This is THE tool for
  putting a person on a project. Idempotent and safe to re-run. Pass `projectId` +
  `userId` (GUIDs); if you only have names, resolve them with `search_projects` /
  `search_users` first."*
- **`update_project_resource`** —
  *"Change an existing project assignment: promote to/from project manager or
  scheduler, adjust allocation %, activate/deactivate, or set resource type. Needs
  the resource assignment `id` from `get_project_resources`."*
- **`remove_project_resource`** —
  *"Unassign a user from a project (delete the assignment). Needs the resource
  assignment `id` from `get_project_resources`."*
- **`get_project_resources`** —
  *"List the users assigned to a project (team members, PMs, schedulers) with their
  roles and allocation. Use this to see who is staffed on a project or to get a
  resource assignment `id` for update/remove."*
- **`add_task_resource`** —
  *"Assign (staff) a user to a specific task, with optional planned work hours and
  allocation %. If the user isn't on the project yet, they are added to the project
  automatically. Pass `taskId` + `userId` (GUIDs)."*
- **`remove_task_resource`** —
  *"Unassign a user from a task (delete the task assignment). Needs the task resource
  assignment `id`."*

Individual parameter descriptions get the same verb treatment (e.g. `userId`:
*"The user (GUID) to assign"*).

### 2. Honesty fix — `add_project_resource` (behavior)

**Today** (`AddProjectResourceAsync`, `McpServerService.cs:674`): calls only
`EnsureProjectResourceAsync(projectId, userId)` and **silently ignores**
`isProjectManager`, `isProjectScheduler`, `percentAllocationDefault`,
`resourceTypeDefaultId`. The switch to the idempotent `ensure-user-is-project-resource`
endpoint (vs. the original spec's `POST /api/project-resource`) gained idempotency
but dropped param support.

**Fix** — keep idempotency *and* honor the params:

1. Call `EnsureProjectResourceAsync(projectId, userId)` as today. On failure, return
   the (improved, see §4) error.
2. If **any** of the optional params was supplied, take the resource id from
   `response.Data.SavedId` and call `SaveProjectResourceAsync` with a `ProjectResource`
   whose `Id` = that id, `ProjectId`/`UserId` set, and the supplied optional fields
   populated. (`TaskSaveResult.SavedId` confirmed to carry the project-resource id;
   `ProjectResource` has `IsProjectManager`, `IsProjectScheduler`,
   `PercentAllocationDefault`, `ResourceTypeDefaultId`.)
3. If **no** optional params were supplied, do nothing further — behavior is
   unchanged (single idempotent call).

**Result reporting:** the success message states what was actually set, e.g.
*"Assigned Jane Doe to project X as Project Manager at 50% allocation (resource id
…)."* If step 1 succeeds but step 2 fails, report **partial success explicitly**
(*"User added to the project, but setting the project-manager role failed: …"*) —
never a blanket success.

### 3. Task auto-ensure — `add_task_resource` (behavior)

`AddTaskResourceAsync` (`McpServerService.cs:808`) already honors `workHours` /
`percentAllocationDefault` (no drop bug), but requires the user to already be a
project resource. Make the §1 description truthful:

1. Resolve the task's project: `GetTaskAsync(taskId, modelProperties: "Project_Id")`
   → `Project_Id`.
2. `EnsureProjectResourceAsync(projectId, userId)` (idempotent — no-op if already a
   member).
3. `SaveTaskResourceAsync(...)` as today.

If the task lookup fails, surface an actionable error (§4) rather than proceeding.

### 4. Actionable errors (light touch, all affected tools)

Failures currently pass through the raw API body. Add a short, agent-actionable
prefix based on status, then append the raw body for debugging:

- `404` → *"Project, task, or user not found — verify the GUIDs with
  `search_projects` / `get_project_tasks` / `search_users`."*
- `403` → *"Insufficient permissions to modify resources on this project."*
- other → generic *"The Project Insight API rejected the request:"* prefix.

Keep this a small helper; do not over-engineer status coverage.

## Components & Data Flow

```
add_project_resource(projectId, userId, [role/allocation])
  └─ EnsureProjectResourceAsync ──▶ SavedId
        └─ (if optional params) SaveProjectResourceAsync(id=SavedId, …fields)

add_task_resource(taskId, userId, [workHours/allocation])
  └─ GetTaskAsync(taskId, "Project_Id") ──▶ projectId
        └─ EnsureProjectResourceAsync(projectId, userId)   (idempotent)
              └─ SaveTaskResourceAsync(taskId, userId, …fields)
```

Each unit keeps one clear purpose: description strings (pure metadata, no logic),
the two tool handlers (orchestrate ensure→save), and an error-formatting helper
(status → message). No new API-service methods are required — all callees already
exist.

## Testing / Verification

No test project exists in the repo today. Add a **minimal xUnit project**
(`servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`) with a mocked
`IProjectInsightApiService`, covering:

- `add_project_resource` **with** an optional param triggers a follow-up
  `SaveProjectResourceAsync` carrying that param; **without** optional params it does
  **not** (single `ensure` call).
- Partial-failure path (ensure OK, save fails) returns `IsError=true` with a message
  that mentions the user was added but the role/allocation was not set.
- `add_task_resource` calls `GetTaskAsync` then `EnsureProjectResourceAsync` then
  `SaveTaskResourceAsync`, in that order, with the resolved project id.
- Error helper maps 404/403 to the expected prefixes.

Description changes verified by asserting the substrings ("assign", "project
manager", "allocation", "automatically") are present in the relevant tool
definitions. Manual smoke test via the existing `src-stdio` server.

## Risks

- `EnsureProjectResourceAsync` must return a usable `SavedId` for an existing member
  (idempotent re-add), not just for a brand-new one. If it returns empty for an
  already-existing member, the follow-up save needs the id from a
  `get_project_resources` lookup instead. Verify during implementation.
- The follow-up `SaveProjectResourceAsync` (update path) may require fields beyond
  `Id` that the PI API validates. If so, hydrate from the ensure result before
  saving.
- `GetTaskAsync` field name for the project (`Project_Id` vs `ProjectId`) — confirm
  the model property during implementation.

## File Changes

| File | Change |
|------|--------|
| `Services/McpServerService.cs` | Rewrite 6 tool descriptions; extend `AddProjectResourceAsync` (honesty fix) and `AddTaskResourceAsync` (auto-ensure); add error-format helper |
| `tests/ProjectInsightMCP.Tests.csproj` (new) | Minimal xUnit project + tests above |
