---
title: 'A page write to a recycled page says to restore it first; 0x8004200C names the recycle bin'
type: 'bugfix'
ticket: '6'
created: '2026-11-16'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The bug says page writes reach COM on a recycled page; they don't. `PageWrite.ReadAsync` has refused a recycled section and a recycled page node with `ObjectNotFound` before COM since 2.9 (`7d172f4`), and both tools' tests cover it (the spike's `0x8004200C` came from a direct COM probe, not the server). What remains wrong: the refusal's `userAction` is the generic "pass the ID of a page that isn't in the recycle bin" rather than restore-first (spine AD-7 "Recycled pages"); the tests pin neither the `userAction` nor the two flag placements apart; and the `0x8004200C` triage row still says only "make it editable".

**Approach:** Give the two "is in the recycle bin" refusals in `PageWrite` their own restore-first `userAction`, rewrite the `0x8004200C` triage text to name the recycle bin as one cause, and pin both in tests.

## Boundaries & Constraints

**Always:** Refusal stays `ObjectNotFound` before any `UpdatePageContent`/`DeletePageContent` call, in the same place in step 3 (after the section walk, before the grant/lock checks for a recycled section; after the page node read for a recycled page). `0x8004200C` stays `Internal`, `retryable: true`, its `userAction` ending "then retry" (AD-9 triage rule). Messages carry IDs only.

**Never:** No new `ErrorCode`. No change to `create_page`'s refusal or `NotASectionUserAction`. No change to `NotAPageUserAction` for the non-recycled cases ("isn't a page", "isn't in a section", step 1's write-grant lookup). No live OneNote in tests. Don't name `restore_from_recycle_bin` in any text: it isn't built (epic 3 entry 5).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Page in Deleted Pages | section `isDeletedPages`+`isInRecycleBin`, page flagged | `ObjectNotFound`, restore-first `userAction`, no write | refused at the section check |
| Recycled section, page unflagged | section `isInRecycleBin`, page has no flag (OneMore lesson 8: flags sit on sections) | same | refused at the section check |
| Page flagged, section live | section live, page `isInRecycleBin` | same | refused at the page check |
| Not a page | a section ID | `ObjectNotFound`, `NotAPageUserAction` unchanged | unchanged |
| `0x8004200C` from COM | any other cause | `Internal`, retryable, new text naming the recycle bin | unchanged mapping |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- `NotAPageUserAction` l.51-53 (keep); `ReadAsync` l.99-137, the two `throw NotAPage("is in the recycle bin")` at l.106 and l.133; `NotAPage` l.380-381 (one `userAction` for every why). Shared by `UpdatePageContentWrite` and `DeletePageContentWrite`, so one change covers both tools.
- `src/OneNoteMcp.Com/HResultMap.cs` -- `Triaged` row l.108, `0x8004200C`. The `0x8004202E` row l.143 has the house wording ("ask the user to restore it in OneNote").
- `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` l.20-33 -- the pattern (public const userAction + a refusal helper); read only.
- `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` -- fixture l.37, l.61-77 (`DeletedPagesId` section flagged, `BinnedPageId` flagged); `APageInTheRecycleBin_IsObjectNotFound` l.848-854; `ASectionId_IsObjectNotFound` l.839-846 pins `NotAPageUserAction`; `AssertRefusedAsync` checks no write.
- `tests/OneNoteMcp.Tests/DeletePageContentToolTests.cs` -- same fixture l.37, l.62-77; tests l.457-472.
- `tests/OneNoteMcp.Tests/HResultMapTests.cs` -- `TriagedInternal` l.120-123 (retry flag), `ToException_CarriesTheRowsUserActionAndRetryFlag` l.186-194 (reads the row back, so it doesn't pin the text).
- `plugin/skills/guide/SKILL.md` l.225 -- `ObjectNotFound` row lists per-tool causes; page writes aren't there.
- Spine AD-7 l.215, AD-9 l.389 and l.407 already describe the target behaviour; no spine edit.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- add `public const string RecycledPageUserAction`: the page is in its notebook's recycle bin; ask the user to restore it in OneNote first, then re-read the hierarchy for its new ID (a restored page gets one); or pass a page that isn't in the recycle bin. Add a `NotAPage` overload or a `RecycledPage()` helper that uses it; use it at l.106 and l.133 only -- restore-first is AD-7's rule and the generic text doesn't say how to get the page back.
- [ ] `src/OneNoteMcp.Com/HResultMap.cs` -- l.108 text: OneNote reports the page as read-only; if the page is in the recycle bin, it must be restored in OneNote and written under its new ID; otherwise ask the user to make it editable in OneNote, then retry. Keep `Retryable: true` -- AD-9 J6.
- [ ] `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` -- pin `RecycledPageUserAction` in the existing recycled test; add the "recycled section, unflagged page" and "page flagged, live section" cases (new fixture IDs) -- the matrix rows.
- [ ] `tests/OneNoteMcp.Tests/DeletePageContentToolTests.cs` -- the same three cases.
- [ ] `tests/OneNoteMcp.Tests/HResultMapTests.cs` -- a fact asserting the `0x8004200C` `userAction` mentions the recycle bin and ends "then retry" -- the ticket's Verify.
- [ ] `plugin/skills/guide/SKILL.md` l.225 -- add "From `update_page_content` or `delete_page_content`: the page is in the recycle bin; the user restores it in OneNote first." -- the guide lists per-tool causes.

**Acceptance Criteria:**
- Given any matrix row with a recycled page, when either page-write tool is called, then `gateway.UpdateCalls` and the delete calls stay empty and no journal entry is written.
- Given the full unit suite, when run, then every existing `NotAPageUserAction` assertion still passes.

## Design Notes

The bug's stated mechanism ("page writes don't [refuse]") is stale: verified by reading `PageWrite.cs` l.104-134 and `git log -S'page.InRecycleBin'` (2.9, 2026-11-12). The fix narrows to the advice, which is what the title says. When epic 3 entry 5 ships `restore_from_recycle_bin`, both texts should name it; that story owns the change.

Estimated ~80 lines over six files: the oneshot side of the line, but kept on `full` so this draft can be reviewed before any code.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the new cases included.

No sitting: the refusal is before COM and the spike already measured `0x8004200C` (`r5-*-update-recycled-page`).
