---
title: 'publish on a locked protected section asks for a read grant instead of saying it is locked'
type: 'bugfix'
ticket: '14'
created: '2026-11-19'
status: 'draft'
route: 'oneshot'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `publish` on a locked password-protected section, or a page in one, without a read grant answers `ReadNotGranted`, so the agent asks the user for a read grant it can't use until the section is unlocked; and the refusal's `userAction` (`ReadFirstUserAction`) speaks of "a page" even when the target is a section (3.10 sitting, 2026-11-19).

**Approach:** Depends on Open Question 1. Either way, `ReadFirstUserAction` is worded for the target's kind (a section or a page), test-first.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- step 1 `AuthorizeAsync` (l.185–245): `CheckReadGrant(section)` at the end, no lock check in step 1; step 4 `Validate` (l.291–307): `CheckReadGrant(now)` then `now.Locked` → `SectionLocked`, with the comment "the protected check comes before the locked one (AD-21)". `ReadFirstUserAction` (l.79–81), used by `CheckReadGrant` (l.426–441).
- `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- the order is pinned on purpose: `AProtectedPage_WithoutAReadGrant_IsReadNotGranted_EvenWhenLocked` (l.316, story 3.3), `AProtectedSection_WithoutAReadGrant_IsReadNotGranted_EvenWhenLocked` (l.626, story 3.13); `AProtectedLockedPage_WithAReadGrant_IsSectionLocked_BeforeCom` (l.388), `AProtectedLockedSection_WithAReadGrant_IsSectionLocked_BeforeCom` (l.635).
- Spine: AD-7 step 1.2 (l.198) and AD-21 "Where the check runs" (l.756) make read grant before `SectionLocked` the rule ("in the same order as the read tools"), with listed exceptions that check locked first from the section's own attributes: a page reorder, `set_page_level`, `create_page` with `parentPageId`, and a move's `position` (Phill, 2026-11-17, 3.14 Fable review). `publish` isn't among them. AD-21 Exports (l.790) is silent on the order.

## Open Questions

1. **Is `publish` an exception to AD-21's order?** The ticket's premise ("publish runs its read-grant check before its lock check") is true, but by design: the spine's rule puts the read grant first for every read and every write that needs one, except the four listed, and stories 3.3 and 3.13 pinned `publish` to it with tests. The fix needs a spine change.
   - (a) **Make `publish` a fifth exception:** step 1 checks the section's `locked` attribute before the read grant (`SectionLocked`), and step 4 does the same; AD-7 step 1.2 and AD-21 l.756 add `publish` to the exceptions. Same reasoning as 3.14's: a locked section's attributes reveal nothing about its pages, and a read grant can't help until the user unlocks it. The two `…_EvenWhenLocked` tests become `…_IsSectionLocked` tests. A notebook package is unchanged (no read grant; its locked sections already answer `SectionLocked`).
   - (b) **Keep the order** (no spine change): only the `userAction` gets fixed, and it also says that a locked section must be unlocked first. The ticket's AC 1 isn't met; AC 4's "no change needed" route records why.
   Recommended: (a). It is what the ticket asks for, and it matches the exceptions the spine already makes for writes whose check reads only the section's attributes.

## Implementation Notes

## Review Triage Log
