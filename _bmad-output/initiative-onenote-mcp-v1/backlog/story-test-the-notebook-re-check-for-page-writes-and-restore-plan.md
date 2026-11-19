---
title: 'Tests show page writes and restore re-check the notebook under the lock'
type: 'chore'
ticket: '18'
created: '2026-11-19'
status: 'built'
baseline_revision: '6853b4bd9f5ef88b31815dedea186395e3d7b5e7'
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

**Problem:** Story 3.12's under-the-lock re-check (`WritePipeline` re-resolving every notebook step 1 granted, `WriteAccess.ReResolveAsync`) has tool tests for create, the moves, merge, `set_page_level` and the hierarchy writes, but none for `update_page_content`, `delete_page_content`, `delete_hierarchy_object` (page), `permanently_delete_hierarchy_object` (page) or `restore_from_recycle_bin` (epic 3 retrospective V1, action 6). A regression could let one of them write under the first notebook's grant with no test failing.

**Approach:** Tests only, no behaviour change. For each of the five, a tool test moves the target's section into a second, also granted, notebook at the end of step 1 (on step 1's last read before the lock), and asserts `WriteNotGranted`, retryable, the re-resolve's `userAction`, nothing sent and no journal entry. Each test is mutation-checked: it fails with the pipeline's re-resolve call removed and passes with it in place (retro action 10).

</frozen-after-approval>

## Implementation Notes

- Oneshot: five tests and a small shared kit, `tests/OneNoteMcp.Tests/NotebookMoveKit.cs` (a granted notebook X, and `MoveOnRead`, which moves a section once on the nth `GetHierarchy` of an ID at a scope). Each test also asserts the move happened, so a trigger that never fires can't pass silently.
- Triggers: the four page writes move the page's section on the first `self` read of their notebook (step 1's grant walk). The restore registers two grants in step 1, the item's and the target's, after its state read; the target's grant walk ends at notebook A's fourth `self` read (state read, its parent walk, the item's walk, then the target's), found by logging the read sequence. A first try that moved the target earlier was granted for the new notebook by step 1 itself and so restored, as it should.
- **Mutation check (2026-11-19):** with `WriteAccess.ReResolveAsync(...)` in `WritePipeline` replaced by a comment, the unit run had 10 failures: the five new tests and the five existing re-resolve tests (create twice, rename, merge, `set_page_level`). The file was then restored with `git checkout` (no diff), rebuilt, and the run is green: unit 3788 passed, 3 skipped; integration 220 passed, 126 skipped; build 0 warnings.
- After review: the kit returns `NotebookMove(Moved, Reads)`, and the restore test pins the read order (`Reads() == 5`: four in step 1, one in the re-resolve's item walk), so a drift that moved the trigger into the re-resolve fails the test. The mutation check was re-run after this change: the same 10 failures, restored, green again.
- No real defect found: each write is refused by the pipeline's re-check, before step 3.

## Review Triage Log

- 2026-11-19, quick lens, 2 findings, both patched:
  - The restore test's `nth: 4` could drift into the re-resolve's own walk and still pass (the later target walk would refuse it anyway). Patched: `Reads() == 5` pins the order.
  - The kit's docs promised the trigger was step 1's last read; it only runs the move once. Patched: the docs say the caller picks the read and can pin it with `Reads`.
  Checked with no finding: each refusal carries `WriteAccess.NotebookChangedUserAction`, raised only by `ReResolveAsync`, whose only caller is the pipeline; none of the five overrides `ReResolvesNotebooks`; each class's refusal helper covers the COM writes its operation can make, and the restore's journal count covers its seeded journal; X's permanent-delete grant isn't read by the re-check.

