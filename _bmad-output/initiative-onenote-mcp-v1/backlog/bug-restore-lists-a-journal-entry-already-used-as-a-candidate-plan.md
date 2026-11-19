---
title: 'restore_from_recycle_bin offers a journal entry that an earlier restore already used'
type: 'bugfix'
ticket: '16'
created: '2026-11-19'
status: 'built'
baseline_revision: '4abcaa12a8c5344fa4a5fce3fad84e067b7e8804'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** When `restore_from_recycle_bin` matches a recycled item by name and several soft-delete entries match, it lists every one as a candidate, including an entry an earlier restore already used, with nothing to tell them apart (3.10 sitting, 2026-11-19). Nothing records which soft-delete entry a restore used.

**Approach:** Phill's decision (2026-11-19, in the ticket): keep listing a used entry, marked "already restored" in the candidate text; and record which soft-delete entry a restore used. The restore's own `hierarchy` journal entry gains an optional `restoredFrom` (the soft-delete entry's ID, when one was used); the candidate list marks an entry that any restore entry names. A journal-format addition (an optional member within `schemaVersion` 1, as `recycleBinId` was), not a tool-contract change; a spine line in AD-8 and AD-10 records it. Matching, choosing and the single-match path don't change. Test-first.

</frozen-after-approval>

## Implementation Notes

- Test-first: `SoftDeleteEntriesTests` (Describe marks a used entry, `Used` reads only restore entries) and `RestoreFromRecycleBinToolTests.ARestore_RecordsTheSoftDeleteEntryItUsed_AndALaterChoiceMarksItAlreadyRestored` ran red (3 failing) on stubs, then green. `ARestoreWithNoJournalEntry_RecordsNoSource` passes either way (a guard).
- `JournalEntry.RestoredFrom` (`restoredFrom`, optional, plain metadata); `SoftDeleteEntries.Used` and `Describe(candidates, used)` (", already restored" after the parent); `RestoreFromRecycleBinWrite` passes `Used` of the call's loaded entries to the candidate text.
- After review: the link is written by rewriting the restore's entry once, after `UpdateHierarchy` succeeded (`JournalStore.TryAddRestoredFrom`, sharing a new private `TryRewrite` with `TryAddRecycleBinId`), not in step 6, so a failed restore marks nothing. A rewrite that fails loses only the mark. Test `AFailedRestore_RecordsNoSource_…` (written with the fix; red by reading on the step-6 version).
- Journal-format addition, not a tool-contract change: AD-8 (Which entry) and AD-10 (contents, optional members, and the write-once exceptions, now two) updated. `restore_from_recycle_bin`'s description and the guide's recipe 8 say a used candidate is marked.
- Verification: build 0 warnings; unit 3788 passed, 3 skipped; integration 220 passed, 126 skipped.

## Review Triage Log

- 2026-11-19, quick lens, 4 findings:
  - medium, patched: the link was written in step 6, before the call, so a failed restore's entry marked its soft-delete entry "already restored". Now added after success.
  - low, patched: `SoftDeleteEntries` repeated the operation-name literal; it now uses `RestoreFromRecycleBinWrite.OperationName` (tests too).
  - low, rejected: no test that a protected restore entry keeps `restoredFrom` readable by `ReadMetadata`; verified by reading (`Protect` moves only title and XML into the blob, `TryRewrite` copies the blob as is), and a test would need a protected restore target fixture for one property.
  - low, rejected: no test of the cross-notebook restore-then-move path; only the restore's own write sets the link (the move's entry can't), checked by reading.
