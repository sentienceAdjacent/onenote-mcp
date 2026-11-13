---
title: 'Permanent delete'
type: 'feature'
ticket: '6'
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

**Problem:** Claude can soft-delete (5.5) but can't permanently delete anything. `PermanentDelete`, `PermanentDeleteNotGranted` and `BackupFailed` exist only as enum values; the permanent-delete grant has a field in `settings.json` but no way to give it, check it or list it; there is no gateway `Publish`, no `backups` folder and no `onenoteBackup` entry.

**Approach:** `DeleteHierarchy(permanent=true)` through the existing pipeline as `PermanentDelete`, following the AD-6 grant rules, the AD-7 `PermanentDelete` bullets, AD-8 Backups, AD-17 and AD-21 Deletes as PR #43 (`plan/spine-sync-epic-2`) words them. The new grant is given, checked and listed per Q1 and Q2. A live page gets a page snapshot. A section, live or recycled, gets a `Publish` `pfOneNote` backup in the journal's `backups` folder, waited for until the file exists and isn't empty, and an `onenoteBackup` entry (plus what Q3 picks); any failure is `BackupFailed` and nothing is deleted. An empty section group gets a `hierarchy` entry. A notebook, a password-protected section, a non-empty section group and a recycled page are refused with `NotSupported`. The pipeline deadline is 600 s for this class.

**Assumes 5.5's recommended answers** (5.5's plan is a draft and Phill hasn't answered it yet): separate tools, `delete_hierarchy_object` for soft deletes and `permanently_delete_hierarchy_object` here (5.5 Q1 b); a page with subpages is refused with `InvalidArgument` (5.5 Q2 a), here too; a journal entry is written before every delete (AD-8). It reuses 5.5's `DeleteHierarchyAsync(objectId, expectedLastModified, permanent, ct)`, its target read and refusals, and 5.4's `HierarchySnapshot` and node reader. If 5.5 or 5.4 is answered otherwise, those are the parts that change here.

## Boundaries & Constraints

**Always:**
- AD-7 step 1, before the lock, in this order: an empty `objectId`, a basis with `elementHashes`, or a `pageHash` for a target that isn't a page, is `InvalidArgument`. A notebook is `NotSupported` with AD-7's notebook `userAction`, and a protected section (`encrypted`, read live by the walk) `NotSupported` with AD-21's `userAction` (soft-delete it, then empty the recycle bin in OneNote), both before any dialog, so no grant is asked for a delete that can't run. For a page in a protected section with no read grant, a basis hash is `ReadNotGranted`, never prompted (AD-17, AD-21). Then the write grant (its dialog as now), then the permanent-delete grant per Q1; missing, `PermanentDeleteNotGranted`. Never a read-grant dialog.
- Step 3 reads the target as 5.5 does; a section also at `pages` scope (the empty-section check, Q3's page list), a group at `pages` scope (empty or not).
- Step 4 refuses before COM, in AD-7's order: the recycle bin itself `ObjectNotFound`; a notebook, a protected section (again, under the lock), a non-empty group (any section or group beneath), a recycled page, each `NotSupported`; the Deleted Pages section per Q4; a locked section holding the page `SectionLocked`; a page with subpages `InvalidArgument`.
- Step 5 (AD-17): a page compares the caller's page-XML date and `pageHash` with step 3 and sends `DeleteHierarchy` the hierarchy date; a section or group compares and sends its `GetHierarchy` date. `MinValue` is never sent.
- Step 6, in order, all before the COM delete: a page, `PageSnapshot` with class `PermanentDelete` (DPAPI in a protected section). An empty group, `HierarchySnapshot`. A section: Q3's page entries, then create `<journal>\backups` (never `Publish` into a missing folder), `Publish` to `backups\<entry name>.one` (a new name; `Publish` refuses an existing file), poll until it exists and isn't empty (and its size is unchanged across two polls 250 ms apart), up to `timeouts.publishSeconds` capped by the deadline, then write the `onenoteBackup` entry under that same name with `backupFile`. Any failure here (a `Publish` HRESULT such as `0x80042006`, `0x8004201A`, `0x80042031`; a timeout; a file that never lands or is empty) is `BackupFailed` naming no path, and the delete doesn't run.
- Step 7: `DeleteHierarchy(id, date, true)`, never retried; a failure names the journal entry. `0x8004200C` maps as now (`Internal`), but step 4 makes it unreachable.
- Deadline 600 s for `PermanentDelete` (AD-7), not counting dialog time. `Publish` runs under `timeouts.publishSeconds` (300 s default), `CallKind.Write`, never retried.
- Results: `ok: true`, `meta.objectId`, `meta.journalEntryId` (the page, `onenoteBackup` or `hierarchy` entry), and for a section `meta.backupFile` as a file name only. The page-text cache drops the page, or the section's pages.
- `restore_from_journal` on an `onenoteBackup` entry keeps its `NotSupported` and existing `userAction`.
- Pruner (AD-8): a backup counts toward the size cap and goes with its entry; a backup with no entry goes once past retention age. `uninstall --purge` removes backup-named files in `backups` and the folder when empty, still never recursing further.
- In a protected section with no read grant, messages and warnings carry counts only (AD-21). Messages carry IDs only (AD-9).
- Tool contract snapshot updated; `<Version>` stays `0.9.0-rc.1`. README's Permanent delete row and the guide skill's tool row only (recipes are entry 7's). Spine sync goes to `deferred-work.md`.

**Never:** a backup of a protected section, or any write of plaintext from one; opening a backup (AD-21); deleting a notebook; a section delete without its backup file present; a retried write; `MinValue`; trusting the file system for the delete's result (AD-7); a tool that grants; touching the user's Quick Notes section; editing `tickets.toml`; spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Page | page ID, basis from `get_page_content` | gone from the hierarchy, not in Deleted Pages; page entry | No error expected |
| Page, protected section, no read grant | date-only basis or none | gone; DPAPI entry; counts-only warnings | No error expected |
| Section, live or recycled | `{lastModifiedTime}` | backup file exists before the call; gone; `onenoteBackup` entry (+ Q3) | No error expected |
| Empty section group | | gone; `hierarchy` entry | No error expected |
| No permanent-delete grant | write grant only | per Q1; then `PermanentDeleteNotGranted` | no lock, no COM |
| Backup fails | `Publish` HRESULT, timeout, no file, empty file | `BackupFailed`; target still there | no delete |
| Notebook | | `NotSupported` + AD-7 `userAction` | step 1, no dialog |
| Protected section | locked or not, live or recycled | `NotSupported` + AD-21 `userAction` | step 1, no dialog |
| Non-empty group | | `NotSupported` | before COM |
| Recycled page | in Deleted Pages | `NotSupported`, `userAction` to empty the recycle bin in OneNote | before COM |
| Recycle bin / Deleted Pages | | `ObjectNotFound` / per Q4 | before COM |
| Hash basis, protected, no read grant | `pageHash` | `ReadNotGranted` | step 1 |
| Page with subpages | | `InvalidArgument` with the count | before COM |
| Stale basis | | `StaleWrite` | no snapshot, no COM |
| OneNote refuses | `0x80042010`, `0x80042014`, `0x8004201B`, `0x80042030` | `StaleWrite`, `ObjectNotFound`, `SectionLocked`, `ModalDialogOpen` | message names the entry |
| COM timeout | | `outcomeUnknown: true` | no retry |

</intent-contract>

## Open Questions

1. **How the permanent-delete grant is given.** AD-6 makes it a stored grant created like the write grant (dialog or CLI); the ticket says "CLI, dialog, listing". It is the one irreversible write, and an elicitation hook can answer any dialog (AD-6 Limits).
   - (a) As the spine says: the dialog (type the notebook's name, worded for permanent deletion) or `grant --permanent-delete "<notebook>"`, stored until revoked. After one answer, every later permanent delete in that notebook runs without asking.
   - (b) CLI only: no dialog. A missing grant is `PermanentDeleteNotGranted` with a `userAction` naming the CLI.
   - (c) The CLI stores the grant; without it, each call raises a one-call dialog naming what will go (the object's kind and name, and its notebook) where the user types that name. Nothing is stored, as with a `once` read grant.
   Recommended: (c). The model can never cause a lasting permission for the riskiest write, and each irreversible delete without a stored grant gets a confirmation naming exactly what goes; a user who wants no prompts grants it once in the terminal. It needs a spine-sync row for AD-6. (a) is the cheaper build if Phill prefers one shape for every grant.
2. **The grant's life beside the write grant.** Today `grant --revoke --write` keeps `permanentDelete: true` with `write: false`, and `SaveWriteGrant` restores it on a later write grant, the dialog's included.
   - (a) Keep it: the permanent-delete grant is inert without a write grant and comes back with one. `list_grants` shows it as `inactiveNoWriteGrant`.
   - (b) Revoking the write grant also revokes the permanent-delete grant; `grant --revoke --permanent-delete` removes only the second; `grant --permanent-delete` refuses a notebook with no write grant.
   Recommended: (b). A user who revokes writes expects Claude can delete nothing; with (a), answering a later write dialog silently brings permanent deletes back. In both, `list_grants` lists the grant as its own row, kind `permanentDeleteNotebook`, and the CLI asks the user to type `yes` after showing the path, as `grant --write` does.
3. **What the journal keeps, so a permanent delete stays recoverable.** A page entry restores today. A section's `Publish` backup has never been shown to bring pages back (spike 5.1 constraint 7; OneMore imports a `.one` with `MergeSections` because an opened section has `areAllPagesAvailable=false`, which epic 3 may use).
   - (a) As the spine says: one `onenoteBackup` entry naming the backup file, nothing more.
   - (b) (a), plus a `page` entry for every page in the section (full page with binaries, DPAPI never needed since protected sections are refused), written before `Publish`, sharing the call's `correlationId`; the result adds `meta.pageEntryIds` in section order. Any snapshot failure is `JournalFailed` and nothing is deleted. The `onenoteBackup` entry also keeps the section's `pages`-scope XML, so the page order and levels are recorded.
   - (c) (a), plus only the section's `pages`-scope XML in the `onenoteBackup` entry: the layout, no content.
   Recommended: (b). It makes the only proven restore path, `restore_from_journal`, cover every page (into `Recovered`, since the section is gone), while the unproven backup stays the full-fidelity copy. Costs: one `GetPageContent` per page under the lock (a section too big for 600 s fails before COM and deletes nothing), the journal's 2 GB cap, and cloud images missing locally are recorded as missing, as for any snapshot. AD-10's `xml | backupFile` becomes `xml` and `backupFile` together: a spine-sync row.
4. **Emptying recycle-bin items.**
   - (a) As the spine says: a recycled section is allowed, with the same backup (and Q3's entries); a recycled page is refused, since COM can't (`0x8004200C`); the recycle bin is `ObjectNotFound`; the Deleted Pages section is `NotSupported`, with a `userAction` to empty it in OneNote.
   - (b) Nothing in the recycle bin: every target with `isInRecycleBin` or `isDeletedPages` is `NotSupported`; the user empties the bin in OneNote.
   - (c) (a), plus the Deleted Pages section, emptied page by page.
   Recommended: (a). It is the only way through the server to finish a soft delete, the backup still comes first, and spike 5.1 removed a recycled section cleanly; (c) can't work, since a recycled page can't be permanently deleted. Probe P8 checks that a recycled section publishes before the PR merges.
5. **A stale date, if probe P1 shows COM ignores it on a permanent delete.**
   - (a) Ship: the server's comparison under the lock (date and `pageHash` for a page, date for a section) is the check, as for `UpdateHierarchy`; the spine records that COM's own check isn't relied on.
   - (b) Hold the PR until a further check is designed.
   Recommended: (a). The server compares before every delete either way, and spike 1 row 21's "stale" date probably still matched.
6. **Unprobed shapes in the build** (an empty group's permanent delete; an empty section's `Publish`, which spike 5.1 saw fail with `0x80042006` only under a dialog).
   - (a) Allowed in the build; the sitting probes them through the tool (P2, P9) before the PR merges, and a bad result becomes a refusal in a follow-up. An empty section whose `Publish` fails stays `BackupFailed`.
   - (b) Refused with `NotSupported` until the probes pass.
   Recommended: (a), as 5.5 Q7: the snapshot or backup comes first either way, and the merge waits on the sitting.
7. **The backup folder as protected** (AD-21: a section opened from it needs a read grant). Nothing protected is ever backed up here, and nothing in the server opens a backup.
   - (a) Build it in this story: every read check also treats a section under `backups` as protected.
   - (b) Defer it to epic 3's spine amendments (3.2), where exports and unpacked packages add the same rule; a Deferred row says so.
   Recommended: (b). With protected sections refused, the folder only ever holds unprotected content, so the rule guards nothing until epic 3, and this story's diff stays on the delete.
8. **Approve the plan** with these answers, so the build can start: Approve / Change.

## Live probes for the sitting

None runs in the build. Each is an opt-in test in `tests/OneNoteMcp.Integration/PermanentDeleteIntegrationTests.cs` (skipped unless `ONENOTE_MCP_INTEGRATION=1`), on throwaway nodes it creates in MCP Test (cloud ones in MCP Test Cloud, Sync Now after each step). "Gateway" probes call COM directly, because the tool refuses or pre-empts that case.

- **P1 (Deferred row, gateway).** A stale date on a permanent delete. A page that isn't displayed: read, edit through `update_page_content`, then `DeleteHierarchy(id, oldHierarchyDate, true)`; and a section: read, add a page, delete with the old date. Records whether COM returns `0x80042010` (Q5). Then the same two through the tool: `StaleWrite`, nothing deleted.
- **P2 (Deferred row).** An empty section group, through the tool: gone from the hierarchy, `hierarchy` entry; what is left on disk and in OneDrive (Phill).
- **P3 (verify).** No permanent-delete grant (write grant only): `PermanentDeleteNotGranted` (after Q1's dialog, declined), nothing deleted, no entry. Then with the grant: proceeds.
- **P4 (verify).** A section with two pages, through the tool: the backup file exists and isn't empty when `DeleteHierarchy` is called (the test hooks the gateway to check the file then), the section is gone, the `onenoteBackup` entry names the file; under Q3 (b) both page entries exist, and `restore_from_journal` on one brings it back as a new page in `Recovered`.
- **P5 (verify, forced backup failure).** Phill opens File > Options; a non-empty throwaway section's permanent delete through the tool returns `BackupFailed` (spike 5.1 saw `Publish` `0x80042006` under that dialog), and the section is still there. Fallback if `Publish` succeeds there: the same test with a gateway decorator over the real gateway that fails `PublishAsync`, then one that skips it so no file lands.
- **P6.** A live page through the tool: gone, not in Deleted Pages, page entry, `restore_from_journal` brings it back.
- **P7.** An unlocked protected section's page, write grant only, date-only basis: gone, DPAPI entry; with a `pageHash`, `ReadNotGranted`. The protected section itself: `NotSupported`, nothing published (no new file in `backups`).
- **P8.** A soft-deleted (recycled) unprotected section, through the tool: `Publish` of a recycled section works, the file exists, then it is gone from `OneNote_RecycleBin` (Q4 a).
- **P9.** An empty section, through the tool with no dialog open: does `Publish` succeed (Q6)?
- **P10 (cloud).** P4 and P6 on MCP Test Cloud: Phill checks OneNote for the web, and whether OneDrive's recycle bin got a copy (the Deferred row on OneDrive copies).
- **P11.** `list_grants` and the CLI on a real notebook: grant, list, revoke (Q2), list again.
- Only if Q4 (c) or Q6 (b) is chosen: a probe of that path before it ships.

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `PublishAsync(objectId, targetPath, ct)` (`pfOneNote` only); 5.5 adds `DeleteHierarchyAsync`, reused with `permanent: true`.
- `src/OneNoteMcp.Com/ComGateway.cs` (`AttemptAsync` l.563, timeout from `settings.Timeouts.Call` l.566; `CallKind` l.956) -- `Publish` under `Timeouts.Publish`, `CallKind.Write`. `Interop/IApplication.cs` l.99 and `Interop/Enums.cs` l.55 already declare `Publish` and `PublishFormat`; `HResultMap.cs` names `0x80042031`.
- `src/OneNoteMcp.Core/Writes/WritePipeline.cs` (`DefaultDeadline` l.41, ctor l.61, deadline use l.85) -- 600 s when `operation.Class` is `PermanentDelete`; the ctor value stays a test override. `WriteOperation.cs` -- no change expected.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` (`Granted` l.98, `WriteGrantMissing` l.131) -- the permanent-delete check and its missing-grant exception, after the write grant. `WriteGrantDialog.cs` (pattern), `GrantPrompt.cs` (`IGrantPrompt` l.101) -- a permanent-delete prompt, default `Unsupported`. `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs` (`AskWriteAsync` l.173, `WriteRequest` l.209) -- its form, with a fresh field name per request.
- `src/OneNoteMcp.Core/Settings/SettingsWriter.cs` (`RevokeWriteGrant` l.86, `SaveWriteGrant` l.133 keeps `permanentDelete`, `Revoke` l.246-273) -- save and revoke the second grant per Q2. `SettingsSnapshot.cs` (`NotebookGrant` l.144).
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs` (args l.83-104, `GrantWriteAsync` l.201, `RevokeWriteAsync` l.244, usage l.36) -- `--permanent-delete` and `--revoke --permanent-delete`.
- `src/OneNoteMcp.Core/UseCases/ListGrantsUseCase.cs` (l.37, l.71) -- the `permanentDeleteNotebook` row.
- `src/OneNoteMcp.Core/Journal/JournalStore.cs` (`Write` l.49), `JournalFiles.cs` (`EntryName` l.51) -- write under a name chosen before `Publish`. `JournalEntry.cs` (`SnapshotKind` l.40, `BackupFile` l.81) -- `onenoteBackup`. `PageSnapshot.cs` (request l.11 already takes the class). `JournalPruner.cs` (top level only, l.32) -- backups. 5.4's `HierarchySnapshot.cs`.
- `src/OneNoteMcp.Server/Cli/UninstallCommand.cs` (l.49) -- backup files.
- `src/OneNoteMcp.Core/Results/ToolErrors.cs` (l.78 `PermanentDeleteNotGranted`, l.84 `BackupFailed` userActions) -- l.78 per Q1. `ToolMeta.cs` -- `BackupFile`, `PageEntryIds` (Q3).
- 5.5's `SoftDeleteUseCase.cs` and its readers (Deleted Pages, locked beneath a group, subpage check) -- share the target read and refusals; don't copy. `RestoreFromJournalUseCase.cs` l.176-182 -- unchanged.
- `src/OneNoteMcp.Server/Tools/` (pattern: `DeletePageContentTool.cs`, `PageBasisArgument.cs`), `ServerSetup.cs` (pipeline l.110, `WithTools`).
- Tests: `FakeApplication.cs` (`Publish` writes a file), `FakeGateway.cs`, `GrantCommandTests.cs`, `WriteGrantListingTests.cs`, `SettingsStoreTests.cs`, `JournalTests.cs` (pruner), `UninstallCommandTests.cs`, `ToolContractTests.cs` + `Contract/tool-contract.json`, `McpSdkBindingTests.cs`; integration patterns `WriteProbes/HierarchyProbes.cs` (backup rows), `WriteProbes/ProtectedPublishProbes.cs`.

## Tasks & Acceptance

**Execution:**
- [ ] `IOneNoteGateway.cs`, `ComGateway.cs`, `FakeGateway.cs`, `FakeApplication.cs` -- `PublishAsync` with the publish timeout, never retried.
- [ ] `SettingsWriter.cs`, `GrantCommand.cs`, `ListGrantsUseCase.cs` (+ tests) -- the second grant per Q1 and Q2.
- [ ] `WriteAccess.cs`, `GrantPrompt.cs`, `ElicitationGrantPrompt.cs` -- the check and, per Q1, its dialog.
- [ ] `WritePipeline.cs` -- the 600 s class deadline.
- [ ] `JournalStore.cs`, `JournalFiles.cs`, `JournalEntry.cs`, `JournalPruner.cs`, `UninstallCommand.cs` (+ tests) -- named entries, `onenoteBackup`, backups pruned and purged.
- [ ] `src/OneNoteMcp.Core/Writes/SectionBackup.cs` (new) -- folder, `Publish`, wait, `BackupFailed`.
- [ ] `src/OneNoteMcp.Core/UseCases/PermanentDeleteUseCase.cs` (new) -- page, section and empty-group operations, step-1 refusals, Q3's entries.
- [ ] `ToolErrors.cs`, `ToolMeta.cs`; new `PermanentlyDeleteHierarchyObjectTool.cs`; `ServerSetup.cs`.
- [ ] `tests/OneNoteMcp.Tests/PermanentDeleteToolTests.cs` (new) -- every matrix row; no COM or entry on refusals; no dialog for a notebook or protected section; backup file present before the delete call; each backup failure kind refuses; hierarchy date sent for a page; counts only without a read grant.
- [ ] Contract snapshot and binding tests.
- [ ] `tests/OneNoteMcp.Integration/PermanentDeleteIntegrationTests.cs` (new, opt-in) -- P1-P11, written, not run.
- [ ] `README.md` (l.79 row), `plugin/skills/guide/SKILL.md` -- tool and grant rows.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync: tool name, Q1-Q4 and Q7 answers, step-1 refusals before dialogs, the backup wait, `meta.backupFile`, the 600 s class deadline, the Deleted Pages refusal; then P1, P2, P8, P9 and P10 results.

**Acceptance Criteria:**
- Given a write grant without the permanent-delete grant, when the tool runs on a page, then `PermanentDeleteNotGranted` returns (after Q1's dialog, if any) and nothing is deleted or journaled.
- Given both grants and a section, when it is permanently deleted, then its backup file exists and isn't empty before `DeleteHierarchy` is called, and the result names the entry and the file.
- Given `Publish` fails or its file never lands, then `BackupFailed` returns and `DeleteHierarchy` is never called.
- Given a protected section or a notebook, then `NotSupported` returns with no dialog, lock, `Publish` or COM delete.
- Given a page entry from a permanent delete, when `restore_from_journal` runs, then a new page with the snapshot's content appears.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**OneMore (checked 2026-11-13).** It never deletes permanently: `deletePermanently` appears only in its test mock, and its wrapper `OneNote.DeleteHierarchy(objectId)` (`OneNote.cs` l.1943) sends the default, logs and swallows errors. Its `Export` wrapper (l.2259) calls `Publish` and returns false on any error; its exporters create the folder and delete an existing target first (research note row 17). Its `Import` (l.2283) opens a `.one` with `OpenHierarchy` and then `MergeSections`, because an opened section has `areAllPagesAvailable=false` (row 20): the likely reason spike 5.1's opened backups listed no pages, and the way epic 3 may restore one. Nothing there checks a date, waits for a published file, or gates deletes; we keep AD-17, AD-9 and AD-6.

**Why step 1 refuses early.** A notebook and a protected section can't be permanently deleted whatever the state, and the walk already reads `encrypted`, so refusing there spares the user grant dialogs for a call that can't run. Step 4 checks again under the lock.

**The backup's name.** The entry name is chosen first (`{takenAtUtc}-{pid}-{guid}`), the backup is published as `<name>.one`, and the entry is written last, so an entry never names a file that isn't there; a crash in between leaves an orphan backup that the pruner removes after retention.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot matches.

**Manual checks (if no CLI):**
- A security review of the diff before merge (the ticket's verify): grants, the dialog, `Publish` paths, pruning and purge.
- Phill's live sitting: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter PermanentDelete` on MCP Test and MCP Test Cloud (P1-P11), Sync Now after each cloud step.

## Auto Run Result

Status: draft. Stopped after the plan step, as the coordinator asked: the Open Questions wait on Phill, and 5.5's answers are assumed (Intent).
