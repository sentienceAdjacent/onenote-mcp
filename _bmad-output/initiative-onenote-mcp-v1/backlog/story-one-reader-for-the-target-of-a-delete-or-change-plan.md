---
title: 'One reader for the target of a delete or change'
type: 'refactor'
ticket: '1'
created: '2026-11-15'
status: 'draft'
baseline_revision: 'f200db7886db1854580e3124a4d3756757a6c1f8'
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

**Problem:** Three Xml records read the target of a hierarchy delete or change: 5.5's `DeleteTarget` (`DeleteTargets.Root`), 5.6's `PermanentDeleteTarget.Read` and 5.4's `HierarchyTarget.Read`, plus two subpage counts (`DeleteTargets.Page`, `PermanentDeleteTarget.Subpages`). They agree on every real OneNote read but disagree on XML OneNote never writes: two recycle-bin rules and three `pageLevel` parses.

**Approach:** One record and one reader for all three tools, with one recycle-bin rule, one `pageLevel` parse and one subpage count, and unit tests pinning the odd inputs. Readers only: no refusal moves or merges (Phill, 5.8 Q2).

## Boundaries & Constraints

**Always:** For well-formed hierarchy XML, every refusal, error code, message, `userAction` and retryability of `delete_hierarchy`, `permanently_delete` and `update_hierarchy` stays as it is. The page-ID, page-date, page-name, containers-beneath, section-paths and protected-pages helpers keep their behaviour.

**Never:** Merge or move refusals; touch `HierarchyNode`, `RecycleBin.IsBin` or `NotebookSections`; touch OneNote, run integration tests, or change any schema, COM call or public message.

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
- [ ] `src/OneNoteMcp.Xml/HierarchyElements.cs` -- add `PageLevel(XElement)`: `NumberStyles.Integer`, invariant, values below 1 and unreadable → 1 -- the one parse.
- [ ] `src/OneNoteMcp.Xml/HierarchyTarget.cs` (new) -- move `HierarchyMember` and `HierarchyTarget` here; replace stored `InRecycleBin` with `IsInRecycleBin` + `IsDeletedPages` and a computed `InRecycleBin`; move `SectionPage` here and add `Page(sectionPagesXml, pageId)` over the root's direct `one:Page` children; children use `HierarchyElements.PageLevel` -- the one reader.
- [ ] `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- keep only `HierarchyUpdate`.
- [ ] `src/OneNoteMcp.Xml/DeleteTargets.cs` -- delete `DeleteTarget`, `Root`, `Page`, `Level`; fix the summary.
- [ ] `src/OneNoteMcp.Xml/PermanentDeleteTarget.cs` -- delete the record, `Read`, `Subpages`, `Level`; the four helpers become a static class (name per Q3).
- [ ] `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs`, `PermanentDeleteUseCase.cs`, `RestoreFromJournalUseCase.cs` -- read through `HierarchyTarget.Read` / `HierarchyTarget.Page`; `RefuseRecycledPage` may read `found.InRecycleBin` (same test).
- [ ] `tests/OneNoteMcp.Tests/HierarchyTargetTests.cs` (new) -- the I/O matrix as theories on `Read`, children and `Page` (level and subpage count around odd levels).
- [ ] `tests/OneNoteMcp.Tests/SoftDeleteReadsTests.cs`, `PermanentDeleteToolTests.cs` -- retarget the existing reader tests to `HierarchyTarget`; keep their cases.

**Acceptance Criteria:**
- Given the merged reader, when the solution builds, then no `DeleteTarget` record, `DeleteTargets.Root`, `DeleteTargets.Page`, `PermanentDeleteTarget.Read` or `.Subpages` remains, and `SoftDeleteUseCase`, `PermanentDeleteUseCase` and `HierarchyWrite` read targets only through `HierarchyTarget`.
- Given every existing unit test, when the suite runs, then each passes with its assertions unchanged except the retargeted reader calls.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

`HierarchyTarget` is the superset already: it has `Path` (5.5) and `Nickname`/`Children` (5.4); only 5.6's split recycle states are missing. Reading a self-scope XML yields no children; a group's pages-scope read (soft delete) lists its sections, which is cheap and unused there.

## Open Questions

1. **Confirm 5.8's assumption for the merged rules.** (a) `HierarchyTarget`'s rules: `isRecycleBin` on any element or a section group named `OneNote_RecycleBin`; `pageLevel` as `Integer`, below 1 → 1; `IsInRecycleBin`/`IsDeletedPages` apart with computed `InRecycleBin`. (b) as (a) but the recycle bin only on a section group (5.5's rule; no soft-delete change). (c) as (a) but `pageLevel` as `None` (5.6's rule; padded or signed → 1).
2. **Which record survives.** (a) Extend `HierarchyTarget`, moved to its own file; 5.4's code and integration tests compile unchanged. (b) A new neutral name for all three, renaming 5.4's uses too. (c) Extend it in place in `HierarchyUpdate.cs`.
3. **Home of the leftover helpers.** (a) `PermanentDeleteTarget` becomes static class `PermanentDeleteReads` (file renamed); `DeleteTargets` keeps its name. (b) Fold both into one `DeleteReads`; its two `PageIds` (all descendants vs direct children) need distinct names. (c) Keep the name `PermanentDeleteTarget` for the static class.
4. **The one subpage count's page list.** (a) The root's direct `one:Page` children (5.6). (b) All descendant pages (5.5). Same on a section's pages-scope read; (a) reports a page under a non-section root as not listed.
5. **Pin the one tool-visible move.** (a) Xml-level tests only. (b) Also one `SoftDeleteUseCase` test: a section carrying `isRecycleBin` is refused as the recycle bin.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors (integration project compiles)
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass; count = the baseline revision's count + the new tests
- `git grep -nE "DeleteTargets\.(Root|Page)\b|PermanentDeleteTarget\.(Read|Subpages)|new DeleteTarget\(" -- src tests` -- expected: no output
