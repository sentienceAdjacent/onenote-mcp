---
title: 'open_package'
type: 'feature'
ticket: '8'
created: '2026-11-17'
status: 'ready-for-dev'
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

**Problem:** A OneNote package (`.onepkg`, which `publish` makes of a notebook) can't be opened through the server. Spike 3.1 (O1–O5, Implication 10) found that `OpenPackage` unpacks into the folder it's given and opens nothing, so a separate `OpenHierarchy` is needed. Every section of an opened copy counts as protected by its path (Q11), which entry 15 builds.

**Approach:** `open_package` as an `ExternalWrite` (AD-5 promoted task, AD-7): unpack a package into a new folder under an export root, open it with `OpenHierarchy`, and return the folder and the notebook. Built only after entry 15 and after probe sitting S0 (P1–P3).

## Boundaries & Constraints

**Always:**
- AD-7 step 1 (before the lock): the package is a local, fully qualified path to an existing `.onepkg` file, in any folder; network and device paths are `InvalidArgument`, as for `publish`'s target (Phill, Q3). The destination is optional: a folder the caller names, resolved under an export root like `publish`'s target, or by default `<package name> <yyyyMMdd-HHmmss>` (local time) under the first export root, created if missing (Phill, Q2); either must not exist (`FileExists`). Steps 3–4 (under the lock): resolved again, still absent; the pipeline creates it (AD-7). Step 7: `OpenPackage(package, destination)`, then `OpenHierarchy(pathOut)` on the existing path (no create, no grant), then the page-listing wait (Design Notes). Never retried.
- A failed or timed-out step 7 names the folder (AD-11). `OpenPackage` failing with nothing written: the folder the pipeline created is removed if still empty, and the error says so; anything left behind stays and is named, as for `publish` (a retry needs a new name). `OpenHierarchy` failing after the unpack: the folder stays, `meta.completedSteps` says the unpack was done.
- A default destination name carries a timestamp, so a folder name is never reused and no read grant carries over to another copy (Phill, Q6).
- The result: `meta.outputPath` (the folder) and `meta.objectId` (the opened notebook's ID). No write grant (Q6 of epic 3); every section is protected by path (entry 15). The user closes the copy in OneNote (Q13); the description says so.
- Messages name IDs and the caller's own paths, never notebook or section names from a package.

**Never:**
- No `MergeFiles` (ends ONENOTE.EXE), no `CloseNotebook` tool (epic 4), no automatic grant, no `NavigateTo`, no unpack into an existing folder.
- The integration tests close only notebooks they opened, through the harness's COM seam, and delete only their own folders.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Open a package | throwaway's `.onepkg`, new destination | folder made with the marker file, notebook opened; `meta.outputPath`, `meta.objectId` | none |
| No destination given | throwaway's `.onepkg` | `<first root>\<package name> <yyyyMMdd-HHmmss>` made and opened | none |
| No page listed in the wait | all sections empty, locked or protected | ok + `pagesNotListed` | none |
| Destination outside every root | `..\x` or another drive | nothing done | `ExportPathNotAllowed` |
| Destination exists | file or folder there | untouched | `FileExists` |
| Package missing, or not `.onepkg` | path | nothing done | `InvalidArgument` |
| Network or device package path | `\\server\x.onepkg` | nothing done | `InvalidArgument` |
| `OpenPackage` fails, nothing written | bad package | empty folder removed | mapped code, names the folder |
| `OpenPackage` times out | — | folder kept, named | `Timeout`, `outcomeUnknown` |
| `OpenHierarchy` fails after the unpack | — | folder kept | mapped code + `meta.completedSteps` |
| Read a page of the opened copy | no read grant | refused | `ReadNotGranted` (entry 15) |
| A call after the open | the copy open under the export root, marker present | no `exportRootHoldsNotebooks` warning; writes not refused by the guard | none |

## Decisions (Phill, 2026-11-17)

- Split: entry 15 builds the path rule first; this story builds `open_package` on it (Q1 b). The path rule covers writes too (Q4 a) and guards export roots (Q5), in entry 15.
- Package paths: local, existing `.onepkg` only, as for `publish` (Q3 a).
- Default destination names carry a timestamp (Q6 a).
- Probe sitting S0 (P1–P3) before this story is built.
- Destination (Q2, settled 2026-11-17 after it was read back to him: `open_package` imports a package rather than exporting one): optional; by default a timestamped folder under the first export root, unless the caller names one.
- Marker file (Phill's final yes on entry 15 Q1, 2026-11-17): `open_package` writes `.onenote-mcp-opened` (`ExportRootGuard.MarkerFileName`) into every folder it creates, the default timestamped one and a named one alike, so the export-root guard doesn't count the opened copy as the user's own notebook.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- `OpenPackage` (l.106) and `OpenHierarchy` (l.32) are declared; `ComGateway.CreateNotebookAsync` (l.339) is the `OpenHierarchy` pattern.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` gets `OpenPackageAsync` and `OpenHierarchyAsync` (existing path); every implementation must follow: `ComGateway`, and 21 test files (grep `: IOneNoteGateway` under `tests`), among them `tests/OneNoteMcp.Tests/FakeGateway.cs` and the integration gateways in `Move…`, `PermanentDelete…`, `ProtectedSection…`, `Publish…`, `SetPageLevel…`, `UpdateHierarchy…IntegrationTests.cs`.
- `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- `Resolve` (l.63) is file-and-extension shaped; add a folder variant beside it. Its root test is `Contains` (l.148), which entry 15 made `PathUnder.IsUnder` (`Writes/PathUnder.cs`), the same test AD-21's path rule uses: reuse it, don't write another.
- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- the `ExternalWrite` shape; `src/OneNoteMcp.Server/Tools/PublishTool.cs`, `ToolArguments.cs`, `ServerSetup.cs` (registration); `src/OneNoteMcp.Core/Results/ToolWarnings.cs`; `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (a new tool).
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `OpenPackageAsync` (l.377), `AdoptThrowawayNotebookAsync`, `Round2.LocalThrowawayName`; no test closes a notebook yet.
- Entry 15 (merged as 8524d7d): `Core/Access/Protection.cs` (built per call by `ReadAccess.Protection()`) makes every section of the opened copy protected, because the copy's folder is under an export root; nothing here re-implements it. `Core/Access/ExportRootGuard.cs` warns on reads and refuses writes (`SettingsInvalid`) while an export root holds an open notebook without `ExportRootGuard.MarkerFileName` in its folder; its verdict is cached for `CacheFor` (30 s). So the marker must be in the folder before `OpenHierarchy` opens it: write it right after the pipeline creates the folder, before `OpenPackage` (spike O2: `OpenPackage` unpacks beside an existing file). `open_package` itself is a write, so the guard refuses it while a user notebook sits under a root.
- Prior art: OneMore calls neither `OpenPackage` nor `MergeFiles` (research note row 18).

## Tasks & Acceptance

**Execution (after entry 15 is built and S0 has run):**
- [ ] `tests/OneNoteMcp.Tests/OpenPackageToolTests.cs` (new) -- first, red: each matrix row on `FakeGateway`, including the marker file in a default and a named folder, and a guarded `ReadAccess` that then gives no `exportRootHoldsNotebooks` warning for the copy.
- [ ] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, and every implementation in the Code Map -- the two methods.
- [ ] `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- the folder variant.
- [ ] `src/OneNoteMcp.Core/UseCases/OpenPackageUseCase.cs` -- including the marker file, written after the folder is created and before `OpenPackage`; a failure to write it fails the call before COM and removes the empty folder. `src/OneNoteMcp.Server/Tools/OpenPackageTool.cs` (new), `ToolArguments.cs`, `src/OneNoteMcp.Core/Results/ToolWarnings.cs` (`pagesNotListed`), registration in `src/OneNoteMcp.Server/ServerSetup.cs`, `tool-contract.json`.
- [ ] `tests/OneNoteMcp.Integration/TestNotebook.cs` -- a guarded `CloseNotebookAsync` that closes only a notebook this run opened, under the run's temp root.
- [ ] `tests/OneNoteMcp.Integration/OpenPackageIntegrationTests.cs` (new) -- publish the throwaway as a package into the temp root (an export root in the test's settings), open it, check the marker file, read one of its pages (`ReadNotGranted`, then allowed with a CLI-style grant), close it, delete the folders.

**Acceptance Criteria:**
- Given the throwaway's package, when `open_package` opens it into a new folder under the export root, then the folder exists, the result names the opened notebook's ID, and `get_hierarchy` lists it.
- Given a destination outside every export root, when `open_package` is called, then it returns `ExportPathNotAllowed` and nothing is written.
- Given the opened copy and no read grant, when a page of it is read, then it is `ReadNotGranted`; when a read grant for its section is added, then the same read succeeds.
- Given the integration test has finished, when its cleanup runs, then the copy it opened is closed and its folders are gone, and nothing else was closed.
- Given a package whose sections are unprotected and unlocked, when the unit tests open it on `FakeGateway`, then the result is ok with no warning (the ticket's "unlocked path").
- Given no integration variables, when `dotnet test tests/OneNoteMcp.Integration` runs, then it builds and every live test skips.

## Design Notes

- The page-listing wait: after `OpenHierarchy`, read the notebook at `Pages` scope every 500 ms for up to 5 s (O1: 0 pages at 0 s, 1 at 5 s), under the write lock, as part of step 7. "Listed" means at least one `one:Page`. A package whose sections are all empty, locked or protected never lists one, so it always waits the 5 s and warns `pagesNotListed`; the spine accepts that (AD-7, O5), and the warning's text says it is expected for such packages.
- `OpenPackage` failing: the pipeline made the folder, so an empty folder after the failure is the pipeline's own and is removed; a non-empty one isn't touched (it may be a partial unpack).

## Live probes, sitting S0 before the build (Phill present; MCP Test and throwaways only)

- P1: open a package of a notebook that is still open (the throwaway): does the copy open, or clash with the original?
  **First run (2026-11-18, results relayed by the coordinating session): P1 opens, no clash.** `OpenPackage` unpacked into the folder that already held the marker file, and opened nothing itself; `OpenHierarchy` of that folder (17 ms, focus kept) opened a new notebook with its own ID while the original stayed open and unchanged; the copy's notebook path was under the publish folder. Its section list was empty at once (0 sections), as O1's pages were; the rerun re-reads it after 5 s. The unpacked folder also held a subfolder named after a section group of the throwaway (left there by story 3.12's probe): a section group unpacks as a folder, as in any notebook, not a nested notebook. P2 didn't run: the probe's close helper threw `XmlException` before any `CloseNotebook` (it built an element name from the namespace string, `OneNoteXml.Namespace2013 + "Notebook"`, instead of an `XNamespace`); fixed test-first (`TestNotebookGuardTests.EnsureCopyToClose_*`, red with the same exception), and the copy was closed by hand.
  **For the build (inferred, to confirm in the rerun):** `open_package` needn't wait for sections. Protection is decided at each read from the live paths, so a section not listed yet has nothing to read; Design Notes' 5 s page wait already covers a copy that lists late, and its `pagesNotListed` warning tells the caller.
- P2: `CloseNotebook` on a copy a test opened: focus, dialogs, time; the original stays open.
- P3: **settled by entry 15's sitting S0 (2026-11-17):** a `.one` under the default root (OneDrive-redirected Documents) and under the test root, opened by hand, reported local paths, not URLs. Still to confirm in P1: that a notebook opened from a package with `OpenHierarchy` (not a loose file) reports its folder and section paths under the export root the same way.

## Implementation Notes

## Plan Change Log

- 2026-11-17, after the Fable final review (findings checked against the sources; all held): the frozen block stopped assuming the split; the Xml-layer marker reads and `CheckBroad`'s input added; step 7 failure outcomes; the ticket's Verify as Given/When/Then; tasks with file paths; probes before the build; the page wait; three more questions; citations corrected.
- 2026-11-17, Phill's answers: the path rule moved to entry 15 (this plan keeps `open_package` only); Q3, Q4, Q5 and Q6 applied as Decisions; Q2 reopened as Open Question 1.
- 2026-11-18, after entry 15 merged (8524d7d; origin/main merged into this branch): the marker file recorded as Phill's decision (entry 15 Q1 final) with a matrix row and tasks; the Code Map names `Protection`, `PathUnder` (the shared root test the folder variant must reuse) and `ExportRootGuard` (marker before `OpenHierarchy`; the guard can refuse `open_package` itself); P3 marked settled by entry 15's S0, with P1 checking the opened notebook's paths. No other change; status stays `ready-for-dev`, build after S0.
- 2026-11-17, Phill settled Q2: optional destination, timestamped default under the first export root; the matrix gains the default row; no open questions left. If entry 15's guard question is answered with its marker-file option, this story also writes `.onenote-mcp-opened` into each folder it creates (entry 15 decides; a Plan Change Log entry here will add the task).

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill; MCP Test and the throwaway only):**
- S0 (before the build): probes P1 and P2 (P3 was settled by entry 15's S0; P1 also checks the opened notebook's paths).
  Probe code: `tests/OneNoteMcp.Integration/WriteProbes/OpenPackageProbes.cs`, scenario `open-package-p1-p2`, with a guarded `TestNotebook.CloseOpenedCopyAsync` (closes only a notebook this run opened whose live path is under the probes' publish folder). Run: `ONENOTE_MCP_INTEGRATION=1 ONENOTE_MCP_SCENARIO=open-package-p1-p2 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~OpenPackageProbes`; the log lands in `%LOCALAPPDATA%\OneNoteMcp-Test\probes\open-package-p1-p2-<utc>.jsonl`.
- S1: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~OpenPackageIntegrationTests` with the throwaway open. Expected: all pass, the copy closed, only temp folders.
- S2: with `onenote-dev`, open a package from Claude Code and read one of its pages: the grant dialog appears; Phill denies once (refused) and allows once (read). Phill closes the copy in OneNote.
