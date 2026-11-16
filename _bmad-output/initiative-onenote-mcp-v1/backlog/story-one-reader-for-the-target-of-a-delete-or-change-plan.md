---
title: 'One reader for the target of a delete or change'
type: 'refactor'
ticket: '1'
created: '2026-11-15'
status: 'built'
baseline_revision: 'f200db7886db1854580e3124a4d3756757a6c1f8'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Three Xml records read the target of a hierarchy delete or change: 5.5's `DeleteTarget` (`DeleteTargets.Root`), 5.6's `PermanentDeleteTarget.Read` and 5.4's `HierarchyTarget.Read`, plus two subpage counts (`DeleteTargets.Page`, `PermanentDeleteTarget.Subpages`). They agree on every real OneNote read but disagree on XML OneNote never writes: two recycle-bin rules and three `pageLevel` parses.

**Approach:** One record and one reader for all three tools, with one recycle-bin rule, one `pageLevel` parse and one subpage count, and unit tests pinning the odd inputs. Readers only: no refusal moves or merges (Phill, 5.8 Q2).

## Boundaries & Constraints

**Always:** For well-formed hierarchy XML, every refusal, error code, message, `userAction` and retryability of `delete_hierarchy`, `permanently_delete` and `update_hierarchy` stays as it is. The page-ID, page-date, page-name, containers-beneath, section-paths and protected-pages helpers keep their behaviour.

**Never:** Merge or move refusals; touch `HierarchyNode`, `RecycleBin.IsBin` or `NotebookSections`; touch OneNote, run integration tests, or change any schema, COM call or public message.

**Decisions (Phill, 2026-11-15):**
1. Rules: 5.4's. `isRecycleBin` on any element, or a section group named `OneNote_RecycleBin`; `pageLevel` as `NumberStyles.Integer`, values below 1 (and unreadable ones) read as 1; `IsInRecycleBin` and `IsDeletedPages` kept apart with a computed `InRecycleBin`.
2. Record: extend `HierarchyTarget`, moved to its own file `HierarchyTarget.cs`.
3. Leftover helpers: `PermanentDeleteTarget` becomes the static class `PermanentDeleteReads` (file renamed); `DeleteTargets` keeps its name.
4. Subpage count: over the root's direct `one:Page` children only.
5. Tests: Xml-level tests plus one `SoftDeleteUseCase` test with the fake gateway: a section carrying `isRecycleBin` is refused as the recycle bin.

## I/O & Edge-Case Matrix

| Scenario | Input | Reader answer | Change |
|---|---|---|---|
| Bin by attribute, any kind | `isRecycleBin="true"` on a section, page or notebook | `IsRecycleBin` true | soft delete read false; a soft delete of such a section is now refused as the recycle bin |
| Bin by name | section group named `OneNote_RecycleBin` | true; a section of that name: false | none |
| Recycle states | `isInRecycleBin` / `isDeletedPages`: neither, either, both | each read as written; `InRecycleBin` = either | none |
| `pageLevel` unreadable | absent, `""`, `x`, `2.5`, `0x2`, overflow | 1 | none |
| `pageLevel` padded or signed | `" 2 "`, `"+2"` | 2 | permanent delete read 1 |
| `pageLevel` below 1 | `"0"`, `"-1"` | 1 | soft delete read 0 / -1 |
| Not well-formed / no root | `<one:Section` | `OneNoteXmlException` | none |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Xml/HierarchyUpdate.cs` l.6-96 -- `HierarchyMember`, `HierarchyTarget` (record, `Read`, `SectionPathsBelow`, private `PageLevel`: `Integer`, below 1 → 1). `HierarchyUpdate` (l.98+) stays.
- `src/OneNoteMcp.Xml/DeleteTargets.cs` -- `DeleteTarget` + `Root` (bin via `RecycleBin.IsBin`, section groups only; folded `InRecycleBin`), `SectionPage` + `Page` (all-descendant pages, `Integer` parse, no clamp), `PageIds`, `WithoutProtectedPages` (keep both).
- `src/OneNoteMcp.Xml/PermanentDeleteTarget.cs` -- record + `Read` (`IsInRecycleBin`, `IsDeletedPages` apart), `Subpages` (root's direct pages, `NumberStyles.None`); keep `ContainersBeneath`, `PageIds`, `PageDates`, `PageNames`.
- `src/OneNoteMcp.Xml/HierarchyElements.cs` -- shared `KindOf`, `IsRecycleBin` (any element), `InRecycleBin`; home for the one `PageLevel`.
- Call sites: `SoftDeleteUseCase.cs` l.106, l.175/213 (`SectionPage`), l.318/341; `PermanentDeleteUseCase.cs` l.162, 538-541, 549, 599, 611-613, 638; `RestoreFromJournalUseCase.cs` l.146 (`DeleteTargets.Root(xml).Kind`, a fourth caller); `HierarchyWrite.cs` (reads `InRecycleBin`, `Children`, `PageLevel`; no edit expected).
- Tests: `SoftDeleteReadsTests.cs` l.88-117; `PermanentDeleteToolTests.cs` l.844-872; `HierarchyUpdateXmlTests.cs` l.60-97; `UpdateHierarchyIntegrationTests.cs` uses `HierarchyTarget.Read` (compile only).

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/HierarchyElements.cs` -- add `PageLevel(XElement)`: `NumberStyles.Integer`, invariant, values below 1 and unreadable → 1 -- the one parse.
- [x] `src/OneNoteMcp.Xml/HierarchyTarget.cs` (new) -- move `HierarchyMember` and `HierarchyTarget` here; replace stored `InRecycleBin` with `IsInRecycleBin` + `IsDeletedPages` and a computed `InRecycleBin`; move `SectionPage` here and add `Page(sectionPagesXml, pageId)` over the root's direct `one:Page` children; children use `HierarchyElements.PageLevel` -- the one reader.
- [x] `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- keep only `HierarchyUpdate`.
- [x] `src/OneNoteMcp.Xml/DeleteTargets.cs` -- delete `DeleteTarget`, `Root`, `Page`, `Level`; fix the summary.
- [x] `src/OneNoteMcp.Xml/PermanentDeleteTarget.cs` → `PermanentDeleteReads.cs` -- delete the record, `Read`, `Subpages`, `Level`; the four helpers become the static class `PermanentDeleteReads`.
- [x] `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs`, `PermanentDeleteUseCase.cs`, `RestoreFromJournalUseCase.cs` -- read through `HierarchyTarget.Read` / `HierarchyTarget.Page`; `RefuseRecycledPage` may read `found.InRecycleBin` (same test).
- [x] `tests/OneNoteMcp.Tests/HierarchyTargetTests.cs` (new) -- the I/O matrix as theories on `Read`, children and `Page` (level and subpage count around odd levels).
- [x] `tests/OneNoteMcp.Tests/SoftDeleteReadsTests.cs`, `PermanentDeleteToolTests.cs` -- retarget the existing reader tests to `HierarchyTarget`; keep their cases.
- [x] `tests/OneNoteMcp.Tests/` (the soft-delete tool tests) -- one fake-gateway test: a section carrying `isRecycleBin="true"` is refused as the recycle bin and nothing is deleted.

**Acceptance Criteria:**
- Given the merged reader, when the solution builds, then no `DeleteTarget` record, `DeleteTargets.Root`, `DeleteTargets.Page`, `PermanentDeleteTarget.Read` or `.Subpages` remains, and `SoftDeleteUseCase`, `PermanentDeleteUseCase` and `HierarchyWrite` read targets only through `HierarchyTarget`.
- Given every existing unit test, when the suite runs, then each passes with its assertions unchanged except the retargeted reader calls.

## Implementation Notes

- Done as planned in 13 source/test files. `PermanentDeleteTarget.cs` was replaced by `PermanentDeleteReads.cs`. `HierarchyTarget.cs` holds `HierarchyMember`, `SectionPage` and `HierarchyTarget`. `HierarchyUpdate.cs` keeps only `HierarchyUpdate`. `HierarchyWrite.cs` and the integration tests were not edited.
- `SoftDeleteReadsTests.Root_ReadsKindDateAndTheRecycleBinStates` compared a whole `DeleteTarget`. It now compares a tuple of the same nine fields with the same values, because `HierarchyTarget` carries a `Children` list, so comparing whole records no longer works.
- New tests: `HierarchyTargetTests.cs` (35 cases, covering every matrix row) and `SoftDeleteToolTests.ASectionCarryingIsRecycleBin_IsRefusedAsTheRecycleBin`.
- One change on XML OneNote never writes isn't in the matrix's Change column. `PermanentDeleteTarget` parsed `pageLevel` with `NumberStyles.None`, so it read `"0"` as 0. A permanent delete of a page at level `"0"` followed by a page at level 1 used to be refused for subpages; it now goes ahead. Decision 1 covers this (values below 1 read as 1). `HierarchyTargetTests.Page_CountsSubpages_WithTheSameLevelParse` pins it at the Xml level.
- Verification: build 0 warnings 0 errors. Unit tests 2922/2922 (2886 at baseline + 36). The plan's `git grep` prints nothing. Integration tests were compiled, not run.

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens): 2 findings, high 0, medium 0, low 2, false 0, maybe-false 0. No patches, no deferrals.

| # | Finding | Verdict | Route | Evidence |
|---|---|---|---|---|
| 1 | The matrix row "`pageLevel` below 1" omits that `permanently_delete` also read `"0"` as 0, so its subpage refusal moves for a level-0 page | low | rejected | Real: `NumberStyles.None` parses `"0"`. The behaviour is the approved Decision 1 and is pinned at the Xml level. The only fix would edit this plan's frozen matrix. Recorded in Implementation Notes for the owner. |
| 2 | AC 1 says the three use cases read targets "only through `HierarchyTarget`", but `PageWrite.cs:125` (a soft-deleted page's node) and `HierarchyWrite.cs:648` (the grant check's section) read through `HierarchyNode.Root` | low | rejected | Both predate this change. `HierarchyNode` is out of scope by the plan's Never list. The ticket's verify line ("the three call sites use the one reader") holds for the target records. The only fix would edit the plan's wording. |

## Design Notes

`HierarchyTarget` is the superset already: it has `Path` (5.5) and `Nickname`/`Children` (5.4); only 5.6's split recycle states are missing. Reading a self-scope XML yields no children; a group's pages-scope read (soft delete) lists its sections, which is cheap and unused there.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors (integration project compiles)
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass; count = the baseline revision's count + the new tests
- `git grep -nE "DeleteTargets\.(Root|Page)\b|PermanentDeleteTarget|new DeleteTarget\(|DeleteTarget\?" -- src tests` -- expected: no output
