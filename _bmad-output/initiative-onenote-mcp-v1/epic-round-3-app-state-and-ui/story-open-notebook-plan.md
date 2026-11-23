---
title: 'open_notebook'
type: 'feature'
ticket: '5'
created: '2026-11-21'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/story-tracer-sync-hierarchy-through-the-appstate-pipeline-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A notebook closed with `close_notebook` (story 4.4) can only be reopened in OneNote, and `OpenHierarchy` with `cftNone` is no guard: it turned an empty folder into a notebook (spike 4.1 O6).

**Approach:** An `open_notebook(path)` tool on the `AppStatePipeline` with 4.4's lock path, which refuses everything but an existing local notebook folder before COM, answers an already-open notebook without COM, and otherwise sends the canonical path to the existing `OpenHierarchyAsync` (`cftNone`) (spine AD-7 `AppState` `open_notebook`).

## Boundaries & Constraints

**Always:**
- Step 1, before COM: the path is made canonical with `FilePathGate.Canonical` (as `PageWrite` does); every check and the COM call use the canonical form. `InvalidArgument` with a userAction for: a URL (`http`/`https`, userAction: reopen a cloud notebook in OneNote), every `FilePathRefusal` (UNC, mapped drive, device, not fully qualified, invalid characters, too many links or a loop), a missing path, a `.one` file, a `.onepkg` file (userAction: `open_package`), and a folder inside a OneDrive-synced folder (`OneDriveFolders.Holds`; userAction: open it in OneNote).
- Step 2: the write lock (4.4's path); `ComBusy` right after it while an abandoned write is outstanding.
- Step 3, under the lock: one read of the open notebooks. A notebook whose `NotebookKey` or `path` (as given and made canonical) equals the canonical path → `ok` with its current ID, no `OpenHierarchy`.
- Step 4, under the lock: the OneDrive check again; the notebook test: the folder holds a `.onetoc2` and no ancestor does. An ancestor `.onetoc2` → `InvalidArgument`, userAction: it's inside another notebook's folder, open that one or open it in OneNote. No `.onetoc2` → `InvalidArgument`, userAction: a notebook just created may not have one yet, wait and try again, or open it in OneNote.
- No grant needed or given; an existing grant keyed by that path applies (AD-6). Protection follows AD-21 on the canonical path, with no new code (`Protection.UnderRoot`).
- Result: `meta.objectId`, `meta.path` (canonical), the notebook's `one:Notebook` element read after the open as `xml`, and the warning `notebookListMayLag` (named here, AD-9): its contents and OneNote's list may lag (16 s for an 11-section cloud notebook; about a minute in OneNote's list).
- A step-7 `Timeout`/`ComUnavailable`/`Cancelled`/`0x800706BE` is `outcomeUnknown`; `OpenHierarchy` already goes as `CallKind.Write` (an abandoned write: lock kept until it settles).
- Logs carry the path's kind, the refusing rule and the outcome, never the path (AD-13). Description starts "User-visible" (AD-11). Annotations: not read-only, not destructive, idempotent.

**Never:** creating anything (no `cftNotebook`/`cftFolder`/`cftSection`); a URL open; giving a grant; changing `FilePathGate`, `GrantKeys` or `Protection.RootsOf`; any spine change; setting the integration env var in the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Open ok | closed local notebook folder, or a junction to it | `OpenHierarchy` sent the canonical path; new ID, `notebookListMayLag` | — |
| Already open | path or junction of an open notebook | current ID, no `OpenHierarchy` | — |
| Refused at step 1 | URL, UNC, mapped drive, device, relative, missing, `.one`, `.onepkg`, OneDrive folder | no COM | `InvalidArgument` + userAction |
| Not a notebook | folder with no `.onetoc2` | no `OpenHierarchy`, nothing written | `InvalidArgument`, wait-and-retry userAction |
| Inside a notebook | a section group's folder (ancestor `.onetoc2`) | no `OpenHierarchy` | `InvalidArgument` |
| Abandoned write outstanding | — | nothing read under the lock | `ComBusy` |
| Open times out | `OpenHierarchy` `Timeout` | — | `outcomeUnknown`, lock held until settled |

## Decisions (Phill, 2026-11-22, Inbox plan approval)

- **Q1 (a):** opening an already-open notebook returns `ok` with its current ID and the existing warning `alreadyExisted` (nothing was opened); that warning's description is widened to cover `open_notebook`.
- **Q2:** the plan is kept whole.

</frozen-after-approval>

## Code Map

- Story 4.4's lock path in `Core/AppState/AppStatePipeline.cs` (`TakesWriteLock`) -- reuse; 4.5 is after 4.4.
- `src/OneNoteMcp.Core/Writes/FilePathGate.cs:43` `Canonical(path, out FilePathRefusal)`, test overload with `ILinkReader` (:46) -- reuse unchanged; caller pattern `PageWrite.cs:643-655`.
- `src/OneNoteMcp.Core/Access/OneDriveFolders.cs:65` `Holds`; live source `Server/OneDriveAccounts.cs:51` passed as `Func<OneDriveFolders>` (`ServerSetup.cs:235`); model `OpenPackageUseCase.cs:137-150` (checked at step 1 and under the lock).
- `src/OneNoteMcp.Core/Access/GrantKeys.cs:16` `NotebookKey`, `OpenNotebooks.Find` (`OpenNotebooks.cs:21`), `NotebookFolders.WithCanonical`/`Same` (`NotebookFolders.cs:35,73`), `PathUnder.Prefixes` (`PathUnder.cs:52`) -- the "both forms" match, as `OpenedCopies.IsOpen` does.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs:101-107`, `ComGateway.cs:390-410` `OpenHierarchyAsync` -- already `cftNone`, `CallKind.Write`, never retried; reuse unchanged.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- add `NotebookListMayLag`; also in `Contract/tool-contract.json`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs:288-299` `OpenHierarchyCalls`/`OpenHierarchyFailure` -- assert none recorded on refusals and the already-open path.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `CloseNotebookAsync` (:558, `CheckClose`), `CheckOpen` (:1485: run probe folder, or b/c's recorded folder), `CreateThrowawayNotebookAsync` (:478), `AppStateDir` (:550); `CreateHierarchyIntegrationTests.cs:98-130` (create, close, `ListGrantsUseCase`); `tests/OneNoteMcp.Tests/TestSettings.cs:9` `SetExportRoots`; `GuardedGateway` pattern (`MergeSectionsIntegrationTests.cs:433`).
- Contract and packaging: `ToolContractTests.cs:81`, `McpSdkBindingTests.cs`, `PluginPackagingTests`, `plugin/skills/guide/SKILL.md`, `README.md`, `spikes/stress-harness/Calls.cs`.

## Tasks & Acceptance

**Execution:**
- [ ] `Core/UseCases/OpenNotebookUseCase.cs` -- the operation (`TakesWriteLock`): step 1, steps 3–4, step 7, the post-open read; injectable file-system probe (exists, `.onetoc2` lookup up the ancestors) for unit tests.
- [ ] `Server/Tools/OpenNotebookTool.cs`, `ServerSetup.cs` -- the tool and wiring (`OneDriveAccounts`).
- [ ] `ToolWarnings.cs` -- `NotebookListMayLag`.
- [ ] Unit tests `OpenNotebookToolTests` -- every matrix row; the junction case through `ILinkReader`; no `OpenHierarchy` and no file written on every refusal; already-open matched by `NotebookKey` and by a `\\localhost\X$` path.
- [ ] Contract snapshot, tool list, binding count; skill, README and stress-harness rows.
- [ ] `tests/OneNoteMcp.Integration/OpenNotebookIntegrationTests.cs` over a `GuardedGateway` (`CheckOpen`, `CheckClose`): close throwaway b, open it by path (new ID; `list_grants` shows its test grant `active`), close it, open it through a junction made in the run's probe folder; an open notebook returns its ID; a URL, missing path, `.onepkg`, a OneDrive folder, a section group's folder of a closed run notebook and a folder with no `.onetoc2` are refused and nothing appears in them; with the run's probe folder set as the test export root, a notebook made there (waiting up to 5 min for its `.onetoc2`), closed and opened reads `ReadNotGranted` until granted; closed and deleted at the end.

**Acceptance Criteria:**
- Given a folder with no `.onetoc2`, when `open_notebook` is called, then the folder's file list is unchanged afterwards.

## Design Notes

- The test export root is the run's own probe folder, so `CheckOpen` and `CheckClose` already allow its notebooks; no guard change.
- Considered refusing a OneDrive folder with `ExportPathNotAllowed` as `open_package` does; going with `InvalidArgument` because the spine names it.
- The already-open answer carries `alreadyExisted` (Decision Q1), with its description widened to `open_notebook`.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- Non-hitl: the integration test runs at the next batched live session (it closes and opens throwaway b). Reopening a cloud notebook is entry 10's.

## Implementation Notes

## Plan Change Log

## Review Triage Log
