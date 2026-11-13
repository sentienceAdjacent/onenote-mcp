---
title: 'Soft delete'
type: 'feature'
ticket: '5'
created: '2026-11-13'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred: []
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

## Open Questions

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
- [ ] `src/OneNoteMcp.Xml/` -- Deleted Pages reader, locked-beneath-a-group reader, subpage check (+ fixture tests).
- [ ] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs`, `FakeApplication.cs` (+ `ComGatewayLifecycleTests`) -- `DeleteHierarchyAsync`, date guard, never retried.
- [ ] `src/OneNoteMcp.Core/Journal/JournalEntry.cs`, `JournalStore.cs` (+ `JournalTests`) -- `recycleBinId` and the one-time rewrite, protected blob untouched, a missing file reported, not thrown.
- [ ] `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- share the page read, basis check and entry naming with the soft delete; snapshot class as a parameter.
- [ ] `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs` (new) -- page, section and group `SoftDelete` operations, the step-1 notebook refusal, the read-back and rewrite.
- [ ] `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` -- Q4's `userAction`.
- [ ] `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- warning and meta.
- [ ] `src/OneNoteMcp.Server/Tools/` new tool per Q1, `ServerSetup.cs` registration.
- [ ] `tests/OneNoteMcp.Tests/SoftDeleteToolTests.cs` (new) -- every I/O matrix row; no COM and no entry on refusals; snapshot before the call; hierarchy date sent for a page; `MinValue` never sent; counts only without a read grant; `recycleBinIdMissing` on an ambiguous diff and on a failed rewrite.
- [ ] `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs` -- contract.
- [ ] `tests/OneNoteMcp.Integration/SoftDeleteIntegrationTests.cs` (new, opt-in) -- L1-L12, written, not run.
- [ ] `README.md`, `plugin/skills/guide/SKILL.md` -- tool rows.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync: tool names (Q1), the subpage rule (Q2), the ID pick (Q3), restore's `userAction` (Q4), `meta.recycleBinId` and `journalEntryId` (Q6), a locked section beneath a group, the step-1 notebook refusal, `recycleBinIdMissing`; then the probe results for the Deferred rows.

**Acceptance Criteria:**
- Given a granted notebook, when a page in it is soft-deleted, then the page is in Deleted Pages, the result carries `meta.recycleBinId`, and the journal entry holds the same ID.
- Given that entry, when `restore_from_journal` runs, then a new page with the snapshot's content appears in the page's section.
- Given a page that isn't displayed and a basis read before another write to it, when it is soft-deleted, then `StaleWrite` returns and no journal entry or COM write happens.
- Given a notebook ID, when the tool runs, then `NotSupported` returns with no grant dialog, lock or COM call.
- Given OneNote answers `DeleteHierarchy` with `0x80042010`, then the result is `StaleWrite`, and its message names the journal entry.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
