---
title: 'publish on a locked protected section asks for a read grant instead of saying it is locked'
type: 'bugfix'
ticket: '14'
created: '2026-11-19'
status: 'built'
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

**Problem:** `publish` on a locked password-protected section, or a page in one, without a read grant answers `ReadNotGranted`, so the agent asks the user for a read grant it can't use until the section is unlocked; and the refusal's `userAction` (`ReadFirstUserAction`) speaks of "a page" even when the target is a section (3.10 sitting, 2026-11-19).

**Approach:** Phill's decision (2026-11-19, Open Question 1 (a)): `publish` becomes a fifth exception to AD-21's grant-before-lock order. Step 1 and step 4 check the section's `locked` attribute before the read grant, so a locked target, a section or a page in one, is `SectionLocked`, with nothing written and no prompt. AD-7 step 1.2 and AD-21 "Where the check runs" list `publish`. The two `…_EvenWhenLocked` tests flip to `SectionLocked`. `ReadFirstUserAction` is worded for the target's kind (a section or a page). Notebook packages are unchanged. Test-first.

</frozen-after-approval>

## Code Map (as found, before the fix)

- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- step 1 `AuthorizeAsync` (l.185–245): `CheckReadGrant(section)` at the end, no lock check in step 1; step 4 `Validate` (l.291–307): `CheckReadGrant(now)` then `now.Locked` → `SectionLocked`, with the comment "the protected check comes before the locked one (AD-21)". `ReadFirstUserAction` (l.79–81), used by `CheckReadGrant` (l.426–441).
- `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- the order is pinned on purpose: `AProtectedPage_WithoutAReadGrant_IsReadNotGranted_EvenWhenLocked` (l.316, story 3.3), `AProtectedSection_WithoutAReadGrant_IsReadNotGranted_EvenWhenLocked` (l.626, story 3.13); `AProtectedLockedPage_WithAReadGrant_IsSectionLocked_BeforeCom` (l.388), `AProtectedLockedSection_WithAReadGrant_IsSectionLocked_BeforeCom` (l.635).
- Spine: AD-7 step 1.2 (l.198) and AD-21 "Where the check runs" (l.756) make read grant before `SectionLocked` the rule ("in the same order as the read tools"), with listed exceptions that check locked first from the section's own attributes: a page reorder, `set_page_level`, `create_page` with `parentPageId`, and a move's `position` (Phill, 2026-11-17, 3.14 Fable review). `publish` isn't among them. AD-21 Exports (l.790) is silent on the order.

## Implementation Notes

- Test-first: `PublishToolTests` split the two `…_EvenWhenLocked` theories into an unlocked case (`ReadNotGranted`, advice for a page or a section) and a locked case (`SectionLocked`, no grant, nothing written); 3 ran red on a stub, then green. The live `PublishIntegrationTests` vault test now expects `SectionLocked` without a grant too (compiled; skips here).
- `PublishUseCase`: `RefuseLocked` (the old step-4 throw, unchanged messages) runs before `CheckReadGrant` in step 1 and step 4 for a page or a section; `ReadFirstSectionUserAction` for a section target. Notebook packages unchanged.
- Spine: AD-7 step 1.2 (a closing sentence on `publish`), AD-21 "Where the check runs" and "Exports", the AD-9 `ReadNotGranted` row, and `ExternalWrite` validation (where the lock check sits in publish's step 1); memlog entry with the rejected option.
- Verification: build 0 warnings; unit 3784 passed, 3 skipped; integration 220 passed, 126 skipped.

## Review Triage Log

- 2026-11-19, quick lens (4) and a Fable check of the spine edit at low effort (11), each checked against the code and spine:
  - patched: step 4's new order had no test (both): `APageMovedIntoALockedProtectedSectionBeforeTheLock_IsSectionLocked_NotReadNotGranted`, mutation-checked 2026-11-19: with step 4's two calls swapped back on the committed tree, only that test failed (1 of 3787); restored with `git restore --source=HEAD`, green again.
  - patched (Fable F1, F3, F4, F5, F10, F11): the AD-9 `ReadNotGranted` row lists `publish`; the 1.2 sentence moved after the 3.14 rationale and stands alone; the lock check's place in publish's step 1 is stated; memlog `updated:`; the rejected option (b) recorded and the Code Map headed "as found"; "fifth" dropped in code, test and memlog.
  - for the coordinator / owner: Fable F6 and the quick lens (the section advice dead-ends for a section with no pages: offer `get_hierarchy` of the section too); F7 (the advice says "password-protected" for a section protected by path only); F8 (the guide could say unlock first); F9 (after unlocking without a grant, the retry is `ReadNotGranted`: an optional combined `userAction`); quick lens (a notebook package holding a path-protected section still gets the page wording, pre-existing).
  - the quick lens's "no Fable on record" is answered by the Fable check above.
