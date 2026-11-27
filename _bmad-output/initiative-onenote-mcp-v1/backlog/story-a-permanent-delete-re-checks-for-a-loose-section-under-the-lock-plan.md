---
title: 'A permanent delete re-checks for a loose section under the lock'
type: 'bugfix'
ticket: '33'
created: '2026-11-27'
status: 'built'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 2
baseline_revision: '557911f8746aef0684b2f31cc0b8783fe9fcd819'
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A permanent delete of a page refuses a page in a section open outside any notebook (a loose section, `NotSupported`) only in step 1, before the lock. Step 4 repeats the other refusals under the lock but not this one, so a section that becomes loose between step 1 and the lock isn't refused as loose there.

**Approach:** Add the loose-section check to the permanent delete's step 4, under the lock, for a page, in the same place in the check order as step 1, with a unit test showing `NotSupported` (the loose-section message and `userAction`), no journal entry, no backup and no `DeleteHierarchy` call. The spine's AD-7 `PermanentDelete` check-order bullet drops its note that step 4 skips it, and a memlog line records the change. Must not change the order of the other refusals, step 1's behaviour, or any other write's checks; never auto-retries a write (AD-7, AD-17).

</frozen-after-approval>

## Implementation Notes

Oneshot: one check added to one write, plus a test and one spine bullet; well under 100 lines.

Investigation finding: the write pipeline re-resolves every notebook step 1 granted under the lock, before the operation's step 3 (`WritePipeline.cs`, `WriteAccess.ReResolveAsync`). A page whose section became loose has no notebook, so that re-resolve refuses it with `WriteNotGranted` (retryable) before `PermanentDeleteWrite.ReadAsync` runs. A loose check placed only in `ReadAsync` would never be reached, and the acceptance test would get `WriteNotGranted`.

Decision (unattended, assumption A1): `PermanentDeleteWrite` now re-resolves its own notebook (`ReResolvesNotebooks => true`, as the move writes do), in `ReadAsync`: it reads the target, runs the loose-section check for a page, then calls the same `WriteAccess.ReResolveAsync` with the one grant step 1 checked (`grantTarget`), then the step-4 refusals as before. So a now-loose section answers `NotSupported` (not retryable), and a section moved to another notebook still answers `WriteNotGranted` (retryable). The pipeline and every other write are unchanged. Changing the pipeline's generic re-resolve to answer `NotSupported` instead was rejected: it would change every other write's under-the-lock answer, which the ticket's boundary forbids.

Assumption A2: the loose check runs before the target's step-4 refusals. For a page those refusals (recycle bin itself, notebook, protected section, Deleted Pages) can't apply to the page itself, so the visible order is step 1's: loose first among the page's own section checks.

Files changed: `src/OneNoteMcp.Core/UseCases/PermanentDeleteUseCase.cs` (the check and the write's own re-resolve), `src/OneNoteMcp.Core/Writes/WriteOperation.cs` and `WritePipeline.cs` (comments only), `tests/OneNoteMcp.Tests/PermanentDeleteToolTests.cs` (the new test; it failed first with `WriteNotGranted`), the spine's AD-7 `PermanentDelete` check-order bullet and its memlog.

Review fixes: the spine bullet and memlog line now say the step-4 loose check is for a page (a section target that became loose still answers `WriteNotGranted` from the re-resolve, as before); a new theory test shows a section or a section group moved to another granted notebook after step 1 is still `WriteNotGranted`, retryable, under the lock, now that this write re-resolves its own notebook for every kind of target.

## Review Triage Log

Pass 1 (quick review): high 0, medium 1, low 3, false 0, maybe-false 0.

- medium, patch: the spine and memlog wording claimed every loose section answers `NotSupported` in step 4; the check runs only for a page target. Reworded both to say "a page whose section".
- low, patch: no test covered the write's own re-resolve for a section or group target after `ReResolvesNotebooks => true`. Added `ASectionOrGroupMovedToAnotherGrantedNotebookAfterStep1_IsStillWriteNotGranted_Retryable_NothingWritten`.
- low, rejected: the write rebuilds one `GrantedNotebook` from `grantTarget` instead of the pipeline's ledger, so a second step-1 grant added later would go unchecked. Step 1 records exactly one grant today (`CheckPermanentDeleteAsync`), the move writes use the same pattern, and a fix would add new API to expose the ledger.
- low, rejected: in step 4 a page with no owning section still gets the re-resolve's `WriteNotGranted` before the "isn't in a section" `ObjectNotFound` that step 1 gives. Behaviour unchanged from before this change, and out of the ticket's scope (the loose-section check only).

Follow-up (Phill, 2026-11-27, Fable 1): the step-4 loose-section check now runs for every target, not only a page, so a section target left open outside any notebook since step 1 also answers `NotSupported` under the lock. `RefuseLooseAsync` reads a section itself when its chain finds no section, and does nothing for a section group (a group can't be loose; one with no notebook still gets the re-resolve's `WriteNotGranted`). New test `ASectionThatBecameLooseAfterStep1_IsNotSupported_UnderTheLock_NothingWritten`. Spine: the check-order bullet covers sections; the loose-section bullet (AD-6 notebook resolution) and the re-resolve bullet now name the permanent delete. Assumption A3: for a section, the loose check runs before `RefuseUndeletable` under the lock; a section protected or recycled at step 1 is refused there, so this only changes the answer for one that became both loose and protected after step 1 (still `NotSupported`, with the loose message).

Pass 2 (Fable review, run by the main session): finding 1 (a section target that became loose still answered `WriteNotGranted`) patched as above, by Phill's choice; the extra-walk finding skipped by Phill.
