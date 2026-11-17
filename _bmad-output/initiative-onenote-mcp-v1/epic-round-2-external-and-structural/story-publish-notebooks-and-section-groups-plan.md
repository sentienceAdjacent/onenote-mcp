---
title: 'publish: notebooks and section groups'
type: 'feature'
ticket: '7'
created: '2026-11-17'
status: 'in-review'
baseline_revision: '935ed20515257f633dd97caee2945f59f6dfec41'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-publish-other-formats-and-sections-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `publish` (3.3, 3.13) refuses a notebook with "a later release". Spike 3.1 X1/X2 proved a notebook exports only as a `.onepkg`, local and cloud, landing 250–530 ms after `Publish` returns; XP1/XP2 proved a package keeps protected sections encrypted, locked or not. AD-7, AD-11 and AD-21 (H1) already allow it.

**Approach:** Extend 3.13's `PublishTarget` in place with a third kind, the notebook: package only, no read grant, same pipeline, same landing wait, same `ExportTarget`. The section-group refusal (Q7) stays as it is and gets its integration check.

## Boundaries & Constraints

**Always:**
- Step 1 order unchanged: empty `objectId`; format outside the enum; the walk; only with no owning section, the `self` read. A notebook with `format` other than `onepkg` (the `pdf` default included) is `NotSupported` before COM, its `userAction` saying to pass `format: onepkg`, or to export its sections one by one (spine AD-11 l.464). A section group stays `NotSupported` (Q7). Then `EnsureWritable` and `ExportTarget.Resolve`.
- A notebook package needs no read grant and gets no `exportNotEncrypted` (AD-21 Exports, H1), whatever its sections are: protected, locked, or protected by path (opened packages and backups keep protected content encrypted or never wrote it).
- Under the lock: re-read at `self`; still a notebook, else `ObjectNotFound`. No locked check for a notebook: XP2 packaged a locked section with no error. Path resolved again and identical; folder created.
- Step 7: `Publish(OneNotePackage)` then `PublishedFile.WaitAsync` within `timeouts.publishSeconds` (default 300 s); a wait that runs out is `Timeout`, `outcomeUnknown`, naming the path.
- Messages name IDs and the caller's own path, never names or content.

**Never:**
- No other notebook format, even PDF/XPS/EMF that worked once on cloud (spine Deferred row; needs a local probe first).
- No `pfOneNote2007` and no `MergeFiles` (both end ONENOTE.EXE); no `open_package` (entry 8); no skill or README text (entry 10); no new warning, argument or error code; no contract change beyond descriptions.
- Don't fork the pipeline, the landing wait or `ExportTarget`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Notebook package | MCP Test, `format: onepkg`, new `.onepkg` | one file at `meta.outputPath`, no warning | none |
| Notebook with protected sections | locked or unlocked, no read grant | package written, no warning | none |
| Notebook, any other format | `pdf` (default), `xps`, `docx`, `mhtml`, `html`, `emf`, `one` | nothing written, no COM | `NotSupported`, userAction names `onepkg` |
| Section group | its ID, any format | before COM | `NotSupported` (unchanged) |
| Late or empty-first package | absent or 0 bytes at return | waits; ok | none |
| Never lands | past `publishSeconds` | path named | `Timeout`, `outcomeUnknown` |
| Notebook closed under the lock | `self` fails or isn't a notebook | nothing written | `ObjectNotFound` |
| Extension mismatch | `onepkg`, path `x.pdf` | nothing written | `InvalidArgument` |
| Existing target | file at the path | untouched | `FileExists` |

## Decisions (Phill, Inbox, 2026-11-17, approving the plan)

- **Q1, a. A notebook with no format, or any format but `onepkg`:** refused `NotSupported` before COM; the message and `userAction` say to ask for a OneNote package (`format: onepkg`). The contract stays as it is. Rejected: making `format` optional (b).
- **Q2, a, as Phill's explicit exception to the test-notebooks-only rule:** sitting S3 exports ONE of Phill's real notebooks as a package with the dev build. Only its size and time are recorded; its name and ID never go into any file, commit, log or plan, and the file is deleted right after. Rejected: test notebooks only (b).
- **Q3, a. Entry 8's package:** 3.7's test deletes its package with its temp folder; entry 8's tests make their own through `publish`. Rejected: a package left at a fixed path (b).
- Full plan kept (about 2,100 tokens), no split.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- `TheSectionItselfAsync` (l.~295) refuses `HierarchyKind.Notebook`: return it instead, after the format check. Replace `bool isSection` with a target kind (page, section, notebook) used by `What`, `ReadAsync`, `Validate` and the warning text. `AuthorizeAsync` skips `CheckReadGrant` for a notebook; `Validate` skips `CheckReadGrant` and the `Locked` check for one. Rewrite `NotebookUserAction` (now "a later release") and the class summary. Section EMF refusal stays section-only.
- `src/OneNoteMcp.Xml/HierarchyNode.cs` -- `HierarchyNode.Root` already parses a `one:Notebook` (`HierarchyKind.Notebook`); reuse.
- `src/OneNoteMcp.Core/Writes/ExportTarget.cs`, `PublishedFile.cs`, `Com/ComGateway.cs` `PublishAsync`/`ToPublishFormat` -- unchanged.
- `src/OneNoteMcp.Server/Tools/PublishTool.cs` -- description: notebooks as `onepkg` only, no read grant, protected sections stay encrypted and open locked; `objectId` text "page, section or notebook".
- `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- notebook `W` (l.28, `Self` at l.60) is already in the fixture; `ANotebook_IsNotSupported_InThisRelease` (l.655) is replaced. Add a protected section to `W` for the no-grant case (helper at l.771).
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- regenerate; expected unchanged (it strips descriptions). `<Version>` stays.
- `tests/OneNoteMcp.Integration/PublishIntegrationTests.cs` -- `GuardedGateway.PublishAsync` (l.312) calls `TestNotebook.EnsureTestTargetAsync`, which takes no throwaways; the Vault test (l.81) and `PauseAsync` (l.227) to reuse.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `EnsureTargetsAsync` (l.498) is private; add a public `EnsureTestTargetAsync` overload taking the throwaway map. `AdoptThrowawayNotebookAsync` (l.347) and `MoveHierarchyObjectIntegrationTests.ThrowawayName` (`MCP Test Throwaway 1112-034716 a`) show the adoption.
- OneMore publishes pages only (research note row 17), so there is no prior art for packages.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- first, red: each matrix row on `FakeGateway`; a theory over the seven non-package formats for a notebook, each `NotSupported` with zero `PublishCalls`; a notebook holding a locked protected section with no grant exports with no warning; the notebook closed under the lock.
- [x] `PublishUseCase.cs` -- the notebook kind, in the Always order.
- [x] `PublishTool.cs`, `tool-contract.json` -- description and snapshot.
- [x] `TestNotebook.cs` -- the public guard overload with throwaways.
- [x] `PublishIntegrationTests.cs` -- (1) MCP Test as a package into its own empty subfolder, with the Vault locked (pause if it isn't) and no read grant: ok, the subfolder holds only the `.onepkg`, size and seconds logged; then `pdf` for MCP Test refused `NotSupported` with no `Publish` call. (2) The adopted throwaway (skip if not open) as a package, no refusal, and asserted to hold no `encrypted` section. (3) A section group of MCP Test refused `NotSupported` with no `Publish` call (skip if none).

**Acceptance Criteria:**
- Given 3.3's and 3.13's publish tests, when they run, then they pass, with only the notebook-refusal test replaced.
- Given `dotnet test tests/OneNoteMcp.Integration` with no integration variables, then it builds and every live test skips.

## Design Notes

- The path rule (AD-21 l.680: sections under an export root or the backup folder count as protected) needs no exception here: H1 removes the read grant for every notebook package, and a package copies nothing out of protection.
- Considered a package scan for the Vault's text (as spike XP1/XP2 did with `expand.exe`); left out: four spike runs settle it and H1 is approved.

## Implementation Notes

- Implemented directly in this lane (the coordinating session asked for no implementation subagents). Test-first: the 14 new unit tests (8 facts plus a 7-row theory replacing `ANotebook_IsNotSupported_InThisRelease`) were run red before any code (14 failed, 90 passed), then green. The integration tests and the `TestNotebook` overload came after the code; they can't run red here (no OneNote).
- `PublishUseCase`: `bool isSection` became a private `TargetKind` (page, section, notebook); `TheSectionItselfAsync` became `TheTargetItselfAsync`, returning a notebook too. A notebook with any format but `OneNotePackage` is refused right after the `self` read, before the section-EMF check and before `EnsureWritable`/`Resolve`. A notebook skips `CheckReadGrant` in step 1 and both `CheckReadGrant` and the `Locked` check under the lock. `NotebookUserAction` now names `format onepkg`; `ObjectIdUserAction` names a notebook's ID.
- `PublishTool` description: notebooks as `onepkg` only (the `pdf` default included in the refusal), no read grant, protected sections stay encrypted and open locked. The contract snapshot is unchanged (descriptions stripped): the full unit suite passed against it.
- Integration: `GuardedGateway.Throwaways` plus the new `TestNotebook.EnsureTestTargetAsync` overload let `Publish` target the adopted throwaway. Three new facts: MCP Test as a package with the Vault locked and no grant (pauses to lock it if needed; then `pdf` refused), the throwaway as a package (skips when it isn't open; asserts no `encrypted` section), and a section group refused before COM (the recycle bin's when MCP Test has no other).

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting steps (main session with Phill, later; never by the build; MCP Test and throwaways only, except S3 by Phill's exception):**
- S1: MCP Test and `MCP Test Throwaway 1112-034716 a` open, Ballast Vault unlocked. `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~PublishIntegrationTests`; Phill presses Ctrl+Alt+L at the pause. Expected: all pass, only temp files; record the MCP Test package's size and seconds.
- S2: with `onenote-dev`, export MCP Test Cloud as a package from Claude Code; Phill confirms the file under the export root and deletes it.
- S3 (Phill's explicit exception, Q2): Phill picks ONE larger notebook of his own and exports it as a package with `onenote-dev`, to a path under the export root. Record only the file's size and the call's seconds; never the notebook's name or ID, in any file, commit, log or plan (the tool's result carries the ID: don't copy it). Phill deletes the file right after.
