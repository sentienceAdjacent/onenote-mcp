---
title: "A write to a page in a loose section says it isn't a page"
type: 'bugfix'
ticket: '12'
created: '2026-11-18'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
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
- [ ] `src/OneNoteMcp.Core/Access/HierarchyWalk.cs` -- a one-pass walk that returns the owning notebook and the nearest section ancestor.
- [ ] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- the loose-section refusal in all three checks, with `LooseSectionUserAction` as a public constant.
- [ ] `tests/OneNoteMcp.Tests/LooseSectionWriteTests.cs` (new) -- every matrix row through the real use cases on `FakeGateway`, with a P3-shaped fixture, asserting that no write call is sent and no prompt is asked.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- AD-6 (the walk: a section with no notebook is loose, so no write grant applies) and AD-9's `NotSupported` row (a write to a loose section); a `.memlog.md` entry.

**Acceptance Criteria:**
- Given a page in a section under `one:OpenSections`, when Claude calls any page write on it, then nothing is written and the result is `NotSupported`, with a message naming the page and its loose section, not "isn't a page".
- Given the existing write tool tests, when they run, then they pass unchanged.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- TDD: write each new test first and watch it fail before writing the code.

**Live check (optional, the main session with Phill):** open a `.one` file by hand in OneNote, then ask Claude, through onenote-dev, to soft-delete one of its pages. Expected: `NotSupported`, with the message above, and nothing deleted. The unit fixture follows P3's recorded shape, so this check only confirms that `GetHierarchyParent` ends at the loose section as the fixture assumes.
