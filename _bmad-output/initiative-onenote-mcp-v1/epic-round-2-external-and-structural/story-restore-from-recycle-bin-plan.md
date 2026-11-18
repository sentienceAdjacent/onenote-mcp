---
title: 'restore_from_recycle_bin'
type: 'feature'
ticket: '5'
created: '2026-11-17'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-move-a-section-or-section-group-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can soft-delete a page or a section (epic 5) but can't bring one back. E7 asks for `restore_from_recycle_bin`. E8 asks that a recycled protected section comes back still protected, and only into its own place.

**Approach:** `restore_from_recycle_bin(objectId, parentId?, entryId?)` is one `HierarchyWrite` through the existing `WritePipeline` (AD-7, AD-8). It sends one `UpdateHierarchy` of the target parent's XML, listing the recycled item, as a move does (spike 3.1 Implication 6). Core finds the item's soft-delete journal entry and builds the XML with the existing move builders: `HierarchyUpdate.MovePage` for a page, `MoveContainer` for a section. A restored page gets a new ID; a section keeps its ID. `entryId` is Q1 (a)'s and is dropped if Phill picks otherwise. Every rule below marked with a Q follows that question's recommendation and is rewritten to Phill's answer before approval.

## Boundaries & Constraints

**Always:**
- **Step 1, before the lock, the arguments.** An empty `objectId` is `InvalidArgument`. An empty `parentId` or `entryId` counts as absent. An `entryId` that isn't a GUID is `InvalidArgument`.
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
- **The target.** An unknown `parentId` is `ObjectNotFound`. A target of the wrong kind is `InvalidArgument`: a page needs a live section, and a section needs a live notebook or section group. A locked target section is `SectionLocked`. A page restored into an unlocked protected section needs only the write grant (AD-21 Writes, as a move without `position`). A section's target must be in the item's own notebook (Q7 a), else `InvalidArgument`.
- **A protected section** (`encrypted` in the bin, and locked there, 3.1 RP):
  - It goes back only to its own place. That is the parent whose path holds its entry's `SectionKey`: the folder of the section's old path, found among the notebook's live section groups, or the notebook itself. The entry's `parentId` is only a shortcut that must agree with it. There is no `parentNamePath` fallback (AD-8).
  - A `parentId` naming any other parent is `InvalidArgument`.
  - If it has no entry, or its own place is gone, the restore is `ObjectNotFound` (as `restore_from_journal` answers for a protected entry whose section is gone), with a `userAction` to restore it in OneNote.
  - It needs the write grant only, never an unlock or a read grant (AD-21).
- **The grants:** the write grant on the item's notebook, and on the target's for a page in another notebook (AD-6). Both are re-resolved under the lock by the pipeline's ledger (story 3.12).
- **Refused like a move:**
  - a section landing on a key that holds another section's read grant is `InvalidArgument`; its own former key is allowed (AD-8);
  - a section whose name a live same-kind sibling has is `InvalidArgument`.
- **Placing it** (Q2 a):
  - A page goes last in the target section, at `pageLevel` 1, with every other page sent at its level as read (5.4 L6). A recycled subpage keeps level 2 in the bin (5.5 L1), so it is sent at 1.
  - A section goes last among the target's sections, before any section group.
  - If the read-back finds it elsewhere (spike 3.1 R1 and R2 landed pages at position 1), the restore stands, with `readBackDiffers` naming the position found.
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
| Section | soft-deleted section | back under its parent, same ID, last among sections | none |
| Section after reopen | ID changed, one title match | back under its parent by `parentNamePath` | warning `restoredByName` |
| Section at root, by path | entry's `parentNamePath` empty, `parentId` stale | back at the notebook root | none |
| Section to other notebook | `parentId` in another notebook | — | `InvalidArgument` |
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

</frozen-after-approval>

## Open Questions

- **Q1. Several journal entries match by name.** The tool takes the recycled item's ID (Decision Q3), but the server can't tell which entry, and so which original parent, is that item's.
  - (a) Refuse with `InvalidArgument`. The message lists the candidates (entry ID, deletion time, original parent ID), and a new optional `entryId` argument picks one on the retry.
  - (b) Refuse the same way with no new argument. The model passes the chosen original parent as `parentId`.
  - (c) Pick the most recent entry and warn.

  (a) is exact and keeps the original-parent logic, the name-path fallback included. (b) needs no new argument but loses that fallback. (c) is the guess the ticket forbids.

  Recommended: (a).
- **Q2. Where in the parent the item goes.**
  - (a) Last: a page last in the section at level 1, a section last among the sections.
  - (b) First, where spike 3.1 R1 and R2 happened to land.
  - (c) An optional `position`, as `move_hierarchy_object` takes it, with its protected-section read-grant rule.

  The original position isn't recorded (a page snapshot has no place in its section). (a) is the simplest and matches a move without `position`. (c) adds a surface the user can already get with a reorder.

  Recommended: (a). S1 checks that OneNote honours it.
- **Q3. A section restored in a cloud notebook.**
  - (a) Apply 3.12's 10 s settle, with `settled`, as for any section move touching a cloud notebook (AD-7 Moves, M2).
  - (b) No settle: spike 3.1 R3 restored a cloud section with no trouble, in one run.

  With Q7 (a) a restore never crosses notebooks, and the crash the settle guards against followed a cross-storage move. (a) still costs only 10 s and keeps every cloud section write alike.

  Recommended: (a).
- **Q4. Both a journal entry and `parentId`.** Decision Q4 allows the original parent or a caller target.
  - (a) The caller's `parentId` wins, except for a protected section.
  - (b) `parentId` is used only when the original parent can't be found.

  (a) puts an item somewhere new in one step, which R2 shows works. (b) needs a second call, a move, for that.

  Recommended: (a).
- **Q5. A plain section whose ID changed (the notebook was reopened).**
  - (a) Fall back to its name (the entry's `title`), with Q1's rule for several matches.
  - (b) No fallback: the restore asks for `parentId`.

  AD-8 gives every stored ID a fallback, and a reopen changes every section ID. Without (a), most restores after a restart need the user to name the parent.

  Recommended: (a).
- **Q6. Telling the caller a match was made by name.**
  - (a) A new warning, `restoredByName`, naming the entry ID and the parent used, so the model can tell the user where the item went and offer a move.
  - (b) No warning.

  A lone name match can be wrong. Suppose page "Notes" was deleted with the tool and restored, and later a different "Notes" was deleted in OneNote: the old entry then sends it to the first page's section. (a) makes that visible. A stricter rule, such as ignoring entries older than some age, would refuse genuine restores.

  Recommended: (a).
- **Q7. A section's caller target in another notebook.**
  - (a) Refused (`InvalidArgument`): a section restores only within its own notebook. To go further, restore it and then move it, so 3.12's cross-notebook rules (J1, the settle, the copies) apply.
  - (b) Allowed, applying every `MoveContainerWrite` cross-notebook rule.

  Spike 3.1 restored a section only into its own group. Cross-notebook section moves have failed or left copies (3.1 Implications 4, 5, 15).

  Recommended: (a).

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
- [ ] Shared helpers, extracted with no change in behaviour:
  - `src/OneNoteMcp.Core/UseCases/JournalTargets.cs` (new), from `RestoreFromJournalUseCase.Target`, plus a section-group path resolver;
  - the read-back and partial-copy helpers of the move writes, where they can be shared unchanged.
  The existing tests prove nothing changed.
- [ ] `src/OneNoteMcp.Core/Journal/SoftDeleteEntries.cs` (new) -- lists one notebook's soft-delete entries from metadata and matches them: bin ID, name, section ID, `SectionKey`, `entryId`, and leaving out entries owned by another binned page. Pure over the entry list.
- [ ] `src/OneNoteMcp.Core/Writes/RestoreFromRecycleBinWrite.cs` (new) -- the operation, per the Always rules.
- [ ] `src/OneNoteMcp.Core/UseCases/RestoreFromRecycleBinUseCase.cs` (new), `src/OneNoteMcp.Server/Tools/RestoreFromRecycleBinTool.cs` (new), `src/OneNoteMcp.Server/ServerSetup.cs` -- the tool. Its description covers what it restores and where, a page's new ID, protected sections, the candidates and `entryId`.
- [ ] `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- `restoredByName` (Q6). `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, the binding test and the packaging test -- 27 tools.
- [ ] `tests/OneNoteMcp.Tests/SoftDeleteEntriesTests.cs` and `RestoreFromRecycleBinToolTests.cs` (new) -- every matrix row, the XML sent, the journal entry, the cache drop, and refusals before any grant dialog and before COM.
- [ ] `tests/OneNoteMcp.Integration/RestoreFromRecycleBinIntegrationTests.cs` (new) -- see S1.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine sync:
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
- Given the existing soft-delete, move and `restore_from_journal` tests, when they run, then they pass unchanged.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
- **S1.** Phill unlocks a protected section in MCP Test once, so the test can soft-delete it; the test protects nothing itself. Then run `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~RestoreFromRecycleBinIntegrationTests`. In sections the run makes:
  1. A page soft-deleted with the tool comes back by its recycled ID, under a new ID, with one journal entry, and is no longer in Deleted Pages. The test records the position it landed at (Q2).
  2. A section soft-deleted with the tool comes back under its parent, with its ID.
  3. Two same-named pages are deleted from different sections, and both entries' `recycleBinId` are removed. The test checks that the restore gives the candidates and sends nothing, and that each `entryId` puts its page back in its own section.
  4. A page restored with `parentId` lands in another section.
  5. The protected section, soft-deleted with the tool (it is locked in the bin), restores to its own place still `encrypted` and `locked`, with its ID. A restore with a `parentId` elsewhere is `InvalidArgument`, and nothing is sent.
- **S2.** Register the dev build as `onenote-dev`. Ask Claude to delete and then restore a page and a section in MCP Test, then the same in MCP Test Cloud, checking the sync status at every step and running Sync Now at the end. Phill checks in OneNote, and on the web for cloud, that each item is back where it was and gone from the recycle bin.
- **S3 (optional; it tests the reopen fallback, Q5).** Phill closes and reopens MCP Test after a section soft delete. Then Claude restores it, and the result carries `restoredByName`.
