# PTO in the time-change draft flow

**Status:** design · **Branch:** `pto-time-changes` · **Release:** 2.2.0
**Builds on:** Phase 1 time-change drafts (2.1.0, `2026-10-29-phase1-time-changes-card-design.md`)

## Why

`create_pto_entry`, `update_pto_entry` and `delete_pto_entry` write to Project Insight immediately: no preview, no review card, no undo. Time entries already go through plan → review card → commit, with cancel and a 24-hour undo. The user wants the same for PTO: see the PTO on a card **next to everything else on those days, with the net total from all activities**, and approve it before anything is written.

Done when: the user can plan "3.00h PTO Mon + 1.75h PTO Tue", see both on a review card whose day and week totals include PTO and work, confirm, and undo within 24 hours, with no orphaned linked time entries left in PI.

## Goals

- PTO changes can be drafted, reviewed on the existing card, committed all-or-nothing, cancelled and undone, exactly like time entries.
- One draft can mix time rows and PTO rows, so a single card shows the true day and week totals.
- Day and week totals include PTO, existing and proposed, in every draft, including time-only drafts.
- Staleness: commit refuses if PTO on an affected day changed since planning, the same as for time entries.

## Non-goals

- Removing or changing what the direct PTO tools do. Only their descriptions change.
- Approving PTO as a manager (`DateApproved` and so on). "Approve" here means the user approving their own draft.
- A full time-code catalogue. Time codes resolve through the same lookup `list_user_pto` uses (see Constraints).
- Deploying to the remote host. That happens after the PR, as for other releases.

## Anti-goals (a failure even if it works)

- A committed or rolled-back draft that leaves an orphaned linked time entry, or a PTO record without its time entry.
- Double-counting PTO hours: the PTO record and its linked time entry are the same hours.
- Existing time-entry drafts behaving differently in anything but the additions listed under "Effect on time-only drafts".

## Constraints

- **Time-entry drafts keep working as they do now:** draft ids, the 24h expiry, receipt fields and `undoUntil`, billable defaulting to false on create, all-or-nothing commit, and the confirm token. Drafts already stored from 2.1.0 must still load, commit and undo.
- **Commit is all-or-nothing.** If any row fails, reverse what was applied, PTO rows included, last first, using the existing outcome vocabulary (`applied`, `reversed`, `reverse_failed`, `unknown`, ...).
- **Creating PTO auto-creates a linked time entry** (non-billable, no project or task). The commit must not create a second one.
- **Time-code names resolve the way `list_user_pto` does.** It reads the person's PTO list with `TimeCode` expanded and collects the distinct codes. So only codes that person has used before can be found by name. There is no time-code list endpoint in our API wrapper.
- `GET /api/user-pto/list-by-user/{userId}` returns every PTO entry the user has. Filter by date on our side.
- `POST /api/user-pto/pto-calendar` creates, or updates when `Id` is set. It takes `Date` as `M/d/yyyy`. `DELETE /api/user-pto/pto-calendar/{id}` deletes.

## Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | PTO becomes new change actions in `plan_time_changes`. One draft and one card for time and PTO. | One approval for mixed changes. Reuses the card, commit, undo and staleness logic. The plan tool's description gets longer, so it must say up front that PTO goes through it too. |
| B | A separate `plan_pto_changes` tool feeding the same draft store and card. | Easier for the agent to find when the user says "book PTO". But one draft can't mix work and PTO ("swap Tuesday's work for PTO" means two cards), and it adds a second planning tool to maintain. |
| C | Keep the direct tools and add a read-only preview card. | Dropped: it shows the numbers but doesn't stop a write before approval. |

The user picked A.

## Design

### Change shapes (`plan_time_changes`)

New actions next to `create`, `update` and `delete`:

- `create_pto {date, hours, timeCode | timeCodeId, description?, user?}`
- `update_pto {ptoEntryId, ...only the fields to change: date, hours, timeCode | timeCodeId, description}`
- `delete_pto {ptoEntryId}`

`user` works as it does for time entries: an id or a name, defaulting to the caller, with the `other_person` warning. `billable`, `taskId`, `project` and `task` are rejected on PTO actions with an `invalid_change` error. `ptoEntryId` on a time action, or `timeEntryId` on a PTO action, is also an `invalid_change`. The same PTO id may not appear in two changes.

**Time code resolution:** `timeCodeId` is taken as given. If the person's known codes don't include it, the row gets an `unknown_time_code` **warning**. A `timeCode` name is matched case-insensitively against the known codes, exactly first, then by unique substring. If nothing matches, or more than one code matches, the result is a **clarification** (`field: "timeCode"`) listing the known codes as candidates. There is no id to write with, so this can't be just a warning.

**Guard:** a time-entry `update` or `delete` aimed at a PTO-linked time entry is refused with a pointer to `update_pto`/`delete_pto`. Editing the linked entry directly would leave the PTO record and its time entry disagreeing.

### Draft model

- `DraftRow` gains a `Kind` (`"time"` | `"pto"`). A missing or null value means `"time"`, so 2.1.0 drafts load unchanged.
- PTO rows carry `PtoEntryId` (update and delete), `SavedId` (the PTO id PI gives a create) and `LinkedTimeEntryId` (filled in at commit, see below). Their before and after values carry time-code id and name, date, hours and description. Whether that's an `EntryValues` with optional PTO fields or a sibling type is the builder's call, as long as the stored JSON for time rows doesn't change.
- `ContextEntry` gains an optional `TimeCodeName` (or `Kind`), so existing PTO on a day shows as PTO rather than as a blank task.
- `DraftTotals`: PTO hours count in `NetHours` and in `NonBillableDelta` (the linked entry is non-billable). Add `PtoDelta`, the net change in PTO hours. (My call.)

### Reading PTO for the card's context

For each person-week the draft touches, the planner already reads timesheets and time entries. It now also reads the person's PTO list once and filters it to the week. Two sources describe the same hours, so:

- **Totals come from time entries only.** The gateway's `ToEntry` currently drops entries with no task, and PTO's linked entries have none (to be confirmed live). It must stop dropping them, so existing PTO hours count in `TotalBefore` and `TotalAfter`.
- **PTO records supply labels.** A time entry whose id equals a PTO record's `TimeEntry_Id` is shown as `PTO · <time code>`, not as a task.
- **Proposed PTO rows** adjust day totals like time rows: create adds the after hours, delete removes the before hours, update does both. The row's linked time entry is excluded from the context list for that day, the same way touched time entries already are.

**When the PTO list can't be read** (my call): this can happen when someone may edit another person's time but not read their PTO. A time-only draft must keep working as in 2.1.0. So:

- For people with **no PTO rows** in the draft, a failed read is skipped. Their task-less entries still count in totals, but are labelled just `PTO`. The draft records that person as "PTO not read".
- At commit, the stale check leaves PTO out of the fingerprint for those people, so their fingerprint shape matches what was planned.
- For people **with** PTO rows, a failed read fails the plan with a clear `pi_unavailable` error, because those rows can't be planned without it.

### Fingerprints and staleness

A day's fingerprint must also cover the PTO records on that day: id, time-code id, hours and description. Commit then refuses with `stale` if PTO on an affected day was added, changed or removed since planning. The stale check re-reads the PTO list once per person in the draft.

**Requirement:** a day with no PTO records and no task-less entries hashes exactly as it does in 2.1.0. Drafts already stored must not all go stale on deploy. Append the PTO part, and the task-less entries, only when there are some.

### Commit, rollback and undo

Rows still apply in order through the same loop, with the same uncertain-write handling: a timeout or 5xx means outcome `unknown`, not reversible. PTO operations:

| Row | Apply | Reverse (rollback) | Undo draft row |
|---|---|---|---|
| `create_pto` | Save PTO, no `Id`. `SavedId` = PTO id. ~~Then read the PTO back for `TimeEntry_Id` → `LinkedTimeEntryId`.~~ (No read-back: nothing uses it.) | Delete PTO `SavedId`. ~~Then clean up the linked entry (below).~~ | `delete_pto {SavedId}` |
| `update_pto` | Save PTO with `Id` and the after values. | Save PTO with the before values. | `update_pto` back to the before values |
| `delete_pto` | Delete PTO. ~~Then clean up the linked entry.~~ | Recreate PTO from the before values. `SavedId` = new id. | `create_pto` from the before values |

**Amended after the live check (2026-11-23):** PI deletes the linked time entry itself when a PTO is deleted, and updates it in place when a PTO is updated. The committer never reads or deletes the linked entry, because doing so straight after the PTO delete races PI: a day search still lists it, and a GET or DELETE of it returns HTTP 500. See `docs/PTO.md` row 4. The cleanup paragraph below, and the "then clean up" steps struck through above, are superseded.

~~**Linked-entry cleanup:** if the live check shows that deleting a PTO entry also deletes its linked time entry, no cleanup is needed. Keep a check: after the delete, read the linked entry, and if it still exists, delete it. If the live check shows PI doesn't remove it, the cleanup is required, and a failed cleanup fails the row (and triggers rollback). The same question applies to updates: does changing a PTO entry's hours or date update its linked entry? If not, the update step must write the linked entry too. The live findings decide which of these paths is real. Whatever is found goes in `docs/PTO.md` (new file), and a code comment points to it.~~

`undo_time_changes` works unchanged for callers: `PlanUndoAsync` maps PTO rows as in the table, and the undo draft gets a card and needs confirming like any other.

### Warnings (warn, never block)

| Code | When | Applies to |
|---|---|---|
| `over_40h_week` (new) | A 7-day span's after-total > 40h | Every changed row in that span, **time or PTO**. 40h/week is company policy, not a PI rule. (My call.) A `WeekView` is a timesheet period, which can be two weeks or cut short, so the check runs per 7-day chunk counted from the period's start date. It uses the day totals already loaded. A chunk shorter than 7 days (a cut-short period) is checked over the days it has. This may under-warn, which is acceptable for a warning, and no extra reads are made. The server is open to any PI tenant, so it assumes no particular week start. |
| `over_8h_day` (new) | The day's `TotalAfter` > 8h and the day has PTO, existing or proposed | PTO rows on that day. A long workday with no PTO is normal and keeps only the existing 12h warning. (My call.) |
| `not_quarter_hour` | Hours not a multiple of 0.25 | PTO rows too (already applies to time rows) |
| `unknown_time_code` (new) | `timeCodeId` isn't among the person's known codes | PTO rows |
| `inactive_time_code` (new, review fix) | A `create_pto`, or an `update_pto` that changes the code, sets a code PI marks inactive (an exact name can pick one over an active code with a longer name) | PTO rows, never undo rows (they put back the code the PTO had) |
| `pto_approved` (new) | `update_pto`/`delete_pto` on a PTO record with `DateApproved` set | That row. The text says PI may refuse the change or clear the approval, and that undoing a delete recreates the PTO **unapproved**. A warning, not a block, matching the rest. If PI refuses, the commit fails and rolls back as usual. (My call.) |
| existing (`timesheet_*`, `over_12h`, `far_date`, `other_person`, `likely_duplicate`) | as today | PTO rows too. `likely_duplicate` for PTO: same person, date and time code as an existing PTO record or an earlier row |

### Effect on time-only drafts

These are the only changes a time-only draft can show:

1. Day and week totals now include existing PTO hours, and those entries appear as greyed `PTO · <code>` context rows.
2. The `over_40h_week` warning.
3. Days that have PTO get a new fingerprint shape (see above).

### Review card and text

- PTO change rows are labelled `PTO · <time code>` and look clearly different from project/task rows: a distinct marker in place of the project colour swatch, and the same add/remove/strike-through treatment for create/delete.
- Existing PTO shows as greyed context rows with the same label.
- Week and day totals already render from `TotalBefore`/`TotalAfter`, so they pick up PTO without card changes. The totals line shows PTO change when `PtoDelta` ≠ 0.
- `TimeChangeText` (the text-only rendering) gets the same labels.

### Tool descriptions

- `plan_time_changes`: the first sentence says it covers time entries **and PTO**, and lists the three PTO shapes.
- `create_pto_entry`, `update_pto_entry`, `delete_pto_entry`: "Writes to Project Insight immediately, with no review or undo. Prefer `plan_time_changes` with a `create_pto`/`update_pto`/`delete_pto` change."
- `undo_time_changes` and `commit_time_changes`: mention that PTO rows are included.

## Live verification (before relying on the PTO write paths)

Done against the user's own account, **only after asking the user**, on a far-future date, cleaned up afterwards. Find out and record in `docs/PTO.md`:

1. What a PTO-linked time entry looks like from `SearchTimeEntriesWithNamesAsync`: is `Task_Id` null, empty, or a special task? This decides the `ToEntry` change.
2. Is the `SavedId` from a PTO save the PTO id or the time entry id? Does a read-back give `TimeEntry_Id`?
3. Does updating a PTO's hours or date update the linked time entry?
4. Does deleting a PTO entry delete the linked time entry?
5. What PI does with PTO on a submitted or locked timesheet (refuses, or accepts).

Then the done-when scenario end to end: plan 3.00h + 1.75h, card, confirm, undo, and check that no PTO or linked entries remain.

## Tests

Using the existing `FakeGateway`, extended with PTO records that auto-manage linked entries. The live check found PI deletes the linked entry itself, so the fake does too (a switch can make it leave an update's linked entry in place); there is no cleanup path to test.

- PTO-only draft: rows, labels, totals including existing PTO, warnings (40h, 8h, quarter hour, unknown code), and an unknown-name clarification.
- Mixed time + PTO draft: one card, correct day and week totals, no double counting of linked entries.
- Rollback when commit fails partway: a PTO row applied, then a later row rejected → the PTO is deleted and no linked entry remains.
- Undo of a PTO row: create → undo deletes it; update → undo restores; delete → undo recreates.
- Stale-day refusal: a PTO record on an affected day changes after planning → commit returns `stale`.
- Regression: a 2.1.0-shaped stored draft (no `Kind`) loads, commits and undoes. A day with no PTO keeps its 2.1.0 fingerprint.
- Guard: a time `update`/`delete` on a PTO-linked entry is refused.

## Release

- Version 2.2.0 everywhere the version is declared (stdio csproj and any manifests that carry it).
- README: PTO appears in the time-change section, with a short example.
- `docs/PTO.md`: live findings.

## Left to the builder

- How PTO values are represented (`EntryValues` extended or a sibling type), within the JSON-compatibility constraint.
- Whether the PTO gateway methods go on `ITimeEntryGateway` or a sibling interface.
- The card's exact PTO marker and colours, within "clearly different from project rows".
- Caching the PTO list per person within one plan or commit.
