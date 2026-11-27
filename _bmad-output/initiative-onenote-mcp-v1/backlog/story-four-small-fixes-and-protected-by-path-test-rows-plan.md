---
title: 'Four small fixes and the missing protected-by-path test rows'
type: 'bugfix'
ticket: '23'
created: '2026-11-27'
status: 'built'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
baseline_revision: 'e17c4fac8d17702a56bf5145c571952d78beada0'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Five low-severity leftovers from the 2026-11-27 triage: D182 (a protected or subpaged loose section gets ReadNotGranted, SectionLocked or the subpage error instead of backlog 12's loose-section NotSupported, on set_page_level, move_page's target, create_page with a parent page and a page permanent delete), D186 (the opened-copies sweep deletes a copy in one try and can leave a marker-less folder later sweeps ignore), D188 (the moved filtered list's store event isn't logged), D189 (`meta.withheld` stays in the header and can push it past `results.inlineMaxChars`), D180 (no by-path protection tests for five tool areas).

**Approach:** Refuse a loose section first in those four writes; delete a copy's contents with retries and its marker last; log the moved list's store event like the main one; move a `meta.withheld` that doesn't fit into its own stored result, exactly as backlog 15 moves `meta.filtered`; add the missing test rows.

## Boundaries & Constraints

**Always:** Tests that fail on main for D182, D186, D188, D189. Every withheld ID stays reachable. Logs carry IDs and sizes only (AD-13). In-notebook sections keep the order locked, read grant, write grant. The loose refusal's message and userAction text are backlog 12's. D189's contract change updates the spine (AD-4, AD-9, AD-20, AD-21, Deferred row), the contract snapshot and the version together (AD-14: v0.10.0-rc.1 is published, so 0.11.0-rc.1).

**Never:** Change which writes are refused (only code and message for D182). Touch `UninstallCommand.DeleteCopy`, `MoveContainerWrite` or the section branch of permanent delete. Delete a folder without the marker, follow a junction, or delete when OneNote's notebook read failed. Change `meta.filtered`'s wire shape.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| D182 protected loose | set_page_level / move_page target with position / create_page with parentPageId / permanent page delete, page in an encrypted or by-path loose section | NotSupported, backlog 12 message and userAction, no dialog, nothing sent | not retryable |
| D182 subpaged loose | permanent delete of a page with subpages in a loose section | same NotSupported | — |
| D182 in-notebook | the same writes in a protected notebook section | unchanged (ReadNotGranted etc.) | — |
| D186 refused once | an entry's delete throws IOException, then succeeds | copy removed, count 1 | retried |
| D186 refused always | an entry stays locked | marker and folder left; next sweep still sees it | swallowed, count excludes it |
| D188 | filtered list moved | a "result stored" line and evictions for the list, IDs and sizes only | — |
| D189 fits | few withheld | header unchanged, `meta.withheld` inline | — |
| D189 too big | thousands withheld, header over threshold after any filtered move | `meta.withheldCount` + `meta.withheldResultId` on every index; read_result returns the JSON ID array whole | lapsed grant → ReadNotGranted, expired → ResultExpired |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- `Loose` (l.160-169), `LooseSectionUserAction` (l.60), `NotebookOrLooseAsync` (l.134-157); `HierarchyWalk.OwnersAsync` (Access/HierarchyWalk.cs:77) returns notebook and nearest section.
- `src/OneNoteMcp.Core/Writes/SetPageLevelWrite.cs` -- section checks l.124-131 run before `CheckAsync` l.133.
- `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` -- target locked l.179-181 and `CheckPositionReadGrant` l.184 before `CheckAsync` l.189; source side fine.
- `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` -- `AuthorizeAsync`: `CheckLockedAndReadGrant` l.127 before `CheckAsync` l.131.
- `src/OneNoteMcp.Core/UseCases/PermanentDeleteUseCase.cs` -- Page case: l.245-254 checks before `CheckPermanentDeleteAsync` l.261.
- `src/OneNoteMcp.Core/Writes/OpenedCopies.cs` -- `OpenedCopySweep.SweepAsync` l.119-156, single `Directory.Delete` l.146; marker `ExportRootGuard.MarkerFileName`. Retry model: `SettingsWriter.ReplaceWithRetry` (Settings/SettingsWriter.cs:459-487, delegate seam, 10 x 50 ms).
- `src/OneNoteMcp.Core/LargeResults/ResultDelivery.cs` -- `Deliver` l.39-76 returns `DeliveryOutcome(Success, StoredEvent?)`; `StoreFilteredList` l.82-96 drops `store.Add`'s result at l.94; filtered move l.66-72.
- `src/OneNoteMcp.Server/ToolRunner.cs` -- logs `LogStored`/`LogEvicted` l.108-115 (messages l.201-205).
- `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- `Withheld` l.50, `FilteredCount`/`FilteredResultId` l.61-62. `ResultResponses.WithMovedList` (l.214-232) puts count and id on expand and run index pages; `OriginalIndex` l.198-208.
- `src/OneNoteMcp.Server/Tools/ReadResultTool.cs`, `FindPagesTool.cs`, `FindMetaTool.cs`, `GetHierarchyTool.cs` -- descriptions naming `filteredResultId`; add `withheldResultId`.
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- envelope meta schema (filtered fields l.504-516); `Directory.Build.props` `<Version>`.
- `plugin/skills/guide/SKILL.md` -- documents the moved filtered list.
- Tests: `LooseSectionWriteTests.cs` (P3 shape, `AssertLoose` l.105), `SoftDeleteToolTests.cs:150`, `ProtectedSectionKit.cs` (`VaultGateway(locked, byPath)`, `VaultHarness`), `OpenedCopiesTests.cs` (`Copy`, `Age`, `OpenNotebooks`), `FindResultCapTests.cs` (`Search(...)`, `InlineMax`), `LargeResultTests.ListLogger` l.667, `TestSettings.SetExportRoots`, the protected tests in UpdatePageContent/CreatePage/SetPageLevel/RestoreFromJournal/ProtectedSectionToolTests.

## Tasks & Acceptance

**Execution:**
- [ ] `WriteAccess.cs` -- add `RefuseLooseAsync(objectId, ct, subjectId?)` that throws `Loose(...)` when the walk finds a section but no notebook -- one shared check.
- [ ] `SetPageLevelWrite.cs`, `MovePageWrite.cs` (target), `CreatePageUseCase.cs` (parent page), `PermanentDeleteUseCase.cs` (Page case) -- call it before their section checks -- D182.
- [ ] `OpenedCopies.cs` -- delete every entry but the marker (entries, never followed), then the marker, then the empty folder, each retried (about 10 x 50 ms) through an internal delegate seam; on final failure leave the marker -- D186.
- [ ] `ResultDelivery.cs`, `ToolRunner.cs` -- carry the moved lists' store events in `DeliveryOutcome` and log each like the main one -- D188.
- [ ] `ResultDelivery.cs`, `ToolMeta.cs`, `ResultResponses.cs`, tool descriptions -- after any filtered move, if the first index is still over the threshold and `withheld` isn't empty, store the IDs as a JSON array result with the main result's grants, set `withheldCount`/`withheldResultId`, drop `withheld` from the header -- D189.
- [ ] `tool-contract.json`, `Directory.Build.props` (0.11.0-rc.1), spine (AD-4 l.141, AD-9 l.475, AD-20 l.774/795, AD-21 l.846, Deferred row l.1030), `plugin/skills/guide/SKILL.md` -- D189 and D188 together.
- [ ] Tests -- D182 rows (protected by marker, by path, subpaged) per write plus one in-notebook guard; D186 fail-then-succeed and always-locked; D188 log lines; D189 thousands withheld (add rows to `TheFirstTextBlock_StaysWithinInlineMax`, a read-back test, both lists moved); D180's nine rows.

**Acceptance Criteria:**
- Given main, when the new D182, D186, D188, D189 tests run, then they fail; on this branch, the unit suite passes.
- Given the contract change, when `ToolContractTests` run, then the snapshot is at 0.11 and matches.

## Design Notes

Considered folding withheld IDs into the moved filtered list; going with a separate `withheldResultId` because the spine says withheld sections are never listed in `meta.filtered`, and the two lists have different item shapes. Strongest case against: a client reading only `meta.withheld` misses it when moved; accepted because it only moves past the inline threshold (thousands of sections), as backlog 15 already does for `meta.filtered`, and the guide skill documents both. Order: filtered moves first (unchanged behaviour), then withheld if still too big.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror --artifacts-path <art23>` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests --artifacts-path <art23>` -- expected: all pass.

## Implementation Notes

- Approved by Phill with the ticket, 2026-11-27 (unattended build; the plan raised no open question). The ticket bundles five items and the plan is about 2,200 tokens, over the 1,600 guide; kept whole because Phill approved the bundled ticket.
- D182 (2026-11-27): `WriteAccess.RefuseLooseAsync` added (shares the loose self read with `NotebookOrLooseAsync` through a new `SectionItselfAsync`); called after the owning section is found in set_page_level (from the page), move_page's positioned target (before its locked check), create_page with a parent (before locked/read grant) and the permanent page delete (before read grant, locked, subpages). Tests: 10 tool rows (marker and by-path per write, plus locked and subpaged for permanent delete) via a shared `LooseRefusal.Matches`, and `RefuseLooseAsync` unit rows plus an in-notebook guard in `LooseSectionWriteTests`. With the four call sites reverted, all 10 tool rows fail (ReadNotGranted, SectionLocked or InvalidArgument); restored after. Surprise: the extra walk adds one notebook self read before step 1, so `APageWhoseSectionMovedToAnotherGrantedNotebookAfterStep1_…` now triggers its move on W's second read (`nth: 2`). Also, on main, a loose protected page delete with no hash basis already reached backlog 12's refusal via `CheckPermanentDeleteAsync`, so the rows use a hash basis (and the locked/subpaged rows) to show the bug.
- D186 (2026-11-27): `OpenedCopySweep` now deletes a copy entry by entry (listed one level, links and junctions deleted as entries, never followed), then the marker, then the empty folder, each tried 10 x 50 ms (`DeleteAttempts`/`DeleteDelay`) through an internal `SweepAsync(..., Action<string> deleteEntry, ct)` seam with `DeleteEntry` as the default; a step that stays refused stops that copy with its marker in place. Tests: refused-once-then-retried (seam), marker-second-to-last-then-folder order (seam), and a real `FileShare.None` lock that keeps the marker so the next sweep deletes the copy once released. With `DeleteCopyAsync` swapped back to main's single recursive delete, all 3 fail; restored after. Surprise: none; the marker (`.onenote-mcp-opened`) sorts first, which is why main's recursive delete lost it before reaching a locked entry. Full unit suite: 4266 passed, 3 skipped.
- D188 (2026-11-27): `DeliveryOutcome` gained `MovedLists` (a list of `StoredEvent`s, in store order, all stored before the main result; a two-argument constructor keeps old callers), so D189 can append the withheld list's event without reshaping it. `StoreFilteredList` returns its `StoredEvent` (ID, JSON length, `StoreChange`); `ToolRunner` logs each moved list's "result stored" line and evictions first, then the main one's, through one `LogStoredEvent`. Tests in `FindResultCapTests`: the outcome carries the list's event (and none when the list fits), and a runner test where storing the list evicts an old result in a 16 MB store logs both stored lines and that eviction, IDs and sizes only (`LargeResultTests.ListLogger` made internal for it). With the list's event dropped from the outcome (main's behaviour), both fail; restored after. Surprise: none, though a main result big enough to evict its own just-stored list would leave `filteredResultId` dangling (pre-existing, not in scope). Full unit suite: 4268 passed, 3 skipped.
- D189 code and tests (2026-11-27): after any filtered move, `ResultDelivery` moves a `meta.withheld` whose first index is still over the threshold into its own stored result (a JSON array of the section IDs, under the main result's grants; `StoreFilteredList` generalised to `StoreList<T>`), sets `ToolMeta.WithheldCount`/`WithheldResultId` (new, last in the record), and appends its store event to `DeliveryOutcome.MovedLists` after the filtered list's, so D188's logging covers it. `ResultResponses.WithMovedList` carries both lists' count and ID on every expand and run index. Descriptions updated in ReadResultTool, FindPagesTool, FindMetaTool and GetHierarchyTool; snapshot regenerated at contractVersion 0.11 (only `withheldCount` and `withheldResultId` added); `<Version>` 0.11.0-rc.1. Spine and guide skill left for the later dispatch. Tests in `FindResultCapTests`: three 5,000-withheld rows in `TheFirstTextBlock_StaysWithinInlineMax`, few-withheld-stay-inline, read-back whole with the main grants, lapsed grant ReadNotGranted then ResultExpired, both lists moved (filtered first, both on expand "/"), and the wire names. With the withheld move disabled, 7 fail; restored after. Surprise: the default `ReadResultUseCase` (no stored grants) refuses any entry with an hours grant, which is what the lapsed-grant test relies on. Full unit suite: 4276 passed, 3 skipped.
- D188/D189 docs (2026-11-27): spine AD-4 l.141, AD-9 l.475, AD-20 l.774 and the contract note l.795 (0.11.0-rc.1, `withheldCount`/`withheldResultId`), AD-21 l.845, and the Deferred row l.1030 (withheld and store-event notes closed, D187 eviction kept); guide skill gains the moved-withheld bullet. AD-20 says a lapsed or expired withheld list means repeating the original call (it can come from `get_hierarchy`, not only a search).
- D180 (2026-11-27): nine protected-by-path rows (update_page_content, create_page with a parent, set_page_level, restore_from_journal over an export root and the backups folder, and a loose OpenSections section via get_hierarchy and get_page_content in the new `LooseSectionProtectedByPathReadTests`). Test-only; restore helpers `SectionSpec`/`EntryAsync` gained optional `At`/`protection`. All passed first time, no product bug found.

## Plan Change Log

## Review Triage Log

### Pass 1 (quick lens, 2026-11-27): high 0, medium 2, low 1, false 0, maybe-false 0, rejected 1

| Finding | Verdict | Route | Evidence / action |
|---|---|---|---|
| readNotGranted warning still says "meta.withheld lists their IDs" after the list moved; README l.132 likewise | low | patch | `AccessDecisions.cs:71` text unchanged and copied to every index. Patched the warning to name `meta.withheldResultId` for a moved list, and README l.132. |
| Final step deleted the copy's folder recursively after the marker was gone, so an entry that appeared late could go without a marker, or a partial failure leave a marker-less folder | medium | patch | `DeleteEntry` recursed for the copy folder. Patched: the folder is deleted only while empty (`DeleteEmptyFolder`), else refused; test `AnEntryThatAppearsAfterTheListing_KeepsTheFolder_AndItsMarker`. |
| A refused final folder delete left an empty folder without its marker, which later sweeps ignore (D186's own bug) | medium | patch | Read `DeleteCopyAsync`: marker deleted before the folder, no restore. Patched: `RestoreMarker` writes the empty marker back; test `AFolderDeleteThatStaysRefused_WritesTheMarkerBack_SoTheNextSweepStillSeesTheCopy`. |
| A moved withheld list can be evicted by the main result's own store | low | rejected | Real only when the main result plus list exceed `results.maxStoreMB` (256 MB default); the same behaviour as the moved filtered list, which the spine accepts (D187: repeat the call on `ResultExpired`), and AD-20 now says the withheld list follows the same eviction rules. A fix needs store-ordering complexity. |

Verification after patches: build 0 warnings 0 errors; unit suite 4287 passed, 3 skipped, 0 failed.
