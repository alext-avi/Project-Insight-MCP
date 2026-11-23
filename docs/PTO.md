# How Project Insight's PTO API behaves

Project Insight (PI) keeps PTO as its own record (`/api/user-pto/...`). Each usage record is linked to an
ordinary time entry that PI creates and maintains for it. The draft flow (`plan_time_changes` with
`create_pto` / `update_pto` / `delete_pto`) relies on the behaviour below.

## Live check, 2026-11-23

Checked against a real PI tenant with one throwaway PTO entry on a date about nine weeks ahead. The entry was
created, updated, moved and deleted, and both affected days were left empty.

| # | Question | Finding |
|---|---|---|
| 1 | What does the linked time entry look like? | Non-billable, with the PTO's hours, date and description, and **no task and no project** (`Task_Id` and `Project_Id` absent/null). The draft flow treats a time entry with no task as PTO-linked. |
| 2 | What id does a PTO save return? | `savedId` is the **PTO record's id**, not the time entry's. Reading the PTO back gives its `TimeEntry_Id`. |
| 3 | Does updating a PTO update its linked entry? | **Yes.** Changing hours, and moving the date, both update the same linked time entry in place, and its id doesn't change. No extra write is needed. |
| 4 | Does deleting a PTO delete its linked entry? | **Yes.** After `DELETE /api/user-pto/pto-calendar/{id}`, the linked time entry is gone. The committer relies on this and never touches the linked entry itself: straight after the PTO delete, a day search can still list the linked entry and a GET or DELETE of it returns HTTP 500, so a defensive cleanup would race PI. |
| 5 | PTO on a submitted or locked timesheet | Not tested: no safe locked timesheet was available. The draft flow warns (`timesheet_*`), and if PI refuses, the commit fails and rolls back. |

Other observations:

- `GET /api/user-pto/list-by-user/{userId}` returns usage records (`PTOHours` set, linked `TimeEntry_Id`) and
  balance/accrual records (`PTOAdded` set, no time entry) together, with no date filter. The draft flow only uses
  usage records.
- Time codes come back expanded on each record (`TimeCode: {Id, Name, IsActive, ...}`). There is no separate
  time-code list, so a code can only be found by name once the person has used it.
- `DateApproved` wasn't present on any usage record read here, so the `pto_approved` warning only fires on tenants
  that do return it.
- `GET` of a time entry that no longer exists returns HTTP 500 (not 404).
- `GET /api/user-pto/{id}` of a deleted PTO returns success with an empty record (no date or hours), which the
  gateway treats as not found, so undo planning of a PTO that's already gone asks for clarification.
- Editing a PTO record recalculates `PTOBalance` on later records. That's PI's own bookkeeping, not ours.
