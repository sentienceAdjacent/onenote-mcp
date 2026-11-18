---
title: 'restore_from_recycle_bin'
type: 'feature'
ticket: '5'
created: '2026-11-17'
status: 'built'
baseline_revision: '64c3f290cc86ef64aa66f1fc9b591a16ff1f94bb'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-move-a-section-or-section-group-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can soft-delete a page or a section (epic 5) but can't bring one back. E7 asks for `restore_from_recycle_bin`. E8 asks that a recycled protected section comes back still protected, and only into its own place.

**Approach:** `restore_from_recycle_bin(objectId, parentId?, entryId?, position?)` is one `HierarchyWrite` through the existing `WritePipeline` (AD-7, AD-8). It sends one `UpdateHierarchy` of the target parent's XML, listing the recycled item, as a move does (spike 3.1 Implication 6). Core finds the item's soft-delete journal entry and builds the XML with the existing move builders: `HierarchyUpdate.MovePage` for a page, `MoveContainer` for a section. A restored page gets a new ID; a section keeps its ID. Phill's answers to Q1–Q8 are below (Decisions). A section restored into another notebook (Q7) is one call that runs in two steps: a restore in place in its own notebook, then the existing cross-notebook section move.

## Boundaries & Constraints

**Always:**
- **Step 1, before the lock, the arguments.** An empty `objectId` is `InvalidArgument`. An empty `parentId` or `entryId` counts as absent. An `entryId` that isn't a GUID is `InvalidArgument`. A `position` below 1 is `InvalidArgument`, as a move refuses it.
- **Step 1, what the object is.** An `objectId` OneNote doesn't know is `ObjectNotFound` (AD-9). Only two kinds of item are restorable: a page in a notebook's Deleted Pages section (`isDeletedPages`), and a section directly under the recycle-bin group (`isRecycleBin`). The rest are refused with `InvalidArgument`, each with its own `userAction`:
  - a live item ("it isn't in the recycle bin");
  - the recycle bin itself, or Deleted Pages itself;
  - a page inside a recycled section ("restore the section");
  - a section group, since OneNote bins a group's sections one by one (5.1 constraint 8).
- **Matching the journal entry** (AD-8; Q3, Q10). Only entries whose `operation` is `delete_hierarchy_object` and whose `notebookPath` is the item's notebook count. Entries are read from their plain metadata only: no protected entry's DPAPI blob is ever opened. A protected section's entry has no plain `title`, but it does have plain `objectId`, `parentId` and `SectionKey`.
  - **A page** matches by `recycleBinId == objectId`. Failing that, by name: the recycled page's `name` against the entry's `title`. An entry whose `recycleBinId` is another page still in the bin belongs to that page and is left out.
  - **A section** matches by `objectId`. Failing that, because a reopen gives the section a new ID while its `path` is kept (AD-6), it matches by name against the entry's `title` for a plain section (Q5), and by `SectionKey` for a protected one.
  - **`entryId`**, when given, picks the entry directly. It must be a soft-delete entry of this notebook and of the item's kind (a page entry for a page, a section entry for a section). Otherwise it is `InvalidArgument`.
  - **Several matches** (Q1 a) are `InvalidArgument`, and nothing is sent. The message lists each candidate's entry ID, deletion time (UTC) and original parent ID, and the `userAction` says to ask the user and retry with `entryId`.
  - **A match made by name** gives the warning `restoredByName` (new; Q6), naming the entry ID and the parent used.
  - **No match.** Sections binned by a group's delete (whose entry names the group), entries pruned by retention, and items deleted in OneNote all have no original parent.
- **Where it goes** (Q4 a). A caller's `parentId` wins, except for a protected section. Otherwise the entry's original parent is used:
  - for a page, its `parentId`, else its `parentNamePath` (groups and section name) among the notebook's live sections;
  - for a section, its `parentId`, else its `parentNamePath` (groups only) among the notebook's live section groups, where an empty path means the notebook itself.
  - Several path matches count as none. A parent that is in the recycle bin counts as missing.
  - With no parent from either source, the restore is `InvalidArgument`, with a `userAction` asking for `parentId`.
- **The target.** An unknown `parentId` is `ObjectNotFound`. A target of the wrong kind is `InvalidArgument`: a page needs a live section, and a section needs a live notebook or section group. A locked target section is `SectionLocked`. A page restored into an unlocked protected section needs only the write grant (AD-21 Writes, as a move without `position`). 
- **A section into another notebook** (Q7, Phill 2026-11-17). One call, two steps:
  1. The section is restored in place, under its original parent in its own notebook, last among the sections. This is the restore pipeline with its own journal entry. With no original parent there, it is `InvalidArgument`: nothing is sent, and the `userAction` asks for a `parentId` in its own notebook.
  2. The section is moved to the caller's `parentId` (and `position`) through the existing `move_hierarchy_object` section path (`MoveContainerWrite`), in its own pipeline run. Every move rule applies: grants and the grant dialog, the J1 cloud-into-group refusal, the settle, `readGrantAtLocation`, the name clash, the granted key and `0x80042004`. The move takes a second journal entry.

  If step 2 is refused or fails, the section stays restored in its own notebook. The result is `ok: true` with the new warning `restoredNotMoved`, which gives the move's error code and message and a `userAction` to move it with `move_hierarchy_object`. `meta.journalEntryIds` lists both entries (restore, then move); `meta.journalEntryId` is the restore's. A protected section is never moved (Q4: a `parentId` elsewhere is refused before anything is sent).
- **A protected section** (`encrypted` in the bin, and locked there, 3.1 RP):
  - It goes back only to its own place. That is the parent whose path holds its entry's `SectionKey`: the folder of the section's old path, found among the notebook's live section groups, or the notebook itself. The entry's `parentId` is only a shortcut that must agree with it. There is no `parentNamePath` fallback (AD-8).
  - A `parentId` naming any other parent is `InvalidArgument`.
  - If it has no entry, or its own place is gone, the restore is `ObjectNotFound` (as `restore_from_journal` answers for a protected entry whose section is gone), with a `userAction` to restore it in OneNote.
  - It needs the write grant only, never an unlock or a read grant (AD-21).
- **The grants:** the write grant on the item's notebook, and on the target's for a page in another notebook (AD-6). Both are re-resolved under the lock by the pipeline's ledger (story 3.12).
- **Refused like a move:**
  - a section landing on a key that holds another section's read grant is `InvalidArgument`; its own former key is allowed (AD-8);
  - a section whose name a live same-kind sibling has is `InvalidArgument`.
- **Placing it** (Q2 c: an optional `position`, validated and placed as `move_hierarchy_object` does):
  - **A page.** `position` is 1-based among the target section's live pages. The page goes before the page now at that position, which must be top-level, and is sent at `pageLevel` 1. Every other page is sent at its level as read (5.4 L6). Without `position` it goes last. A recycled subpage keeps level 2 in the bin (5.5 L1), so it is sent at 1.
    - A position past the end, or inside a subpage block, is `InvalidArgument`. No message carries the page count.
    - In a protected target section, a `position` needs that section's read grant held at the call's start, never prompted for. It is checked in step 1, after a locked target and before the write grants (AD-7 step 1.2). Without the grant, the restore is `ReadNotGranted`. Without `position`, it needs no read grant.
  - **A section.** `position` is 1-based among the target's sections, as `MoveContainerWrite` places a section. Without `position` it goes last among the sections, before any section group. A position past the end is `InvalidArgument`.
  - If the read-back finds the item elsewhere (spike 3.1 R1 and R2 landed pages at position 1 when no position logic applied), the restore stands, with `readBackDiffers` naming the position found.
- **A section in a cloud notebook** (Q3 a): 3.12's 10 s settle under the write lock, with the existing warning `settled`.
- **Step 4 repeats every check above** against step 3's read under the lock: the item, the match, the target, the protected rule and the name clash.
- **Before COM and after it:**
  - No basis (AD-8).
  - Step 6 takes one `hierarchy` journal entry of the target parent; nothing more is needed, since the item stays in the bin if the call fails.
  - Step 7 is one `UpdateHierarchy`, never retried. `0x80042004` is handled as a move handles it: `outcomeUnknown`, then a best-effort re-read.
- **Read-back:**
  - For a page, its new ID is the one ID in the target that wasn't there before, preferring the recycled name; that is `meta.objectId`.
  - For a section, it must be back under the target with its ID and gone from the bin.
  - A miss gives `readBackDiffers`; a failed re-read gives `readBackFailed`.
  - The cache drops the recycled page ID.
- **The result** carries `meta.objectId` (the current ID), `meta.parentId` (the target) and `meta.journalEntryId`. Messages carry IDs, counts and dates only (AD-9).

**Never:**
- No restore of a whole section group.
- No restore from `onenoteBackup` entries (`restore_from_journal`'s).
- No server dialog for the candidates (Q3 b was rejected).
- No unlock, and no read-grant prompt.
- No `NavigateTo`.
- No behaviour change to `delete_hierarchy_object`, `move_hierarchy_object` or `restore_from_journal`. Shared helpers are extracted from them unchanged.
- No skill or README text (entry 10).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Page, by recycleBinId | soft-deleted page, entry has its bin ID | back last in its section at level 1, new ID | none |
| Page, by name | bin ID stale or missing, one name match | as above | warning `restoredByName` |
| Two same-named | two entries match by name, no `entryId` | nothing sent; candidates in message | `InvalidArgument` |
| Chosen entry | the same, with `entryId` of one | back under that entry's parent | none |
| Wrong entryId | another notebook's, a page entry for a section, not a soft delete | — | `InvalidArgument` |
| Caller target | `parentId` = live section, same or other notebook | page lands there | none |
| Page at a position | `position` 2 in a 3-page section | lands second, level 1 | none |
| Bad position | 0; past the end; inside a subpage block | — | `InvalidArgument` |
| Protected position | `position` in a protected target with no read grant | nothing sent, no write-grant dialog | `ReadNotGranted` |
| Section at a position | `position` 1 among its parent's sections | lands first | none |
| Section | soft-deleted section | back under its parent, same ID, last among sections | none |
| Section after reopen | ID changed, one title match | back under its parent by `parentNamePath` | warning `restoredByName` |
| Section at root, by path | entry's `parentNamePath` empty, `parentId` stale | back at the notebook root | none |
| Section to other notebook | `parentId` in another local notebook | restored in place, then moved there; two journal entries | none |
| Section to other notebook, move refused | e.g. from a cloud notebook into a local notebook's section group (J1) | restored in place, not moved | warning `restoredNotMoved` (move's code `NotSupported`) |
| Section to other notebook, no original parent | no entry, `parentId` in another notebook | nothing sent | `InvalidArgument` |
| Cloud section | section in a cloud notebook | restored, then 10 s settle | warning `settled` |
| No entry, no target | deleted in OneNote, pruned, or binned by a group delete | nothing sent | `InvalidArgument` (ask for `parentId`) |
| Original parent gone | entry's parent soft-deleted too, no target | nothing sent | `InvalidArgument` |
| Protected section | recycled, locked | back in its own place by `SectionKey`, still encrypted and locked, same ID | none |
| Protected elsewhere | `parentId` ≠ its own place | nothing sent | `InvalidArgument` |
| Protected orphan | no entry, or own place gone | nothing sent | `ObjectNotFound` |
| Not recycled / bin itself | live item; recycle bin, Deleted Pages, group, page in recycled section | — | `InvalidArgument` |
| Unknown IDs | `objectId` or `parentId` OneNote doesn't know | — | `ObjectNotFound` |
| Wrong-kind or binned target | page into a group; section into a section; target in the bin | — | `InvalidArgument` |
| Locked target | page into a locked section | — | `SectionLocked` |
| Name clash | section whose name a live sibling has | — | `InvalidArgument` |
| No grant | target notebook not granted | — | `WriteNotGranted` |

## Decisions (Phill, 2026-11-17, plan approved)

- **Q1, a.** Several matches are refused, listing the candidates, and a new `entryId` argument picks one.
- **Q2, c.** An optional `position`, validated and placed as `move_hierarchy_object` does. Absent, the item goes last.
- **Q3, a.** A section restored in a cloud notebook gets the 10 s settle, with `settled`.
- **Q4, a.** The caller's `parentId` wins, except for a protected section.
- **Q5, a.** A plain section falls back to its name.
- **Q6, a.** A match made by name warns `restoredByName`.
- **Q8.** One story.
- **Q7, decided (Phill 2026-11-17).** A section restore whose `parentId` is in another notebook is one call in two steps: restore in place in its own notebook, then the existing `MoveContainerWrite` cross-notebook move, so every move rule applies. Two journal entries. If the move fails or is refused, the section stays restored in its own notebook, and the result says the move didn't happen and why. A protected section is never moved.

- **Protected by path, no SectionKey (Phill 2026-11-18, option a).** A recycled section that counts as protected by its path (under an export root or the backups folder, entry 15), but whose journal entry has no `SectionKey` because it wasn't protected when deleted, is `ObjectNotFound` whatever `parentId` is given: its own place can't be proven.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` and `MoveContainerWrite.cs` -- the models for the new write.
  - Both: step order, `FailureNote`, `0x80042004` handling (`PartialCopyFindingsAsync`), the read-back, and `MovedPage` (l.459).
  - `MoveContainerWrite.cs`: the name clash (`NameClashUserAction`, l.80); the granted-key refusal (`GrantLocationUserAction`, l.88; `MovedLocations`, l.512); the settle (`SettleTime`, l.46; `SettleQuietlyAsync`, l.247–286); `ReResolvesNotebooks` (l.150).
  - The step-4 re-checks follow `MovePageWrite.cs` l.344–372.
- `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- `MovePage` (l.58) and `MoveContainer` (about l.96) build the XML. A restore is the same XML: the recycled ID listed under the target.
- `src/OneNoteMcp.Xml/RecycleBin.cs` -- `BinId`, `DeletedPages`, `IsRecycleBin`. Also `HierarchyElements.IsRecycleBin` and `InRecycleBin`, and `HierarchyTarget.Read` (`IsDeletedPages`, `InRecycleBin`, `Encrypted`, `Locked`, `Path`).
- `src/OneNoteMcp.Core/Journal/JournalEntry.cs` -- `ObjectId`, `ParentId`, `ParentNamePath`, `SectionKey`, `RecycleBinId` (l.82), `Title`, `TakenAtUtc`, `NotebookPath`.
  - Read through `JournalStore.cs` and `JournalFiles.cs`, metadata only (a protected entry's `title` is inside its blob, `JournalStore.cs` l.207).
  - A page entry's `ParentNamePath` includes the section's name (`PageSnapshot.cs` l.87). A section entry's holds its groups only (`HierarchySnapshot.cs` l.103). A protected section's entry has `SectionKey` from its path (l.104).
  - Entries are pruned by retention (`JournalPruner.cs` l.130).
- `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` l.254–270, `Target` -- matches live sections by a section name path. Extract the section half for pages, without the `Recovered` fallback, and add a group-path resolver for sections.
- `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs` -- what a soft delete writes (`OperationName` l.39; page snapshot with `recycleBinId`; a section's `hierarchy` entry). Read only.
- `src/OneNoteMcp.Core/UseCases/MoveHierarchyObjectUseCase.cs`, `src/OneNoteMcp.Server/Tools/MoveHierarchyObjectTool.cs`, `src/OneNoteMcp.Server/ServerSetup.cs` (l.175, l.234) -- the shapes for the use case and tool. Annotations: `ReadOnly=false, Destructive=false, Idempotent=false, OpenWorld=false`. `ToolWarnings.Settled` already exists (l.130).
- Tests: `tests/OneNoteMcp.Tests/MoveHierarchyObjectToolTests.cs` (fixture), `FakeGateway.cs`, `McpSdkBindingTests.cs:92` (26 → 27), `Contract/tool-contract.json`, `ToolContractTests.cs`, `NavigateToToolTests.cs`, and `PluginPackagingTests.cs:256` (add the tool to `DocumentedByRound2Entry10`).
- Integration: `SoftDeleteIntegrationTests.cs` (soft delete, then read the bin), `MoveHierarchyObjectIntegrationTests.cs` (the Vault pause, the 2 s settle), and `TestNotebook`'s guarded calls.

## Tasks & Acceptance

**Execution:**
- [x] Shared helpers, extracted with no change in behaviour:
  - `src/OneNoteMcp.Core/UseCases/JournalTargets.cs` (new), from `RestoreFromJournalUseCase.Target`, plus a section-group path resolver;
  - the read-back and partial-copy helpers of the move writes, where they can be shared unchanged.
  The existing tests prove nothing changed.
- [x] `src/OneNoteMcp.Core/Journal/SoftDeleteEntries.cs` (new) -- lists one notebook's soft-delete entries from metadata and matches them: bin ID, name, section ID, `SectionKey`, `entryId`, and leaving out entries owned by another binned page. Pure over the entry list.
- [x] `src/OneNoteMcp.Core/Writes/RestoreFromRecycleBinWrite.cs` (new) -- the operation, per the Always rules.
- [x] `src/OneNoteMcp.Core/UseCases/RestoreFromRecycleBinUseCase.cs` (new), `src/OneNoteMcp.Server/Tools/RestoreFromRecycleBinTool.cs` (new), `src/OneNoteMcp.Server/ServerSetup.cs` -- the tool. Its description covers what it restores and where, `position`, a page's new ID, protected sections, the candidates and `entryId`.
- [x] `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- `restoredByName` (Q6). `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, the binding test and the packaging test -- 27 tools.
- [x] `tests/OneNoteMcp.Tests/SoftDeleteEntriesTests.cs` and `RestoreFromRecycleBinToolTests.cs` (new) -- every matrix row, the XML sent, the journal entry, the cache drop, and refusals before any grant dialog and before COM.
- [x] `tests/OneNoteMcp.Integration/RestoreFromRecycleBinIntegrationTests.cs` (new) -- see S1.
- [x] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine sync:
  - AD-5 and AD-14: the tool, with its arguments.
  - AD-8 `restore_from_recycle_bin`: the matching (bin ID, name, `SectionKey`, `entryId`), the candidates, the placing, the own-notebook rule for sections, and the protected place by `SectionKey`.
  - AD-9: the `restoredByName` warning, and `ObjectNotFound` for a protected orphan.
  - A `.memlog.md` entry.

**Acceptance Criteria:**
- Given a page soft-deleted with `delete_hierarchy_object`, when Claude restores it by its recycled ID, then it is back in its section under the ID in `meta.objectId`, with a journal entry, and Deleted Pages no longer lists it.
- Given a section soft-deleted with `delete_hierarchy_object`, when Claude restores it, then it is back under its parent with the same ID and a journal entry.
- Given two same-named recycled pages whose entries both match only by name, when Claude restores one without `entryId`, then nothing is sent and the error lists both candidates; with one's `entryId`, it goes back to that entry's section.
- Given a recycled protected section, when Claude restores it, then it is back in its own place, still `encrypted` and `locked`; when Claude names another `parentId`, nothing is sent.
- Given a recycled page with no matching entry, when Claude restores it without `parentId`, then nothing is sent and the `userAction` asks for one.
- Given a soft-deleted plain section and a `parentId` in another notebook, when Claude restores it, then it is restored in place and moved there through the move rules, with two journal entries; if the move is refused, the section stays restored in its own notebook and the result carries `restoredNotMoved`.
- Given the existing soft-delete, move and `restore_from_journal` tests, when they run, then they pass unchanged.

## Implementation Notes

- **Implemented by a subagent** in the s35 worktree, from this plan and its context files; it never touched OneNote, never set the integration variable and never read OneNoteMcp's app-data folders.
- **Shared helpers, extracted unchanged.** `JournalTargets.SectionsAt` is `restore_from_journal`'s name-path match (its `Recovered` fallback stays in that use case); `GroupsAt` is the new group-path resolver, over the new `NotebookSections.FindGroups`. The move writes' read-back pick (`MovedPage`) and `Why` now live in `Writes/MoveReadBack.cs`; the partial-copy findings weren't shareable unchanged (each write words its own), so restore has its own, shaped like the move's.
- **Metadata only.** `JournalStore.ReadMetadata` deserialises an entry and never calls DPAPI; `SoftDeleteEntries.Load` lists the journal folder with it and skips any entry it can't read (damaged, newer `schemaVersion`, pruned meanwhile).
- **Judgment calls the plan left open.**
  - Every refusal, not only the locked target and the position read grant, runs in step 1 before the write grants, so no grant dialog is shown for a refused call; step 4 repeats them all. A target that differs between step 1 and step 3 is `WriteNotGranted`, retryable.
  - An entry whose `objectId` is another section still in the bin is left out of a section's name match, as the plan's rule does for pages.
  - A plain section's "own former key" for the granted-key check is the path in its entry's XML root; a protected one's is its `SectionKey`. A landing path the server can't predict is `NotSupported`, as a move's.
  - A protected section matched by name compares the file name of its path in the bin with the file name in the entry's `SectionKey`, ignoring case. A section OneNote renamed on entering the bin (spike data shows `Fixtures 2`) won't match by name.
  - A page target that is the recycle bin, Deleted Pages or in the bin is `InvalidArgument` (the matrix's "binned target"), not `ObjectNotFound` as a move answers it.
  - `meta.objectId` for a page whose read-back finds no new page is the recycled ID when Deleted Pages still lists it, else absent, as the move does.
- **Files touched beyond the task list:** `src/OneNoteMcp.Xml/NotebookSections.cs` (`FindGroups`, `NotebookSectionGroup`), `src/OneNoteMcp.Core/Journal/JournalStore.cs` (`ReadMetadata`), `src/OneNoteMcp.Core/Writes/MoveReadBack.cs` (new), `tests/OneNoteMcp.Tests/ToolContractTests.cs` and `NavigateToToolTests.cs` (the tool lists), and `McpSdkBindingTests.cs`'s property counts (64 → 68, 25 → 26 tools with arguments).
- **TDD, not followed to the letter.** The code was written before its tests. To make up for it, a mutation check ran: with the protected-position read grant, the Q7 seam, the own-former-key exception, the cloud settle, the step-1 `Check` and the owned-entry exclusion each disabled, 6 of 6 were caught (one needed the added `ARefusalAboutTheTarget_ComesBeforeAnyGrantDialog` theory). The files were restored.
- **Integration (S1).** A section is restored into a run section group, never to MCP Test's root: story 5.1's `UpdateHierarchy` guard refuses the test notebook's own ID. So S1.5 needs a hand-made section group `Restore 3.5 Vault group` holding the unlocked protected section `Restore 3.5 Vault` (skipped otherwise).
- **Verification (2026-11-17):**
  - `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors.
  - `dotnet test tests/OneNoteMcp.Tests`: 3,428 passed, 0 failed (the contract snapshot included).
  - `dotnet test tests/OneNoteMcp.Integration` with no integration variable: 217 passed, 119 skipped, 0 failed; the 5 new live tests are skipped.
- **Second pass (2026-11-17): review fixes and Q7, strict TDD.** Each test below was written first and seen failing for the reason given, then the code was written.
  - Q7 success, `ASection_IntoAnotherNotebook_IsRestoredInPlace_ThenMovedThere_...`: failed `InvalidArgument` (the old seam).
  - Q7 J1, `ACloudSection_IntoAnotherNotebooksGroup_..._RestoredNotMoved`: failed `InvalidArgument` (the seam).
  - Q7 grant declined, `..._WhoseGrantIsDeclined_StaysRestored_WithRestoredNotMoved`: failed `InvalidArgument` (the seam).
  - Q7 no original parent, `..._WithNoOriginalParent_SendsNothing_...`: failed, the message lacked "no original parent".
  - Fix 1, `AProtectedSection_WithAnotherSectionsEntryId_IsInvalidArgument` and `APage_WithAnEntryIdItDoesntMatch_IsInvalidArgument`: both failed `ok: true` (the foreign entry was used).
  - Fix 2, `SoftDeleteEntriesTests.ASectionDeletedTwice_MatchesItsNewestIdEntry_NotCandidates`: failed `Assert.Same` (no entry; candidates instead).
  - Fix 3, `TheJournal_IsLoadedOnce_PerCall`: failed, expected 1 load. A loader seam (`loadEntries`) was added to the use case first, with no change in behaviour, so the test could count loads.
  - Fix 4, `TheNoTargetUserAction_AndTheToolDescription_NameTheRetentionPeriod_NotThirtyDays`: failed, "30 days" found.
  - Fix 5, `AProtectedSection_WhoseEntryParentIdDisagreesWithItsSectionKeyPlace_IsObjectNotFound`: failed `ok: true`.
  - Added alongside, passing before the change (guards, not TDD items): `AProtectedSection_WhoseEntryParentIdAgrees_Restores`, the `OtherId` row of `AProtectedSection_SentElsewhere_...`, and the `GroupedId, OtherId` row of `ARefusalAboutTheTarget_ComesBeforeAnyGrantDialog` (replacing the seam's row).
  - Q7's move runs without a basis, and its `noBasis` warning is dropped: the restore placed the section a moment before, so no caller read exists to compare. `OtherNotebookRefusal` and `OtherNotebookUserAction` are gone; `OwnNotebookParentUserAction` is new.
  - Integration: `S4_ASection_IntoTheThrowaway_IsRestoredInPlace_ThenMovedThere` (S4.1), skipped unless the throwaway is open. S4.2 stays a sitting step.
  - Verification: `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 3,439 passed, 0 failed; `RestoreFromRecycleBinIntegrationTests` with no integration variable: 6 skipped, 0 failed.

## Plan Change Log

## Review Triage Log

- 2026-11-17, Fable final review of the spine sync, low effort: 6 must fix and 8 optional. Phill had all 14 applied (Inbox, 2026-11-17).
  - **Finding 4, a code change, the 3.14 pattern.** After an `outcomeUnknown` or cancelled move in the restore-then-move path, `restoredNotMoved` says the section "may or may not have been moved", its `userAction` says to re-read the target and its own notebook, and `meta.parentId` is absent. Red test first: `ASection_IntoAnotherNotebook_WhoseMoveOutcomeIsUnknown_SaysItMayOrMayNotHaveMoved_AndClaimsNoParent` (Timeout and Cancelled). Both cases failed on the missing wording before the fix. The tool description and the `ToolWarnings` doc were updated.
  - **The other 13, spine and memlog only:**
    - AD-5: the Q7 exception (`restoredNotMoved`, not `completedSteps`).
    - AD-8: the restore section restructured with labelled bullets. It covers the three-entry count, the `noBasis` drop, the name clash, the grant-key rule for every section, `restoredByName` unless `entryId`, the step 1/3/4 wording and the Q7 move-side checks. It adds the read-back, the arguments and case sensitivity, and puts the protected order first, with the Q4 reading.
    - AD-9: `journalEntryIds`, plus restore causes in the ObjectNotFound, InvalidArgument, WriteNotGranted, ReadNotGranted and NotSupported rows.
    - AD-21: "its own place".
    - The memlog has one entry for Phill's decisions.
  - Verification: build with 0 warnings; unit tests 3,443 passed; integration without the variable 217 passed, 120 skipped, 0 failed.
- 2026-11-17, automated security review, medium, verified, patched test-first. The landing-location grant check exempted `now.Entry?.SectionKey ?? OldPath` whatever the match. So a section matched by name, or by an `entryId` that picks a name match, could land where a different, earlier section's read grant still sits, and inherit it: the backlog 8 class. My quick review's fix 1 (`entryId` limited to the item's own matches) didn't cover it, because the own matches include name matches.
  - Red test first: `ASectionMatchedByName_NeverInheritsTheReadGrantAtItsEntrysFormerKey` (both with and without `entryId`). Both cases failed with `ok:true`, restored onto the granted key.
  - Fix: the exemption applies only when the entry matched by the section's own ID (`!ByName` and `entry.ObjectId == objectId`). Otherwise a grant at the landing key refuses the restore with `InvalidArgument`.
  - Consequence: a protected section matched by `SectionKey` after a reopen, landing on its own old key that holds a read grant, is refused. The user revokes the grant or restores in OneNote. This is accepted as the safe side.
  - Spine AD-8 and the memlog are updated. The team lead's call on dropping `noBasis` in the Q7 path is recorded in the memlog.
  - Unit tests: 3,441 passed.
- 2026-11-17, quick lens, iteration 0: 6 findings, all verified and patched test-first in the second pass. Nothing was deferred.
  - **high, patch.** `SoftDeleteEntries.ById` let an `entryId` pick another item's entry. A protected section could then be restored to another section's place, or land on another key through the own-former-key exemption. Fix: `entryId` picks only among the item's own matches.
  - **medium, patch.** A section deleted, restored and deleted again had two entries with its ID, and the restore offered them as candidates. Fix: ID matches take the newest by `TakenAtUtc`; name matches still list candidates.
  - **medium, patch.** The journal was read in full twice per call, once under the write lock. Fix: it is loaded once in step 1 and reused in step 3.
  - **medium, patch.** A protected section's entry `parentId` wasn't checked against its `SectionKey` place. Fix: if they disagree, the restore is `ObjectNotFound` with the restore-in-OneNote `userAction`.
  - **low, patch.** "30 days" was hard-coded where the retention period is a setting. Fix: the text now says "the journal's retention period".
  - **low, patch.** S1's setup wasn't in the sitting steps. Fix: Verification has the S1 setup with a tree.
- 2026-11-17, Q7 built (Phill's decision), test-first. Four tests failed before the code: success, the J1 refusal, a declined grant, and no original parent.
  - Deviation from the frozen text: a successful move-on adds two journal entries, not one, because `MoveContainerWrite` snapshots both parents. So `meta.journalEntryIds` holds three: the restore's, then the move's two.
  - Build choice: the move's `noBasis` warning is dropped, because the restore placed the section moments before and no caller basis exists.
- Verification after the second pass: build 0 warnings; unit tests 3,439 passed; integration without the variable 217 passed, 120 skipped, 0 failed.

- 2026-11-17, Fable final review of the draft, low effort: 8 must fix, 7 optional, all applied in this revision.
  - The protected place is now found by `SectionKey`.
  - Sections now have fallbacks: by name after a reopen (Q5), and resolution by group path.
  - A section's target is limited to its own notebook (Q7).
  - Q-dependent rules are marked, and `entryId` has been added.
  - Every task has a full path, and the extraction task is added.
  - The ACs now cover sections, candidates, protected sections and no entry.
  - A name match warns (Q6).
  - Entries owned by another binned page are left out.
  - New matrix rows: no entry, pruned entries, group deletes, unknown IDs, wrong-kind targets.
  - A page landing other than last gives `readBackDiffers`.
  - A protected orphan is `ObjectNotFound`, matching `restore_from_journal`.
  - The step-4 repeat is stated.
  - The DPAPI wording is corrected.

## Design Notes

Considered matching every item by name only. Going with the bin ID (or the section ID) first, because 5.5 L9 picked the right one among three same-named pages. Names are only the fallback AD-8 names.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the contract snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` with no integration variable -- expected: it builds, and every live test is skipped.
- TDD: write each new test first and watch it fail before writing the code.

**Sitting steps.** The main session runs these with Phill. The build never runs them, never sets the integration variable, never touches OneNote, and never reads OneNoteMcp's app-data folders.
- **S1 setup (Phill, once before S1; after each run the section is left locked, so repeat the unlock before a re-run).** In MCP Test, make a section group named exactly `Restore 3.5 Vault group`. Inside it, make a section named exactly `Restore 3.5 Vault`, password-protect it, and leave it unlocked:
  ```
  MCP Test (notebook)
  └── Restore 3.5 Vault group (section group)   <-- new
      └── Restore 3.5 Vault (section)            <-- new, password-protected, unlocked
  ```
  The test skips S1.5 if these aren't there. A section can't be restored to MCP Test's root in a live test, because the guard refuses the test notebook's own ID.
- **S1.** With the setup done
  1. A page soft-deleted with the tool comes back by its recycled ID, under a new ID, with one journal entry, and is no longer in Deleted Pages. Without `position` it lands last; a second page restored with `position` 1 lands first.
  2. A section soft-deleted with the tool comes back under its parent, with its ID.
  3. Two same-named pages are deleted from different sections, and both entries' `recycleBinId` are removed. The test checks that the restore gives the candidates and sends nothing, and that each `entryId` puts its page back in its own section.
  4. A page restored with `parentId` lands in another section.
  5. The protected section, soft-deleted with the tool (it is locked in the bin), restores to its own place still `encrypted` and `locked`, with its ID. A restore with a `parentId` elsewhere is `InvalidArgument`, and nothing is sent.
- **S2.** Register the dev build as `onenote-dev`. Ask Claude to delete and then restore a page and a section in MCP Test, then the same in MCP Test Cloud, checking the sync status at every step and running Sync Now at the end. Phill checks in OneNote, and on the web for cloud, that each item is back where it was and gone from the recycle bin.
- **S4 (Q7).** Through onenote-dev:
  1. Soft-delete a run section in MCP Test, then restore it with `parentId` = a section group in the local throwaway notebook (`MCP Test Throwaway 1112-034716 a`). Expected: `ok`, the section is in the throwaway with its ID, and there are two journal entries.
  2. Soft-delete a section in MCP Test Cloud, then restore it with `parentId` = a section group in MCP Test (local). Expected: `ok` with `restoredNotMoved` (the move's J1 refusal, `NotSupported`). The section is back in its MCP Test Cloud parent, and nothing is in MCP Test. Check sync status before and after.
- **S3 (optional; it tests the reopen fallback, Q5).** Phill closes and reopens MCP Test after a section soft delete. Then Claude restores it, and the result carries `restoredByName`.

**Sitting result (2026-11-18, with Phill): passed.**
- S1: `RestoreFromRecycleBinIntegrationTests` 6 of 6 passed. The six cover page position, a section, same-named candidates, `parentId`, the protected section, and S4.1 into the throwaway.
- S2, local: in a run section in MCP Test, a page was soft-deleted and restored to its section under a new ID. A section was soft-deleted and restored to the notebook root with its ID and both pages.
- S2, cloud: the same in MCP Test Cloud. The page was restored with no warnings, and the section with `settled`. Phill confirmed there was no sync error.
- S4.2: a cloud section was soft-deleted, then restored with `parentId` set to a section group in MCP Test. The result was `ok` with `settled` and `restoredNotMoved` (the move was `NotSupported`: cloud into another notebook's group). The section was back in MCP Test Cloud and the MCP Test group was unchanged. Phill confirmed there was no sync error after Sync Now.
- S3 (optional): not run. The name fallback is covered by unit tests.
