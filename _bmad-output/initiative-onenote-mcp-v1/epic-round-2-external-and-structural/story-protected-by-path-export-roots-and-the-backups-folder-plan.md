---
title: 'Protected by path: export roots and the backups folder'
type: 'feature'
ticket: '15'
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
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** AD-21 "What's protected" says a section under the journal's `backups` folder or an `exportRoots` entry, or in a notebook whose path is, counts as password-protected whatever its `encrypted` marker, because an opened backup, export or package carries no trustworthy marker (epic 3 Q11, kept by H1). The server doesn't do this: "protected" is the marker only, read in Core and in the Xml layer. A user who opens a `.one` export or a backup by hand gets its content read with no grant, and `open_package` (3.8) can't ship until this holds.

**Approach:** One rule for "protected": the marker, or a section or notebook `path` under a protected root (each `exportRoots` entry and the journal's `backups` folder), applied everywhere "protected" is decided today: every read check, every protection-gated write and the journal snapshots (Phill, 2026-11-17, 3.8 Q4). Plus Phill's guard: an export root that holds an open notebook of the user's is refused, saying why (3.8 Q5; the mechanism is Open Question 1). Probe P3 runs before the build, because the rule depends on OneNote reporting local paths under a root.

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
| Gated write | soft delete of a page in a path-protected section | refused as for a marked one, wording per Always | `NotSupported` |
| Journal snapshot | a group holding a path-protected section | its pages stripped; entry protected | none |
| Restore an entry whose section is still under a root | `restore_from_journal` | restored as for a marked section | none |
| Export root holding the user's open notebook | per Open Question 1 | refused, saying why | per Open Question 1 |
| `settings.json` invalid | roots unknown | per Open Question 2 | — |
| Notebook package of a notebook with a path-protected section | `publish` `onepkg` | per Open Question 3 | — |

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

**Spine:** AD-21 l.696 already carries the writes sentence (this branch); AD-10, AD-11 and AD-21 get Open Questions 1–3's answers.

## Tasks & Acceptance

**Before the build:** sitting S0 runs probe P3 (Verification). If OneNote reports a URL, not a local path, for a notebook under the default root, this plan comes back for a change.

**Execution:**
- [ ] `tests/OneNoteMcp.Tests/PathUnderTests.cs` (new) -- first, red: segment boundary, case, separators, missing root, mapped-drive root falling back to `GetFullPath`, URL path; `ExportTarget`'s existing tests still pass on it.
- [ ] `tests/OneNoteMcp.Tests/ProtectedByPathTests.cs` (new) -- first, red: each matrix row through the real use cases on `FakeGateway`: `get_hierarchy`, `get_page_content`, `find_pages`, `find_meta`, `get_binary_page_content`, the grant listing, `update_page_content`, `delete_page_content`, `create_page`, `set_page_level`, a move, soft delete, permanent delete, `restore_from_journal`, `publish`, and a journal snapshot; plus the wording rule.
- [ ] `src/OneNoteMcp.Core/Writes/PathUnder.cs` (new), `ExportTarget.cs` -- the one helper.
- [ ] `src/OneNoteMcp.Xml/EncryptedSections.cs`, `HierarchyNode.cs`, `HierarchyTarget.cs`, `NotebookSections.cs`, `DeleteTargets.cs` -- the required predicate and the rename.
- [ ] `src/OneNoteMcp.Core/Access/Protection.cs` (new), `HierarchyWalk.cs`, `ReadAccess.cs`, `ProtectedSections.cs` -- build `Protection` per call and use it; `HierarchyWalk` callers pass it.
- [ ] Every Core site in the Code Map -- replace the marker read with `Protection`; adjust message texts per the wording rule.
- [ ] `src/OneNoteMcp.Server/Cli/GrantCommand.cs`, `Grants/ElicitationGrantPrompt.cs`, the tool descriptions -- pass roots to the grant listing; wording.
- [ ] Per Open Questions 1–3: the guard, the invalid-settings rule, the package rule, each with red-first tests.
- [ ] `README.md` (Limits) -- one line: sections under an export root or the backups folder are treated as password-protected.
- [ ] Spine (AD-10, AD-11, AD-21) and `.memlog.md` -- the answers to Open Questions 1–3.

**Acceptance Criteria:**
- Given an unmarked section under an export root, under the backups folder, or in a notebook under a root, when any read tool or gated write reaches it with no grant, then it is refused or withheld as a marked protected section is, with the wording rule's messages.
- Given a section under `…\exports2` and a root `…\exports`, when it is read, then it is treated as unprotected.
- Given a group holding a path-protected section, when a journal snapshot of it is taken, then that section's pages are stripped and the entry is protected.
- Given the grant CLI's listing, when it runs, then it lists path-protected sections beside marked ones.
- Given an export root that holds an open notebook of the user's, when a call applies the rule, then it behaves as Open Question 1's answer says.
- Given the existing unit tests, when they run, then they pass, changed only where a fixture section sits under a root or a message's wording changed.
- Given no integration variables, when `dotnet test tests/OneNoteMcp.Integration` runs, then it builds and every live test skips.

## Design Notes

- Journal entries across a settings change: an entry taken while its section was under a root stays protected; if the root is later removed, restoring it is refused as "no longer protected" (the existing check, reworded). Entries taken before this story for such sections stay in plaintext until the journal prunes them (`retentionDays`). Neither is fixed here; the README line mentions the second.
- `HierarchyWalk` is built without settings in four places; they get `Protection` from their use case, which already reads settings.

## Open Questions

1. **The guard on an export root (Phill's Q5: refuse a root that contains an open notebook, saying why).** `open_package` (3.8) deliberately opens notebooks inside a root, so the plain test would refuse every root once a package is opened, and "open notebook" needs a COM read each time. (a) Refuse a root that contains an open notebook except one `open_package` made, which it marks with a small marker file in each folder it creates (`.onenote-mcp-opened`; 3.8 writes it, this story reads it); also a root that sits inside an open notebook's folder. Checked on each call that applies the rule, from the notebooks list (one `GetHierarchy`). Effect: reads continue with a `exportRootHoldsNotebooks` warning naming the root and the reason, and that root still protects (fails closed); writes fail with `SettingsInvalid`, as for any invalid setting. (b) Only roots related to OneNote's default notebook folder (`GetSpecialLocation`): cheaper, but misses a notebook kept elsewhere, such as root `D:\Notes` holding `D:\Notes\Work`. Recommended: a.
2. **`settings.json` invalid.** Reads continue on defaults today, so a non-default root stops protecting while the file is broken. (a) Keep the last valid roots of this server run and use them while the file is invalid; at startup with an invalid file, protect only under the default root and the backups folder, and warn. (b) While the file is invalid, refuse reads of any local section that isn't marked. (c) Accept the gap and document it. Recommended: a.
3. **Notebook packages and path-protected sections (H1).** H1 lets a notebook package skip read grants because it keeps marked sections encrypted. A path-protected section is plaintext, so a package of a notebook holding one (an opened package, say) would copy it out with no grant. (a) A notebook package needs the read grant of every path-protected section in it (marked ones stay exempt), and warns `exportNotEncrypted`. (b) Refuse packaging a notebook that holds a path-protected section. (c) Keep H1 as is. Recommended: a.

## Implementation Notes

## Plan Change Log

- 2026-11-17, after the Fable final review (each finding checked against the code; all held): the site list now starts from `HierarchyWalk` and adds `PageWrite`, `HierarchyWrite`, `GetHierarchyUseCase:46`, the search use cases and `GrantCommand`, with lines; fail-closed by a required Xml predicate and the `EncryptedMarker` rename instead of an optional default; the rule's comparison lives in Core (Xml can't reference Core) as one helper shared with `ExportTarget`; null canonical paths fall back to `GetFullPath`; the notebook-path clause restored; the wording rule for messages; P3 moved before this build; Open Question 1 rewritten to deliver Phill's rule (option c dropped, as he chose to refuse); Open Questions 2 and 3 added; the ACs cover the ticket's Verify; journal-across-settings noted; citations corrected.

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill; MCP Test and throwaways only):**
- S0 (before the build): probe P3. `publish` an MCP Test section as `.one` into the test root and into the default root `Documents\OneNote Exports`; open each by hand in OneNote; record the `path` `get_hierarchy` reports for it (local path or URL). Phill closes both and deletes the files.
- S1 (after the build): open such a `.one` by hand, read a page of it with `onenote-dev`: the grant dialog appears; Phill denies once (refused) and allows once (read). If the copy lists no pages at first (spine l.348), wait and list again. Then try a soft delete of one of its pages: refused. Phill closes the copy and deletes the file.
- S2: permanently delete a throwaway section of MCP Test (its `.one` backup lands in the journal's `backups` folder, AD-8), open that backup in place by hand in OneNote, and read a page of it: refused without a grant. Phill closes it.
