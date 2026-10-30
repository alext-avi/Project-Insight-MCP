# Phase 1: reviewable time-entry changes with an MCP Apps card

**Status:** implemented on branch `phase1-time-changes` (PR pending live test) · **Issue:** #3 · **Release:** 2.1.0
**Builds on:** Phase 0 (2.0.0, SDK on .NET 10) · **Related:** #4 (remote host), #5 (SDK-style tools)

## Goal

Give agents a safer way to change time entries. The agent drafts its changes, then the user sees them as an interactive card in Claude, in the context of each affected week, and confirms, cancels or undoes them there. Where cards don't render (e.g. Claude Code), the same flow works as plain text.

The draft flow is **additive**. `create_time_entry`, `update_time_entry` and `delete_time_entry` stay. Each customer's harness decides which tools to allow, e.g. through Claude's per-tool permissions.

## Decisions

| Topic | Decision |
|---|---|
| Existing time-entry tools | Kept. Gain `destructiveHint`; no other change. |
| Where drafts live | Server-owned records keyed by PI user id, not MCP session. Local: files under `~/.projectinsight/drafts/`. Remote (#4): Azure Table behind the same interface. |
| Whose time | Anything PI lets the caller edit. The draft flow never permits more than the existing tools do. |
| Locked/submitted timesheets | Warning on the row, not a block. PI's own refusal fails the commit, which is then rolled back. |
| Tool style | The six new tools are SDK-native (`McpServerTool`) and use `ModelContextProtocol.Extensions.Apps` 2.3.0. The existing tools stay on the Phase 0 adapter. The SDK merges the two lists and dispatches by name. |
| Scope | Local stdio host only. Logic and card live in Core so #4 can serve them. |
| Delivery | One PR, tested live against real data on the developer's own time and cleaned up with undo. |

## Tools

All six return `structuredContent` matching a declared `outputSchema`. Each also returns a text block that reads on its own and tells the model what to do next. All six link to `ui://project-insight/time-changes.html` via `_meta.ui.resourceUri`.

| Tool | Visible to | Input | Notes |
|---|---|---|---|
| `plan_time_changes` | model | `intent` (one line), `changes[]` (1–100) | Writes nothing. Returns a draft, or `needs_clarification` with candidates and no draft. |
| `commit_time_changes` | model · `destructiveHint` | `draftId` | Channel `agent`. |
| `confirm_time_changes` | app only | `draftId`, `confirmToken` | The card's Confirm. Channel `confirmed_in_chat`. |
| `cancel_time_changes` | model + app | `draftId` | |
| `undo_time_changes` | model + app | `receiptId` | Returns a new draft reversing the receipt. That draft needs its own commit or confirm. |
| `get_time_changes` | model + app | `id` | Draft or receipt with current status. The card calls it on load. |

Change shapes:

- `create {date, hours, taskId | project + task names, description?, billable?, user?}`. `user` is an id or a name and defaults to the caller. Names resolve like project and task names.
- `update {timeEntryId, …changed fields}` (date, hours, task, description, billable)
- `delete {timeEntryId}`

Ambiguous or unresolved names return `needs_clarification` listing candidates per change, and no draft is created.

Example text results:

- Plan: "Draft d_7kq2: 5 changes across 2 days for 2 people. Nothing has changed yet. Summarize the draft and ask the user to confirm."
- Commit: "Committed d_7kq2. Undo is available until <time>."
- Stale: "Nothing was changed: Project Insight changed after this draft was made (You · Tue 27 Oct). Draft the changes again."

The existing `ToolCallGate` is moved from the adapter's call handler into an SDK call-tool filter, so tool calls of both styles run one at a time.

## Draft model

```
Draft {
  id, status, intent, proposedBy (PI user id), proposedAt, expiresAt (= proposedAt + 24h),
  committedAt?, channel? (agent | confirmed_in_chat), undoOf?, undoOfCommittedAt?, revertedBy?,
  confirmTokenHash, rows[], weeks[], dayFingerprints{(userId, date) → hash}, totals,
  error? (failed commits), outcomeUnknown? (failed commits that may have left changes in PI)
}
Row  { index, action, userId, userName, timeEntryId?, savedId?, date, project, task,
       before?, after?, warnings[], outcome? (applied | reversed | rejected | not_attempted | reverse_failed | unknown) }
Week { userId, userName, start, end, timesheetUrl?, days[{date, totalBefore, totalAfter, entries[] (context rows)}] }
Status: draft | committed | stale | expired | cancelled | failed
```
The committer is always the proposer (only the proposer can commit), so no separate committedBy is stored.

- A **receipt** is the committed draft. It keeps the same id and adds `committed*`, `channel` and the PI-saved ids.
- **Redaction:** drafts, receipts and the card hold hours, project, task, description and billable flag only. `RateBill`, `RateBurden`, `BillableTotal` and `ActualTotal` are never requested into a draft or stored.
- **Weeks:** the period of that person's PI timesheet containing the date. PI creates timesheets on first submit, so a current week often has none: then the period copies the shape of the person's latest earlier timesheet (weekly or two-weekly, looking back 26 weeks), so a Sunday–Saturday company gets Sunday–Saturday weeks. Monday–Sunday only when there is no timesheet history. The planner reads each person's timesheets once and each touched person-week's entries once. The same data feeds context rows, totals and fingerprints.
- **Warnings:** timesheet submitted, approved or locked; day over 12h; hours not on a quarter-hour; likely duplicate (same user, day and task with equal hours or description); someone else's time.

## Store

`IDraftStore` with `GetAsync`, `CreateAsync`, `TryTransitionAsync`, `PruneAsync(ownerId)` and `PruneAllAsync()`.

The local `FileDraftStore`:

- Writes one JSON file per record at `~/.projectinsight/drafts/<piUserId>/<id>.json`, mode 0600 and directory 0700. Each write goes to a temp file and is then renamed.
- `TryTransition` holds an exclusive lock file for the read-check-write, so two server processes (e.g. two Claude windows) cannot both move a draft out of `draft`.
- Prunes records 7 days after their last change, on startup and on each plan.

## Commit

1. Load the draft and check the caller is `proposedBy`, the status is `draft` and it hasn't expired. For confirm, also check the token hash; the token is single-use.
2. Move the draft to `committing`. This is internal and never shown; it stops a second process committing the same draft.
3. Re-read the touched person-weeks and recompute the fingerprint of each changed day. If any differ, mark the draft `stale` (naming the days) and write nothing.
4. Apply the rows in order, recording each `savedId`.
5. On the first failure, reverse the applied rows in reverse order:
   - created → delete
   - updated → restore the before-state
   - deleted → recreate from the before-state (new id)

   Mark the draft `failed` with PI's error and a per-row outcome. A reversal that itself fails is `reverse_failed` and is shown in red with a link to the entry.
6. On success, mark the draft `committed`.

A write whose result PI didn't report (a timeout or connection failure, as opposed to an HTTP error or a `success: false` answer) may have been applied. That row is marked `unknown`, not `rejected`. The earlier rows are still reversed. The draft is marked `failed` with `outcomeUnknown`, and the error names the change and its day(s). The text and card never say "nothing was changed"; they tell the user to check the named days in PI before drafting again.

A draft left in `committing` for over 10 minutes (e.g. the process died mid-commit) is reported as `failed` with `outcomeUnknown`. The card and text name the touched days so the user can check them in PI.

Fingerprint = hash over the day's entries (id, task, hours, billable, description, sorted by id) plus the timesheet's submitted, approved and locked flags.

**Undo** is allowed for 24h after commit, by the proposer only. It builds a reverse draft (`undoOf` set) from the receipt's rows and saved ids, through the same planner (fresh context and fingerprints). It is then committed or confirmed like any draft.

## Security

- Only the proposer can read (`get_time_changes`), commit, confirm, cancel or undo a draft, since drafts can hold other people's time. "Proposer" is the PI user id of the caller's credentials.
- The confirm token is 128 random bits, returned only in the plan result's `_meta` (per spec, not intended for model context) and stored as SHA-256. It is single-use.
- Beyond that, PI's permissions apply. The draft flow can't do anything the existing tools can't.
- **To verify live:** whether Claude passes the plan result's `_meta` to the model. If it does, the docs describe "app only" confirm as a hint, not a guarantee. A harness that requires a human can block `commit_time_changes` and leave the card's Confirm as the only path.

## Card

One self-contained HTML file embedded in Core and served from memory as `text/html;profile=mcp-app`.

- **Build:** inline CSS and JS. No external loads; the resource's CSP allows no network. The DOM is built with `createElement` and text nodes only.
- **MCP Apps protocol:**
  - `ui/initialize` → apply the host theme → `ui/notifications/initialized`
  - handle the tool result and host-context changes
  - send `size-changed` after every render
  - buttons call `tools/call`
  - links use `ui/open-link`, falling back to `window.open`
  - ignore messages not from the parent
  - render even if the handshake fails
- **Data:** reads `structuredContent`, falling back to the first text block parsed as JSON. On load it calls `get_time_changes` so a re-shown card shows the current status.
- **Theme:** colour tokens on `:root`, with dark mode via `prefers-color-scheme` and `[data-theme]`.

Layout (mockups approved in brainstorming):

- Header: a status pill, the agent's intent, and a byline ("Written by the agent · expires …" / "confirmed by you …").
- Totals: net hours change (with billable change), number of changes, days and people touched, warning count.
- One block per **person-week**. The header shows the week's total before → after, the change, and "open in PI" (the timesheet's `urlFull`). Someone else's time is labelled.
  - **Days with changes** are open. They list the day's existing entries greyed, with the draft's changes highlighted (struck through for deletes), plus row warnings and the day's total before → after.
  - **Other days in the same week** show only their total, greyed, and open on click to list their entries. Weekend days appear only when they have hours. Days outside the touched weeks are not shown.
- Buttons and banners by status:

| Status | Pill | Banner | Buttons |
|---|---|---|---|
| draft | Draft · nothing changed yet | — | Confirm N changes, Cancel |
| committed | Committed · confirmed in chat / by the agent | — | Draft an undo (until expiry) |
| stale | Out of date · nothing was changed | Names the changed days; "ask the agent to draft again" | Cancel draft |
| failed | Failed · rolled back, or Failed · check Project Insight when `outcomeUnknown` or a row is `reverse_failed`/`unknown` | PI's error; per-row outcome | — |
| cancelled / expired | Cancelled / Expired | — | — |
| undo draft | Draft · undo of <time> changes | — | as draft |

The card also has a busy state while a call runs, and inline errors.

## Code layout

```
core/TimeChanges/
  TimeChangePlanner.cs      resolve names → ids, read weeks, build rows/warnings/totals/fingerprints
  TimeChangeCommitter.cs    checks, stale detection, apply + reverse, receipts
  Drafts/IDraftStore.cs, FileDraftStore.cs, Draft.cs
  Card/time-changes.html    embedded resource
src-stdio/ProjectInsightServer.cs   register tools, resource, Apps extension; gate as call filter
src-stdio/TimeChangeTools.cs        SDK tool definitions (six tools), typed inputs/outputs, card resource
                                    (SDK registration lives in the stdio host so Core stays SDK-free:
                                    the net8.0 Functions host references Core)
```

Planner and committer depend on `ProjectInsightApiService` and `IDraftStore` through interfaces, so tests use fakes.

## Testing

- **Planner:**
  - writes nothing; reads each person-week once
  - context rows, totals and every warning
  - 100-change cap
  - name resolution and `needs_clarification`
- **Committer:**
  - all-or-nothing, including a failure partway through, reversal order and `reverse_failed`
  - stale per day, so an unrelated day's edit doesn't make the draft stale
  - expiry (via an injected `TimeProvider`)
  - a stuck `committing` draft is reported as failed
  - undo for each action
- **Security:** a non-proposer can't read, commit, confirm, cancel or undo; a bad or reused confirm token is rejected.
- **File store:** atomic writes; concurrent `TryTransition` calls from separate store instances (contending on the same OS file lock that separates processes) yield exactly one winner; pruning.
- **Contract:**
  - every tool's `structuredContent` validates against its `outputSchema` (type, required, properties, items, recursively), with null-writing and null-omitting serializer settings
  - the resource is listed, readable and has the right MIME type, and is linked from all six tools
  - `tools/list` snapshot updated: six new tools, `destructiveHint` on the three existing time-entry tools
- **Live, local server, real data on the developer's own time:**
  - Claude Desktop: the card renders; confirm, cancel, undo, and stale (edit the day in PI between plan and confirm); check whether the confirm token reaches the model. Expiry is covered by unit tests only.
  - Claude Code: the same flow as text
  - everything created is removed with undo

## Release

2.1.0: the changes are additive. Update the mcpb manifest version, the READMEs (the new tools and how harnesses can gate them) and `docs/agents.md`. After installing, users must restart Claude Desktop or reconnect, because tools and resources are cached at connect time.

## Out of scope

- Draft flow for other write tools. They stay as they are; adding `destructiveHint` to them is a separate change.
- The Azure Table store and remote host (#4).
- Converting existing tools to SDK style (#5).
- Updating the user's `timesheet` skills to use drafts. This is optional, since the old tools remain.
