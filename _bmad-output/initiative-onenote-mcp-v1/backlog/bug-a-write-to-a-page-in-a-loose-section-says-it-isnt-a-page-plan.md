---
title: "A write to a page in a loose section says it isn't a page"
type: 'bugfix'
ticket: '12'
created: '2026-11-18'
status: 'done'
baseline_revision: 'e9674596fb5ce9c7516278a73a892850e0c0791b'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A `.one` file opened by hand sits under `one:OpenSections`, outside any notebook (entry 15's probe P3). A write to a page in such a loose section fails in step 1 with the caller's "isn't inside a notebook" fallback. A page write gives `ObjectNotFound` "… isn't a page", which is untrue and gives no reason. Nothing is written, and refusing is right, since a write grant is per notebook; only the code and message are wrong.

**Approach:** Fix it once, in `WriteAccess`, which every write's step 1 calls (`CheckAsync`, `CheckContainerAsync`, `CheckPermanentDeleteAsync`). When the walk to the owning notebook finds none, it looks for the section the object is in, or is. If there is one, the write is refused with `NotSupported`. The message names the object and its section as open outside any notebook. Otherwise the caller's fallback stands, unchanged.

## Boundaries & Constraints

**Always:**
- **What counts as loose:** the walk finds no `one:Notebook`, and either the object's nearest ancestor of kind `Section` exists, or the object itself reads at `self` as a `one:Section`. Every write through `WriteAccess` is covered: page writes, the hierarchy writes, the deletes, `create_page`, `set_page_level`, the moves, the restore and the merge.
- **The refusal:** `NotSupported`, not retryable. Its message is: "{Kind} {objectId} is in section {sectionId}, which is open in OneNote outside any notebook (a .one file opened by hand), and the server can't write there, so nothing was written." For a loose section itself, the message says "Section {sectionId} is open …". It carries IDs only (AD-9).
- **Its `userAction`:** "Edit it in OneNote, or move the section into a notebook in OneNote and retry. Reading it works as for any section; a password-protected one, or one under an export root or the backups folder, needs a read grant."
- It is decided in step 1, before any grant dialog and before the lock. Nothing is read beyond the walk and one `self` read.
- An object no section and no notebook owns (a notebook itself, or an unknown kind) keeps the caller's existing fallback error unchanged.

**Never:**
- No write to a loose section: no grant kind for one is added.
- No change to entry 15's protection rule, or to read tools.
- No change to which errors the existing callers give for a notebook or for objects inside notebooks.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Page write | `update_page_content` on a page in a loose section | nothing written; message names page and section | `NotSupported` |
| Delete | `delete_page_content`, `delete_hierarchy_object` (page), `permanently_delete_hierarchy_object` (page) on such a page | same | `NotSupported` |
| Level, move | `set_page_level` on such a page; `move_hierarchy_object` of such a page | same | `NotSupported` |
| Loose section as target | `create_page` into it; a loose section as move target | message names the section | `NotSupported` |
| Loose section itself | `delete_hierarchy_object` / rename of the loose section | same | `NotSupported` |
| No grant prompt | a loose page write with a prompt that would accept | dialog never shown | `NotSupported` |
| Normal page | page in a granted notebook | unchanged | none |
| Notebook fallback | an object no section or notebook owns | the caller's existing error, unchanged | as before |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Access/WriteAccess.cs`:
  - `CheckAsync` (l.~62), `CheckContainerAsync` (l.~79) and `CheckPermanentDeleteAsync` (l.~104) each `?? throw notInsideANotebook()` after `walk.OwningNotebookAsync`. Put the loose check before each throw: one private helper, used by all three.
  - `CheckContainerAsync` already reads `self` when the walk is empty. Reuse that read, so its section case is caught.
- `src/OneNoteMcp.Core/Access/HierarchyWalk.cs` -- `OwningNotebookAsync` (l.52), and `AncestorsAsync` (l.180), which ends the chain on an empty parent. Add a walk that returns the notebook and the first `Section` ancestor in one pass, so no extra COM reads happen on the normal path.
- Callers that pass a fallback (no change, but each is covered by the new check): `PageWrite.cs:104` (`NotAPage("isn't a page")`, the reported case), `SoftDeleteUseCase.cs:339`, `PermanentDeleteUseCase.cs:261`, `CreatePageUseCase.cs:131`, `HierarchyWrite.cs:115`, `MovePageWrite.cs:201–202`, `MoveContainerWrite.cs:195–196`, `SetPageLevelWrite.cs:133`, `RestoreFromRecycleBinWrite.cs:229–230`, `MergeSectionsWrite.cs:172–173`, `CreateHierarchyUseCase.cs:301`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- `Parents` (an empty string ends the chain) and `Hierarchies`. The P3 shape: a section whose parent is `""`, whose `self` XML is a `one:Section` with a local `path` and no notebook, and pages whose parent is that section.
- `tests/OneNoteMcp.Tests/WriteAccessTests.cs` (if it exists; otherwise new) and the tool tests of each tool above, for the end-to-end rows.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Access/HierarchyWalk.cs` -- a one-pass walk that returns the owning notebook and the nearest section ancestor.
- [x] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- the loose-section refusal in all three checks, with `LooseSectionUserAction` as a public constant.
- [x] `tests/OneNoteMcp.Tests/LooseSectionWriteTests.cs` (new) -- built at the `WriteAccess` seam that every row passes through, with a P3-shaped fixture. Only the soft-delete row runs end to end through its tool. The other rows aren't driven through their tools, and nor is the no-prompt row (see Implementation Notes and the Review Triage Log).
- [x] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- AD-6 (the walk: a section with no notebook is loose, so no write grant applies) and AD-9's `NotSupported` row (a write to a loose section); a `.memlog.md` entry.

**Acceptance Criteria:**
- Given a page in a section under `one:OpenSections`, when Claude calls any page write on it, then nothing is written and the result is `NotSupported`, with a message naming the page and its loose section, not "isn't a page".
- Given the existing write tool tests, when they run, then they pass unchanged.

## Implementation Notes

- **Where:** `HierarchyWalk.OwnersAsync` returns the notebook and the nearest section in one pass up the chain, with no extra COM reads on the normal path. `WriteAccess.NotebookOrLooseAsync` serves `CheckAsync` and `CheckPermanentDeleteAsync`; `CheckContainerAsync` reuses its existing `self` read. All three refuse a loose section with `NotSupported` (`LooseSectionUserAction`). With no section and no notebook, the caller's fallback is unchanged. The object is read at `self` only when the chain finds no section.
- **Matrix coverage:** every write reaches a loose section through one of the three `WriteAccess` methods, as the Code Map lists, so `LooseSectionWriteTests` covers the rows at that seam:
  - `CheckAsync`: a page, a subpage and the section itself. This is the path for page writes, the deletes of a page, `set_page_level`, a move's source and `create_page`.
  - `CheckContainerAsync`: the section, the path for a move target and for a rename or reorder of the section.
  - `CheckPermanentDeleteAsync`: a page.
  - The notebook fallback is unchanged.

  `SoftDeleteToolTests.APageInALooseSection_IsNotSupported_NamingTheSection_NothingDeleted` runs the reported case end to end: `NotSupported`, the section named, no `DeleteHierarchy`, and the lock never taken. Because the refusal isn't `WriteGrantMissing`, the pipeline raises no grant dialog. That follows from the code, but no test drives it through a prompt.
- **TDD:**
  - `LooseSectionWriteTests` was written first. It failed to build (CS0117, no `LooseSectionUserAction`). With only the constant added, 5 of 6 failed with `ObjectNotFound`, not `NotSupported`; the notebook-fallback test passed, as it should.
  - The end-to-end soft-delete test was written after the code. As a check, reverting the refusal in `NotebookOrLooseAsync` made it and 4 others fail (5 of 7). The file was restored.
- **Verification (2026-11-18):** build 0 warnings; unit tests 3,771 passed, 3 skipped; integration without the variable 220 passed, 126 skipped, 0 failed.
- **Spine:** an AD-6 bullet and the AD-9 `NotSupported` row; one memlog entry.

## Plan Change Log

## Review Triage Log

- 2026-11-18, quick lens, iteration 0: 0 high, 1 medium, 4 low, 0 false. Four were patched and one deferred.
  - **low, patch.** `set_page_level` and a page move pass the page's section to `CheckAsync`, so the refusal named only the section. Fix: an optional `subjectId` on `CheckAsync`, passed as the page by both. The test `CheckAsync_OfALooseSection_ForAPage_NamesThePage` failed to compile (CS1739) before the fix.
  - **low, patch.** The extra `self` read on the no-section path could replace the caller's fallback with a gateway error. Fix: a failed read leaves the fallback as before. Test: `AnObjectWhoseSelfReadFails_KeepsTheCallersFallback`. A mutation check, with the catch removed, made it fail; the code was then restored.
  - **low, patch.** The section-versus-page test compared IDs exactly. It now ignores case.
  - **low, patch.** The XML docs of `CheckAsync` and `CheckContainerAsync` now name the `NotSupported` refusal.
  - **medium, defer.** Some writes refuse before step 1's `WriteAccess` call:
    - `set_page_level` and a move with `position`: the read grant and locked checks of 3.14;
    - `create_page` with `parentPageId`: the same;
    - a permanent delete: the subpage check.
    
    So a protected loose section gets `ReadNotGranted`, or a loose page with subpages gets the subpage refusal, before `NotSupported`. Plain loose sections get `NotSupported`, as the bug report needs. Each of these refusals is true, and none leads to a write, but the advice it gives can't help. Recorded in deferred-work.
  - Plan task 3 (end-to-end tests for every row) was reworded to what was built, at the seam plus soft delete end to end.
- Verification after the patches (2026-11-18): build 0 warnings; unit tests 3,773 passed, 3 skipped.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- TDD: write each new test first and watch it fail before writing the code.

**Live check (optional, the main session with Phill):** open a `.one` file by hand in OneNote, then ask Claude, through onenote-dev, to soft-delete one of its pages. Expected: `NotSupported`, with the message above, and nothing deleted. The unit fixture follows P3's recorded shape, so this check only confirms that `GetHierarchyParent` ends at the loose section as the fixture assumes.
