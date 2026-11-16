---
title: 'Tracer: publish a page to PDF under an export root'
type: 'feature'
ticket: '3'
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
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/spike-write-probes-part-3-exports-move-merge-and-recycle-bin-restore-findings.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can't export anything from OneNote. Epic 3's tracer proves the `ExternalWrite` class end to end: export roots, the read-grant rule for protected content, and a file that is really on disk.

**Approach:** One `publish` tool, for a page as PDF only, as one `ExternalWrite` operation through the existing `WritePipeline`. It reuses 5.6's `Publish` port method (plus a format), 2.9's path canonicaliser, and 5.6's file-landing wait. Entries 13, 7 and 8 extend it.

## Boundaries & Constraints

**Always:** Run AD-7's order. Step 1, before the lock:
- the target read at `self`; anything but a page is `NotSupported`;
- `EnsureWritable` settings;
- the target path made canonical and inside a canonical `exportRoots` entry;
- the page's owning section resolved through the walk;
- for a protected section, a read grant held at the call's start, never prompted (AD-21 Exports).

Steps 3–4 run under the lock: the section re-read (locked is `SectionLocked`), the file rechecked as absent, and the target's folder created. Step 7 is `Publish(pfPDF)` and then the landing wait (exists, non-empty, two stable polls 250 ms apart, opens exclusively, within `timeouts.publishSeconds`), so a wait that runs out is `Timeout` with `outcomeUnknown`. Every step-7 failure names the output path (AD-11). The result has `meta.objectId`, `meta.outputPath` (canonical), and the `exportNotEncrypted` warning for a protected page. The 600 s `ExternalWrite` deadline already exists. Messages name IDs and the caller's own output path, never names or content.

**Never:**
- No write grant and no dialog of any kind.
- No overwrite argument.
- No format but PDF; no section, section-group or notebook scope (`NotSupported`, "pages only in this release").
- `pfOneNote2007` is never reachable: the Core enum has no member for it, and Com maps unknown values to `NotSupported` before COM.
- No path-based protection rule (backup folder and export roots; story 3.8 builds it).
- No skill or README text (entry 10).
- Don't fork the pipeline or the landing wait.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Happy | MCP Test page, new `x.pdf` under a root | file on disk; `meta.outputPath` | none |
| Outside root | path not under any root (`..`, a junction out, another drive) | nothing written | `ExportPathNotAllowed` |
| Exists | target file exists (step 1 or under the lock) | untouched | `FileExists` |
| Bad path | UNC, device, a relative path (see OQ1), invalid characters, a stream (`:`) | — | `InvalidArgument` |
| Protected, no grant | page in an encrypted section, locked or not | nothing read further | `ReadNotGranted` |
| Protected, granted, locked | — | — | `SectionLocked`, before COM |
| Protected, granted, unlocked | — | file written | ok + `exportNotEncrypted` |
| Not a page | section, group or notebook ID | — | `NotSupported` |
| Never lands or keeps growing | Publish returns, file absent or growing | — | `Timeout`, `outcomeUnknown`, path named |
| COM fails | `0x8004201A` / others | — | the mapped code (`FileExists`), path named |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- the runner; `DeadlineFor` already gives `ExternalWrite` 600 s. Don't change it.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs` -- the base class; use `FailureNote` + `CallNamingFailureNoteAsync` to name the path on step-7 failures.
- `src/OneNoteMcp.Core/Writes/SectionBackup.cs` -- `WaitForFileAsync`/`Size`/`OpensExclusively` (l.116–180): move them into a shared helper; the backup keeps its `BackupFailed` mapping.
- `src/OneNoteMcp.Core/Writes/FilePathGate.cs` -- `Canonical(path, out refusal)`: reuse it for the target and each root.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs:135`, `src/OneNoteMcp.Com/ComGateway.cs:479` (`LocalFileProblem` l.512), `src/OneNoteMcp.Com/Interop/Enums.cs:55` -- Publish today is `pfOneNote` only.
- `src/OneNoteMcp.Core/Access/ReadAccess.cs` -- `OwningSectionAsync`, `HoldsReadGrant(section, at)`, `Now()`; `HierarchyNode` has `Encrypted`, `Locked` and `Path`.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs:61` -- `snapshot.EnsureWritable()`.
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:98` -- `ExportRoots` (Default `Documents\OneNote Exports`), already parsed by `SettingsStore.ReadExportRoots`.
- `src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs` + `src/OneNoteMcp.Server/Tools/DeleteHierarchyObjectTool.cs` -- the shape of a use case and a thin tool to copy.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs`, `ToolWarnings.cs` -- add `OutputPath` and `exportNotEncrypted`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs:361` -- `PublishCalls`, `PublishFailure`, `PublishedBytes`, `OnPublish`.
- `tests/OneNoteMcp.Integration/PermanentDeleteIntegrationTests.cs:500` (`GuardedGateway`), `ProtectedSectionIntegrationTests.cs` (Ballast Vault, temp settings, `SaveReadGrant`), `WriteProbes/ProbeSession.cs:1124` (`PauseAsync`, pause file).
- OneMore (c5c1008): `OneNote.Export` calls `Publish(id, path, (PublishFormat)format)` and swallows every exception to `false`. `Archivist.Export` deletes an existing file, then `EnsurePathExists(dir)`, then publishes. `ExportCommand` picks the extension from the format (`.pdf`) and names files from the cleaned page title, cut to 40 characters. We keep the folder creation; we refuse to overwrite (AD-11) and map HRESULTs instead of swallowing them.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/ExportFormat.cs` (new) -- `enum ExportFormat { OneNote, Pdf }` -- Core can't see Com's internal enum; 3.13 adds members, and there is never a OneNote2007 member.
- [ ] `IOneNoteGateway.cs`, `ComGateway.cs`, `FakeGateway.cs`, the integration `GuardedGateway`s -- `PublishAsync(objectId, targetPath, ExportFormat format, ct)`. Com maps `OneNote`→`pfOneNote` and `Pdf`→`pfPDF`; any other value is `NotSupported` before COM. Fake records the format. -- One port method per COM method (AD-5).
- [ ] `src/OneNoteMcp.Core/Writes/PublishedFile.cs` (new) + `SectionBackup.cs` -- extract the landing wait; it returns why it failed (never appeared, empty, growing, still open), and each caller maps that to its own code. -- Reuse, not a fork.
- [ ] `src/OneNoteMcp.Core/Writes/ExportTarget.cs` (new) -- `Resolve(raw, ExportRoots)` gives the canonical path or throws (`InvalidArgument`, `ExportPathNotAllowed`, `FileExists`). Roots are made canonical too; containment is root + separator, `OrdinalIgnoreCase`; the root itself is never a target; a `:` after the drive is refused.
- [ ] `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` (new) -- `IUseCase`, `OperationName = "publish"`, class `ExternalWrite`, with a nested `PublishPage : WriteOperation<…>` that follows the Always steps.
- [ ] `src/OneNoteMcp.Server/Tools/PublishTool.cs` (new) + `ServerSetup.cs` -- `publish(objectId, path, format = "pdf")`, with `format` a single-value enum (`PublishFormatArgument`). Its description gives the export roots and says to tell the user `meta.outputPath`. `ReadOnly=false, Destructive=false, Idempotent=false, OpenWorld=false`.
- [ ] `ToolMeta.cs`, `ToolWarnings.cs`, `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `Directory.Build.props` -- the meta and warning; the snapshot regenerated; the version per AD-14 (0.9 is unreleased, so check the bump against the last tag, `v0.8.0-rc.2`).
- [ ] `tests/OneNoteMcp.Tests/PublishToolTests.cs` (new) + `ComGatewayTests.cs` -- every matrix row on `FakeGateway` with a temp root, including the unlocked granted path with its warning, a junction that leads out, a step-4 `FileExists` race, a never-lands `Timeout` naming the path, and a raw `(ExportFormat)8` refused before COM.
- [ ] `tests/OneNoteMcp.Integration/PublishIntegrationTests.cs` (new) -- temp `settings.json` with `exportRoots` set to a temp dir; a guarded gateway allows only test notebooks. It covers PDF lands, `ExportPathNotAllowed`, and `FileExists`. The Vault case is per OQ3.
- [ ] `_bmad-output/.../research-onemore-lessons.md` -- row 28: OneMore's export path (above).

**Acceptance Criteria:**
- Given an MCP Test page and the default settings, when Claude Code calls `publish`, then a PDF exists at `meta.outputPath` under `Documents\OneNote Exports` (created if missing), and Phill opens it.
- Given `SectionBackup`'s existing tests, when they run after the extraction, then they pass unchanged.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

The landing wait runs inside step 7 (after `Publish` returns, before success), so the pipeline's existing `IsAmbiguous` makes a `Timeout` there `outcomeUnknown`, and `FailureNote` names the path. That is AD-11's "the `Timeout` result names the output path".

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the contract snapshot included.

**Sitting steps (main session with Phill; this lane never runs them):**
- S1: MCP Test open, with the Ballast Vault unlocked if OQ3 is (a). Run `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~PublishIntegrationTests`. Phill presses Ctrl+Alt+L at the pause. Expected: all pass, and only temp files are written.
- S2: register the dev build (`claude mcp add --scope user onenote-dev -- "<path>\OneNoteMcp.exe"`), ask for an MCP Test page as PDF, open the reported file, then delete it.

## Open Questions

1. **Path argument.** (a) Absolute only. (b) Absolute, or relative to the first `exportRoots` entry. (c) Optional too, defaulting to `<cleaned page title>.pdf` (OneMore's style). Recommended: (b).
2. **Extension.** (a) Must end `.pdf`, otherwise `InvalidArgument`. (b) The server appends `.pdf` when it is missing. (c) Any name. Recommended: (a).
3. **Locked-Vault integration case.** OneNote hides a locked section's pages, so the test needs a page ID read while the Vault is unlocked. (a) Read the ID unlocked, pause for Ctrl+Alt+L, then run both refusals. The parent walk on a locked page is unprobed, and a locked page move crashed OneNote in 3.1. (b) Unit tests only for both protected refusals, with `verify` amended. (c) Live `ReadNotGranted` against the Vault left unlocked (no grant), and `SectionLocked` in unit tests only. Recommended: (a).
