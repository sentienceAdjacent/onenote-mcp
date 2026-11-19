---
title: 'open_package'
type: 'feature'
ticket: '8'
created: '2026-11-17'
status: 'done'
baseline_revision: '2982032d3e2a65718031195099ab5f3670605ea6'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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
- A failed or timed-out step 7 names the folder (AD-11). `OpenPackage` failing with nothing written: the folder the pipeline created is removed if still empty, and the error says so; anything left behind stays and is named, as for `publish` (a retry needs a new name). `OpenHierarchy` failing after the unpack: the folder stays, and the error's message says the unpack was done and names the folder (Phill, 2026-11-18: in the message, not a `meta.completedSteps` field).
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
| No destination given | throwaway's `.onepkg` | `%LOCALAPPDATA%\OneNoteMcp\opened\<package name> <yyyyMMdd-HHmmss>` made and opened (Q3) | none |
| Destination inside a OneDrive-synced folder | e.g. under a redirected `Documents` | nothing done (Q3) | `ExportPathNotAllowed`, saying OneDrive would convert the copy |
| No page listed in the wait | all sections empty, locked or protected | ok + `pagesNotListed` | none |
| Destination outside every root | `..\x` or another drive | nothing done | `ExportPathNotAllowed` |
| Destination exists | file or folder there | untouched | `FileExists` |
| Package missing, or not `.onepkg` | path | nothing done | `InvalidArgument` |
| Network or device package path | `\\server\x.onepkg` | nothing done | `InvalidArgument` |
| `OpenPackage` fails, nothing written | bad package | empty folder removed | mapped code, names the folder |
| `OpenPackage` times out | — | folder kept, named | `Timeout`, `outcomeUnknown` |
| `OpenHierarchy` fails after the unpack | — | folder kept | mapped code; the message says the unpack was done and names the folder |
| Read a page of the opened copy | no read grant | refused | `ReadNotGranted` (entry 15) |
| A call after the open | the copy open under the export root, marker present | no `exportRootHoldsNotebooks` warning; writes not refused by the guard | none |

## Decisions (Phill, 2026-11-17)

- Split: entry 15 builds the path rule first; this story builds `open_package` on it (Q1 b). The path rule covers writes too (Q4 a) and guards export roots (Q5), in entry 15.
- Package paths: local, existing `.onepkg` only, as for `publish` (Q3 a).
- Default destination names carry a timestamp (Q6 a).
- Probe sitting S0 (P1–P3) before this story is built.
- Destination (Q2, settled 2026-11-17 after it was read back to him: `open_package` imports a package rather than exporting one): optional; by default a timestamped folder under the first export root, unless the caller names one. **Superseded by Q3 (2026-11-18), below.**
- **Q3 (Phill, 2026-11-18, option a): copies go to a non-synced folder in AppData, and OneDrive is refused.** With no destination, `open_package` unpacks into `%LOCALAPPDATA%\OneNoteMcp\opened\<package name> <yyyyMMdd-HHmmss>`, outside OneDrive; that opened-copies folder is a protected root like the journal's backups folder (AD-21), so every copy in it is protected by path whatever export roots say. Any destination inside a OneDrive-synced folder is refused before anything is written. Evidence: the 3.8 OneDrive crash probe (run 1119-023029, 2026-11-18) and two copies opened the day before: OneNote closed about 2 min after a copy was opened inside the OneDrive-synced export folder, with nothing in the Application log; on disk, the OneDrive sync client had converted each notebook folder inside OneDrive into an online notebook, deleting the folder and leaving `<name>.url` (an Internet shortcut to OneDrive's redirect page, called by the OneNote and Windows-sync scenarios) and `<name>_1\` holding only the `.onenote-mcp-opened` marker; the copy opened outside OneDrive was untouched. That conversion is also why such copies came back with `https://d.docs.live.net/…` paths (backlog 13). Phill's manual comparison (2026-11-18, his screenshots): he double-clicked the same throwaway `.onepkg`; OneNote's Unpack Notebook dialog defaulted to `<OneDrive>\Documents\OneNote Notebooks`, and he chose the package's own folder inside OneDrive. Unpacking took about 1 min; afterwards the folder held only the `.onepkg` and an Internet shortcut for the notebook: OneNote itself had uploaded it as an online notebook, in an orderly way, and it did not close. Inference (not measured): through its own UI OneNote knows the folder is in OneDrive and uploads the notebook itself; through COM `OpenPackage` + `OpenHierarchy` it doesn't, and the OneDrive client converts the folder underneath it, after which OneNote closes. Where a named destination may go: Open Question 1; cleanup of closed copies: Open Question 2.
- **Keep `open_package` (Phill, 2026-11-18, option B, after he was asked whether to drop it):** ship it with the AppData default, OneDrive destinations refused, and a cleanup rule (Open Question 2). He confirmed Q3 (a) after the manual comparison above. The rework waits for backlog 13 (#97) to merge and this branch to be rebased.
- Completed steps (Phill, 2026-11-18, option a): a failure after the unpack says so in the error's message, not in a `meta.completedSteps` field, because the error envelope's `meta` holds only `correlationId` for every tool, and a field there would change the public error contract (AD-9, AD-14). The plan and spine AD-5 amended to match.
- Marker file (Phill's final yes on entry 15 Q1, 2026-11-17): `open_package` writes `.onenote-mcp-opened` (`ExportRootGuard.MarkerFileName`) into every folder it creates, the default timestamped one and a named one alike, so the export-root guard doesn't count the opened copy as the user's own notebook.

</frozen-after-approval>

- **Q3 follow-ups (Phill, 2026-11-18):**
  - *Named destination, (i):* a named destination may only be inside the opened-copies folder (a folder name lands in it; a full path must be inside it). Export roots are no longer used by `open_package`.
  - *Cleanup, (b):* when the server starts, a copy's folder in the opened-copies folder is deleted when it holds the marker, its notebook is no longer open in OneNote, and the folder is more than 7 days old. Folders without the marker are never touched.
  - *`uninstall --purge`, (b):* it deletes the opened copies, but skips any copy OneNote still has open and lists the ones it skipped. When OneNote isn't running it can't tell what is open, so it skips every copy and lists them (Phill confirmed, 2026-11-18, after the review of 6fe92b3 changed it from deleting them all).

## Open Questions

All answered (Decisions, 2026-11-18); kept as asked.

1. **Where a named destination may go (Q3 follow-up).** (i) Only inside the opened-copies folder: `destination` becomes a folder name under it (or a full path inside it); export roots stop being used by `open_package` at all. (ii) Inside an export root or the opened-copies folder, either, as long as it isn't OneDrive-synced. (iii) Only inside an export root, refusing OneDrive. Recommended: (i): every copy in one known, non-synced, always-protected place; the export folders stay for files the user exports.
2. **Cleaning up closed copies (asked of Phill by the coordinating session).** Why not delete a copy at once: its folder *is* the open notebook, so it can only go after the user closes it. Recommended there: a sweep when the server starts that deletes folders in the opened-copies folder that hold the marker, whose notebook isn't open, and that are older than 7 days. Its answer may add tasks below.

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
- [x] `tests/OneNoteMcp.Tests/OpenPackageToolTests.cs` (new) -- first, red: each matrix row on `FakeGateway`, including the marker file in a default and a named folder, and a guarded `ReadAccess` that then gives no `exportRootHoldsNotebooks` warning for the copy.
- [x] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, and every implementation in the Code Map -- the two methods.
- [x] `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- the folder variant.
- [x] `src/OneNoteMcp.Core/UseCases/OpenPackageUseCase.cs` -- including the marker file, written after the folder is created and before `OpenPackage`; a failure to write it fails the call before COM and removes the empty folder. `src/OneNoteMcp.Server/Tools/OpenPackageTool.cs` (new), `ToolArguments.cs`, `src/OneNoteMcp.Core/Results/ToolWarnings.cs` (`pagesNotListed`), registration in `src/OneNoteMcp.Server/ServerSetup.cs`, `tool-contract.json`.
- [x] `tests/OneNoteMcp.Integration/TestNotebook.cs` -- a guarded `CloseNotebookAsync` that closes only a notebook this run opened, under the run's temp root.
- [x] `tests/OneNoteMcp.Integration/OpenPackageIntegrationTests.cs` (new) -- publish the throwaway as a package into the temp root (an export root in the test's settings), open it, check the marker file, read one of its pages (`ReadNotGranted`, then allowed with a CLI-style grant), close it, delete the folders.

**Acceptance Criteria:**
- Given the throwaway's package, when `open_package` opens it into a new folder under the export root, then the folder exists, the result names the opened notebook's ID, and `get_hierarchy` lists it.
- Given a destination outside every export root, when `open_package` is called, then it returns `ExportPathNotAllowed` and nothing is written.
- Given the opened copy and no read grant, when a page of it is read, then it is `ReadNotGranted`; when a read grant for its section is added, then the same read succeeds.
- Given the integration test has finished, when its cleanup runs, then the copy it opened is closed and its folders are gone, and nothing else was closed.
- Given a package whose sections are unprotected and unlocked, when the unit tests open it on `FakeGateway`, then the result is ok with no warning (the ticket's "unlocked path").
- Given no integration variables, when `dotnet test tests/OneNoteMcp.Integration` runs, then it builds and every live test skips.

### Rework for Q3 (planned 2026-11-18 on main's backlog 13 seam; not to be built until Open Questions 1 and 2 are answered)

Main now has `OneDriveFolders` (Core/Access; accounts with their folder's plain and canonical forms), filled by `OneDriveAccounts.Current` (Server, registry plus `%OneDrive*%`, 30 s cache) and passed to `ReadAccess`, `ExportRootGuard`, `restore_from_journal` and the grant CLI. The rework reuses it, test-first:

- [ ] `tests/OneNoteMcp.Tests/OpenPackageToolTests.cs` -- red first: no destination unpacks into `<opened-copies>\<stem> <yyyyMMdd-HHmmss>`, the marker written there; a destination inside a OneDrive folder (seam set by hand, the placeholder folder only) is `ExportPathNotAllowed` with nothing created, whichever way it is spelled (case, `..`, a junction into it); the opened-copies folder itself inside OneDrive (a redirected `%LOCALAPPDATA%`) is refused the same way; a named destination per Open Question 1; the copy is protected with `exportRoots` empty of it (the opened-copies folder is a root); the Q2 tests that expect the export root as default are rewritten, not deleted.
- [ ] `src/OneNoteMcp.Core/Journal/JournalFiles.cs` (or a sibling `OpenedCopies.cs`) -- the opened-copies folder: `%LOCALAPPDATA%\OneNoteMcp\opened`, beside the journal's default, with the same test override rule (`ONENOTE_MCP_JOURNAL_DIR`'s sibling, or a folder beside the settings file the override names), so tests and probes never touch the user's AppData. `SettingsStore` carries it as it carries `DefaultJournal`.
- [ ] `src/OneNoteMcp.Core/Access/Protection.cs` -- `RootsOf` adds the opened-copies folder after the backups folder; its `ByPathReason` and `Phrase` name it ("under an export root, the opened-copies folder or the backups folder"). `ReadAccess`'s invalid-settings fallback keeps it (it doesn't come from `settings.json`).
- [ ] `src/OneNoteMcp.Core/Access/OneDriveFolders.cs` -- `Holds(path)`: true when a path's plain or canonical form is a known OneDrive folder or lies inside one (segment boundary, case-insensitive), reusing `FolderPrefixes`. With no OneDrive folder known it is false (nothing to refuse; the protection rule still fails closed by its two-segment tail).
- [ ] `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- `ResolveFolder` takes the allowed roots per Open Question 1 (recommended (i): the opened-copies folder only, `destination` a folder name or a path inside it).
- [ ] `src/OneNoteMcp.Core/UseCases/OpenPackageUseCase.cs` -- takes `Func<OneDriveFolders>` and the opened-copies folder; step 1 resolves the default there, refuses `oneDrive().Holds(folder)` with `ExportPathNotAllowed` ("inside OneDrive, whose sync converts an unpacked notebook into an online one and closes OneNote"); Validate re-checks it under the lock. The tool description says where copies go and that OneDrive is refused.
- [ ] `src/OneNoteMcp.Server/ServerSetup.cs` -- passes `OneDriveAccounts.Current` and the folder.
- [ ] Cleanup per Open Question 2 (recommended there: a startup sweep of marker folders in the opened-copies folder whose notebook isn't open and that are older than 7 days); its tasks are added once answered.
- [ ] `tests/OneNoteMcp.Integration` -- the S1 test and the probes open into the run's own temp opened-copies folder (override), never OneDrive; `CloseOpenedCopyAsync` unchanged.
- [ ] Docs: spine AD-5/AD-7 `open_package` bullets (the "under an export root" text that predates Q3), AD-21's root list (already names the folder), AD-11 for where a named destination may go; README's `open_package` row; `uninstall --purge` removes the opened-copies folder only if Phill says so (its copies may be open notebooks).
- [ ] Sitting: S1 and S2 rerun on the rework (default location outside OneDrive; a OneDrive destination refused; the copy protected after a restart).

**Acceptance Criteria (rework):**
- Given no destination, when `open_package` runs, then the copy is unpacked into a new timestamped folder under `%LOCALAPPDATA%\OneNoteMcp\opened` and opened, and a page of it is `ReadNotGranted` without a grant.
- Given a destination inside a OneDrive folder, when `open_package` runs, then it returns `ExportPathNotAllowed` naming OneDrive and nothing is created.
- Given `exportRoots` that doesn't cover the opened-copies folder, when a page of an opened copy is read, then it is still protected.

## Design Notes

- The page-listing wait: after `OpenHierarchy`, read the notebook at `Pages` scope every 500 ms for up to 5 s (O1: 0 pages at 0 s, 1 at 5 s), under the write lock, as part of step 7. "Listed" means at least one `one:Page`. A package whose sections are all empty, locked or protected never lists one, so it always waits the 5 s and warns `pagesNotListed`; the spine accepts that (AD-7, O5), and the warning's text says it is expected for such packages.
- `OpenPackage` failing: the pipeline made the folder, so an empty folder after the failure is the pipeline's own and is removed; a non-empty one isn't touched (it may be a partial unpack).

## Live probes, sitting S0 before the build (Phill present; MCP Test and throwaways only)

- P1: open a package of a notebook that is still open (the throwaway): does the copy open, or clash with the original?
  **First run (2026-11-18, results relayed by the coordinating session): P1 opens, no clash.** `OpenPackage` unpacked into the folder that already held the marker file, and opened nothing itself; `OpenHierarchy` of that folder (17 ms, focus kept) opened a new notebook with its own ID while the original stayed open and unchanged; the copy's notebook path was under the publish folder. Its section list was empty at once (0 sections), as O1's pages were; the rerun re-reads it after 5 s. The unpacked folder also held a subfolder named after a section group of the throwaway (left there by story 3.12's probe): a section group unpacks as a folder, as in any notebook, not a nested notebook. P2 didn't run: the probe's close helper threw `XmlException` before any `CloseNotebook` (it built an element name from the namespace string, `OneNoteXml.Namespace2013 + "Notebook"`, instead of an `XNamespace`); fixed test-first (`TestNotebookGuardTests.EnsureCopyToClose_*`, red with the same exception), and the copy was closed by hand.
  **For the build (inferred, to confirm in the rerun):** `open_package` needn't wait for sections. Protection is decided at each read from the live paths, so a section not listed yet has nothing to read; Design Notes' 5 s page wait already covers a copy that lists late, and its `pagesNotListed` warning tells the caller.
- P2: `CloseNotebook` on a copy a test opened: focus, dialogs, time; the original stays open.

**S0 result (2026-11-18, the rerun; log `open-package-p1-p2-20261118-181231.jsonl` in the probes folder): passed.**
- P1: opened, no clash. A new notebook ID (not the original's); `OpenHierarchy` 14 ms, focus kept; the original stayed open and unchanged; the copy's path under the publish folder. At 0 s it listed 0 sections and 0 pages; at 5 s, 9 sections and 11 pages, every section path under the publish folder. So the copy's sections load late, as its pages do; protection reads live paths at each call, so `open_package` needs no section wait beyond Design Notes' page wait.
- P2: `CloseNotebook` (not forced) 66 ms, focus kept; the copy was gone and the original still open. No dialog was seen, here or at the first run's close by hand.
- Cleanup deleted both probe folders. No HRESULT was logged. A pause before P1 asked for the copy to show elsewhere (`p1-show-elsewhere`, the harness's usual shown check).
- P3: **settled by entry 15's sitting S0 (2026-11-17):** a `.one` under the default root (OneDrive-redirected Documents) and under the test root, opened by hand, reported local paths, not URLs. Still to confirm in P1: that a notebook opened from a package with `OpenHierarchy` (not a loose file) reports its folder and section paths under the export root the same way.

## Implementation Notes

- 2026-11-18, the Q3 rework, built test-first (23 new or changed tests ran red: `OpenPackageToolTests`, the new `OpenedCopiesTests`, and three `UninstallCommandTests`; then green):
  - `Core/Writes/OpenedCopies.cs` (new): `DefaultFolder` (`ONENOTE_MCP_OPENED_DIR`, else beside an overridden settings file, else `%LOCALAPPDATA%\OneNoteMcp\opened`), `MarkedCopies` (direct subfolders with the marker, not links), `IsOpen`; `OpenedCopySweep.SweepAsync` (older than 7 days by folder creation time; reads OneNote only when a copy is old enough; `OneNoteNotRunning` means none open; other failures delete nothing).
  - `SettingsStore` takes and carries the opened-copies folder; every snapshot has `OpenedCopies`; `Protection.RootsOf` adds it; the by-path phrase now says "under the opened-copies folder, an export root or the backups folder" everywhere (it keeps the old wording as a substring, so earlier assertions hold).
  - `OneDriveFolders.Holds(path)`; `ExportTarget.ResolveFolder(raw, openedCopies)` with its own messages and user action; `OpenPackageUseCase` refuses OneDrive (the opened-copies folder, then the resolved folder) at step 1 and in Validate.
  - Server: `OpenedCopiesSweepService` (once at start, counts only in the log) via `AddOpenedCopiesSweep` in `Program`; `ServerSetup` passes `OneDriveAccounts.Current`; `UninstallCommand` removes marked copies whole, skipping and listing open ones, through `OpenNotebooksIfOneNoteRuns` (never starts OneNote; empty when it isn't running; null, so every copy is skipped, when the read fails).
  - Tool description, README (purge), spine (AD-7 open_package, the ExternalWrite row, AD-19 uninstall, AD-21 unpacked packages); the integration test's store is given the run's temp root as its opened-copies folder.

- Implemented directly in this lane (no implementation subagents). **Test-first:** `OpenPackageToolTests` (15) ran red against a stub use case (15 failed), then green; the five `ComGatewayLifecycleTests.OpenPackage*`/`OpenHierarchy*` refusal and pass-through tests were written after `ComGateway`'s two methods but ran red on the two that reach COM (the fake application threw for `OpenPackage`), green once it recorded the call; the integration test came after the code (it can't run without OneNote).
- `IOneNoteGateway.OpenPackageAsync` and `OpenHierarchyAsync` have default bodies that refuse with `NotSupported`, so the 21 test gateways needn't change; `ComGateway` and `FakeGateway` implement them. `OpenPackage` runs under `timeouts.publishSeconds`; neither is retried after a disconnect.
- `ExportTarget.ResolveFolder` shares one private core (`UnderARoot`) with `Resolve`, so the folder variant uses the same root test (`PathUnder`) as exports and AD-21's path rule; its messages say "nothing was opened".
- `OpenPackageUseCase`: step 1 checks the package (fully qualified, local, existing, `.onepkg`) and resolves the destination (default `<package name> <yyyyMMdd-HHmmss>`, local time); under the lock it re-resolves, creates the folder and writes the marker (a failure removes the folder); step 7 unpacks, checks the reported path is the destination, opens it, and waits for a page (500 ms polls, 5 s). New warning `pagesNotListed`.
- **Deviation from the plan, since decided (Phill, 2026-11-18, option a: keep it in the message; plan and spine amended):** "`meta.completedSteps`" after an `OpenHierarchy` failure is carried in the error's message ("The package was unpacked into <folder>; opening it there failed."), not as a meta field: the error envelope's `meta` holds only `correlationId`, and a new field there would change the public error contract (AD-9, AD-14) for every tool.
- Contract: `open_package` added to `tool-contract.json` (570 lines added, nothing else changed; `<Version>` stays `0.9.0-rc.1`, unreleased since `v0.8.0-rc.2`); tool counts 28→29, property counts 71→73; `open_package` joins the tools whose skill and README text waits for entry 10.

## Plan Change Log

- 2026-11-18, Phill answered the named-destination (i), cleanup (b) and purge (b) questions; status in-progress, building the Q3 rework test-first.
- 2026-11-18, merged main after backlog 13 (#97, 30cb630) and planned the Q3 rework on its `OneDriveFolders` seam (Tasks, "Rework for Q3"); build waits for Open Questions 1 and 2.
- 2026-11-18, Phill's Q3 (option a) after the OneDrive crash probe: the default destination moves to `%LOCALAPPDATA%\OneNoteMcp\opened\…`, a protected root, and OneDrive-synced destinations are refused; status back to `draft` until Open Questions 1 and 2 are answered. **Rework, after backlog 13 (#97) merges and this branch is rebased** (it brings the OneDrive folder seam `OneDriveFolders` to reuse for "is this OneDrive-synced"): `Protection.RootsOf` adds the opened-copies folder; `OpenPackageUseCase` takes it as the default and refuses OneDrive destinations; `ExportTarget.ResolveFolder` gains the opened-copies root per Open Question 1; tests first (default location, OneDrive refusal, the copy protected with no export root covering it); the integration test and S1/S2 rerun. The built code on this branch (default under the export root) is not to merge as it stands.

- 2026-11-17, after the Fable final review (findings checked against the sources; all held): the frozen block stopped assuming the split; the Xml-layer marker reads and `CheckBroad`'s input added; step 7 failure outcomes; the ticket's Verify as Given/When/Then; tasks with file paths; probes before the build; the page wait; three more questions; citations corrected.
- 2026-11-17, Phill's answers: the path rule moved to entry 15 (this plan keeps `open_package` only); Q3, Q4, Q5 and Q6 applied as Decisions; Q2 reopened as Open Question 1.
- 2026-11-18, after entry 15 merged (8524d7d; origin/main merged into this branch): the marker file recorded as Phill's decision (entry 15 Q1 final) with a matrix row and tasks; the Code Map names `Protection`, `PathUnder` (the shared root test the folder variant must reuse) and `ExportRootGuard` (marker before `OpenHierarchy`; the guard can refuse `open_package` itself); P3 marked settled by entry 15's S0, with P1 checking the opened notebook's paths. No other change; status stays `ready-for-dev`, build after S0.
- 2026-11-17, Phill settled Q2: optional destination, timestamped default under the first export root; the matrix gains the default row; no open questions left. If entry 15's guard question is answered with its marker-file option, this story also writes `.onenote-mcp-opened` into each folder it creates (entry 15 decides; a Plan Change Log entry here will add the task).

## Review Triage Log

- 2026-11-18, one reviewer on 6fe92b3 (the Q3 rework); each finding checked against the code:
  - 1 (high, fixed): the sweep took `OneNoteNotRunning` as "nothing open". A closed OneNote still lists its notebooks and reopens them, and the code also covers a OneNote with no window yet, so a copy with the user's edits could go. Now only a successful read counts; the purge likewise treats "OneNote not running" as can't tell (skips and lists). Test flipped: `OneNoteNotRunning_IsCantTell_SoNothingIsDeleted`.
  - 2 (high, fixed): `IsOpen` compared plain forms only, while OneNote is given the canonical path; through a link in the opened-copies path an open copy looked closed. Now every form of both. Test `IsOpen_MatchesTheSameFolderInEveryForm_ThroughAJunction`.
  - 3 (medium-low, fixed): the purge's gateway used the user's settings, so `allowSilentActivation` could start a hidden OneNote in a race; it now reads defaults.
  - 4 (low, fixed): a nested destination (`opened\a\b`) left a copy the sweep and purge never find; a destination must be a direct child. Test `ANestedDestination_…`.
  - 5 (low, fixed): the purge now checks what is open again after the question. Test `Purge_ChecksAgainAfterTheQuestion_…`.
  - 6 (low, fixed): the invalid-settings warning now names the opened-copies folder.
- The four review-fix tests ran red (4 failing) before the fixes, then green.

- 2026-11-18, quick lens, iteration 0 (findings written to the build's scratchpad): 9 findings, each checked against the code: 0 high, 2 medium, 5 low, 1 rejected, 1 for Phill. Patched test-first where a test fit (7 new tests in `OpenPackageToolTests`; 4 ran red first, the other 3 passed before the patch and stay as guards); no loopback.
  - **medium, patch.** A cancel or the pipeline's deadline during the page wait turned a successful open into a failure saying "opening it there failed", losing the copy's ID. Fix: the wait ends on a cancel with `pagesNotListed` and the result keeps `meta.objectId`; once `OpenHierarchy` has succeeded the failure note names the opened notebook. Test: `ACancelDuringThePageWait_…`.
  - **decided by Phill (2026-11-18, option a).** `meta.completedSteps` (frozen block, spine AD-5) is carried in the error message, not a meta field: the error envelope's `meta` holds only `correlationId` for every tool, and adding a field changes the public error contract (AD-9, AD-14). Phill decides: keep the message (and amend the plan and spine) or add the field.
  - **low, patch.** The "nothing was unpacked, folder removed" branch disagreed with the pipeline's own test of "may have written" (`ComUnavailable`, `Cancelled`, `0x800706BE`). Fix: it now uses `WritePipeline.IsAmbiguous`. Test: `AComUnavailableFromOpenPackage_KeepsTheFolder_…`.
  - **low, patch.** An `OpenHierarchy` failure replaced the gateway's `userAction` and dropped the inner exception. Fix: both kept (a fallback action when there is none). Test: `AnOpenHierarchyFailure_KeepsItsOwnUserAction`.
  - **low, patch.** Missing tests: a file at the destination, `OpenPackage` reporting another path, and a guarded `ReadAccess` and `WritePipeline` after an open (no warning, the next write not refused). Added; all passed on the existing code. Rejected: a unit test for reading the copy's page (entry 15's tests cover the rule; the integration test covers the copy) and for a marker-write failure (no seam short of a file-system fake).
  - **low, patch.** The integration test didn't check the copy is listed while open (AC 1) or that its folder is gone (AC 4). Added. Rejected: the "opened this run" set built from the copy's own ID is tautological; it is the ID this run opened, and the path-under-the-root check guards it.
  - **low, patch.** A folder made between the re-resolve and the create would have been unpacked into. Fix: an existence check right before the create, and the marker written with `CreateNew`.
  - **low, patch.** A destination inside an open notebook's folder (an earlier copy's) was accepted. Fix: refused with `ExportPathNotAllowed` after one notebooks read in step 1 (a failed read gives no verdict). Test: `ADestinationInsideAnOpenNotebooksFolder_…`.
  - **low, patch.** A stale comment ("71 properties") and test name ("TwentyEight"). Fixed.

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

**Q3 rework sitting (2026-11-18/19, Phill present, the build at 704a9fd through onenote-dev): S1, S2 and S3 passed.**
- S1.1: `open_package` with no destination gave `meta.outputPath` `%LOCALAPPDATA%\OneNoteMcp\opened\<package> 20261118-230451`; the `.onenote-mcp-opened` marker was there and the copy was open in OneNote.
- S1.2: `get_hierarchy` of the copy at pages scope withheld both sections, each `readNotGranted` naming the opened-copies folder.
- S1.3: `get_hierarchy` of one section at pages scope raised the grant dialog; Phill chose Deny, and the call returned `ReadNotGranted` ("The user declined the grant dialog").
- S1.4: the copy stayed open more than 5 minutes (opened 04:04:51Z, checked 04:10Z) and OneNote didn't close, against about 2 minutes when copies were opened inside OneDrive. After Phill restarted OneNote, the copy was still reported by its local AppData path, not a URL, and both sections were still withheld.
- S2: each was `ExportPathNotAllowed`, with no folder created: a full path in the export root ("isn't inside the opened-copies folder"), `a\b` ("isn't directly inside"), and a path inside the OneDrive folder ("isn't inside"). The OneDrive-specific refusal (a redirected AppData) is unit-tested only.
- S3 (cleanup): Phill closed the S1 copy in OneNote, with OneNote still running. Its folder, and an unmarked folder beside it, were both backdated 8 days (CreationTime). After an onenote-dev reconnect restarted the server, the copy's folder was gone and the unmarked folder was kept. The unmarked folder was then removed by hand.
