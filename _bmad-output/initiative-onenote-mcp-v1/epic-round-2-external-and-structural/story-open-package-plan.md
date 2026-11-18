---
title: 'open_package'
type: 'feature'
ticket: '8'
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
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-publish-notebooks-and-section-groups-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A OneNote package (`.onepkg`, which `publish` now makes of a notebook) can't be opened through the server. Spike 3.1 (O1–O5, Implication 10) found that `OpenPackage` unpacks into the folder it's given and opens nothing, so a separate `OpenHierarchy` is needed. An opened copy carries no trustworthy protection marker, so AD-21 says every section of it counts as protected by its path (Q11). That path rule isn't built: today "protected" means the `encrypted` marker only, decided in Core and in the Xml layer.

**Approach:** Two parts, which Open Question 1 decides whether to ship as one story or two. **Part A, protected by path:** AD-21 "What's protected" (a section under an export root or the journal's `backups` folder, or in a notebook whose path is, counts as protected whatever its marker), applied wherever "protected" is decided today (the read checks always; the writes per Open Question 4). **Part B, `open_package`:** an `ExternalWrite` (AD-5 promoted task, AD-7) that unpacks a package into a new folder under an export root, opens it with `OpenHierarchy`, and returns the folder and the notebook. Part B is planned around live probes P1–P3, which run before it is built.

## Boundaries & Constraints

**Always:**
- Part A: one rule decides "protected": the `encrypted` marker, or a `path` under a protected root. Protected roots are every `exportRoots` entry and the journal's `backups` folder. Both sides are made canonical and compared with AD-6's transform (`GrantKeys`), case-insensitively, at a path-segment boundary (`…\exports2` is not under `…\exports`). It holds in the Xml layer too, wherever a marker is read today. When the XML doesn't show a section's notebook (a read rooted below the notebook), the section's own `path` decides; a section's path lies inside its notebook's folder, so this loses nothing for local notebooks.
- Part B, AD-7 step 1 (before the lock): the package is an existing `.onepkg` file (Q6; which kinds of path, Open Question 3); the destination (Open Question 2) resolves under an export root like `publish`'s target and must not exist (`FileExists`). Steps 3–4 (under the lock): resolved again, still absent; the pipeline creates it (AD-7). Step 7: `OpenPackage(package, destination)`, then `OpenHierarchy(pathOut)` on the existing path (no create, no grant), then the page-listing wait (Design Notes). Never retried.
- A failed or timed-out step 7 names the folder (AD-11). `OpenPackage` failing with nothing written: the folder the pipeline created is removed if still empty, and the error says so; anything left behind stays and is named, as for `publish` (a retry needs a new name). `OpenHierarchy` failing after the unpack: the folder stays, `meta.completedSteps` says the unpack was done.
- The result: `meta.outputPath` (the folder) and `meta.objectId` (the opened notebook's ID). No write grant (Q6); every section counts as protected (Q11). The user closes the copy in OneNote (Q13); the description says so.
- Messages name IDs and the caller's own paths, never notebook or section names from a package.

**Never:**
- No `MergeFiles` (ends ONENOTE.EXE), no `CloseNotebook` tool (epic 4), no automatic grant, no `NavigateTo`, no unpack into an existing folder.
- The integration tests close only notebooks they opened, through the harness's COM seam, and delete only their own folders.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Open a package | throwaway's `.onepkg`, new destination | folder made, notebook opened; `meta.outputPath`, `meta.objectId` | none |
| No page listed in the wait | all sections empty, locked or protected | ok + `pagesNotListed` | none |
| Destination outside every root | `..\x` or another drive | nothing done | `ExportPathNotAllowed` |
| Destination exists | file or folder there | untouched | `FileExists` |
| Package missing, or not `.onepkg` | path | nothing done | `InvalidArgument` |
| `OpenPackage` fails, nothing written | bad package | empty folder removed | mapped code, names the folder |
| `OpenPackage` times out | — | folder kept, named | `Timeout`, `outcomeUnknown` |
| `OpenHierarchy` fails after the unpack | — | folder kept | mapped code + `meta.completedSteps` |
| Read a page of the opened copy | no read grant | refused | `ReadNotGranted` (prompt as for any protected section) |
| Unmarked section under an export root or `backups` | any read tool | treated as protected | `ReadNotGranted` / withheld |
| Look-alike root | section under `…\exports2`, root `…\exports` | not protected by path | none |

</frozen-after-approval>

## Code Map

**Part A (where "protected" is decided today):**
- Xml: `EncryptedSections.cs` (`IsEncrypted`, `Find` l.31: only encrypted sections, `NotebookPath` empty when the XML doesn't show the notebook, l.8–9), `HierarchyNode.cs` (`Encrypted`), `HierarchyTarget.cs:84`, `NotebookSections.cs:46`, `DeleteTargets.cs:26` (`WithoutProtectedPages`). These have no settings, so they take the protected roots as an argument (a small `ProtectedRoots` value: canonical paths plus the segment-boundary test).
- Core reads: `Access/ReadAccess.cs` `CheckObjectAsync` l.87, `CheckSectionAsync` l.98, `HoldsReadGrant` l.129, `CheckBroad` l.180 (input `IReadOnlyList<EncryptedSection>` from `EncryptedSections.Find`: becomes the protected sections under the rule), `CheckSearchScopeAsync` l.217/237; `UseCases/GetHierarchyUseCase.cs:57`; `Access/ProtectedSections.cs:26` (grant CLI listing).
- Core writes and journal (Open Question 4): `Writes/MovePageWrite.cs`, `MoveContainerWrite.cs` (l.410 `.Encrypted`, l.419 `Find`), `SetPageLevelWrite.cs`, `UseCases/SoftDeleteUseCase.cs`, `PermanentDeleteUseCase.cs`, `CreatePageUseCase.cs`, `RestoreFromJournalUseCase.cs` (l.224, via `NotebookSections`), `PublishUseCase.cs`, `Journal/HierarchySnapshot.cs` (l.66–68, `WithoutProtectedPages`; l.104 `SectionKey`), `PageSnapshot.cs` (l.88).
- Roots: `Settings/SettingsSnapshot.cs` `ExportRoots` (l.96–101; default `Documents\OneNote Exports`), `SettingsStore.cs:340–350` (entries absolute, not canonical), `Journal/JournalFiles.cs:58` `BackupsFolderName`; `Access/GrantKeys.cs` for the transform; `Writes/FilePathGate.cs` `Canonical`.

**Part B:**
- `Com/Interop/IApplication.cs` -- `OpenPackage` (l.106) and `OpenHierarchy` (l.32) are declared; `ComGateway.CreateNotebookAsync` (l.339) is the `OpenHierarchy` pattern.
- `Core/IOneNoteGateway.cs` gets `OpenPackageAsync` and `OpenHierarchyAsync` (existing path); every implementation must follow: `ComGateway`, and 21 test files (grep `: IOneNoteGateway` under `tests`), among them `tests/OneNoteMcp.Tests/FakeGateway.cs` and the integration gateways in `Move…`, `PermanentDelete…`, `ProtectedSection…`, `Publish…`, `SetPageLevel…`, `UpdateHierarchy…IntegrationTests.cs`.
- `Core/Writes/ExportTarget.cs` -- `Resolve` (l.63) is file-and-extension shaped; add a folder variant sharing its root and canonical checks.
- `Core/UseCases/PublishUseCase.cs` -- the `ExternalWrite` shape; `Server/Tools/PublishTool.cs`, `ToolArguments.cs`; `Core/Results/ToolWarnings.cs`; `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (a new tool).
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `OpenPackageAsync` (l.377), `AdoptThrowawayNotebookAsync`, `Round2.LocalThrowawayName`; no test closes a notebook yet.
- Prior art: OneMore calls neither `OpenPackage` nor `MergeFiles` (research note row 18).

## Tasks & Acceptance

**Execution (Part A):**
- [ ] `tests/OneNoteMcp.Tests/ProtectedByPathTests.cs` (new) -- first, red: the rule (each root kind, a notebook under a root, case and separator variants, the look-alike root, XML without the notebook) through `get_hierarchy`, `get_page_content`, `find_pages`, the grant listing, a journal snapshot, and one gated write per Open Question 4.
- [ ] `src/OneNoteMcp.Xml/ProtectedRoots.cs` (new), `EncryptedSections.cs`, `HierarchyNode.cs`, `HierarchyTarget.cs`, `NotebookSections.cs`, `DeleteTargets.cs` -- the rule in the Xml layer.
- [ ] `src/OneNoteMcp.Core/Access/ReadAccess.cs`, `ProtectedSections.cs`, `UseCases/GetHierarchyUseCase.cs`, and each write and journal file in the Code Map -- pass the roots from settings and use the rule.
- [ ] Per Open Question 5: `src/OneNoteMcp.Core/Settings/SettingsStore.cs` and its tests.

**Execution (Part B, after probes P1–P3):**
- [ ] `tests/OneNoteMcp.Tests/OpenPackageToolTests.cs` (new) -- first, red: each Part B matrix row on `FakeGateway`.
- [ ] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, and every implementation in the Code Map -- the two methods.
- [ ] `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- the folder variant.
- [ ] `src/OneNoteMcp.Core/UseCases/OpenPackageUseCase.cs`, `src/OneNoteMcp.Server/Tools/OpenPackageTool.cs` (new), `ToolArguments.cs`, `Core/Results/ToolWarnings.cs` (`pagesNotListed`), registration in `Server/ServerSetup.cs`, `tool-contract.json`.
- [ ] `tests/OneNoteMcp.Integration/TestNotebook.cs` -- a guarded `CloseNotebookAsync` that closes only a notebook this run opened, under the run's temp root.
- [ ] `tests/OneNoteMcp.Integration/OpenPackageIntegrationTests.cs` (new) -- publish the throwaway as a package into the temp root, open it, read one of its pages, close it, delete the folders.

**Acceptance Criteria:**
- Given the throwaway's package, when `open_package` opens it into a new folder under the export root, then the folder exists, the result names the opened notebook's ID, and `get_hierarchy` lists it.
- Given a destination outside every export root, when `open_package` is called, then it returns `ExportPathNotAllowed` and nothing is written.
- Given the opened copy and no read grant, when a page of it is read, then it is `ReadNotGranted`; when a read grant for its section is added, then the same read succeeds.
- Given the integration test has finished, when its cleanup runs, then the copy it opened is closed and its folders are gone, and nothing else was closed.
- Given a package whose sections are unprotected and unlocked, when the unit tests open it on `FakeGateway`, then the result is ok with no warning (the ticket's "unlocked path").
- Given the existing unit tests, when they run after Part A, then they pass, changed only where a fixture section sits under an export root or the backups folder.
- Given no integration variables, when `dotnet test tests/OneNoteMcp.Integration` runs, then it builds and every live test skips.

## Design Notes

- The page-listing wait: after `OpenHierarchy`, read the notebook at `Pages` scope every 500 ms for up to 5 s (O1: 0 pages at 0 s, 1 at 5 s), under the write lock, as part of step 7. "Listed" means at least one `one:Page`. A package whose sections are all empty, locked or protected never lists one, so it always waits the 5 s and warns `pagesNotListed`; the spine accepts that (AD-7, O5), and the warning's text says it is expected for such packages.
- `OpenPackage` failing: the pipeline made the folder, so an empty folder after the failure is the pipeline's own and is removed; a non-empty one isn't touched (it may be a partial unpack).

## Open Questions

1. **One story or two?** (a) One: Part A and Part B together, as the ticket says. (b) Two: first Part A, protected by path (it already matters for backups and exports opened by hand), then Part B, `open_package`, after the probes. Part A touches about 20 files and every read tool; Part B is contained. Recommended: b.
2. **The destination.** (a) Optional, defaulting to `<package name> <yyyyMMdd-HHmmss>` under the first export root (created if missing), so the default never collides (see 6). (b) Required. Recommended: a.
3. **Which package paths.** Q6 says any folder. (a) Local, fully qualified paths only; network and device paths refused, as for `publish`'s target. (b) Network paths too. Recommended: a.
4. **Does the path rule cover writes?** AD-21 says "every read check" and doesn't mention writes. (a) Reads and writes alike (E8's reading): for example, a permanent delete of a section under an export root becomes refused, and journal entries for such sections are encrypted like protected ones; needs a spine amendment. (b) Reads only, as the spine words it. Recommended: a.
5. **An export root that holds the user's notebooks.** Any absolute folder is accepted as an export root; if one holds the user's own notebooks (Documents, say), each of their sections becomes protected after Part A. (a) Refuse such a setting: `settings.json` is invalid while an export root contains an open notebook's folder, with a message saying why. (b) Only warn in the README. (c) Accept it. Recommended: a.
6. **Grants on reused folder names.** Read grants are keyed by the section's path, so a copy unpacked later into a folder of the same name, with sections of the same names, inherits the earlier grant (inferred). (a) Let the timestamped default (2 a) avoid it, and say in the description that reusing a folder name reuses grants. (b) Also refuse a caller-chosen destination that a grant already names. Recommended: a.

## Live probes before Part B (sitting, Phill present; MCP Test and throwaways only)

- P1: open a package of a notebook that is still open (the throwaway): does the copy open, or clash with the original?
- P2: `CloseNotebook` on a copy a test opened: focus, dialogs, time; the original stays open.
- P3: the `path` OneNote reports for the opened copy's notebook and sections, under the test root and under the default root `Documents\OneNote Exports` (often OneDrive-redirected): a local path, or a URL that the path rule wouldn't match?

## Implementation Notes

## Plan Change Log

- 2026-11-17, after the Fable final review (findings checked against the sources; all held): the frozen block no longer assumes Open Question 1's answer and splits the work into Parts A and B; Part A covers the Xml-layer marker reads and `CheckBroad`'s input, with the notebook-not-shown case; step 7 failure outcomes defined; the acceptance criteria restate the ticket's Verify; tasks carry file paths and the missing implementations, the close seam and the grant listing; probes P1–P3 moved before Part B, P3 widened to the default root; the page wait specified; Open Questions 4–6 added; citations corrected.

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill; MCP Test and the throwaway only):**
- S0 (before Part B is built): probes P1–P3.
- S1: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~OpenPackageIntegrationTests` with the throwaway open. Expected: all pass, the copy closed, only temp folders.
- S2: with `onenote-dev`, open a package from Claude Code and read one of its pages: the grant dialog appears; Phill denies once (refused) and allows once (read). Phill closes the copy in OneNote.
