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

**Problem:** A OneNote package (`.onepkg`, which `publish` now makes of a notebook) can't be opened through the server. Spike 3.1 (O1–O5, Implication 10) found that `OpenPackage` unpacks into the folder it's given and opens nothing, so a separate `OpenHierarchy` is needed. An opened copy carries no trustworthy protection marker, so AD-21 says every section of it counts as protected by its path (Q11). That path rule isn't built yet: today protection is the `encrypted` marker only.

**Approach:** Build AD-21's path rule ("What's protected": a section under an export root, or under the journal's `backups` folder, or in a notebook whose path is, counts as protected whatever its marker) for every read check and every protection-gated write. Then add the `open_package` tool as an `ExternalWrite` (AD-5 promoted task, AD-7): unpack into a new folder under an export root, open it with `OpenHierarchy`, and return the folder and the notebook.

## Boundaries & Constraints

**Always:**
- The path rule is one Core function used by every place that decides "protected" today (marker-based): target and broad read checks, `get_hierarchy`, search, the grant CLI listing, and the writes that gate on protection (moves, page-level, soft and permanent delete, create, restore, publish). The comparison uses AD-6's path transform (`NotebookKey`/`SectionKey` rules), on canonical paths, and is case-insensitive.
- `open_package` step 1: the package is an existing `.onepkg` file in any folder (Q6; which kinds of path, Open Question 3); the destination (Open Question 2) resolves under an export root like `publish`'s target and must not exist (`FileExists`). Under the lock the destination is resolved again and must still be absent; the pipeline then creates it (AD-7), and nothing else is written before step 7.
- Step 7: `OpenPackage(package, destination)`, then `OpenHierarchy(pathOut)` (no create, no grant given), then wait up to 5 s for the first page listing; still empty is the warning `pagesNotListed`. A failure after the unpack returns `meta.completedSteps` and names the folder (AD-11). Never retried.
- The result: `meta.outputPath` (the folder) and `meta.objectId` (the opened notebook's ID). The notebook gets no write grant (Q6) and every section in it counts as protected (Q11). The user closes it in OneNote (Q13); the description says so.
- Messages name IDs and the caller's own paths, never notebook or section names from the package.

**Never:**
- No `MergeFiles` (ends ONENOTE.EXE), no `CloseNotebook` tool (epic 4), no automatic grant of any kind, no `NavigateTo`, no overwrite or merge into an existing folder.
- The integration tests close only the notebooks they opened, through the test harness's own COM seam, and delete only their own folders.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Open a package | throwaway's `.onepkg`, new folder under the root | folder made, notebook opened; `meta.outputPath`, `meta.objectId` | none |
| Pages list late | 0 pages for 5 s | ok + `pagesNotListed` | none |
| Destination outside every root | `..\x` or another drive | nothing done | `ExportPathNotAllowed` |
| Destination exists | file or folder there | untouched | `FileExists` |
| Package missing or not `.onepkg` | path | nothing done | `InvalidArgument` |
| Network or device package path | `\\server\x.onepkg` | per Open Question 3 | `InvalidArgument` if refused |
| Open fails after the unpack | `OpenHierarchy` error | folder kept | mapped code + `meta.completedSteps` |
| Read a page of the opened notebook | no read grant | refused | `ReadNotGranted` (prompt as for any protected section) |
| Section under the backups folder or an export root, no marker | any read tool | treated as protected | `ReadNotGranted` / withheld |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Xml/HierarchyNode.cs`, `EncryptedSections.cs` (`Find`, l.31) -- the marker reads (section-only `Encrypted`). Keep them; the path rule sits in Core on top.
- `src/OneNoteMcp.Core/Access/ReadAccess.cs` -- `CheckObjectAsync` (l.87), `CheckSectionAsync` (l.98), `HoldsReadGrant` (l.129), `CheckBroad` (l.180), `CheckSearchScopeAsync` (l.217): the read funnel. `ProtectedSections.Find` (grant CLI) and `GetHierarchyUseCase` (l.57) call `EncryptedSections.Find` directly.
- Protection-gated writes reading `.Encrypted`: `Writes/MovePageWrite.cs`, `MoveContainerWrite.cs` (l.419), `SetPageLevelWrite.cs`, `UseCases/SoftDeleteUseCase.cs`, `PermanentDeleteUseCase.cs`, `CreatePageUseCase.cs`, `RestoreFromJournalUseCase.cs`, `PublishUseCase.cs`, `Journal/HierarchySnapshot.cs`, `PageSnapshot.cs` (grep `\.Encrypted\b`).
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs` -- `ExportRoots`, journal path; `Journal/JournalFiles.cs:58` `BackupsFolderName`. `Access/GrantKeys.cs` -- AD-6's path transform to reuse.
- `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- `Resolve` (l.63) is file-and-extension shaped; add a folder variant beside it, sharing the root and canonical-path checks.
- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- `OpenPackage` (slot 21, l.106) and `OpenHierarchy` (l.32) are declared; `ComGateway.CreateNotebookAsync` (l.341) is the `OpenHierarchy` pattern. Add `OpenPackageAsync` and an open-existing `OpenHierarchyAsync` (`cftNone`) to `IOneNoteGateway`, `ComGateway`, `FakeGateway` and the integration test gateways.
- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- the `ExternalWrite` shape to follow; `Server/Tools/PublishTool.cs` and `ToolArguments.cs` for the tool; `tests/.../Contract/tool-contract.json` regenerated (a new tool).
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `OpenPackageAsync` (l.377), `AdoptThrowawayNotebookAsync`, `Round2.LocalThrowawayName`; add a guarded `CloseNotebook` for notebooks the test opened (never used by tests so far).
- Prior art: OneMore calls neither `OpenPackage` nor `MergeFiles` (research note row 18).

## Tasks & Acceptance

**Execution:**
- [ ] `tests/OneNoteMcp.Tests/` -- first, red: the path rule (a section under each kind of root, a notebook under one, case and separator variants, a look-alike prefix such as `exports2` not matching) across `get_hierarchy`, `get_page_content`, `find_pages` and one gated write; each matrix row for `open_package` on `FakeGateway`.
- [ ] `Core/Access/` -- one `ProtectedByPath` rule and its use in every site above.
- [ ] `IOneNoteGateway`, `ComGateway`, `FakeGateway` -- `OpenPackageAsync`, `OpenHierarchyAsync` (existing path only).
- [ ] `ExportTarget.cs` -- the folder variant.
- [ ] `UseCases/OpenPackageUseCase.cs`, `Tools/OpenPackageTool.cs`, `ToolWarnings.cs` (`pagesNotListed`), registration, `tool-contract.json`.
- [ ] `tests/OneNoteMcp.Integration/OpenPackageIntegrationTests.cs` -- publish the throwaway as a package into the temp root, open it, read a page of it (`ReadNotGranted`), a CLI read grant then allows it, close the opened notebook through the seam, delete the folders.

**Acceptance Criteria:**
- Given the existing read and write tests, when they run, then they pass unchanged except where a fixture section sits under an export root or the backups folder.
- Given `dotnet test tests/OneNoteMcp.Integration` with no integration variables, then it builds and every live test skips.

## Open Questions

1. **Split the path rule out?** (a) One story: the path rule and `open_package` together, as the ticket says. (b) Two: first "protected by path" (AD-21's rule for every read and gated write; it protects hand-opened backups and exports today), then `open_package` on top. The rule touches about 18 files and every read tool; the tool is self-contained. Recommended: b.
2. **The destination argument.** (a) Optional `destination`, defaulting to a folder named after the package (its file name without `.onepkg`) under the first export root. (b) Required. Recommended: a.
3. **Which package paths.** Q6 says a package may come from any folder. (a) Local, fully qualified paths to an existing `.onepkg` only; network and device paths refused, as for `publish`'s target. (b) Network paths too. Recommended: a.

## Live probes needed (sitting, Phill present; MCP Test and throwaways only)

- P1: open a package of a notebook that is still open in OneNote (the throwaway): does `OpenHierarchy` of the copy work, or clash with the original (same notebook identity)? O1–O5's packages came from notebooks that may have been open; the record doesn't say.
- P2: `CloseNotebook` of a notebook the test opened: focus, dialogs, time, and that the original stays open. No test has closed a notebook yet.
- P3: the opened copy's sections and `path` under the export root as `get_hierarchy` reports them, so the path rule matches what OneNote returns (O5 saw `encrypted` and `locked` for protected sections only).

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill, later; MCP Test and the throwaway only):**
- S1: the probes P1–P3, then `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~OpenPackageIntegrationTests` with the throwaway open. Expected: all pass, the opened copy closed, only temp folders.
- S2: with `onenote-dev`, open a package from Claude Code, then read one of its pages: the grant dialog appears; Phill denies once (refused) and allows once (read). Phill closes the copy in OneNote.
