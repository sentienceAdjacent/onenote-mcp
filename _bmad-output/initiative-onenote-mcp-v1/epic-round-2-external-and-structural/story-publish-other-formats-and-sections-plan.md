---
title: 'publish: other formats and sections'
type: 'feature'
ticket: '13'
created: '2026-11-16'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
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

</frozen-after-approval>

## Open Questions

1. **Wire names and extensions** (public contract, AD-14). (a) `format` values `pdf`, `xps`, `docx`, `mhtml`, `html`, `emf`, `one`, `onepkg`, default `pdf`; `path` must end in that format's extension, accepting `.mht`/`.mhtml` and `.htm`/`.html`, else `InvalidArgument`. (b) Drop `format` and infer it from the extension: shorter calls, but a breaking change to 3.3's argument. (c) As (a) with one extension per format only. **Recommended: (a).**
2. **A section's EMF** (spine Deferred: "3.13 checks it before offering EMF for a section, or `publish` warns"). (a) Offer it with a warning `emfMayHoldOnePage` on every section EMF; the sitting checks a multi-page section's EMF, and if it holds every page a follow-up drops the warning. (b) Refuse EMF for a section (`NotSupported`) until a sitting proves it whole. (c) Offer it with no warning. **Recommended: (a)**, the spine's own fallback, no lost scope.
3. **HTML's companion folder.** X1's probe pages held no images, so "one file each" is unproven for HTML with images; OneMore puts every HTML export in its own folder (`Archivist.ExportHTML`), which suggests extra files (inferred). (a) Offer HTML; for `html`, `ExportTarget` also treats an existing `<stem>_files` beside the target as `FileExists`; the description says images may land in a folder beside the file; the sitting exports a page with an image and records what lands. (b) Leave HTML out until the sitting settles it (MHTML is single-file). **Recommended: (a).**

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
- [ ] `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- first, red: each matrix row on `FakeGateway`, as a theory over formats for page and section; extension aliases; a raw `ExportFormat` value outside the enum; the section warnings (Q2); for `html`, an existing `<stem>_files` is `FileExists` (Q3).
- [ ] `ExportFormat.cs`, `ComGateway.cs`, `ComGatewayTests.cs` -- the members and the map; `(ExportFormat)8` still refused before COM.
- [ ] `ExportTarget.cs` -- per-format extensions (Q1) and the HTML companion check (Q3).
- [ ] `PublishUseCase.cs` -- section scope and formats, in the Always order.
- [ ] `ToolWarnings.cs`, `ToolArguments.cs`, `PublishTool.cs`, `tool-contract.json` -- wire enum, description (formats by scope, section groups and notebooks refused, the Q2/Q3 caveats), snapshot.
- [ ] `PublishIntegrationTests.cs` -- a theory: the test page and its section in every format, each into its own empty subfolder of the temp root, asserting the subfolder then holds only the target (HTML: also at most `<stem>_files`) and logging sizes; the locked Ballast Vault section (not a page) refused `ReadNotGranted` without a grant and `SectionLocked` with one, after the pause.
- [ ] `_bmad-output/.../research-onemore-lessons.md` -- a row: OneMore's per-page HTML folder.

**Acceptance Criteria:**
- Given 3.3's existing publish tests, when they run, then they pass with only the format argument added where needed.
- Given `dotnet test tests/OneNoteMcp.Integration` with no integration variables, then it builds and every live test skips.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill, later; never by the build; MCP Test and throwaways only):**
- S1: MCP Test open, Ballast Vault unlocked. `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~PublishIntegrationTests`; Phill presses Ctrl+Alt+L at the pause. Expected: all pass, only temp files.
- S2: with the dev build (`onenote-dev`), export a multi-page MCP Test section as EMF; Phill opens it (for example inserted into Word) and says how many pages it shows (Q2). Record it in the spine's Deferred row through Phill.
- S3: export an MCP Test page holding an image as HTML; record what lands beside the `.htm` (Q3). Then each format of one page from Claude Code; Phill opens a few and deletes them.
