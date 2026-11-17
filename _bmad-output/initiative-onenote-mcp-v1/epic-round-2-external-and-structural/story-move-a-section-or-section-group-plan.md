---
title: 'Move a section or section group'
type: 'feature'
ticket: '12'
created: '2026-11-17'
status: 'in-progress'
baseline_revision: '2d26988097c2d83e56c0fb8f9ad45db1b2283b71'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/spike-write-probes-part-3-exports-move-merge-and-recycle-bin-restore-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-move-a-page-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `move_hierarchy_object` moves pages only (3.4). Epic 3 E5 also needs sections and section groups moved, within a notebook and across notebooks, under E8's protected-section rule. And AD-7's re-resolve of a write's notebook under the lock exists only for the move tool, so a section moved between notebooks between another write's step 1 and step 3 could still take the first notebook's grant (epic 2's create-page race, deferred-work story 2.4).

**Approach:** stacked on 3.4 (PR #67). The use case classifies `objectId`: a section or section group goes to a new `MoveContainerWrite`, a sibling of `MovePageWrite`, and anything else to `MovePageWrite` as now. Core builds one `UpdateHierarchy` of the new parent's XML (spike 3.1's `m3`/`m4` shape, with the same-kind siblings listed for position). The pipeline re-resolves, under the lock, every notebook a write's step 1 granted, for every write.

## Boundaries & Constraints

**Always:**
- Step 1, before the lock: 3.4's argument checks (empty IDs, `position` below 1, a basis hash). The object's write grant (`CheckAsync`), then the new parent's (`CheckContainerAsync`), then step 4's refusals against a read made now.
- Step 3, under the lock: both notebooks re-resolved as 3.4 does (`WriteNotGranted`, retryable, before COM). Read the object at `self`, its parent at `children`, the new parent at `children`, a group at `sections` scope, and both notebooks' paths.
- Step 4 refuses, in this order (AD-7 Moves, AD-21; ticket notes):
  1. A protected section (`encrypted`, locked or not), or a group holding one anywhere beneath: `NotSupported`, `userAction` to move it in OneNote (Q9, Q14, H2, Fable M1). So no read grant or protected journal entry is ever re-keyed.
  2. The object, or the new parent, being or in the recycle bin: `ObjectNotFound`.
  3. A new parent that isn't a notebook or section group, is the object, or is inside the moved group: `InvalidArgument`.
  4. The object's own parent as new parent: `InvalidArgument`, `userAction` naming `reorder_hierarchy_children`.
  5. J1: an object in a cloud notebook (path is a URL) going to another notebook's section group: `NotSupported`, `userAction` to move it to that notebook's root first.
  6. Q2: a same-kind child of the new parent with the object's name, ignoring case: `InvalidArgument`.
  7. Q1: a `position` past the end of the new parent's same-kind children: `InvalidArgument`.
  8. A moved section, or one beneath a moved group, landing on a `SectionKey` that holds a read grant or a live `once` window: `InvalidArgument`, as a rename (5.4). A new path the server can't predict is `NotSupported`, as rename's strict check.
  9. The AD-16 XSD gate.
- No `SectionLocked` path: the new parent is never a section, and a locked section is protected (Fable M1).
- Step 5: the basis is the object's own hierarchy `lastModifiedTime` (AD-17), through `HierarchyBasis`.
- Step 6: one `hierarchy` entry per parent, old then new (`meta.journalEntryId`, `meta.journalEntryIds`, as 3.4 Q1).
- Step 7: one `UpdateHierarchy`. `0x80042004` keeps `ObjectNotFound` and sets `outcomeUnknown`, after a best-effort re-read of both parents by ID and name. `0x80042018` on a group move is `NotSupported`, HRESULT kept, no `outcomeUnknown` (J3).
- J5 settle: when the old or new notebook is a cloud notebook, a step between 7 and 8 holds the lock 10 s, then the read-back runs, and the result warns `settled`. Q4: the same 10 s settle runs before the re-read after a failed call on such a move.
- Read-back: the item is found by its ID (sections keep their IDs). `meta.objectId` is the ID, `meta.parentId` the new parent. Not in the new parent, or still in the old: `readBackDiffers`; a failed re-read: `readBackFailed`.
- Q3: `WritePipeline` re-resolves, under the lock before step 3, every notebook that `WriteAccess` granted in step 1, for every write, `create_page` included. A changed key is `WriteNotGranted`, retryable, before COM. The two move writes keep their own, more specific check, and opt out.
- Messages carry IDs and counts only (AD-9).

**Never:**
- No relaxation of the protected refusal; no re-keying of grants or journal entries.
- No skill or README text (entry 10). No `merge_sections` (entry 6).
- No retry of the write. No `NavigateTo`.
- No settle for moves between local notebooks.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Section to group | section → group in its notebook | moved, same ID, two entries | none |
| Group to group | group → another group | moved | none |
| Section to other notebook root | both local, granted | moved, no settle | none |
| Cloud involved | either notebook a URL | moved after 10 s settle | `settled` |
| Protected | protected section, or group holding one | nothing sent | `NotSupported` |
| Recycle bin | object or new parent in it | — | `ObjectNotFound` |
| Bad parent | a section, page, the object, inside the group | — | `InvalidArgument` |
| Same parent | object's own parent | — | `InvalidArgument` → reorder |
| Cloud into other group | J1 | — | `NotSupported` |
| Name clash | same-kind sibling, same name | — | `InvalidArgument` |
| Read grant at new path | a stored read grant at the predicted key | — | `InvalidArgument` |
| Not granted | new parent's notebook | — | `WriteNotGranted` |
| Partial copy | `0x80042004` | both parents re-read | `ObjectNotFound`, `outcomeUnknown` |
| Group to cloud root | `0x80042018` | — | `NotSupported` |
| create_page race | section moved to another notebook after step 1 | nothing sent | `WriteNotGranted`, retryable |

## Decisions (answered overnight with the recommendation, per Phill's standing overnight rule (2026-11-17); Phill reviews in the morning)

- **Q1, a.** `position` is 1-based among the new parent's children of the same kind (sections, or section groups, since sections always come first); omitted, last. The read-back doesn't check the place: OneMore notes that OneNote shows top-level groups alphabetically whatever the order sent. Rejected: (b) no position for sections.
- **Q2, a.** A same-kind child with the object's name is refused before COM with `InvalidArgument`, as a rename's sibling name is (AD-7 Names). OneNote's behaviour on a clash is unprobed, and the predicted `SectionKey` would be wrong. Rejected: (b) send it and report what the read-back finds.
- **Q3, a.** The re-resolve for every write lives in the pipeline: `WriteAccess` records each grant it checks in a per-call ledger, and the pipeline walks each one again under the lock. Every write, future ones included, gets it without opting in. Rejected: (b) each operation re-walking its own target.
- **Q4, a.** A failed call on a move that touches a cloud notebook settles 10 s before the re-read too, so the re-read sees a late copy and the next write waits. Rejected: (b) settle only after a success.
- **Q5, a.** The full plan is kept, over the 1,600-token guide.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` -- the model for the new write: its step order, `CheckNotebook`, `PartialCopyFindingsAsync`, `FailureNote`, read-back. Its `NotAPageAsync` loses the "pages only until 3.12" branch; it opts out of the pipeline re-resolve.
- `src/OneNoteMcp.Core/UseCases/MoveHierarchyObjectUseCase.cs` -- classifies with `HierarchyWalk.OwningSectionAsync`, then a `self` read: section or group → `MoveContainerWrite`.
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs:420` `GrantLocationRefusal` and `src/OneNoteMcp.Core/Access/RenamedLocations.cs` -- the rename's grant-location check; add `MovedLocations` beside it (new parent folder + the leaf, the target's separator).
- `src/OneNoteMcp.Core/CloudNotebookCreate.IsUrl` -- cloud test. `ReadAccess.HoldsReadGrantAt`, `Now`.
- `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- add `MoveContainer(parentKind, parentId, siblings, kind, id, index)`. `HierarchyTarget.Read`, `SectionPathsBelow`; `EncryptedSections.Find` for a group's protected sections.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` `Granted` -- records into the ledger. `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- clears the ledger before each step-1 try, re-walks after the abandoned-write check. `WriteOperation.cs` -- `ReResolvesNotebooks` opt-out.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- `settled`. `MoveHierarchyObjectTool.cs` description; `tool-contract.json` snapshot.
- `tests/OneNoteMcp.Tests/MoveHierarchyObjectToolTests.cs` -- fixture shape to copy; `CreatePageToolTests.cs` for the race test.
- OneMore: no section move (`gh search code`, 2026-11-17). `AddTopSectionGroupCommand.cs` says omitting a sibling deletes it (spike 5.1 constraint 4 saw otherwise) and that top-level groups show alphabetically.

## Tasks & Acceptance

**Execution:**
- [ ] `HierarchyUpdate.MoveContainer` + `HierarchyUpdateXmlTests`.
- [ ] `WriteAccess` ledger, `WriteOperation.ReResolvesNotebooks`, `WritePipeline` re-resolve + tests (create_page race, a rename).
- [ ] `MovedLocations` (new) + tests.
- [ ] `MoveContainerWrite.cs` (new), use case dispatch, `ToolWarnings.Settled`, tool description, contract.
- [ ] `tests/OneNoteMcp.Tests/MoveContainerToolTests.cs` (new): every matrix row.
- [ ] `tests/OneNoteMcp.Integration/MoveHierarchyObjectIntegrationTests.cs`: section and group moves, throwaway, protected refusal.
- [ ] `research-onemore-lessons.md` row 19: the section-move note.

**Acceptance Criteria:**
- Given a section and a granted section group, when Claude calls `move_hierarchy_object`, then the section is in the group under its own ID with two journal entries.
- Given a section moved to another notebook between `create_page`'s step 1 and step 3, when the write runs, then it is `WriteNotGranted`, retryable, and no page is created.

## Design Notes

Considered extending `MovePageWrite`; going with a sibling, because a section move's parents, refusals and read-back are all different, and 3.4's file is already 500 lines.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet test tests/OneNoteMcp.Integration` with no integration variables -- every live test skipped.

**Sitting steps** (the main session with Phill; the build never runs them):
- S1: MCP Test and the throwaway open, the Ballast Vault locked. `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~MoveHierarchyObjectIntegrationTests`. Expected: all pass.
- S2: `onenote-dev` registered. Ask Claude to move an MCP Test run section into a group, a run group into another group, and a run section to the throwaway's root. Phill checks each in OneNote, with its pages.
- S3: on MCP Test Cloud, move a run section into a group in the same notebook; Phill checks it synced (Sync Now, then the web).
