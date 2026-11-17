---
title: 'publish: other formats and sections'
type: 'feature'
ticket: '13'
created: '2026-11-16'
status: 'done'
baseline_revision: '1b0384eef6b5c94ca41dcf4eb7a2242e36de31c6'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-tracer-publish-a-page-to-pdf-under-an-export-root-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `publish` (3.3) exports only a page, only as PDF. Spike 3.1 X1 proved seven more formats for a page and a section, and AD-7/AD-11 already allow them.

**Approach:** Extend 3.3's `PublishUseCase`, `ExportTarget` and `PublishTool` in place: a section scope beside the page scope, and every AD-7 "Formats by scope" format (PDF, XPS, Word, MHTML, HTML, EMF, OneNote `.one`, OneNote package `.onepkg`). Same pipeline, same landing wait, same E8 rule.

## Boundaries & Constraints

**Always:**
- Step 1 order: empty `objectId` (`InvalidArgument`); a format value outside the enum (`NotSupported`); the walk to the owning section (a page). Only when it finds none, the `self` read: a section is the target itself; a section group is `NotSupported` with Q7's recipe ("export its sections one by one"); a notebook is `NotSupported` ("notebooks export as a package in a later release", entry 7); anything else `ObjectNotFound`. Then `EnsureWritable`, `ExportTarget.Resolve(path, roots, format)`, then the read grant held at the call's start for an `encrypted` section, never prompted (AD-21 Exports).
- Under the lock: re-read the section (a page re-walks, a section re-reads `self`); protected check before the locked check (`SectionLocked`); path resolved again and identical; folder created.
- Step 7: `Publish(format)` then `PublishedFile.WaitAsync`, so a `.onepkg` that lands 250–530 ms late, or appears empty first, counts only once stable (X2). A wait that runs out is `Timeout`, `outcomeUnknown`, naming the path (X3, AD-11).
- Section and page exports in every format, `.onepkg` included, keep Q1: read grant plus `exportNotEncrypted` (H1 relaxes only notebook packages). The warning names the format.
- Messages name IDs and the caller's own path, never names or content.

**Never:**
- No `pfOneNote2007`: no Core or wire member; Com's map refuses the raw value before COM.
- No notebook scope (entry 7), no `open_package`, no path-based protection rule (3.8), no skill or README text (entry 10), no overwrite argument, no write grant, no dialog.
- Don't fork the pipeline, the landing wait or `ExportTarget`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Page, each format | MCP Test page, new file with the format's extension | one file at `meta.outputPath` | none |
| Section, each format | MCP Test section | one file at `meta.outputPath` | none |
| Late package | `.onepkg` absent or empty at return, lands later | waits; ok | none |
| Never lands | file absent or growing past `publishSeconds` | path named | `Timeout`, `outcomeUnknown` |
| Extension mismatch | `format: docx`, path `x.pdf` | nothing written | `InvalidArgument` |
| Unlisted format | raw `(ExportFormat)99` or `8` at the gateway | before COM | `NotSupported` |
| Section group / notebook | their IDs | before COM | `NotSupported` |
| Protected section or its page, no grant | locked or not | before COM | `ReadNotGranted` |
| Protected, granted, locked | — | before COM | `SectionLocked` |
| Protected, granted, unlocked | — | file written | ok + `exportNotEncrypted` |
| Existing target | file or folder at the path (step 1 or under the lock) | untouched | `FileExists` |

## Decisions (answered overnight with the recommendation, per Phill's standing overnight rule (2026-11-17); Phill reviews in the morning)

- **Q1, a. Wire names and extensions** (public contract, AD-14): `format` is `pdf`, `xps`, `docx`, `mhtml`, `html`, `emf`, `one` or `onepkg`, default `pdf`; `path` must end in that format's extension, `.mht`/`.mhtml` and `.htm`/`.html` both accepted, else `InvalidArgument`. Rejected: inferring the format from the extension (b, breaks 3.3's argument); one extension per format (c).
- **Q2, a. A section's EMF** (spine Deferred row): offered, with the warning `emfMayHoldOnePage` on every section EMF; sitting S2 checks a multi-page section, and if it holds every page a follow-up drops the warning. Rejected: refusing it (b); no warning (c).
  - **Superseded by Phill, 2026-11-17, after sitting S2:** a 4-page section's EMF showed only its first page, so `publish` refuses a section as EMF with `NotSupported` before any write, its `userAction` saying to export each page as EMF or the section as PDF. A page as EMF is unchanged; the `emfMayHoldOnePage` warning is gone.
- **Q3, a. HTML's companion folder:** X1's probe pages held no images, and OneMore puts each HTML export in its own folder (`Archivist.ExportHTML`), which suggests extra files (inferred). HTML is offered; for `html`, an existing `<stem>_files` beside the target is also `FileExists`; the description says images may land in a folder beside the file; sitting S3 records what lands. Rejected: leaving HTML out until a sitting (b).

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/ExportFormat.cs` -- add `Xps, Word, Mhtml, Html, Emf, OneNotePackage`; never a 2007 member.
- `src/OneNoteMcp.Com/ComGateway.cs:522` `ToPublishFormat` -- map each to `pf*` (`Interop/Enums.cs:55`); default arm stays `null` → `NotSupported`.
- `src/OneNoteMcp.Core/Writes/ExportTarget.cs` -- `Resolve(raw, roots)` hard-codes `PdfExtension` (l.56, l.89) and its messages say `.pdf`: take the format, check its extension(s) on the raw and canonical path.
- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- `PublishPage` becomes one operation over a target that is a page or a section: l.130 PDF-only gate goes; `NotInASectionAsync` (l.228) returns the section node instead of refusing it; `ReadAsync` re-reads by kind; l.209 passes `format`; warning text by format.
- `src/OneNoteMcp.Core/Access/ReadAccess.cs` -- `OwningSectionAsync`, `HoldsReadGrant`; a section's `self` read carries `Encrypted`/`Locked`/`Path` (`HierarchyNode`).
- `src/OneNoteMcp.Core/Writes/PublishedFile.cs` -- reuse unchanged.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- one new warning (Q2).
- `src/OneNoteMcp.Server/Tools/ToolArguments.cs:61` `PublishFormatArgument` + `ToCore`; `Tools/PublishTool.cs` -- description and parameter text.
- `tests/OneNoteMcp.Tests/FakeGateway.cs:361` -- records `Format`; it already hides a locked section's pages (3.3 S1).
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (l.9958, `format.enum`) -- regenerate. `<Version>` stays `0.9.0-rc.1` (unreleased since `v0.8.0-rc.2`).
- `tests/OneNoteMcp.Integration/PublishIntegrationTests.cs` -- `StartAsync`, `GuardedGateway`, the Vault test and `PauseAsync` to extend.
- Parallel lane 3.4 also edits `tool-contract.json` and tool-count tests: rebase on whichever lands first and regenerate the snapshot rather than merge it by hand.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- first, red: each matrix row on `FakeGateway`, as a theory over formats for page and section; extension aliases; a raw `ExportFormat` value outside the enum; the section warnings (Q2); for `html`, an existing `<stem>_files` is `FileExists` (Q3).
- [x] `ExportFormat.cs`, `ComGateway.cs`, `ComGatewayTests.cs` -- the members and the map; `(ExportFormat)8` still refused before COM.
- [x] `ExportTarget.cs` -- per-format extensions (Q1) and the HTML companion check (Q3).
- [x] `PublishUseCase.cs` -- section scope and formats, in the Always order.
- [x] `ToolWarnings.cs`, `ToolArguments.cs`, `PublishTool.cs`, `tool-contract.json` -- wire enum, description (formats by scope, section groups and notebooks refused, the Q2/Q3 caveats), snapshot.
- [x] `PublishIntegrationTests.cs` -- a theory: the test page and its section in every format, each into its own empty subfolder of the temp root, asserting the subfolder then holds only the target (HTML: also at most `<stem>_files`) and logging sizes; the locked Ballast Vault section (not a page) refused `ReadNotGranted` without a grant and `SectionLocked` with one, after the pause.
- [x] `_bmad-output/.../research-onemore-lessons.md` -- a row: OneMore's per-page HTML folder.

**Acceptance Criteria:**
- Given 3.3's existing publish tests, when they run, then they pass with only the format argument added where needed.
- Given `dotnet test tests/OneNoteMcp.Integration` with no integration variables, then it builds and every live test skips.

## Implementation Notes

- Implemented directly in this lane (a build lane already running as a subagent), not through a further subagent. The new unit tests were written before the code, but not run red on their own first.
- `PublishPage` became `PublishTarget`; a section is found by the `self` read only after the walk finds no owning section, so 3.3's walk-first order stands. Under the lock a section is re-read at `self` (its ID survives a move).
- `ExportTarget.Resolve` takes the format; `ExportTarget.Extensions(format)` and `FilesFolder(target)` are public for the use case and tests. `PagesOnlyUserAction` now reads "a page's or a section's ID"; new `GroupUserAction`, `NotebookUserAction`, `EmfOnePageUserAction` (since replaced by `SectionEmfUserAction`, after sitting S2).
- Tests touched beyond the plan's list: `ComGatewayTests` (`(ExportFormat)2` is now XPS, so the unknown-value row uses 99; a test that the enum has eight members, none named 2007), and 3.3's link test now expects "doesn't end in .pdf". The contract snapshot changed only in `publish.format.enum`; tool count and property counts are unchanged.

## Plan Change Log

- 2026-11-17, after the sitting: Q2 changed by Phill from "offer a section's EMF with a warning" to "refuse it" (see Q2). Code: the section-plus-EMF refusal in `PublishUseCase.AuthorizeAsync`, right after the target is found to be a section; `ToolWarnings.EmfMayHoldOnePage` removed; the tool description says so. The contract snapshot is unchanged (it strips descriptions).

## Review Triage Log

- 2026-11-17, quick lens, iteration 0: 0 high, 0 medium, 6 low, 2 false, 0 maybe-false. Four were patched, four rejected; nothing deferred, no loopback.
  - **false.** "The frozen block was edited (Open Questions became Decisions) without Phill's answer." Phill's standing overnight rule (2026-11-17, relayed by the coordinating session) says to answer every open question with its recommendation and record it so; step 2 writes answers into the frozen block. Phill reviews them in the morning.
  - **low, rejected.** "3.3's tests changed beyond the format argument (AC 1)." The section scope retires `AnythingButAPage_IsNotSupported` and the PDF-only gate's test by design, and the link test's message follows the per-format wording; the fix would be to edit this plan's AC, which triage can't do.
  - **low, patch.** The notebook refusal didn't say notebooks come as packages later (Always rule). Fix: the message, `NotebookUserAction` and the tool description say so.
  - **low, patch.** No test that a section's `Timeout` names the section, its format and the path. Fix: `ASectionExportThatNeverLands_IsTimeout_NamingTheSectionTheFormatAndThePath`.
  - **low, patch.** The HTML `_files` check had no test under the lock. Fix: `AnHtmlFilesFolderThatAppearsUnderTheLock_IsFileExists`.
  - **low, rejected.** No protected-page test in a non-PDF format, and no extension-mismatch test for a section: both go through the same format-agnostic code the section `docx` and page mismatch tests cover.
  - **false.** "The Timeout wording names a section as a page": `What` comes from `isSection`, now shown by the new section test.
  - **low, patch.** Stale comments: `IOneNoteGateway.PublishAsync` named 3.3's PDF as the only `publish` caller; `PagesOnlyUserAction` renamed `ObjectIdUserAction`.
- 2026-11-17, sitting with Phill (S1 to S3): S2 found that a section's EMF holds only its first page. **Decision (Phill, Inbox):** refuse a section as EMF with `NotSupported` before any write; patched test-first (`ASectionAsEmf_IsNotSupported_BeforeCom`, red then green), the warning removed.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill, later; never by the build; MCP Test and throwaways only):**
- S1: MCP Test open, Ballast Vault unlocked. `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~PublishIntegrationTests`; Phill presses Ctrl+Alt+L at the pause. Expected: all pass, only temp files.
- S2: with the dev build (`onenote-dev`), export a multi-page MCP Test section as EMF; Phill opens it (for example inserted into Word) and says how many pages it shows (Q2). Record it in the spine's Deferred row through Phill.
- S3: export an MCP Test page holding an image as HTML; record what lands beside the `.htm` (Q3). Then each format of one page from Claude Code; Phill opens a few and deletes them.

**Sitting status (2026-11-17, with Phill): done.** S1: 10 of 10 `PublishIntegrationTests` passed. S2: a 4-page section (T1, S2 parent, T2, T3) as EMF showed only the first page (T1) in Paint; recorded in the spine's Deferred row, and Phill decided to refuse a section as EMF (Q2, Plan Change Log). S3: an HTML export of a page with an image left a `<name>_files` folder holding `image001.png` and `filelist.xml` beside the `.htm`; all seven page formats exported.
