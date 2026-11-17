---
title: 'Make a page a subpage'
type: 'feature'
ticket: '14'
created: '2026-11-17'
status: 'built'
baseline_revision: '2af944aecc2c726e090624b438162d1e63806855'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-move-a-page-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can't make a page a subpage. E10 asks for two ways: change an existing page's level, and create a new page under a parent page.

**Approach:** A new tool, `set_page_level(pageId, level, basis?)`, is one `HierarchyWrite` through the existing `WritePipeline`. It sends one `UpdateHierarchy` of the page's section: every live page in its current order, each with its level, and the page and its subpages re-levelled. `create_page` gains an optional `parentPageId`. After `CreateNewPage`, still under the lock, one `UpdateHierarchy` puts the new page after the parent's last subpage, at the parent's level plus one.

## Boundaries & Constraints

**Always (set_page_level):**
- Step 1, before the lock. These are `InvalidArgument`: an empty `pageId`, a `level` outside 1 to 3 (Q3: the absolute level wanted; the 2013 XSD's `pageLevel` range), and a basis carrying a hash (AD-17). The call then walks from the page to its section, never reading `self` first (3.3 S1). With no section above it, the ID isn't a page: `InvalidArgument`. Next come the write grant on the section's notebook, then a pre-check of every refusal below against a fresh read, so the lock is never taken for a refusal.
- Refusals, in this order:
  - A page in the recycle bin or in Deleted Pages: `ObjectNotFound`.
  - A locked section: `SectionLocked`. This is checked from the section's own attributes, before any page is read.
  - A password-protected section without its read grant held at the call's start: `ReadNotGranted` (Q1 a). The grant is never prompted for, and this comes before any page-level check.
  - A page that isn't a live page of the section: `ObjectNotFound`.
  - Each of these is `InvalidArgument`: a first page whose new level isn't 1; a new level more than one above the level of the page before it; a subpage that would leave 1 to 3 (Q2: the page's subpages shift by the same amount); and a first page after the moved block whose level would be more than one above the block's new last page.
- Every other page is sent with its level as read (5.4 L6). The XML must pass the AD-16 XSD gate.
- A level equal to the current one returns `ok: true` with `unchanged`. No basis check, journal entry or COM call happens.
- The basis is the page's hierarchy `lastModifiedTime`, checked by `HierarchyBasis` (`noBasis`, `partialBasis`, `StaleWrite`).
- The call writes one `hierarchy` journal entry for the section, makes one `UpdateHierarchy` call, and never retries.
- Read-back, under the lock: the section is re-read. Each of these gives `readBackDiffers`: a page ID that is gone, a page whose level differs from the level sent, or an order that differs. A failed re-read gives `readBackFailed`.
- The result gives `meta.objectId` (the page), `meta.parentId` (the section) and `meta.journalEntryId`. The cache drops the page and every page in its block.

**Always (create_page with parentPageId):**
- Q5: `sectionId` stays required. `parentPageId` is optional. Without it, `create_page` behaves exactly as today. An empty string counts as absent.
- With it, the call needs a read grant on a protected section, held at the call's start (Q1 a; same order as above), else `ReadNotGranted`. Before COM, it refuses with `InvalidArgument` a parent that isn't a live page in the section, and a parent at level 3. In both cases no page is created.
- Step 6 writes a `hierarchy` journal entry for the section.
- Step 7, after `CreateNewPage`: the call re-reads the section and finds the parent and the new page. It sends every page at its level, with the new page moved to just after the parent's block at the parent's level plus one. The XSD gate applies, and it makes one `UpdateHierarchy` call.
- Q4: if the placement fails for any reason (an exception, the parent gone, the deadline or a cancel), the result is `ok: true` with `meta.pageId` and a new warning, `subpageNotPlaced`. The warning says the page is at the end of the section at level 1, and its `userAction` says to use `set_page_level` or `reorder_hierarchy_children`.
- A read-back of the placement gives `readBackDiffers` when the page isn't where it was sent.
- Messages carry IDs, counts and levels only, never names (AD-9).

**Never:**
- No change to `create_page` without a parent, to `reorder_hierarchy_children`, or to `move_hierarchy_object`.
- No `isSubPage` or `isCollapsed` written, and no level above 3.
- No page XML (`UpdatePageContent`) unless the sitting shows OneNote needs it (see Design Notes).
- No retry, no `NavigateTo`, and no skill or README text (entry 10).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Indent | P1, A1 → A to 2 | A at 2, others unchanged | none |
| Indent with block | P1, A1, B2 → A to 2 | A 2, B 3 | none |
| Block overflows | P1, A2, B3 → A to 3 | — | `InvalidArgument` |
| Gap | P1, A1 → A to 3 | — | `InvalidArgument` |
| First page | first page → 2 | — | `InvalidArgument` |
| Outdent re-parents | P1, A2, B2 → A to 1 | A 1; B now under A | none |
| Gap after block | P1, Q2, A3, F3 → A to 1 | — | `InvalidArgument` (F) |
| Same level | A at 2 → 2 | `unchanged` | none |
| Locked / protected | locked section; protected with no read grant | nothing sent | `SectionLocked`; `ReadNotGranted` |
| Recycle bin, not a page | Deleted Pages page; a section ID | — | `ObjectNotFound`; `InvalidArgument` |
| Stale | basis ≠ page date | — | `StaleWrite` |
| Create under parent | parent P with subpages S1, S2 | new page after S2 at level 2 | none |
| Parent invalid | parent at 3, in another section, or not a page | no page created | `InvalidArgument` |
| Placement fails | `UpdateHierarchy` throws after create | `ok`, `meta.pageId` | warning `subpageNotPlaced` |

## Decisions (Phill, 2026-11-17, Inbox)

- **Q1, a.** In a password-protected section, `set_page_level` and `create_page` with `parentPageId` need the section's read grant, held at the call's start and never prompted for, else `ReadNotGranted`. `create_page` without a parent needs only the write grant.
- **Q2, a.** A page's subpages move with it by the same amount. The change is refused if any of them would leave 1 to 3.
- **Q3, a.** `set_page_level` takes the absolute target level.
- **Q4, a.** A page created but not placed returns `ok: true` with `pageId` and a warning that it's still top-level.
- **Q5, a.** `sectionId` stays required, with an optional `parentPageId` that must be a page in that section, else `InvalidArgument`.
- **Q6.** The build writes the spine changes itself as a spine sync, as 5.4 did.
- **Level cap.** 3 is the 2013 XSD's limit (`OneNoteApplication_2013.xsd` l.143–150). The sitting sends level 4 straight to OneNote (S1). If OneNote accepts deeper levels, we follow OneNote.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` -- the template for the new op: the step-1 walk (`OwningSectionAsync`, never `self` first), the pre-check `Check(...)` reused in step 4, `HierarchyBasis.Compare` on the page date, the `HoldsReadGrantAt(GrantKeys.SectionKey(path), callStart)` check (l.367), the XSD gate and the read-back shape. Write a sibling `WriteOperation`; don't subclass `HierarchyWrite`, which reads its target at `self`.
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` -- `ReorderHierarchyWrite.Parents` (l.~545), which defines what a parent page is; the `unchanged` result text; `IdList`. Leave reorder's behaviour alone.
- `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- `Reorder(Section, id, members)` already writes each page's `pageLevel`. Pass members with their new levels. Add a `PageLevels`/`PlaceSubpage` builder only if a test shows `Reorder` can't express it.
- `src/OneNoteMcp.Xml/HierarchyTarget.cs` -- `Read` (children with `PageLevel`, `Encrypted`, `Locked`) and `Page(xml, id)` (`Subpages` count = the block). Put the pure level rules in Xml or Core as a static helper (`PageLevels`), so they can be unit-tested without the pipeline.
- `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` -- the nested `CreatePage` op: add the parent path (a `Children` read in step 3, a snapshot, placement inside `CallAsync` with try/catch, read-back in `AfterCallAsync`). `HierarchyWriteServices` has `ReadAccess` and `Snapshot`, so inject what the op needs through `ServerSetup`.
- `src/OneNoteMcp.Core/UseCases/MoveHierarchyObjectUseCase.cs`, `src/OneNoteMcp.Server/Tools/MoveHierarchyObjectTool.cs`, `ReorderHierarchyChildrenTool.cs` (annotations l.21–23), `ServerSetup.cs` -- the shape for `SetPageLevelUseCase` and `SetPageLevelTool`. Annotations: `ReadOnly=false, Destructive=false, Idempotent=true, OpenWorld=false`.
- `src/OneNoteMcp.Server/Tools/CreatePageTool.cs` -- add `parentPageId` and the description sentence.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` (`subpageNotPlaced`), `ToolMeta.cs`.
- Tests: `tests/OneNoteMcp.Tests/FakeGateway.cs` (`UpdateHierarchyCalls`, `UpdateHierarchyFailure`, `OnUpdateHierarchy`); `MoveHierarchyObjectToolTests.cs` (pattern, and a locked section hidden from `self`); `McpSdkBindingTests.cs:92` (25 → 26); `Contract/tool-contract.json`; `ToolContractTests.cs`, `NavigateToToolTests.cs`; and `PluginPackagingTests.cs:256` (add `set_page_level` to `DocumentedByRound2Entry10`).
- Integration: `MoveHierarchyObjectIntegrationTests.cs` (setup, the 2-second settle, the Vault lock pause), and `TestNotebook.UpdateHierarchyAsync` (the guarded raw call; the level-4 probe goes through it).
- Spine (Q6): AD-5 l.147 and AD-14 l.507 (tool lists), AD-7 l.240 (`Create` validation) and l.248+ (`HierarchyWrite` validation, a new **Page levels** bullet), the AD-7 table l.307, and AD-21 l.716–721 (Writes: the read-grant exception). Add a `.memlog.md` entry.
- `research-onemore-lessons.md` -- a new row for `CreateChildPage` and `SectionEditor`: OneMore places the child first, not last, sets the level on both the hierarchy and the page XML, and has no cap.

## Tasks & Acceptance

**Execution:**
- [x] `PageLevels` (new, pure) -- validate a level change and the subpage placement, and return the members with their new levels -- every matrix rule in one place.
- [x] `src/OneNoteMcp.Core/Writes/SetPageLevelWrite.cs` (new) -- the op, per the Always rules.
- [x] `SetPageLevelUseCase.cs`, `SetPageLevelTool.cs`, `ServerSetup.cs` -- `set_page_level`. The description covers the absolute level, the parent rule, subpages moving with the page, the basis, and protected sections.
- [x] `CreatePageUseCase.cs`, `CreatePageTool.cs` -- `parentPageId`, Q4's warning, and how to make a subpage.
- [x] `ToolWarnings.cs`, the contract snapshot, binding and packaging tests -- 26 tools. `<Version>` stays `0.9.0-rc.1`.
- [x] `tests/OneNoteMcp.Tests/PageLevelsTests.cs`, `SetPageLevelToolTests.cs`, `CreatePageToolTests.cs` -- every matrix row, the XML sent (order and every level), the unchanged path, the cache drop, refusals before any dialog or COM call, and create-without-parent unchanged.
- [x] `tests/OneNoteMcp.Integration/SetPageLevelIntegrationTests.cs` (new) -- see S1.
- [x] The spine sync, the memlog and the research-note row.

**Acceptance Criteria:**
- Given a page in a granted MCP Test section, when Claude calls `set_page_level` with level 2, then OneNote shows the page indented under the page before it, and every other page's level is unchanged.
- Given the existing `create_page`, `reorder_hierarchy_children` and `move_hierarchy_object` tests, when they run, then they pass unchanged.

## Implementation Notes

- **Judgment calls the plan left open.**
  - `set_page_level` re-resolves the page's notebook under the lock (`PageLocationAsync`), as the move does, and refuses a changed `NotebookKey` with `WriteNotGranted`, retryable (AD-7's re-resolve). The plan didn't ask for it; it costs one walk.
  - `create_page` with `parentPageId` returns `meta.journalEntryId` (the section's entry), and a step-7 failure names it, as the other hierarchy writes do. Without a parent, `meta` is unchanged.
  - With a parent, step 1 reads the section at `children` and checks, in order: not a section or in the recycle bin (`ObjectNotFound`, as step 4 already did), locked (`SectionLocked`), protected without the read grant (`ReadNotGranted`), then the parent (`InvalidArgument`). Step 4 repeats them against step 3's read.
  - A placement whose re-read can't find the parent or the new page throws inside the try, so it becomes `subpageNotPlaced` with no `UpdateHierarchy`. A placement read-back checks the new page's position and level only; `set_page_level`'s read-back checks every page sent (gone, level, order).
  - `PageLevels` lives in `OneNoteMcp.Xml` and returns a `PageLevelProblem` plus the offending page's ID; Core turns it into messages with IDs and levels only. `HierarchyUpdate.Reorder` expresses both writes, so no new builder was added.
  - `CreatePageTool`'s `parentPageId` is the method's last parameter, after `cancellationToken` and `server`, so the existing positional calls in `CreatePageToolTests` compile unchanged; the schema lists it third.
- **Files touched beyond the task list.** `ToolMeta.cs` (doc comment only); `McpSdkBindingTests.cs` (26 tools, 64 properties over 25 tools, `create_page`'s argument list in two messages, and an integer required argument handled by the two generic theories); `ToolContractTests.cs` and `NavigateToToolTests.cs` (the new tool listed). The contract snapshot only grew: `set_page_level`, and `parentPageId` on `create_page`. `<Version>` stays `0.9.0-rc.1`.
- **Spine sync (Q6):** AD-5 and AD-14 (the tool), AD-6's read-grant exceptions, AD-7 `Create` validation (a `parentPageId` sub-bullet), `HierarchyWrite` validation (a new **Page levels** bullet, which also records that locked comes before `ReadNotGranted` here, unlike step 1.2), the AD-7 table's `Create` and `HierarchyWrite` rows, and AD-21 Writes (the read-grant exception). One memlog entry. Not Fable-reviewed yet.
- **Research note row 29** was written from OneMore's code at `4ece17f` (read 2026-11-17 with `gh api`): `CreateChildPage` places the child straight after the parent, sets `pageLevel` on the hierarchy element and the page XML's root, and checks no cap; `SectionEditor.MovePageToParent` does the same for an existing page.
- **Not TDD for the tool tests.** Only `PageLevelsTests` was written first (it failed to compile before `PageLevels` existed). `SetPageLevelToolTests` and the `parentPageId` tests in `CreatePageToolTests` were written after the code, against the plan's TDD rule, so none of them was seen failing first. As a partial substitute, a mutation check ran: with the gap-after rule disabled, 2 tests failed; with the locked check skipped for protected sections, 2 failed; with `create_page`'s placement catch narrowed and its read-grant check disabled, 4 failed. Each file was restored and the suite rerun green.
- **Verification (2026-11-17):**
  - `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors.
  - `dotnet test tests/OneNoteMcp.Tests`: 3,176 passed, 0 failed.
  - `dotnet test tests/OneNoteMcp.Integration` with no integration variable: 217 passed, 101 skipped, 0 failed. The 4 new live tests are skipped.
  - Sittings S1 and S2 not run (the main session runs them with Phill).

- Verification after the review patches (2026-11-17): `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings; `dotnet test tests/OneNoteMcp.Tests` 3,177 passed; `dotnet test tests/OneNoteMcp.Integration` without the variable 217 passed, 101 skipped, 0 failed.

## Plan Change Log

## Review Triage Log

- 2026-11-17, quick lens, iteration 0: 0 high, 1 medium, 1 low, 0 false, 0 maybe-false. Both were patched; nothing was deferred.
  - **medium, patch.** `CreatePage.CallAsync` caught an abandoned placement `UpdateHierarchy` (a timeout, a cancel, the deadline or the call-failed HRESULT) as a plain failure. Its `subpageNotPlaced` message said the page "is at the end of the section at level 1", though OneNote may still apply the call. And because `CallAsync` returned normally, the pipeline released the lock at once instead of waiting for the abandoned call to settle (AD-3, AD-18). Verified in `WritePipeline.RunAsync`: `callFailed` was false, so `settling` was already complete. Fix:
    - The warning now says the page "may or may not have been placed" when the placement was sent and the failure is ambiguous.
    - `WritePipeline` keeps the lock after a step 7 that returned whenever an abandoned write is outstanding. Step 2 refuses any write already outstanding, so one outstanding once step 7 has started belongs to this write; the step-2 `ComBusy` refusal still releases at once.
    - New test: `CreatePageToolTests.APlacementTimeout_IsSubpageNotPlaced_SayingItMayHaveBeenPlaced_AndKeepsTheLockUntilItSettles`. It failed before the fix. The first, global version of the pipeline change broke three step-2 `ComBusy` tests; it was narrowed to the case after step 7 starts, and all three pass again.
  - **low, patch.** The doc comment on `SetPageLevelWrite.ReadBackDifferences` said the method was shared with `create_page`'s placement. It isn't: that has its own check. Fix: the sentence was removed.

## Design Notes

Considered building the placement in `AfterCallAsync`, whose failures already turn into warnings. Going with `CallAsync` plus a try/catch instead, because `AfterCallAsync` is the read-back, and a write hidden there would be invisible to the step model.

Locked comes before `ReadNotGranted`. A locked section's attributes reveal nothing about its pages, so E10's "locked protected section is refused" gives `SectionLocked` whether or not a read grant is held. That makes the sitting deterministic.

The sitting settles several unknowns: whether the page keeps its ID, whether `GetPageContent` shows the new level, what happens to a re-levelled block, and whether level 4 is refused. Some outcomes reopen the plan:
- If the page XML needs the level too (OneMore sets both), the build goes back through a Plan Change Log entry, not a silent fix.
- If OneNote keeps level 4 on read-back, the cap is reopened with Phill, because AD-16's XSD gate would refuse our own level-4 XML.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the contract snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` with no integration variable -- expected: it builds, and every live test is skipped.
- TDD: write each new test first and watch it fail before writing the code.

**Sitting steps.** The main session runs these with Phill. The build never runs them, never sets the integration variable, never touches OneNote, and never reads OneNoteMcp's app-data folders.
- **S1.** MCP Test is open, and the Ballast Vault starts unlocked. Run `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~SetPageLevelIntegrationTests`. Phill locks the Vault at the pause. Expected: all pass, and the output records each `RESULT` line. The run makes its own section and checks the following:
  1. **Level 4.** The guarded raw `UpdateHierarchy` sends one run page with `pageLevel="4"`, after pages at levels 1, 2 and 3. It records the HRESULT, or the level read back. Expected: refused, or read back at 3 or lower.
  2. **Indent and outdent.** A page goes to 2, then 3, then back to 1. Each step keeps the page's ID, `GetPageContent`'s root shows the level (recorded), and the other pages keep their levels.
  3. **Block.** A page with a subpage is indented, and its subpage moves to 3.
  4. **Create under a parent.** `create_page` with a parent that has 2 subpages lands after them at the parent's level plus one.
  5. **Locked Vault.** `set_page_level` on a Vault page ID read while the Vault was unlocked gives `SectionLocked`, and no `UpdateHierarchy` is sent.
- **S2.** Register the dev build as `onenote-dev`. Ask Claude to:
  1. Indent an MCP Test page to 2 and then 3, outdent it back, and create a subpage under a page that has subpages. Phill checks each one in OneNote.
  2. Do the same in MCP Test Cloud, then Sync Now. Check the sync status after each step (watch for `0xE0000644`), and re-read the levels after the sync.
  3. Try a level change in the locked Vault. Expected: `SectionLocked`.
