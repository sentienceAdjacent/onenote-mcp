---
title: 'Protected by path: export roots and the backups folder'
type: 'feature'
ticket: '15'
created: '2026-11-17'
status: done
baseline_revision: '4be192b2e404aec66c89a5da059bb075af6c7800'
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

**Problem:** AD-21 "What's protected" says a section under the journal's `backups` folder or an `exportRoots` entry, or in a notebook whose path is, counts as password-protected whatever its `encrypted` marker, because an opened backup, export or package carries no trustworthy marker (epic 3 Q11, kept by H1). The server doesn't do this: "protected" is the marker only, read in Core and in the Xml layer. A user who opens a `.one` export or a backup by hand gets its content read with no grant, and `open_package` (3.8) can't ship until this holds.

**Approach:** One rule for "protected": the marker, or a section or notebook `path` under a protected root (each `exportRoots` entry and the journal's `backups` folder), applied everywhere "protected" is decided today: every read check, every protection-gated write and the journal snapshots (Phill, 2026-11-17, 3.8 Q4). Plus Phill's guard: an export root that holds an open notebook of the user's is refused, saying why (3.8 Q5; the mechanism is Q1 in Decisions). Probe P3 runs before the build, because the rule depends on OneNote reporting local paths under a root.

## Boundaries & Constraints

**Always:**
- **One implementation of "under a root".** A Core helper (beside `ExportTarget.Contains`, which it replaces) decides it for exports and for protection alike: the root made canonical with `FilePathGate.Canonical`, falling back to `Path.GetFullPath` when that returns null (a root on a mapped or network drive still protects); the section or notebook path through `Path.GetFullPath` (no file I/O per section); case-insensitive, at a path-segment boundary (`…\exports2\x.one` is not under `…\exports`). A root that doesn't exist yet still counts. A path that can't be parsed as a local path (a URL) never matches.
- **Fails closed by construction.** The Xml layer stays free of settings and file I/O: every Xml reader that decides "protected" today takes a required predicate (`Func<string, bool>` on a path) from Core, and `HierarchyNode.Encrypted` is renamed `EncryptedMarker`, so every Core site must be revisited to compile. Core decides with one `Protection` value built once per call from the settings read (roots plus the backups folder): protected = marker, or the section's `path` under a root, or, when the XML shows it, its notebook's `path` under a root.
- A path-protected section behaves like a marked one everywhere: read grants keyed by `SectionKey(path)`, prompts, `readNotGranted` withholding, the grant CLI listing, the search cache, journal snapshots (pages stripped, entries DPAPI-protected with a `SectionKey`), and every write gate (moves and merges out refused, soft delete of its pages refused, permanent delete refused, `publish` needs its read grant and warns `exportNotEncrypted`). Its `locked` and `encrypted` attributes are reported as OneNote gives them.
- **Messages say "protected", not "password-protected", wherever a path-protected section can reach them,** and say why when it's the path ("it is under an export root or the backups folder, so it's treated as password-protected"); advice that only fits a password ("password-protect it again", "unlock it") is given only for a marked section.
- Messages name IDs, the caller's own paths and the user's own settings paths, never notebook or section names.

**Never:**
- No change for a section that is neither marked nor under a root. No new grant kind or tool argument.
- Don't read content to decide; path and marker only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Unmarked section under an export root | `get_page_content` of its page, no grant | refused; prompt as for any protected section | `ReadNotGranted` |
| Same, under the `backups` folder | `get_hierarchy` pages scope | pages withheld, `readNotGranted` warning | none |
| Same, with a read grant | any read | allowed | none |
| Notebook folder under a root, read from a section group | `get_hierarchy` of the group | its sections protected (section paths) | withheld |
| Look-alike root | section under `…\exports2`, root `…\exports` | unprotected | none |
| Case or separator variants | `C:/Users/X/Exports` vs `c:\users\x\exports\` | protected | — |
| Root not yet created; root on a mapped drive | section path under it | protected | — |
| Cloud section | URL path | marker only | none |
| Loose section under `one:OpenSections` (a `.one` opened by hand, P3) | its path under a root; no notebook | protected by its own path, in broad reads (`get_hierarchy` of all notebooks) and target reads alike | `ReadNotGranted` / withheld |
| Gated write | soft delete of a page in a path-protected section | refused as for a marked one, wording per Always | `NotSupported` |
| Journal snapshot | a group holding a path-protected section | its pages stripped; entry protected | none |
| Restore an entry whose section is still under a root | `restore_from_journal` | restored as for a marked section | none |
| Export root holding an open notebook the user opened (not one with `.onenote-mcp-opened`) | any call | reads continue with a warning naming the root and why, the root still protects; writes refused | writes: `SettingsInvalid` (Q1) |
| `settings.json` invalid, a valid read earlier in this run | any read | this run's last valid roots still protect | none (warning `settingsInvalid` as today) |
| `settings.json` invalid at startup | any read | default root and backups folder protect; warning | none |
| Notebook package holding a path-protected section | `publish` `onepkg`, no grant for it | refused, naming the section's ID | `ReadNotGranted` |
| Same, with its read grant | `publish` `onepkg` | written | ok + `exportNotEncrypted` |

## Decisions (Phill, 2026-11-17)

- One story, as planned (approved).
- **Q1, a (final yes 2026-11-17, after he asked why the guard is needed and it was explained):** refuse an export root that holds an open notebook the user opened, but not one `open_package` made (it writes `.onenote-mcp-opened` into each folder it creates, the default timestamped folder included); also a root inside an open notebook's folder. Reads continue with a warning naming the root and why, and the root still protects; writes fail `SettingsInvalid` until it's fixed.
- **Q2, a:** while `settings.json` is invalid, keep this run's last valid roots; at startup with an invalid file, protect the default root and the backups folder, and warn.
- **Q3, a:** a notebook package needs the read grant of every path-protected section in it (marked sections stay exempt, H1) and warns `exportNotEncrypted`.
- Probe P3 (sitting S0, 2026-11-17): a `.one` exported to the default root (OneDrive-redirected Documents) and one copied into the test root, each opened by hand, are reported by `GetHierarchy` with local paths (`C:\Users\<user>\OneDrive\Documents\OneNote Exports\…\x.one`, the same form as `publish`'s `outputPath`; and `%LOCALAPPDATA%\…\x.one`), never a URL. Hand-opened files sit under `one:OpenSections`, outside any notebook, so they have no notebook path and the section's own path decides. Opening a file made OneNote rewrite it (its `lastModifiedTime` moved about 5 minutes after the export), as spike run 3 found.

</frozen-after-approval>

## Code Map

**Xml (each takes a required path predicate; no settings, no I/O):** `src/OneNoteMcp.Xml/EncryptedSections.cs` (`IsEncrypted`, `Find` l.31; `NotebookPath` set when the XML shows the notebook, l.52, else empty, l.8–9), `HierarchyNode.cs` (`Encrypted` → `EncryptedMarker`), `HierarchyTarget.cs:84`, `NotebookSections.cs:46`, `DeleteTargets.cs:26` (`WithoutProtectedPages`).

**Core, the rule:** new `src/OneNoteMcp.Core/Access/Protection.cs` (built from `SettingsSnapshot`: `ExportRoots` l.97–102, the journal path plus `Journal/JournalFiles.cs:58` `BackupsFolderName`) and new `src/OneNoteMcp.Core/Writes/PathUnder.cs` (the one "under a root" test; `Writes/ExportTarget.cs:145–154` `Contains` moves onto it; `Writes/FilePathGate.cs` `Canonical` l.43/46). `Access/GrantKeys.cs:51–55` only trims separators; case and separators come from `GetFullPath` and the comparer.

**Core, every site that decides "protected" today** (the rename makes the compiler list them; this is the list as of `8d3de0f`):
- Walk: `Access/HierarchyWalk.cs:192` (every target check's section; constructed with no settings at `UseCases/PermanentDeleteUseCase.cs:241,318`, `Writes/PageWrite.cs:109,186`).
- Reads: `Access/ReadAccess.cs` l.87–91, 98–102, 129–137, 180, 217–238; `UseCases/GetHierarchyUseCase.cs:46,57`; `FindPagesUseCase.cs:82,107`, `FindMetaUseCase.cs`, `SearchResults.cs` (through `BroadAccess`); `Access/ProtectedSections.cs:26` and its caller `src/OneNoteMcp.Server/Cli/GrantCommand.cs:454`.
- Writes: `Writes/PageWrite.cs:118,187`, `HierarchyWrite.cs:647–648`, `MovePageWrite.cs:344,432,504,531`, `MoveContainerWrite.cs:410,419`, `SetPageLevelWrite.cs:344`; `UseCases/CreatePageUseCase.cs:143,329`, `SoftDeleteUseCase.cs:196`, `PermanentDeleteUseCase.cs:564`, `RestoreFromJournalUseCase.cs:224,241`, `PublishUseCase.cs:386,408,417`.
- Journal: `Journal/HierarchySnapshot.cs:66–68,104`, `PageSnapshot.cs:88`.
- Message texts saying "password-protected" (grep): Core in `Access/` (3 files), `Results/ToolErrors.cs`, the use cases and writes above; Server in `Cli/GrantCommand.cs`, `Grants/ElicitationGrantPrompt.cs` and 22 tool descriptions under `src/OneNoteMcp.Server/Tools/`.

**Spine:** AD-21 l.696 already carries the writes sentence (this branch); AD-10, AD-11 and AD-21 get the Q1–Q3 decisions in the build.

## Tasks & Acceptance

**Before the build:** sitting S0 runs probe P3 (Verification). If OneNote reports a URL, not a local path, for a notebook under the default root, this plan comes back for a change.

**Execution:**
- [x] `tests/OneNoteMcp.Tests/PathUnderTests.cs` (new) -- first, red: segment boundary, case, separators, missing root, mapped-drive root falling back to `GetFullPath`, URL path; `ExportTarget`'s existing tests still pass on it.
- [x] `tests/OneNoteMcp.Tests/ProtectedByPathTests.cs` (new) -- **Partly** (see the Review Triage Log and deferred-work: no path-mode test for `update_page_content`, `create_page` subpage, `set_page_level`, `restore_from_journal`, or `one:OpenSections` through a read tool) -- first, red: each matrix row, the `one:OpenSections` row with a fixture shaped as P3 saw it, through the real use cases on `FakeGateway`: `get_hierarchy`, `get_page_content`, `find_pages`, `find_meta`, `get_binary_page_content`, the grant listing, `update_page_content`, `delete_page_content`, `create_page`, `set_page_level`, a move, soft delete, permanent delete, `restore_from_journal`, `publish`, and a journal snapshot; plus the wording rule.
- [x] `src/OneNoteMcp.Core/Writes/PathUnder.cs` (new), `ExportTarget.cs` -- the one helper.
- [x] `src/OneNoteMcp.Xml/EncryptedSections.cs`, `HierarchyNode.cs`, `HierarchyTarget.cs`, `NotebookSections.cs`, `DeleteTargets.cs` -- the required predicate and the rename.
- [x] `src/OneNoteMcp.Core/Access/Protection.cs` (new), `HierarchyWalk.cs`, `ReadAccess.cs`, `ProtectedSections.cs` -- build `Protection` per call and use it; `HierarchyWalk` callers pass it.
- [x] Every Core site in the Code Map -- replace the marker read with `Protection`; adjust message texts per the wording rule.
- [x] `src/OneNoteMcp.Server/Cli/GrantCommand.cs`, `Grants/ElicitationGrantPrompt.cs`, the tool descriptions -- pass roots to the grant listing; wording. **Deviation:** the 22 tool descriptions are unchanged (still true for marked sections; the README explains the path rule); the CLI, the dialog and the Core messages are reworded.
- [x] `src/OneNoteMcp.Core/Settings/SettingsStore.cs` (or a holder beside it) -- keep this run's last valid `ExportRoots` and journal path; use them, or the defaults at startup, while the file is invalid (Q2), red-first.
- [x] `src/OneNoteMcp.Core/Access/Protection.cs`, `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- the export-root guard and its warning; writes refused `SettingsInvalid` (Q1), red-first.
- [x] `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- a notebook package checks every section for path protection and needs each one's read grant; warning `exportNotEncrypted` (Q3), red-first.
- [x] `README.md` (Limits) -- one line: sections under an export root or the backups folder are treated as password-protected.
- [x] Spine (AD-10 for Q2, AD-11 and AD-21 Exports for Q3, AD-10/AD-21 for Q1) -- the decisions.

**Acceptance Criteria:**
- Given an unmarked section under an export root, under the backups folder, or in a notebook under a root, when any read tool or gated write reaches it with no grant, then it is refused or withheld as a marked protected section is, with the wording rule's messages.
- Given a section under `…\exports2` and a root `…\exports`, when it is read, then it is treated as unprotected.
- Given a group holding a path-protected section, when a journal snapshot of it is taken, then that section's pages are stripped and the entry is protected.
- Given the grant CLI's listing, when it runs, then it lists path-protected sections beside marked ones.
- Given an export root that holds an open notebook of the user's, when a call applies the rule, then it behaves as Q1's decision says.
- Given the existing unit tests, when they run, then they pass, changed only where a fixture section sits under a root or a message's wording changed.
- Given no integration variables, when `dotnet test tests/OneNoteMcp.Integration` runs, then it builds and every live test skips.

## Design Notes

- Journal entries across a settings change: an entry taken while its section was under a root stays protected; if the root is later removed, restoring it is refused as "no longer protected" (the existing check, reworded). Entries taken before this story for such sections stay in plaintext until the journal prunes them (`retentionDays`). Neither is fixed here; the README line mentions the second.
- `HierarchyWalk` is built without settings in four places; they get `Protection` from their use case, which already reads settings.

## Implementation Notes

- Implemented directly in this lane (no implementation subagents, as the coordinating session asked).
- **Test-first, honestly:** `PathUnderTests` and the whole story-1.20 vault matrix run in path mode (`ProtectedByPathToolTests`, the matrix made an abstract base with a marker and a path subclass) were run red first (60 failed against a stub). The write-side, grant-listing, guard and invalid-settings tests (in `SoftDeleteToolTests`, `PermanentDeleteToolTests`, `PublishToolTests`, `MoveHierarchyObjectToolTests`, `ProtectedSectionsTests`, `ExportRootGuardTests`) were written before the code but couldn't compile until its API existed, so they weren't seen red then; afterwards, a mutation (`Protection.UnderRoot` forced false) made them fail, then was reverted.
- **Fails closed by construction:** `HierarchyNode`, `HierarchyTarget` and `NotebookSection` renamed `Encrypted` to `EncryptedMarker`; `EncryptedSections.Find` and `DeleteTargets.WithoutProtectedPages` take a required `underRoot` test; `HierarchySnapshotRequest` and `PageSnapshotRequest` take a required `Protection`. The compiler listed every site (Core, Server and tests); none was left on the marker alone.
- **The rule:** `Core/Access/Protection.cs` (roots = `exportRoots` + the journal's `backups` folder; `IsProtected`, `ByPathOnly`, `Phrase`) over `Core/Writes/PathUnder.cs`, which `ExportTarget` now uses too. A section's notebook path counts where the XML shows it (`EncryptedSections`); `one:OpenSections` sections go by their own path (P3).
- **Q2:** `ReadAccess.Protection()` remembers this run's last valid roots; with none and an invalid or newer file, defaults plus a `settingsInvalid` warning. Other one-off readers (`RestoreFromJournalUseCase`, the grant CLI) use `Protection.From(settings)` on the file as it reads now.
- **Q1:** `Core/Access/ExportRootGuard.cs` (one `GetHierarchy` of the notebooks, kept 30 s; a failed read gives no verdict); `ReadAccess` attaches `exportRootHoldsNotebooks` to target and broad reads (`TargetAccess.Warnings`, new `BroadAccess.Extra`); `WritePipeline` step 1 throws `SettingsInvalid`. Wired in `ServerSetup` as one singleton; constructors take it as an optional argument, so unit tests that don't exercise it see no extra gateway call. `McpSdkBindingTests` now expects the guard's notebooks read on `get_hierarchy`.
- **Q3:** `PublishUseCase` checks each `ByPathOnly` section of a notebook for its read grant at the call's start and warns `exportNotEncrypted`.
- **Wording:** refusals use `Protection.Phrase` ("is password-protected" / "is under an export root or the backups folder, so it is treated as password-protected"); restore's "no longer protected" message and `userAction` cover both causes.
- Not done: the integration tests have no live check of the rule; sittings S1 and S2 cover it. The plan's Code Map listed 22 tool descriptions with "password-protected"; they are left as they are (still true for marked sections, and the README explains the path rule) rather than lengthened in every tool.

## Plan Change Log

- 2026-11-17, P3 passed (local paths, `one:OpenSections` for hand-opened files) and Phill's final yes on Q1: both recorded in Decisions; a matrix row and test for loose sections under `one:OpenSections`; status ready-for-dev.
- 2026-11-17, Phill's answers: Q1 a (provisional), Q2 a, Q3 a and one story recorded as Decisions; the matrix rows that pointed at open questions now state the behaviour; tasks for each; no open questions left. Status stays draft until P3's result and Q1's final yes.

- 2026-11-17, after the Fable final review (each finding checked against the code; all held): the site list now starts from `HierarchyWalk` and adds `PageWrite`, `HierarchyWrite`, `GetHierarchyUseCase:46`, the search use cases and `GrantCommand`, with lines; fail-closed by a required Xml predicate and the `EncryptedMarker` rename instead of an optional default; the rule's comparison lives in Core (Xml can't reference Core) as one helper shared with `ExportTarget`; null canonical paths fall back to `GetFullPath`; the notebook-path clause restored; the wording rule for messages; P3 moved before this build; Open Question 1 rewritten to deliver Phill's rule (option c dropped, as he chose to refuse); Open Questions 2 and 3 added; the ACs cover the ticket's Verify; journal-across-settings noted; citations corrected.

## Review Triage Log

- 2026-11-17, quick lens, iteration 0 (the reviewer wrote its findings to a file in the build's scratchpad): 9 findings. Each checked against the code: 0 high, 3 medium, 5 low, 1 rejected; 7 patched, 1 deferred in part, no loopback.
  - **medium, patch.** The main read refusal (`ReadAccess.NotGranted`) and the broad withheld warning still said "password-protected" for path-protected sections. Fix: `NotGranted` takes the section and uses `Protection.Phrase`; `BroadAccess.ByPath` marks the path-only withheld IDs, whose warning says why. Test: `ProtectedByPathTests.ANotebookFolderUnderARoot_…_SayingWhy`.
  - **low, patch.** Other refusals and userActions reached by path-protected sections (`PageWrite` x2, `HierarchyWrite` reorder, `PermanentDeleteUseCase` x2 and its userActions, `ResultGrantCheck`, `ToolErrors.ReadNotGrantedUserAction`, the grant CLI). Fix: "protected (password-protected, or under an export root or the backups folder)" where the section's marker isn't known, `Protection.Phrase` where it is (the reorder keeps its old text for a marked section).
  - **low, patch.** The grant dialog opened "Claude wants to read a password-protected OneNote section". Fix: "a protected OneNote section (password-protected, or a copy under an export or backups folder)"; `GrantCommandTests` updated.
  - **low, patch.** The tool-descriptions task was ticked though not done. Fix: recorded as a deviation on the task.
  - **low, rejected.** Restore's advice to password-protect the section again is given for path-protected entries too: an entry's `SectionKey` doesn't record which cause applied, and the message already offers both remedies (password, or the export root back in settings).
  - **medium, patch.** `PathUnder.IsUnder` made each root canonical (file I/O) on every section test of a broad read. Fix: `Protection` builds the root prefixes once; `PathUnder.IsUnderPrefixes` tests without I/O.
  - **medium, patch.** While `settings.json` was invalid the guard checked the default roots, not the last valid ones that protect, and in a write it ran before the invalid-settings refusal. Fix: `ReadAccess` remembers the last valid export roots and gives those to the guard; `WritePipeline` runs the guard only on a readable file.
  - **low, patch / defer.** Matrix rows without a use-case test: added the look-alike root, case and separator variants, the notebook folder under a root beside marked sections (`ProtectedByPathTests`), and `delete_page_content` (`DeletePageContentToolTests`). Deferred: `update_page_content`, `create_page` subpage, `set_page_level`, `restore_from_journal` under a root, and `one:OpenSections` through a read tool (deferred-work). The fixture move of the vault paths to `C:\vaults` in marker mode was rejected as an AC 6 breach: no assertion's meaning changed, only fixture constants.
  - **low, patch.** AD-10 named only Q2; it now also says writes fail `SettingsInvalid` under the export-root guard (Q1).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill; MCP Test and throwaways only):**
- S0 (before the build): probe P3. `publish` an MCP Test section as `.one` into the test root and into the default root `Documents\OneNote Exports`; open each by hand in OneNote; record the `path` `get_hierarchy` reports for it (local path or URL). Phill closes both and deletes the files.
- S1 (after the build): open such a `.one` by hand, read a page of it with `onenote-dev`: the grant dialog appears; Phill denies once (refused) and allows once (read). If the copy lists no pages at first (spine l.348), wait and list again. Then try a soft delete of one of its pages: refused. Phill closes the copy and deletes the file.
- S2: permanently delete a throwaway section of MCP Test (its `.one` backup lands in the journal's `backups` folder, AD-8), open that backup in place by hand in OneNote, and read a page of it: refused without a grant. Phill closes it.

**Sitting status (2026-11-17, results relayed by the coordinating session):**
- S1 passed: withheld pages carry the new wording; a declined grant gives `ReadNotGranted` with the path reason; an allowed grant reads all 19 pages. One finding: soft-deleting a page of the hand-opened copy (a loose section under `one:OpenSections`) returned `ObjectNotFound` "isn't a page", not the protected refusal; nothing was written. It predates this story (page writes' step 1 needs an owning notebook, and its fallback message is "isn't a page"; this story didn't change `WriteAccess`, `HierarchyWalk` or that fallback), so it is filed as backlog 12.
- S2 passed (2026-11-18, this build through the dev server). A permanent delete of an empty MCP Test section was refused with `BackupFailed` (an empty section can't be backed up; expected, and predating this story) and deleted nothing; with one page added, it succeeded after the name was typed in the dialog, and its `.one` backup landed in the journal's `backups` folder with a journal entry and one page entry. That backup, opened by hand, appeared under `one:OpenSections` as a loose section named after the backup file. `get_hierarchy` at pages scope on it was refused with `ReadNotGranted`, its message giving the path reason ("is under an export root or the backups folder, so it is treated as password-protected"); the grant dialog appeared and wasn't completed, so nothing was granted. Cleanup: S1's copy and its folder are deleted; the backup file stays as the real backup, and its opened copy is closed in OneNote.
