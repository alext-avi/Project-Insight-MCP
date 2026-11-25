# PTO in the Time-Change Draft Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `plan_time_changes` draft PTO creates/updates/deletes alongside time entries, shown on the existing review card with day and week totals that include PTO, committed all-or-nothing and undoable for 24h.

**Architecture:** PTO becomes three new change actions in the existing planner → draft → committer pipeline. `ITimeEntryGateway` gains PTO reads and writes. The gateway stops dropping task-less (PTO-linked) time entries, so totals include PTO. PTO records supply labels and an extra fingerprint part. Rows carry a `Kind` so drafts stored by 2.1.0 load unchanged.

**Tech Stack:** .NET 10, C#, xUnit, ModelContextProtocol C# SDK 2.4, MCP Apps card (vanilla JS in `time-changes.html`).

**Spec:** `docs/superpowers/specs/2026-11-23-pto-time-changes-design.md`

All paths below are relative to `servers/project-insight/` unless they start with `docs/` or `README.md`.

## Global Constraints

- Time-entry drafts keep: draft ids, 24h expiry (`TimeChangePlanner.Lifetime`), receipt fields, `undoUntil`, billable default `false` on create, confirm token.
- Drafts stored by 2.1.0 (no `kind`, no PTO fields) must load, commit and undo unchanged. New draft fields are nullable and omitted when null in stored JSON.
- A day with no PTO records and no task-less entries fingerprints exactly as in 2.1.0.
- Commit is all-or-nothing. Reversal runs last-first, with the existing `RowOutcome` values. A timeout or 5xx means `unknown`, never reversed.
- Creating PTO must not create a second time entry. PI creates the linked one.
- Time-code names resolve only against the person's own PTO history (the `list_user_pto` lookup).
- Warnings never block.
- Card label for PTO: `PTO · <time code name>`.
- Version `2.2.0` in `src-stdio/ProjectInsightMCP.Stdio.csproj` and `mcpb/build/manifest.json`.

## Review Focus

1. **`update_pto` moving PTO into another timesheet period.** Both periods appear on the card, and each total is right. Test in Task 4.
2. **A swap in one draft (`delete_pto` + time `create` on the same day).** The day's net is 0 when hours match. The deleted PTO's linked entry isn't also counted as context. Test in Task 4.
3. **Undo after the PTO was deleted in PI since commit.** The undo plan returns a clarification, "No PTO entry with id …", not a crash. Test in Task 5.
4. **Redo of an undone `delete_pto`.** Undo recreates the PTO under a new id. Redo deletes that new id, not the original. Test in Task 5.
5. **A time-code name that matches several codes ("PTO" vs "PTO - Floating").** An exact case-insensitive match wins. Only ties or zero matches clarify. Test in Task 3.

---

### Task 1: Live API check of PTO behaviour → `docs/PTO.md`

**Requires the user's go-ahead before any write.** Use the `Project_Insight_MCP_Remote` tools (`list_user_pto`, `create_pto_entry`, `get_pto_entry`, `update_pto_entry`, `delete_pto_entry`, `search_time_entries`, `get_time_entry` as available) against the user's own account. Use a weekday at least 8 weeks ahead on an unsubmitted timesheet, with a time code from `list_user_pto`.

**Files:**
- Create: `docs/PTO.md`

- [ ] **Step 1: Record the baseline.** Get the user's id (`get_current_user`), their known time codes, and the time entries on the chosen date. There should be none.
- [ ] **Step 2: Create 1.00h PTO.** Record the returned `SavedId`. Fetch it with `get_pto_entry` and record whether the id is the PTO id, and the `TimeEntry_Id`.
- [ ] **Step 3: Inspect the linked time entry** via a search over that date. Record `Task_Id`, `Project_Id`, `IsBillable` and `Description` exactly as returned. This decides Task 2's `ToEntry` rule.
- [ ] **Step 4: Update the PTO to 2.00h, then move it to the next day.** Record whether the linked entry's hours and date follow, and whether its id stays the same.
- [ ] **Step 5: Delete the PTO.** Record whether the linked time entry is gone. If it remains, delete it with `delete_time_entry` and record that.
- [ ] **Step 6: Confirm nothing is left** on either date (no PTO, no time entries).
- [ ] **Step 7: Write `docs/PTO.md`.** Include the date of the check, findings 1–5 from the spec's "Live verification" list (mark 5, locked timesheets, as "not tested" unless a locked sheet was safely available), and which cleanup path Task 5 must use. Include no ids, names or tenant URLs: the repo is public.
- [ ] **Step 8: Commit**

```bash
git add docs/PTO.md
git commit -m "docs: record how PI's PTO API treats linked time entries"
```

---

### Task 2: Gateway PTO operations, task-less entries, fake PTO

**Files:**
- Modify: `core/TimeChanges/TimeEntryGateway.cs`
- Modify: `tests/TimeChanges/FakeGateway.cs`
- Test: `tests/TimeChanges/TimeEntryGatewayTests.cs`

**Interfaces:**
- Produces (in `TimeEntryGateway.cs`):
  - `public sealed record PiTimeCode(string Id, string Name);`
  - `public sealed record PiPto(string Id, string UserId, DateOnly Date, decimal Hours, string TimeCodeId, string? TimeCodeName, string? Description, string? TimeEntryId, bool Approved);`
  - `public sealed record PtoWrite(string UserId, DateOnly Date, decimal Hours, string TimeCodeId, string? Description);`
  - `ITimeEntryGateway` additions:
    - `Task<IReadOnlyList<PiPto>> ListPtoAsync(string userId, CancellationToken ct)`: every record with `PTOHours > 0`, with `TimeCode` expanded. Records with no date, or with `PTOAdded`/accrual only, are excluded.
    - `Task<PiPto?> GetPtoAsync(string ptoEntryId, CancellationToken ct)`: null on 404/400.
    - `Task<PiWrite> CreatePtoAsync(PtoWrite pto, CancellationToken ct)`: `SavedId` is the PTO id.
    - `Task<PiWrite> UpdatePtoAsync(string ptoEntryId, PtoWrite pto, CancellationToken ct)`
    - `Task<PiWrite> DeletePtoAsync(string ptoEntryId, CancellationToken ct)`
  - `PiEntry` gains `bool IsPto = false` (last positional parameter, defaulted). `ToEntry` keeps entries with no `Task_Id`, giving `TaskId = ""`, `TaskName = ""`, `ProjectId = ""`, `ProjectName = ""` and `IsPto = true`. The exact "no task" test follows `docs/PTO.md` finding 3.
  - `PiTimeCode` lists come from `ListPtoAsync(...).Select(p => (p.TimeCodeId, p.TimeCodeName))`, distinct. No separate method.
- `FakeGateway` additions:
  - `List<PiPto> Pto`, `List<string> Writes` entries `"create_pto:pto-100"`, `"update_pto:pto-1"`, `"delete_pto:pto-1"`.
  - `bool DeletePtoRemovesLinkedEntry { get; set; } = true` and `bool UpdatePtoMovesLinkedEntry { get; set; } = true`.
  - `PiPto AddPto(string id, string userId, DateOnly date, decimal hours, string timeCodeId = "tc-1", string timeCodeName = "PTO", bool approved = false)` adds the record and its linked `PiEntry` (`te-pto-<id>`, `IsPto = true`, non-billable).
  - `bool PtoListThrows`: `ListPtoAsync` throws `pi_unavailable`.
  - PTO writes honour `FailWrite`, `UncertainWrite` and `ThrowOnWrite`, with operation names `create_pto`, `update_pto` and `delete_pto`.
  - `CreatePtoAsync` adds the PTO and its linked entry. Update and delete behave per the two flags.

- [ ] **Step 1: Write failing gateway tests** in `TimeEntryGatewayTests.cs`, using the existing `StubHttpMessageHandler` pattern:
  - `Search_keeps_task_less_entries_as_pto`: a search response with one `Task_Id: null` entry → one `PiEntry` with `IsPto == true` and `TaskId == ""`.
  - `ListPto_maps_records_and_time_codes`: PTO list JSON (two records, one with `PTOHours: 0, PTOAdded: 8`) → one `PiPto` with `TimeCodeName == "PTO"` and `TimeEntryId` set.
  - `CreatePto_posts_date_as_M_d_yyyy_without_id`: the request body has `"Date":"11/2/2026"`, no `Id`, and `SavedId` is returned.
  - `UpdatePto_posts_id`: the body has `"Id":"pto-1"`.
  - `DeletePto_502_is_uncertain`: `Uncertain == true`.
- [ ] **Step 2: Run them and confirm they fail.** `dotnet test tests --filter FullyQualifiedName~TimeEntryGatewayTests`. Expected: compile errors or FAIL.
- [ ] **Step 3: Implement the gateway additions** by wrapping `ProjectInsightApiService.ListUserPtoAsync` (use the `modelProperties` string `list_user_pto` uses), `GetUserPtoAsync`, `SaveUserPtoAsync` and `DeleteUserPtoAsync`. Map failures through the existing `Failed`/`MissingOrThrow`/`Unavailable` helpers.
- [ ] **Step 4: Extend `FakeGateway`** as listed under Interfaces.
- [ ] **Step 5: Run all tests.** `dotnet test tests`. Expected: all PASS. Existing planner tests must not change.
- [ ] **Step 6: Commit** with `feat(time-changes): PTO reads and writes in the gateway; keep PTO-linked entries`.

---

### Task 3: Draft model and planning PTO rows

**Files:**
- Modify: `core/TimeChanges/TimeChangeModels.cs`
- Modify: `core/TimeChanges/TimeChangePlanner.cs` (`BuildRowAsync` and helpers)
- Modify: `core/TimeChanges/TimeChangeResolver.cs` (time-code resolution)
- Test: `tests/TimeChanges/TimeChangePlannerTests.cs`

**Interfaces:**
- Consumes: Task 2's `PiPto` and `ListPtoAsync`.
- Produces:
  - `ChangeAction.CreatePto = "create_pto"`, `UpdatePto = "update_pto"`, `DeletePto = "delete_pto"`, and `static bool IsPto(string action)`.
  - `RowKind.Time = "time"` and `RowKind.Pto = "pto"`.
  - `ChangeInput` gains `string? PtoEntryId`, `string? TimeCode` and `string? TimeCodeId`, with `[Description]` text.
  - `DraftRow` gains:
    - `string? Kind`: null means time.
    - `string? PtoEntryId`
    - `string? LinkedTimeEntryId`
    - `bool? PtoApproved`: true when the PTO record being updated or deleted had `DateApproved` set, else null.
    - `Action` stores the input action as given (`create_pto`, …).
  - `EntryValues` gains `string? TimeCodeId` and `string? TimeCodeName`. PTO rows set `TaskId`, `TaskName`, `ProjectId` and `ProjectName` to `""` and `Billable = false`.
  - `WarningCode.Over40HoursWeek = "over_40h_week"`, `Over8HoursPtoDay = "over_8h_day"`, `UnknownTimeCode = "unknown_time_code"`, `PtoApproved = "pto_approved"`.
  - `TimeChangeResolver.TimeCodeAsync(int changeIndex, string userId, string? timeCodeId, string? timeCode, CancellationToken ct) → Resolved<PiTimeCode>`. It returns a value plus an `unknown` flag, or a clarification with `Field = "timeCode"` and candidates for all known codes. Order: `timeCodeId` as given (flag unknown if not known), then exact name, then unique substring.
  - The resolver caches the PTO list per user for one plan.

- [ ] **Step 1: Write failing planner tests**, using `Seeded()` plus `pi.AddPto("pto-1", "u-1", Wed, 4m)`:
  - `Create_pto_row_resolves_time_code_by_name`: `{create_pto, date "2026-10-29", hours 3, timeCode "pto"}` gives a row with `Kind == "pto"`, `After.TimeCodeName == "PTO"` and `After.Hours == 3`.
  - `Exact_time_code_name_beats_substring`: known codes "PTO" (tc-1) and "PTO - Floating" (tc-2), with `timeCode "pto"`, resolve to tc-1.
  - `Unknown_time_code_name_asks_for_clarification`: `timeCode "Sabbatical"` gives `Clarifications[0].Field == "timeCode"` and two candidates.
  - `Unknown_time_code_id_warns`: `timeCodeId "tc-9"` gives a draft whose row has warning `unknown_time_code`.
  - `Pto_actions_reject_task_and_billable`: `create_pto` with `billable: true` throws `TimeChangeException` whose message contains "Change 1".
  - `Update_pto_reads_before_values`: `{update_pto, ptoEntryId "pto-1", hours 2}` gives `Before.Hours == 4`, `After.Hours == 2` and `PtoEntryId == "pto-1"`.
  - `Missing_pto_id_asks_for_clarification`: `ptoEntryId "pto-x"` gives a clarification with message "No PTO entry with id pto-x."
  - `Time_change_on_pto_linked_entry_is_refused`: `{update, timeEntryId "te-pto-pto-1", hours 1}` throws, and the message contains "update_pto".
  - `Approved_pto_is_flagged_on_the_row`: `AddPto(..., approved: true)` plus `update_pto` gives `row.PtoApproved == true`.
  - `Same_pto_id_twice_is_refused`.
- [ ] **Step 2: Run them and confirm they fail.** `dotnet test tests --filter FullyQualifiedName~TimeChangePlannerTests`.
- [ ] **Step 3: Add the model types and constants** listed under Interfaces.
- [ ] **Step 4: Implement the PTO branch of `BuildRowAsync`.** `ParseDate`/`ParseHours` are reused. PTO `user` resolution matches time create. Update/delete read the record via `GetPtoAsync` and the owner via `UserByIdAsync`. The guard refuses time `update`/`delete` when `GetEntryAsync` returns `IsPto == true`. Set `PtoApproved` from the record. The `pto_approved` warning itself is added in Task 4's `AddWarnings`. Set `LinkedTimeEntryId` from the record's `TimeEntryId` for update and delete.
- [ ] **Step 5: Run all tests.** `dotnet test tests`. Expected: PASS.
- [ ] **Step 6: Commit** with `feat(time-changes): plan create_pto, update_pto and delete_pto rows`.

---

### Task 4: Views, totals, fingerprints and warnings with PTO

**Files:**
- Modify: `core/TimeChanges/TimeChangePlanner.cs` (`LoadWeeksAsync`, `BuildViews`, `AddWarnings`, `IsLikelyDuplicate`)
- Modify: `core/TimeChanges/Fingerprint.cs`
- Modify: `core/TimeChanges/TimeChangeModels.cs` (`ContextEntry`, `DraftTotals`, `Draft`)
- Test: `tests/TimeChanges/TimeChangePlannerTests.cs`, `tests/TimeChanges/FingerprintTests.cs`

**Interfaces:**
- Consumes: Task 2 and Task 3 types.
- Produces:
  - `ContextEntry` gains `string? TimeCodeName` and `bool? IsPto`, both null for time context.
  - `DraftTotals` gains `decimal PtoDelta`.
  - `Draft` gains `List<string>? PtoUnreadUsers`: user ids whose PTO list failed to read at plan time. Null when empty.
  - `Fingerprint.Compute(IEnumerable<PiEntry> dayEntries, PiTimeSheet? sheet, IEnumerable<PiPto>? dayPto = null)`. With null or empty `dayPto`, the output equals today's for the same entries. Otherwise append `"pto|" + id|timeCodeId|hours|description` lines sorted by id. Task-less entries hash like other entries.
  - `WeekData` gains `IReadOnlyList<PiPto> Pto` (empty when unread).

- [ ] **Step 1: Write failing tests.**
  - `FingerprintTests.No_pto_hashes_as_before`: assert `Compute(entries, sheet, null)` and `Compute(entries, sheet, [])` both equal a hard-coded hash captured from the 2.1.0 implementation for a fixed input. Capture it **before** changing `Fingerprint.cs`.
  - `FingerprintTests.Pto_change_changes_hash`
  - `Pto_only_draft_totals_include_existing_and_proposed_pto`: Seeded, `AddPto("pto-1", u-1, Wed, 4m)` (Wed becomes 12h), plan `create_pto Mon 3h` + `create_pto Tue 1.75h`. Then:
    - Mon day `TotalBefore 6 → TotalAfter 9`, Tue `2.5 → 4.25`.
    - Week `TotalBefore == 20.5m` and `TotalAfter == 25.25m`.
    - Wed's context contains one entry with `IsPto == true` and `TimeCodeName == "PTO"`.
    - `Totals.PtoDelta == 4.75m` and `Totals.NonBillableDelta == 4.75m`.
  - `Mixed_draft_counts_linked_entry_once`: a time `create` Mon 2h plus `update_pto pto-1` from 4h to 2h. Wed `TotalAfter == 10`, and Wed context lists no PTO entry (it's the touched row).
  - `Swap_pto_for_work_nets_zero`: `delete_pto pto-1` plus time `create` Wed 4h. Wed `TotalBefore == TotalAfter == 12`. (Review Focus 2.)
  - `Update_pto_into_next_period_shows_both_weeks`: move pto-1 to 2026-11-04. Two `WeekView`s, and each week total moves by 4h. (Review Focus 1.)
  - `Over_40h_week_warns_time_and_pto_rows`: Seeded plus time creates to reach 41h in the week. Every changed row in that week has `over_40h_week`.
  - `Over_40h_is_per_7_day_chunk_in_two_week_period`: a 14-day timesheet with 35h in each week gives no warning.
  - `Over_8h_day_warns_only_pto_rows`: `create_pto` Mon 3h (Mon 9h) gives `over_8h_day` on the PTO row. A time-only draft on a 9h day gives no `over_8h_day`.
  - `Pto_not_quarter_hour_warns`: 1.3h.
  - `Pto_list_unreadable_for_time_only_person_is_skipped`: `PtoListThrows = true` plus a time-only draft gives a draft, with `PtoUnreadUsers == ["u-1"]` and context PTO labelled with `TimeCodeName == null`.
  - `Pto_list_unreadable_with_pto_rows_fails`: `PtoListThrows` plus `create_pto` throws `TimeChangeException` with code `pi_unavailable`.
  - `Approved_pto_update_warns`: `AddPto(..., approved: true)` plus `update_pto` gives warning `pto_approved` on that row.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
  - `LoadWeeksAsync` reads `ListPtoAsync` once per user.
  - `BuildViews` labels context entries whose id matches a PTO record's `TimeEntryId` (or `IsPto`). It treats a PTO row's linked entry (`Before`'s PTO `TimeEntryId`) as touched, and passes the day's PTO into `Fingerprint.Compute`, except for users in `PtoUnreadUsers`.
  - `AddWarnings` adds the four new codes per the spec's warnings table. The 7-day chunks start at `WeekView.Start`.
  - `IsLikelyDuplicate` for PTO creates means the same user, date and time code as an existing PTO record or an earlier PTO create.
- [ ] **Step 4: Run all tests.** `dotnet test tests`. Expected: PASS, with existing tests unchanged. Seeded weeks have no PTO, so existing totals hold.
- [ ] **Step 5: Commit** with `feat(time-changes): PTO in day and week totals, fingerprints and warnings`.

---

### Task 5: Commit, rollback, stale check and undo for PTO rows

**Files:**
- Modify: `core/TimeChanges/TimeChangeCommitter.cs` (`ApplyAsync`, `ReverseAsync`, `FindStaleDaysAsync`, create `SavedId` handling)
- Modify: `core/TimeChanges/TimeChangePlanner.cs` (`PlanUndoAsync`)
- Test: `tests/TimeChanges/TimeChangeCommitterTests.cs`, `tests/TimeChanges/TimeChangeServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces: no new public types. Behaviour:
  - Apply:
    - `create_pto` → `CreatePtoAsync`. `SavedId` is the PTO id. Then `GetPtoAsync(SavedId)` → `LinkedTimeEntryId`. A failed read-back doesn't fail the row.
    - `update_pto` → `UpdatePtoAsync`, plus the linked-entry update only if `docs/PTO.md` says PI doesn't do it.
    - `delete_pto` → `DeletePtoAsync`, then cleanup.
  - Cleanup after any PTO delete: if `GetEntryAsync(linkedId)` still returns the entry, `DeleteEntryAsync(linkedId)`. A cleanup failure fails the row.
  - **Amended after the live check (2026-11-23):** the read-back and the cleanup above were removed. PI deletes a PTO's linked time entry itself when the PTO is deleted, and updates it in place when the PTO is updated, so the committer only writes the PTO and never reads or deletes the linked entry (doing so straight after the delete races PI: a day search still lists it, and a GET or DELETE of it returns HTTP 500). The "then clean up" steps for `delete_pto` and the `create_pto` reverse, and the `create_pto` read-back of `LinkedTimeEntryId`, are superseded. See `docs/PTO.md` row 4 and the spec's matching amendment.
  - Reverse:
    - `create_pto` → delete PTO and clean up.
    - `update_pto` → `UpdatePtoAsync` with the before values.
    - `delete_pto` → `CreatePtoAsync` with the before values, and `SavedId` gets the new id.
  - Stale check: for each person in the draft not in `PtoUnreadUsers`, call `ListPtoAsync` once and pass each day's PTO to `Fingerprint.Compute`.
  - `PlanUndoAsync` maps:
    - `create_pto` → `{delete_pto, PtoEntryId = SavedId}`
    - `update_pto` → `{update_pto, PtoEntryId, Date, Hours, TimeCodeId, Description = Before.Description ?? ""}`
    - `delete_pto` → `{create_pto, User, Date, Hours, TimeCodeId, Description}` from `Before`
    - For a receipt whose row was a recreated `delete_pto` during an earlier undo, `SavedId` holds the new id, and redo uses it.

- [ ] **Step 1: Write failing tests.**
  - `Commit_pto_only_draft_creates_pto_without_extra_time_entry`: the "Mon 3h + Tue 1.75h" plan, committed, gives `Writes == ["create_pto:pto-100", "create_pto:pto-101"]` (no `create:` writes), two PTO entries in `_pi.Pto`, and their linked entries in `_pi.Entries`.
  - `Mixed_commit_failure_rolls_back_pto`: `[create_pto Mon 3h, create (time) …]` with `FailWrite = (op, _) => op == "create" ? "nope" : null` gives status `failed`, row 0 `reversed`, `_pi.Pto` empty, and no `IsPto` entries left.
  - `Rollback_cleans_linked_entry_when_pi_leaves_it`: as above with `DeletePtoRemovesLinkedEntry = false`. No `IsPto` entries remain, and `Writes` contains `delete:te-pto-…`.
  - `Stale_when_pto_changes_on_affected_day`: plan a time `create` on Wed, then mutate pto-1's hours in `_pi.Pto` (and its linked entry). Commit gives `Stale`, with `StaleDays` containing Wed's label.
  - `Stale_check_skips_pto_for_unread_people`: plan with `PtoListThrows = true` (time-only), then set it false and commit. Result is `Committed`.
  - `Undo_of_created_pto_deletes_it_and_its_linked_entry`: commit "Mon 3h", then `UndoAsync`, then commit the undo draft. `_pi.Pto` is empty and there are no `IsPto` entries.
  - `Undo_of_updated_pto_restores_before_values`.
  - `Undo_of_deleted_pto_recreates_it`, and the redo (undo of the undo) deletes the **new** id. (Review Focus 4.)
  - `Undo_after_pto_deleted_in_pi_clarifies`: commit `create_pto`, remove it from `_pi.Pto` directly, then `UndoAsync` gives `Kind == "needs_clarification"`. (Review Focus 3.)
  - `Stored_2_1_0_draft_still_commits`: load JSON of a 2.1.0 draft (a time-only fixture without `kind`) into `FileDraftStore`, then commit gives `Committed`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement** the behaviour listed under Interfaces. Branch on `ChangeAction.IsPto(row.Action)` in `ApplyAsync`/`ReverseAsync`. `UnansweredError`/`ChangedDayLabels` need no change.
- [ ] **Step 4: Run all tests.** `dotnet test tests`. Expected: PASS.
- [ ] **Step 5: Commit** with `feat(time-changes): commit, roll back and undo PTO rows`.

---

### Task 6: Card and text rendering for PTO

**Files:**
- Modify: `core/TimeChanges/Card/time-changes.html` (`contextRow`, `changeRow`, `failedEntry`, `totals`)
- Modify: `core/TimeChanges/TimeChangeText.cs` (`Row`, `Draft` summary line)
- Test: `tests/TimeChanges/TimeChangeCardTests.cs`, and the existing text tests in `tests/TimeChanges/TimeChangeServiceTests.cs`

**Interfaces:**
- Consumes: `DraftRow.Kind`, `EntryValues.TimeCodeName`, `ContextEntry.IsPto`/`TimeCodeName`, `DraftTotals.PtoDelta`.
- Card rules:
  - The PTO label is `"PTO · " + (timeCodeName || "time off")`, with no project swatch. Use a `.pto` row class with a distinct left marker (a `--pto` token defined in light and dark).
  - PTO actions map to create/update/delete for the `+`/`−`/`~` symbols and moved-day logic.
  - `failedEntry` shows `PTO <ptoEntryId or savedId>`.
  - The totals line appends `· PTO ±Xh` when `ptoDelta` ≠ 0.
- Text rules:
  - The row reads `1. create_pto · You · Mon 26 Oct · PTO · PTO · +3h`. Use `PTO · <code>` in place of `Project › Task`, and no "(billable)".
  - The draft summary adds `, PTO {Signed(PtoDelta)}h` after billable when ≠ 0.

- [ ] **Step 1: Write failing tests.**
  - Text: `Pto_row_reads_as_pto_with_time_code`: the text contains `PTO · PTO · +3h` and doesn't contain `›` for that row.
  - Text: `Summary_mentions_pto_delta`.
  - Card: `Card_labels_pto_rows`. Follow the existing `TimeChangeCardTests` approach: assert the HTML contains the `.pto` class rule, the `--pto` token in both theme blocks, and the `"PTO · "` label code path.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement** the card and text rules.
- [ ] **Step 4: Run all tests.** `dotnet test tests`. Expected: PASS.
- [ ] **Step 5: Commit** with `feat(card): show PTO rows as PTO · <time code>`.

---

### Task 7: Tool descriptions, snapshots, README, version 2.2.0

**Files:**
- Modify: `server/TimeChangeTools.cs` (`PlanDescription`, `CommitDescription`, `UndoDescription`)
- Modify: `core/Services/McpServerService.cs` (descriptions of `create_pto_entry`, `update_pto_entry` and `delete_pto_entry`, near line 4463)
- Modify: `tests/Snapshots/tools-list.json`, `tests/Snapshots/time-change-tools.json` (regenerated)
- Modify: `tests/ToolCallGateTests.cs:74` only if it reads the product version. It currently tests a literal, so leave it.
- Modify: `src-stdio/ProjectInsightMCP.Stdio.csproj` (`<Version>2.2.0</Version>`), `mcpb/build/manifest.json` (`"version": "2.2.0"`)
- Modify: `README.md` (repo root)

**Copy:**
- `PlanDescription` starts: `"Draft changes to Project Insight time entries and PTO for the user to review. Writes nothing to Project Insight: …"`. Append the PTO shapes: `create_pto {date, hours, timeCode name or timeCodeId, description?, user?}`, `update_pto {ptoEntryId plus only the fields to change}`, `delete_pto {ptoEntryId}`. Also add: `"Prefer this over create_pto_entry/update_pto_entry/delete_pto_entry. PTO creates its own linked time entry; never add a time entry for PTO."`
- `CommitDescription` and `UndoDescription`: add `"Includes PTO rows."`
- The three direct PTO tools get this prefix: `"Writes to Project Insight immediately, with no review or undo. Prefer plan_time_changes with a create_pto/update_pto/delete_pto change. "`, followed by the existing text.

- [ ] **Step 1: Update descriptions and version.**
- [ ] **Step 2: Regenerate snapshots** with `UPDATE_SNAPSHOTS=1 dotnet test tests --filter "FullyQualifiedName~Snapshot"`. Review `git diff tests/Snapshots`: only description text (and any new `changes[]` schema properties) should differ.
- [ ] **Step 3: README.** In the time-change section, add PTO to the list of what drafts cover, with the example "3h PTO Monday and 1.75h Tuesday" and a pointer to `docs/PTO.md`. Add a 2.2.0 entry wherever the README lists releases.
- [ ] **Step 4: Run the full suite** with `dotnet test tests`. Expected: PASS.
- [ ] **Step 5: Commit** with `feat: PTO through plan_time_changes (2.2.0)`.

---

### Task 8: Live end-to-end check (done-when)

**Requires the user's go-ahead.** Run the stdio server from this branch (`dotnet run --project src-stdio`, or the project's `run` setup) against the user's account. Use a Mon/Tue pair at least 8 weeks ahead.

- [ ] **Step 1:** `plan_time_changes` with `[create_pto Mon 3h, create_pto Tue 1.75h]` and `timeCode` set to a known name. Check that the card or text shows both rows labelled `PTO · <code>`, and that the week totals include existing work and PTO.
- [ ] **Step 2:** Commit. Check in PI that there are two PTO entries and exactly two linked time entries.
- [ ] **Step 3:** `undo_time_changes`, then commit the undo draft. Check in PI that there's no PTO and no linked time entries on either day.
- [ ] **Step 4:** Add the result, with no ids, to `docs/PTO.md` under "End-to-end check", and commit with `docs: PTO draft flow live check`.
