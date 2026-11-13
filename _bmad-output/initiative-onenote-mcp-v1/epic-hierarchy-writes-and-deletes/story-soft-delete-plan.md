---
title: 'Soft delete'
type: 'feature'
ticket: '5'
created: '2026-11-13'
status: 'built'
baseline_revision: '47224c5489ce8ae6d97e6af2596138396bdf61b8'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred:
  - summary: >-
      A page in a locked section might return ObjectNotFound instead of SectionLocked, because step 1's self read of the page runs before PageWrite's section-first read.
    evidence: |-
      Unverified (maybe-false): PageWrite reads the section first because OneNote may not list a locked section's pages (PageWrite.cs ReadAsync comment; story 2.9's plan flags it as unverified live). SoftDeleteUseCase.ExecuteAsync reads GetHierarchy(pageId, self) before the pipeline. Settle it with a live check at the sitting: what GetHierarchy at self returns for a page in a locked section. If it fails with 0x80042014, fall back to the page path on ObjectNotFound in step 1. The risk may not be 5.5's alone (rebase review, 2026-11-13): PageWrite's own section-first read, HierarchyWalk.OwningSectionAsync, also starts with GetHierarchyParent(pageId), so update_page_content (and delete_page_content) may return ObjectNotFound for a page in a locked section too; the live check should cover both the delete and a page-content write.
    location: >-
      src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs:101
    severity: medium (unverified)
---

<intent-contract>

## Intent

**Problem:** Claude can create, rename and reorder structure (5.3, 5.4) but can't delete anything. The `SoftDelete` class exists only as an enum value: there is no gateway `DeleteHierarchy`, no recycle-bin ID read-back, and no journal rewrite.

**Approach:** `DeleteHierarchy(permanent=false)` for a page, section or section group through the existing pipeline as `SoftDelete`, following the AD-7 `SoftDelete` bullets, AD-8, AD-17 and AD-21 as PR #43 (`plan/spine-sync-epic-2`) words them. A page gets a page snapshot, compares its page-XML basis and sends `DeleteHierarchy` its hierarchy date (AD-17's page-delete exception); after the delete it reads back the page's new ID in Deleted Pages and rewrites the entry once with `recycleBinId`. A section or section group gets a `hierarchy` snapshot at `pages` scope. A notebook, and a page in a protected section, are refused with `NotSupported`.

**Assumes 5.4's recommended answers** (5.4's plan is a draft and Phill hasn't answered it yet): two structured tools (5.4 Q1 b), Core builds the `UpdateHierarchy` XML, and one `hierarchy` journal entry per write. This story reuses 5.4's `HierarchySnapshot`, `JournalEntry.HierarchyKind`, its node reader with `lastModifiedTime`, `isRecycleBin`, `isDeletedPages` and `pageLevel`, and its date-only basis check. If 5.4 is answered otherwise, those are the parts that change here. The tool shape below (Q1 b) follows the same reasoning as 5.4 Q1 b.

## Boundaries & Constraints

**Always:**
- AD-7 order. Step 1, before the lock: an empty `objectId` is `InvalidArgument`; a basis that carries `elementHashes`, or a `pageHash` for a target that isn't a page, is `InvalidArgument` (the schema closes `basis` to `{lastModifiedTime?, pageHash?}`). Then the write grant on the target's notebook (`WriteAccess.CheckAsync`, grant dialog as for `create_page`). A notebook target is refused here with `NotSupported` and AD-7's notebook `userAction`, before any grant dialog: no grant is asked for for a delete COM can't do. No read grant is ever asked for (AD-21 Deletes).
- Under the lock, step 3 reads the target at `self`. For a page, it also reads its owning section, the page XML (piBasic), the page's hierarchy `lastModifiedTime` and `pageLevel`, the IDs of the pages in the notebook's Deleted Pages section (none if the notebook has no recycle bin yet; Q3), and whether the next page in the section is its subpage (Q2). For a section or group, it reads the node at `pages` scope, which is the snapshot.
- Step 4 refuses before COM, in this order: the recycle bin itself or anything in it (`isRecycleBin`, `isInRecycleBin`, `isDeletedPages`), as `ObjectNotFound`; a locked section, or a page in one, or a group holding a locked section anywhere beneath it, as `SectionLocked` (probe L7 records what OneNote would do); a page in a protected section as `NotSupported` with AD-21's `userAction`, verbatim; a page with subpages per Q2.
- Basis (AD-17), step 5. A page: the caller's page-XML `lastModifiedTime` and `pageHash` are compared with step 3's page read (mismatch `StaleWrite`, partial `partialBasis`, none `noBasis` with step 3's page-XML date), and `DeleteHierarchy` is sent the page's hierarchy date from the same step 3. A section or group: `basis: {lastModifiedTime}` from `GetHierarchy`, compared, and the same date sent to COM. `DateTime.MinValue` is never sent: the gateway refuses an empty, unparsable or minimum date with `InvalidArgument`, as `UpdatePageContentAsync` does. The tool description says the hierarchy check is weak and that a delete straight after one of our own writes can get a spurious `StaleWrite` (spike 5.1 constraint 2): re-read and retry.
- Snapshot (AD-8), step 6. A page: `PageSnapshot` (full page, with binary), entry kind `page`, operation the tool's name, class `SoftDelete`. A section or group: a `hierarchy` entry of step 3's `pages`-scope XML. A protected section's entry is protected (DPAPI, its `SectionKey`); a group's snapshot keeps a protected section's `one:Section` but drops its `one:Page` elements (AD-8).
- Step 7: `DeleteHierarchy(id, date, false)`, never retried; a failure names the journal entry (2.11 Q1).
- A page's step between 7 and 8, under the lock: read Deleted Pages again and pick the new ID (Q3). Then rewrite the entry once, adding `recycleBinId`, through a temp file and `File.Replace` (AD-10), never decrypting a protected entry's `protected` blob. If the read-back finds no ID, or the rewrite fails, the entry stays as written, the result carries `recycleBinIdMissing` with no `userAction`, and the delete still succeeds.
- Results: `ok: true`, with `meta.objectId`, `meta.journalEntryId`, and `meta.recycleBinId` for a page when read back (Q6). The page-text cache drops the deleted page, or a section's or group's pages from step 3's read (AD-12).
- In a protected section with no read grant, messages and warnings carry counts only, never page IDs or names (AD-21).
- `restore_from_journal`: page entries restore unchanged (Q5); a soft-delete `hierarchy` entry's `NotSupported` gets the `userAction` Q4 picks.
- Tool contract snapshot updated; `<Version>` stays `0.9.0-rc.1`. Tool rows only in the guide skill and README (recipes are entry 7's). Spine sync goes to `deferred-work.md`.

**Never:** permanent deletes (5.6); deleting a notebook; lifting the protected-page refusal in the build (Design Notes); retrying a write; a read-back that fails the delete; sending `MinValue`; trusting the file system (AD-7: the hierarchy only); touching the user's Quick Notes section in a probe; any grant write; editing `tickets.toml`; spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

Tool name from Q1 (b): `delete_hierarchy_object`.

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Page | page ID, basis from `get_page_content` | in Deleted Pages; entry with `recycleBinId`; `meta.recycleBinId` | No error expected |
| Section | section ID, `{lastModifiedTime}` | in `OneNote_RecycleBin`, same ID; `hierarchy` entry | No error expected |
| Protected section, unlocked | | as above; DPAPI entry | No error expected |
| Section group | group ID | dissolves; `hierarchy` entry holds its layout | No error expected |
| Recycle-bin ID not found | read-back finds no single new ID, or rewrite fails | `ok: true`, `recycleBinIdMissing` | never fails |
| Ungranted notebook | any | grant dialog, then as above or `WriteNotGranted` | no lock, no COM |
| Notebook | notebook ID | `NotSupported` + AD-7's `userAction` | step 1, no dialog |
| Stale basis | page hash or date ≠ step 3; node date ≠ step 3 | `StaleWrite` | no snapshot, no COM |
| Page in a protected section | unlocked | `NotSupported` + AD-21's `userAction` | before COM |
| Locked section | the section, a page in it, a group holding it | `SectionLocked` | before COM |
| Page with subpages | | per Q2 | before COM (a) |
| Recycle bin or in it | bin, Deleted Pages, a recycled page or section | `ObjectNotFound` | before COM |
| Bad basis | `elementHashes`; `pageHash` on a section | `InvalidArgument` | before the lock |
| OneNote refuses | `0x80042010`, `0x80042014`, `0x8004201B`, `0x80042030` | `StaleWrite`, `ObjectNotFound`, `SectionLocked`, `ModalDialogOpen` | message names the entry |
| COM timeout | | `outcomeUnknown: true` | no retry |

</intent-contract>

## Answers (Phill, 2026-11-13, Inbox thread "5.5 soft delete")

He took the recommendation on every question.

- Q1 (b): two tools, `delete_hierarchy_object` here and 5.6's `permanently_delete_hierarchy_object`.
- Q2 (a): a page that has subpages is refused, saying how many subpages it has; delete those first.
- Q3 (a): the deleted page's new ID in Deleted Pages comes from comparing Deleted Pages before and after the delete, under the lock; if more than one page is new, narrow by name; if that still isn't exactly one, return no ID and warn (`recycleBinIdMissing`).
- Q4 (a): `restore_from_journal` on a deleted section or group says where it went: a section is in the notebook's recycle bin under the same ID; a group's sections are there one by one, and the journal entry records how the group was laid out.
- Q5 (a): restoring a soft-deleted page from the journal makes a new page while the original stays in Deleted Pages; leave it. The guide points to OneNote's own restore, or epic 3's restore tool.
- Q6 (a): a delete returns the object's ID, the journal entry's ID, and, for a page, its new ID in the recycle bin.
- Q7 (a): an empty section group and a nested section group (not yet tested live) are allowed in the build; opt-in integration probes for them (L5, L6) run at the sitting before merge.
- Q8: approved with these answers.

## Build Directions (unattended build, 2026-11-13)

These settle what the answers leave to the build. They are binding for this build and sit beside the Answers; where the intent contract above predates an answer, the answer and these directions win. Items marked **Assumption** are the build's conservative choice on something only Phill can decide; each is listed in the Auto Run Result.

- **5.4's code is not on this base.** This branch sits on 5.4's plan commit (b1f8afc) only; 5.4 (rename and reorder) is being built in parallel on `story/5.4-update-hierarchy`, and 5.3's branch is getting security fixes in parallel too. So the pieces the intent contract borrows from 5.4 (`HierarchySnapshot`, `JournalEntry.HierarchyKind`, a node reader with `lastModifiedTime`, `isRecycleBin`, `isDeletedPages`, `pageLevel`, a date-only basis check) do not exist yet. **Assumption:** build the minimum of them that 5.5 needs here, each in its own small file or as a small additive member, with the names the plan uses (`JournalEntry.HierarchyKind = "hierarchy"`, `HierarchySnapshot`), so that the rebase onto 5.4 later unifies two near-identical pieces rather than untangling an edit. Don't build anything for 5.4's own use (no `UpdateHierarchy`).
- **Keep it additive and separate.** Own use case (`SoftDeleteUseCase.cs`), own tool file (`DeleteHierarchyObjectTool.cs`), own tests file, own integration tests file. Touch shared files (`PageWrite.cs`, `JournalEntry.cs`, `JournalStore.cs`, `IOneNoteGateway.cs`, `ComGateway.cs`, `ServerSetup.cs`, `ToolWarnings.cs`, `ToolMeta.cs`, `RestoreFromJournalUseCase.cs`, the fakes) only where the plan needs it, with small, local edits; prefer adding a member to reshaping an existing one. Don't touch 5.3's create-hierarchy files beyond what sharing requires.
- **Q2's error code.** A page with subpages is refused with `InvalidArgument` before COM: the message gives the subpage count, the `userAction` says to delete the subpages first (deepest first) or move them out. In a protected section without a read grant the message gives the count only (it is a count already).
- **Q7's probes.** L5 (empty group) and L6 (nested group) are allowed through the tool in the build; the opt-in integration tests for them are written, and run at the sitting before merge.
- **This build never touches OneNote.** No integration tests run, no COM, no starting, closing or killing OneNote, no `ONENOTE_MCP_INTEGRATION`, no server exe, never `NavigateTo`. Don't read or touch `%APPDATA%\OneNoteMcp` or `%LOCALAPPDATA%\OneNoteMcp`; don't edit settings, `CLAUDE.md` or config files. Only `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`. The integration tests (`tests/OneNoteMcp.Integration/SoftDeleteIntegrationTests.cs`) use `TestNotebook`'s guard and the MCP Test notebooks only, and are written, not run.
- **Known flaky unit test.** `ComGatewayLifecycleTests.AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked` can fail on timing; rerun once before treating it as a failure.
- **Privacy.** No real notebook, employer or person names in code, tests, fixtures or docs; use "notebook A/B/C", "MCP Test" and "MCP Test Cloud".
- **Don't commit.** Leave the changes in the working tree; the build session commits after review.

## Open Questions

Closed: all answered above (Phill, 2026-11-13).

1. **Tool shape.** AD-21 names one `delete_hierarchy`; AD-7 takes the class from an explicit `permanent` argument. 5.6 adds the permanent delete.
   - (a) One `delete_hierarchy(objectId, permanent?, basis?)`, `permanent` defaulting to false, shared with 5.6.
   - (b) Two tools: `delete_hierarchy_object(objectId, basis?)` here (to the recycle bin) and 5.6's `permanently_delete_hierarchy_object(objectId, basis?)`.
   - (c) One per kind: `delete_page`, `delete_section`, `delete_section_group`.
   Recommended: (b). A user's Claude Code permission rules work per tool name, so they can allow the recoverable delete and keep a prompt on the permanent one; each description states one rule set (grants, backup, refusals) with no conditional argument, as 5.3 Q1 and 5.4 Q1 (b) chose. The spine's `delete_hierarchy` mentions get renamed in the spine sync.
2. **A page with subpages.** OneMore's sync notes that deleting a page leaves its subpages behind, so it deletes them separately.
   - (a) Refused with `InvalidArgument` before COM, giving the subpage count, with a `userAction` to delete the subpages first (deepest first) or reorder them out.
   - (b) The page and its subpages, one `DeleteHierarchy` and one journal entry each, in one call.
   - (c) The page only; the subpages stay, with a warning `subpagesLeft`.
   Recommended: (a). Nothing is re-parented without the caller asking, each delete keeps its own entry and recycle-bin ID, and it is cheap to relax. Probe L8 records what OneNote does.
3. **Picking the deleted page's ID in Deleted Pages** (the ticket's unknown: two pages there can share its name).
   - (a) Compare the Deleted Pages page IDs read in step 3 with those read after the delete, under the lock. One new ID is it; several are narrowed to those with the page's name; anything but one is left out, with `recycleBinIdMissing`.
   - (b) By name only, the newest match by `lastModifiedTime`.
   - (c) By name and creation `dateTime`.
   Recommended: (a). It doesn't depend on names or on dates OneNote may rewrite, and the write lock keeps our own processes out; only a user's own delete in the same moment can make it ambiguous, and then it falls back safely. Probe L1 checks that the recycled page keeps its name, and L9 checks same-named pages and a notebook's first delete.
4. **What `restore_from_journal` says for a deleted section or group.** AD-8 keeps `NotSupported` for `hierarchy` entries.
   - (a) For a soft delete's entry, the `userAction` says where the thing went: a section is in the notebook's recycle bin under the same ID, to restore in OneNote (or with epic 3's `restore_from_recycle_bin`); a group's sections are there one by one, and the entry file records the group's layout to rebuild it.
   - (b) 5.4's generic `userAction`, pointing to the entry file.
   Recommended: (a). The entry can't bring a section back, but the recycle bin can, and the user needs to know that rather than open a JSON file.
5. **`restore_from_journal` on a soft-deleted page's entry.** It works today: a new page from the snapshot, with new IDs and only the binaries AD-8 allows. The original stays in Deleted Pages, so a restore leaves two copies.
   - (a) Unchanged. The skill (entry 7) says to prefer OneNote's restore, or epic 3's `restore_from_recycle_bin`, which keep the original.
   - (b) Also warn `originalInRecycleBin` when the entry's `recycleBinId` still resolves there.
   Recommended: (a). It leaves story 2.11's code alone, and epic 3's tool is the full-fidelity path. A deleted section afterwards counts as gone, so `parentNamePath` or `Recovered` applies (AD-8).
6. **Result fields.** AD-9 gives `meta.journalEntryId` to page writes only.
   - (a) `meta.objectId`, `meta.journalEntryId`, plus `meta.recycleBinId` for a page when it was read back.
   - (b) `objectId` and `journalEntryId` only; `recycleBinId` lives in the journal.
   Recommended: (a). The caller can find or restore the page without opening the journal, and epic 3's tool takes that ID.
7. **Unprobed group shapes in the build** (an empty group, a nested group).
   - (a) Allowed in the build; the live sitting probes them through the tool (L5, L6) before the PR merges, and a bad result becomes a refusal in a follow-up.
   - (b) Refused with `NotSupported` until the probes pass, then lifted in a follow-up.
   Recommended: (a). The snapshot is taken first either way, the probe exercises the real code path, and the merge waits on the sitting anyway.
8. **Approve the plan** with these answers, so the build can start: Approve / Change.

## Live probes for the sitting

None runs in the build; each is an opt-in test in `tests/OneNoteMcp.Integration/SoftDeleteIntegrationTests.cs` (skipped unless `ONENOTE_MCP_INTEGRATION=1`), on throwaway nodes it creates in MCP Test (cloud ones in MCP Test Cloud, with Sync Now after each step). Probes marked "gateway" call `DeleteHierarchy` directly, because the tool refuses that case.

- **L1 (verify).** Soft-delete a throwaway page through the tool: it is in Deleted Pages, the entry's `recycleBinId` matches the read-back, and `restore_from_journal` brings it back as a new page. Records what the recycled page keeps (name, `dateTime`, `pageLevel`, dates).
- **L2 (verify).** Read a page that isn't displayed, edit it through `update_page_content`, then delete it with the old basis: `StaleWrite` from the `pageHash` (its page-XML date doesn't move, spike 2.2 constraint 4), and nothing deleted.
- **L3 (Deferred row).** A displayed page on MCP Test Cloud whose two dates differ (Phill types one letter), deleted through the tool with the hierarchy date: deleted, not `0x80042010`.
- **L4 (Deferred row, gateway).** An unlocked protected section's page, soft-deleted with its hierarchy date, local and cloud: where does it land (Deleted Pages, and is that `encrypted`), is it refused (which HRESULT), or is it deleted outright? Read without asking for content.
- **L5 (Deferred row).** An empty section group: what is left in the hierarchy and the recycle bin.
- **L6 (Deferred row).** A nested group (a section, plus a subgroup holding a section): does the subgroup dissolve too, and do both sections land in the bin?
- **L7 (gateway).** A group holding a locked protected section: `0x8004201B`, a partial delete, or all of it? The tool refuses it first either way.
- **L8 (gateway).** A page with one subpage: what OneNote does to the subpage (OneMore: left behind).
- **L9.** Two same-named pages already in Deleted Pages, then a third same-named delete: the right `recycleBinId`. Optional: a first page delete in 5.3's throwaway notebook, which has no recycle bin yet.
- **L10.** Soft-delete a section, and an unlocked protected section, through the tool: in `OneNote_RecycleBin` with the same ID (the protected one locked, `encrypted` kept), a `hierarchy` entry (DPAPI for the protected one), and `restore_from_journal` gives Q4's `NotSupported`.
- **L11.** Read a section's date, add a page to it, delete with the old basis: `StaleWrite`, nothing deleted.
- **L12 (cloud).** Page, section and group soft deletes on MCP Test Cloud: Phill checks each reached OneDrive in OneNote for the web.
- Only if Q2 (b) or (c), or Q7 (b), is chosen: a probe of that path before it ships.

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `DeleteHierarchyAsync(objectId, expectedLastModified, permanent, ct)`; 5.6 reuses it. Doc comment lists it.
- `src/OneNoteMcp.Com/ComGateway.cs` (pattern: `CreateSectionAsync`, `UpdatePageContentAsync`'s date guard) -- `CallKind.Write`, never retried; `Interop/IApplication.cs` slot 10 already declares `DeleteHierarchy`. `HResultMap.cs` already maps `0x80042010`, `0x80042014`, `0x8004201B`, `0x80042030`.
- `src/OneNoteMcp.Core/Writes/PageWrite.cs` (`ReadAsync` l.99, `CheckBasis` l.227, `CallNamingEntryAsync` l.179) -- the page path's read order (recycle bin, `SectionLocked`, page), basis comparison and entry-naming failure. Share them; don't copy. `PageWrite.Class` and `SnapshotAsync` hard-code `PageWrite`, so the snapshot request must take the class.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs`, `WritePipeline.cs` -- steps 5 and 6 and `AfterCallAsync`/`AfterCallFailed` host the read-back and rewrite; no runner change expected.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` (`CheckAsync` l.51), `HierarchyWalk.cs` (`OwningSectionAsync`, `OwningNotebookAsync`) -- the notebook refusal in step 1 comes where the walk finds no owning notebook for the target itself.
- `src/OneNoteMcp.Xml/HierarchyNode.cs`, `HierarchyChildren.cs`, `NotebookSections.cs` and 5.4's node reader -- add a reader for a notebook's Deleted Pages page IDs and names (`isDeletedPages` inside `isRecycleBin`), and for locked sections beneath a group (`LockedSections` exists).
- `src/OneNoteMcp.Core/Journal/JournalEntry.cs`, `JournalStore.cs` (`Write` l.49, `File.Move` l.75), `PageSnapshot.cs`, 5.4's `HierarchySnapshot.cs` -- add `RecycleBinId` and a one-time rewrite (temp file, `File.Replace`); entry kinds `page` and `hierarchy`.
- `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` l.172-186 -- the `NotSupported` branch for Q4's `userAction`.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- `recycleBinIdMissing`; `RecycleBinId` per Q6.
- `src/OneNoteMcp.Server/Tools/DeletePageContentTool.cs`, `PageBasisArgument.cs`, `ServerSetup.cs` (`WithTools`, l.161) -- tool, closed basis argument, registration.
- Tests: `FakeApplication.cs` l.94 (`DeleteHierarchy` throws today), `FakeGateway.cs`, `DeletePageContentToolTests.cs`, `PageWriteSecurityTests.cs`, `ProtectedSectionKit.cs`, `JournalTests.cs`, `RestoreFromJournalToolTests.cs`, `ToolContractTests.cs` + `Contract/tool-contract.json`, `McpSdkBindingTests.cs`.
- `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs` l.336-408 (soft-delete clean-up, `RecycleBinPageIdsAsync` diff), `WriteProbes/HierarchyProbes.cs` l.382-452 (spike 5.1's delete rows) -- opt-in pattern and the before/after diff to reuse.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/` -- Deleted Pages reader, locked-beneath-a-group reader, subpage check (+ fixture tests).
- [x] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs`, `FakeApplication.cs` (+ `ComGatewayLifecycleTests`) -- `DeleteHierarchyAsync`, date guard, never retried.
- [x] `src/OneNoteMcp.Core/Journal/JournalEntry.cs`, `JournalStore.cs` (+ `JournalTests`) -- `recycleBinId` and the one-time rewrite, protected blob untouched, a missing file reported, not thrown.
- [x] `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- share the page read, basis check and entry naming with the soft delete; snapshot class as a parameter.
- [x] `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs` (new) -- page, section and group `SoftDelete` operations, the step-1 notebook refusal, the read-back and rewrite.
- [x] `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` -- Q4's `userAction`.
- [x] `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- warning and meta.
- [x] `src/OneNoteMcp.Server/Tools/` new tool per Q1, `ServerSetup.cs` registration.
- [x] `tests/OneNoteMcp.Tests/SoftDeleteToolTests.cs` (new) -- every I/O matrix row; no COM and no entry on refusals; snapshot before the call; hierarchy date sent for a page; `MinValue` never sent; counts only without a read grant; `recycleBinIdMissing` on an ambiguous diff and on a failed rewrite.
- [x] `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs` -- contract.
- [x] `tests/OneNoteMcp.Integration/SoftDeleteIntegrationTests.cs` (new, opt-in) -- L1-L12, written, not run.
- [x] `README.md`, `plugin/skills/guide/SKILL.md` -- tool rows.
- [x] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync: tool names (Q1), the subpage rule (Q2), the ID pick (Q3), restore's `userAction` (Q4), `meta.recycleBinId` and `journalEntryId` (Q6), a locked section beneath a group, the step-1 notebook refusal, `recycleBinIdMissing`; then the probe results for the Deferred rows.

**Acceptance Criteria:**
- Given a granted notebook, when a page in it is soft-deleted, then the page is in Deleted Pages, the result carries `meta.recycleBinId`, and the journal entry holds the same ID.
- Given that entry, when `restore_from_journal` runs, then a new page with the snapshot's content appears in the page's section.
- Given a page that isn't displayed and a basis read before another write to it, when it is soft-deleted, then `StaleWrite` returns and no journal entry or COM write happens.
- Given a notebook ID, when the tool runs, then `NotSupported` returns with no grant dialog, lock or COM call.
- Given OneNote answers `DeleteHierarchy` with `0x80042010`, then the result is `StaleWrite`, and its message names the journal entry.

## Implementation Notes

- **5.4's pieces, built here (Build Directions assumption).** `JournalEntry.HierarchyKind`, `Journal/HierarchySnapshot.cs`, `Xml/DeleteTargets.cs` (the node reader: kind, `lastModifiedTime`, `isRecycleBin`, `isInRecycleBin`, `locked`, `encrypted`, subpage count, page IDs, protected-page stripping) and a date-only check inside `SoftDeleteNode`. The rebase onto 5.4 merges them with 5.4's versions.
- **Step 1's kind check is a `self` read in the use case, before the pipeline** (an assumption: the plan's Code Map hints at detecting a notebook through the grant walk). It reads only the object's own element, before any grant dialog and before the lock; its messages carry the ID only.
- **A page or node whose hierarchy has no usable `lastModifiedTime`** is refused as `Internal` before the snapshot, so `MinValue` is never sent (the gateway also refuses it).
- **A read-back that found the ID but whose rewrite failed** still returns `meta.recycleBinId`, beside `recycleBinIdMissing` (the warning's message says the ID and that the entry lacks it).
- **Shared edits.** `PageWrite`: snapshot takes the operation's `Class`; a `CheckSection` hook (the protected-section refusal, before the page is read); `OwningSection`, `PageNode`, `JournalFile` exposed; `CheckBasis(..., comparePageHash)`. The entry-naming wrapper moved to `WriteOperation.CallNamingFailureNoteAsync`, with `PageWrite.CallNamingEntryAsync` delegating. `HierarchyWalk.NodeLocationAsync`. `ComGateway.BadExpectedDate` takes an optional noun.
- **Live-sitting setup.** L3, L4, L7 and the protected half of L10 need throwaways made by hand in MCP Test, found by exact name ("Soft delete 5.5 Vault", "Soft delete 5.5 Locked group", "Soft delete 5.5 L3"); they skip with a `PHILL:` instruction when absent.
- **Guide skill.** Its "can't delete" line now says "can't delete permanently"; the description grew from 1095 to 1166 characters (`claude plugin validate --strict` not run in this build).
- **Verification.** `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 2539 passed. Nothing touched OneNote.

## Plan Change Log

### 2026-11-13 — Rebase onto 5.4 (origin/story/5.4-update-hierarchy 889c5dc, on 5.3 92b07d3)

- **Trigger:** 5.4's code landed, so 5.5's stand-ins for it (Build Directions, first item) are replaced by the real pieces. No change inside `<intent-contract>`.
- **Resolved:** `HierarchySnapshot` is 5.4's (notebook roots allowed, `HierarchyWalk.ContainerLocationAsync`, `HierarchyNode.Root`, `DisplayName` as the title, an empty `ParentId` for a notebook root) plus 5.5's rule: unless the root is a password-protected section (kept whole, DPAPI), the stored XML drops every protected section's `one:Page` elements (`DeleteTargets.WithoutProtectedPages`), so a section group's entry never holds a protected page's ID or title. 5.5's `HierarchyWalk.NodeLocationAsync`/`NodeLocation` are dropped. `JournalEntry` keeps 5.4's `HierarchyKind` wording and adds 5.5's `RecycleBinId`; `ToolWarnings` keeps both `Unchanged` and `RecycleBinIdMissing`. `IOneNoteGateway`, `ComGateway`, `ServerSetup`, the fakes and every test gateway carry both `UpdateHierarchyAsync` and `DeleteHierarchyAsync`; 5.4's integration `GuardedGateway` gained a `DeleteHierarchyAsync` that throws. Counts: 22 tools, 19 gateway methods, 21 tools with arguments and 51 properties. `tool-contract.json` regenerated from the test's output, not hand-merged. README and the guide skill keep 5.4's text with 5.5's rows and sentences added (the skill now says "ten write tools"; its description is 1253 characters).
- **Kept unchanged:** `SoftDeleteUseCase`, `DeleteTargets`, `RecycleBin`, `SoftDeleteToolTests` (including `Group_IsSoftDeleted_ItsEntryHoldsItsLayout_WithoutAProtectedSectionsPages`) and the integration probes.
- **Verified:** `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 2730 passed.

## Review Triage Log

### 2026-11-13 — Review pass
- verdicts: 5 findings — high 0, medium 2, low 2, false 0, maybe-false 1
- findings:
  - `[medium]` `[patch]` A page delete's `noBasis`/`partialBasis` warnings and not-a-date error carried `PageWrite.NoBasisUserAction` ("pass meta.basis unchanged"), whose `elementHashes` this tool refuses, and the `noBasis` message said a change "could be overwritten" — fixed: `PageWrite` gained overridable `BasisUserAction` and `UnbasedRisk` (defaults unchanged for other writes); `SoftDeletePage` supplies `SoftDeleteUseCase.PageBasisUserAction` (lastModifiedTime and pageHash only, no elementHashes) and "wasn't caught"; a test asserts it.
  - `[maybe-false]` `[defer]` A page in a locked section might come back `ObjectNotFound`, not `SectionLocked`, because step 1's `GetHierarchy(pageId, self)` runs before `PageWrite`'s section-first read — would settle with a live check of what `GetHierarchy` at `self` returns for a page in a locked section (no probe L1-L12 covers it; the unit test passes because the fake answers). If true, medium.
  - `[low]` `[reject]` AC4 says "no ... COM call" for a notebook, but step 1 makes a COM read to learn the target is a notebook — no harm (a read is the only way to know the kind, and no grant dialog, lock or write happens); the fix would be to edit this build's plan's AC wording.
  - `[low]` `[patch]` The tool description's spurious-`StaleWrite` note (spike 5.1 constraint 2) covered only sections and groups, though a page delete sends the page's hierarchy date too — fixed: the note now covers every target.
  - `[medium]` `[patch]` `restore_from_journal`'s description, `entryId` text, `EntryIdUserAction`, README row and guide-skill row named only `update_page_content`/`delete_page_content` as entry sources — fixed: they name `delete_hierarchy_object` and say a page's entry restores as a new page while a section's or group's returns `NotSupported` pointing to the recycle bin.

## Design Notes

**OneMore (checked 2026-11-13).** Its wrapper `OneNote.DeleteHierarchy(objectId)` calls COM with the default date and `deletePermanently` false, so it never checks a date, and it logs and swallows every error; we keep AD-17's basis and AD-9's errors. Its commands delete pages one by one (duplicate removal, merge, sync), and its hierarchy sync notes that deleting a page leaves its subpages behind (Q2). Its Quick Notes filing deletes that section's pages one by one because OneNote treats the Quick Notes section as read-only; its hierarchy readers skip `isRecycleBin` and `isInRecycleBin` everywhere. It never looks up a deleted page's new ID, so Q3 has no prior art; spike 5.1's clean-up already used the before/after diff of recycle-bin page IDs.

**The protected-page probe.** The build keeps AD-21's refusal. L4 calls the gateway directly. If the page lands somewhere `encrypted`, AD-21 says 5.5 allows it, so a follow-up in this story lifts the refusal, with a spine-sync row. If it lands in the unencrypted Deleted Pages, is refused, or is deleted outright, the refusal stays and the Deferred row is closed with the evidence. If the page did land in the plain Deleted Pages, COM can't remove it from there (a recycled page's permanent delete is `0x8004200C`, spike 5.1 constraint 9), so the probe prints a line asking Phill to empty the recycle bin in OneNote before the sitting ends. The probe page holds only throwaway text.

**The journal and restore.** A page delete's entry is an ordinary `page` entry, so `restore_from_journal` (2.11) restores it as a new page without change (Q5). A section's or group's entry is `hierarchy`: `restore_from_journal` returns `NotSupported` (AD-8), and the real way back is OneNote's recycle bin, or epic 3's `restore_from_recycle_bin`. A group has no unit to restore, because it dissolves, so its entry is the only record of its layout (AD-7). A notebook is refused before step 6, so it never gets an entry. A protected section's entry is DPAPI and needs no read grant to write. A later `restore_from_journal` of a page whose section was soft-deleted since treats the section as gone, and falls to `parentNamePath` or `Recovered` (AD-8).

**Not probed.** The user's Quick Notes section, which lives outside MCP Test; OneMore says OneNote treats it as read-only. If OneNote refuses it, the error passes through the HRESULT map (`0x8004200B` is `Internal`, retryable, with its `userAction`).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot matches.

**Manual checks (if no CLI):**
- Phill's live sitting: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter SoftDelete` on MCP Test and MCP Test Cloud (L1-L12), with Sync Now after each cloud step and an OneNote-for-the-web check for L12.

## Auto Run Result

**Status:** built (2026-11-13, bmad-build-auto, full route, quick review).

**Summary.** New tool `delete_hierarchy_object(objectId, basis?)` soft-deletes a page, section or section group through the AD-7 pipeline as `SoftDelete`. A notebook is refused with `NotSupported` before any grant dialog. A page is refused in the recycle bin, a locked section, a password-protected section (AD-21's `userAction`) or when it has subpages (Q2, count only); its page-XML basis (date and `pageHash`) is compared, `DeleteHierarchy` is sent its hierarchy date, it gets a `page` journal entry, and its new ID in Deleted Pages is picked by a before/after diff narrowed by name (Q3) and written into the entry once (`recycleBinId`), else `recycleBinIdMissing`. A section or group gets a `hierarchy` entry of its `pages`-scope read (DPAPI for a protected section; a group's entry drops protected sections' pages), with a locked section anywhere beneath refused. `restore_from_journal` says where a soft-deleted section or group went (Q4). Results carry `meta.objectId`, `meta.journalEntryId` and, for a page, `meta.recycleBinId` (Q6).

**Files changed.**
- `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs` (new) — the use case: step-1 checks, `SoftDeletePage` (a `PageWrite`) and `SoftDeleteNode`.
- `src/OneNoteMcp.Server/Tools/DeleteHierarchyObjectTool.cs` (new), `ServerSetup.cs` — the tool and its registration.
- `src/OneNoteMcp.Core/Journal/HierarchySnapshot.cs` (new), `JournalEntry.cs`, `JournalStore.cs` — `hierarchy` entries, `RecycleBinId`, the one-time rewrite.
- `src/OneNoteMcp.Xml/RecycleBin.cs`, `DeleteTargets.cs` (new) — Deleted Pages reader and Q3 pick; target, subpage and page-ID readers.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs` — `DeleteHierarchyAsync` with the empty-ID and date guards, never retried.
- `src/OneNoteMcp.Core/Writes/PageWrite.cs`, `WriteOperation.cs`, `Access/HierarchyWalk.cs` — shared hooks (snapshot class, `CheckSection`, `comparePageHash`, `BasisUserAction`, `UnbasedRisk`, `CallNamingFailureNoteAsync`, `NodeLocationAsync`).
- `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs`, `Server/Tools/RestoreFromJournalTool.cs` — Q4's `userAction`; entry-source wording.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs`, `ToolWarnings.cs` — `recycleBinId`, `recycleBinIdMissing`.
- `tests/OneNoteMcp.Tests/SoftDeleteToolTests.cs`, `SoftDeleteReadsTests.cs` (new) plus `JournalTests`, `ComGatewayLifecycleTests`, `RestoreFromJournalToolTests`, fakes, counts and `Contract/tool-contract.json`.
- `tests/OneNoteMcp.Integration/SoftDeleteIntegrationTests.cs` (new) — opt-in probes L1-L12, written, not run.
- `README.md`, `plugin/skills/guide/SKILL.md`, `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — tool rows and spine-sync items.

**Review findings.** 5 from the quick lens. Patched 3 (2 medium, 1 low): the page delete's basis `userAction` and `noBasis` wording; the spurious-`StaleWrite` note for every target; `restore_from_journal`'s entry-source wording. Deferred 1 (maybe-false, medium if true): a page in a locked section may read as `ObjectNotFound` because of step 1's `self` read. Rejected 1 (low): AC4's "no COM call" versus step 1's COM read — harmless, and its fix would edit this plan's AC.

**Follow-up review recommended: true.** Patched by verdict: high 0, medium 2, low 1. Named risk: the post-review `PageWrite` hooks (`BasisUserAction`, `UnbasedRisk`) change the shared page-write path that `update_page_content` and `delete_page_content` use, and no reviewer has read them (the existing tests for those tools pass).

**Verification.** `dotnet build OneNoteMcp.slnx -warnaserror --no-incremental`: 0 warnings, 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 2539 passed, 0 failed (after the patches; the known flaky lifecycle test passed first time). Matrix audit: every I/O matrix row has a passing test in `SoftDeleteToolTests`. `claude plugin validate --strict plugin`: only the pre-existing missing-icon warning. Nothing touched OneNote.

**Residual risks.**
- 5.4's pieces (`HierarchySnapshot`, `JournalEntry.HierarchyKind`, `DeleteTargets`, the date-only check) are stand-ins built here; the rebase onto 5.4 must merge them.
- Unprobed live behaviour waits on the sitting: L3 (displayed page, hierarchy date), L4 (protected-page soft delete), L5/L6 (empty and nested groups, Q7), L7 (group holding a locked section), L8 (subpages), L9 (same-named pages, first delete), and the deferred locked-section page read.
- Assumptions (Build Directions and Implementation Notes): 5.4 stand-ins; step 1's kind check as a `self` read; `Internal` for a missing hierarchy date; `meta.recycleBinId` still returned when the rewrite fails.
