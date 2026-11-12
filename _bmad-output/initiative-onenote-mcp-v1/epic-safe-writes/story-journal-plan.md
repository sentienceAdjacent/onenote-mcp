---
title: 'Journal'
type: 'feature'
ticket: '8'
created: '2026-11-12'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** AD-7 step 6 is an empty hook: no page write can leave a recovery copy, so stories 2.9–2.11 have nothing to snapshot into or restore from. Nothing prunes a journal, and `uninstall --purge` run with the test path overrides still targets the real `%LOCALAPPDATA%\OneNoteMcp\journal` (2.6 live check).

**Approach:** Build AD-8's journal in Core: an entry model in AD-10's format, a writer (gzip JSON, temp file then rename, DPAPI for protected sections), a reader, a pruner run at startup and daily, and a reusable page-snapshot step that `PageWrite` (2.9, 2.10) and epic 5's page deletes call from `SnapshotAsync`. Any snapshot failure aborts the write before COM.

## Boundaries & Constraints

**Always:**
- Entry file `{takenAtUtc:yyyyMMddTHHmmssfff}-{pid}-{guid:N}.json.gz`, written once to `.{name}.tmp` in the same folder and renamed. Contents exactly AD-10's list, `schemaVersion: 1`, `snapshotKind: "page"`, `entryId` a GUID, dates UTC ISO-8601.
- Extensible: optional members are left out when null, and the reader ignores members it doesn't know, so epic 5.5 can add a recycle-bin page ID field to schemaVersion 1 without breaking older readers.
- Protected page (section `encrypted`, read live at the snapshot): `title` and `xml` absent; `protected` = base64 DPAPI blob (CurrentUser) of JSON `{title, xml}`; `sectionKey` = `GrantKeys.SectionKey`. Everything else stays readable.
- Snapshot = piBinaryData XML of the page, with `binaryComplete` and `missingBinary[]` from the piBasic read step 3 already made. `notebookPath` is `NotebookKey`; `parentId` the owning section; `parentNamePath` its groups and name joined by `/`.
- Failures: a step-6 failure is never `outcomeUnknown`; the COM call doesn't run and the lock is released as for any pre-7 failure. Messages and logs carry IDs, counts and sizes only (AD-9, AD-13).
- Prune (each process, at startup in the background, then every 24 h on `TimeProvider`): retention by the file name's timestamp, then the size cap oldest first, never the newest entry; also `.tmp` leftovers older than a day. Vanished files are ignored. Only journal-named files are touched.
- Settings are re-read per write and per prune (`journal{path, retentionDays, maxSizeMB}`).
- Decision Q1 (Phill, 2026-11-12, option c): `journal.path` in settings wins; otherwise the default is `ONENOTE_MCP_JOURNAL_DIR` when set, else a `journal` folder beside the file `ONENOTE_MCP_SETTINGS_PATH` names when that is set, else `%LOCALAPPDATA%\OneNoteMcp\journal`. One resolver, used by the server (snapshot and settings), the pruner and `UninstallPaths.Resolve`. Test overrides only (spine Conventions); no spine edit.
- Decision Q2 (Phill, 2026-11-12, option a): DPAPI through Microsoft's `System.Security.Cryptography.ProtectedData` 10.0.14 package, with a row in `THIRD-PARTY-NOTICES.md`.
- Decision Q3 (Phill, 2026-11-12, option a): the snapshot's own COM reads (the hierarchy walk, the piBinaryData read) keep their own error codes and retry rules. `JournalFailed` (not retryable) covers only building or storing the entry: I/O, DPAPI, unreadable XML.
- Decision Q4 (Phill, 2026-11-12, option b): each `one:InsertedFile` is listed in `missingBinary` as `kind: "attachment"`, so a page with one has `binaryComplete: false`. Copying attachment bytes is not in this story.

**Never:** a tool, `restore_from_journal`, or reading entries back into OneNote (2.11); the `PageWrite` operation or its abandoned-write check (2.9); hierarchy or backup entries (epic 5); README journal sections (2.12); spine edits; real OneNote in tests.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Page snapshot | local page, granted | one `.json.gz`, `binaryComplete: true`, no `.tmp` left | — |
| Cloud image missing | piBasic has an image piBinaryData lacks | `binaryComplete: false`, one `missingBinary` item | — |
| Protected section | section `encrypted="true"` | no `title`/`xml`; `protected` decrypts to them; `sectionKey` set | — |
| Journal unwritable | folder is a file / access denied | COM call not made, lock released | `JournalFailed`, not `outcomeUnknown` |
| Retention | entries 31 and 29 days old, 30-day setting | 31-day entry removed | — |
| Size cap | total over `maxSizeMB` | oldest removed until under; newest kept | — |
| Vanishing entry | another process deletes mid-prune | prune completes | none raised |
| Attachment | page with a `one:InsertedFile` | `binaryComplete: false`, one `missingBinary` item `kind: "attachment"` | — |
| Snapshot COM read fails | piBinaryData read throws `ComBusy` | COM write not made, lock released | `ComBusy`, retryable, not `JournalFailed` |
| Journal override | `ONENOTE_MCP_JOURNAL_DIR` set, or only `ONENOTE_MCP_SETTINGS_PATH` set | server, pruner and purge use that folder | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/WritePipeline.cs:136-139` -- step 6 is `operation.SnapshotAsync`. Wrap only that call: a non-`OneNoteException`, non-cancellation exception becomes `JournalFailed`. Everything else stays.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs:29` -- the `SnapshotAsync` hook, unchanged; `CreatePageUseCase` keeps no snapshot (AD-7 `Create`).
- `src/OneNoteMcp.Core/Access/HierarchyWalk.cs` -- `AncestorsAsync`, `LocationAsync`. Add `PageLocationAsync(pageId)`: one walk giving the section node (ID, name, path, `Encrypted`), the group names and the notebook node. `HierarchyNode` already carries all of these.
- `src/OneNoteMcp.Core/Access/GrantKeys.cs:16,22` -- `NotebookKey`, `SectionKey`.
- `src/OneNoteMcp.Xml/ImageCount.cs` -- count only. Add a `MissingBinaries(basic, binary)` beside it: missing images, plus every `one:InsertedFile` (decision Q4). Images in OE content carry no `objectID`, only a `one:CallbackID` (`Fixtures/cloud/page-Basic.xml:86-93`).
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:30-36`, `SettingsStore.cs:31-39,257-270` -- `JournalSettings.Default()` hardcodes `%LOCALAPPDATA%`. Route the default through one resolver that takes the env getter (decision Q1).
- `src/OneNoteMcp.Server/Cli/UninstallCommand.cs:19-23,334-352,453-456` -- `UninstallPaths.Resolve` and `JournalFolder` use that resolver. Move the journal file-name regex to Core (shared with the pruner) and add the `.tmp` form. `scripts/clean-install-check.ps1:56` mirrors the pattern.
- `src/OneNoteMcp.Server/ServerSetup.cs:97-114` -- register the journal store and page-snapshot service beside the pipeline. `Program.cs:62-81` uses Generic Host, so the pruner is a `BackgroundService`. Log it through the Server, as `WriteLockLog` does: Core takes a callback.
- `src/OneNoteMcp.Core/Results/ToolErrors.cs:83` -- `JournalFailed` advice exists.
- Tests: `CreatePageToolTests.cs:883` (`TimedOutPageWrite`) is the model for a test `WriteOperation`. `TempDirectory.cs`, `FakeGateway.cs`, `Fixtures/mcp-test/page/image-*.xml`, `cloud/page-*.xml`, `UninstallCommandTests`, `SettingsStoreTests.cs:129`.
- OneMore: no page journal or snapshot to learn from (only a SQLite database backup and gzip of reminders).

## Tasks & Acceptance

**Execution:**
- [ ] `Directory.Packages.props`, `src/OneNoteMcp.Core/OneNoteMcp.Core.csproj`, `THIRD-PARTY-NOTICES.md` -- DPAPI dependency (decision Q2).
- [ ] `src/OneNoteMcp.Core/Journal/JournalEntry.cs`, `JournalFiles.cs` -- model, JSON options, names and patterns.
- [ ] `src/OneNoteMcp.Core/Journal/JournalStore.cs` -- `Write` (gzip, temp, rename, DPAPI, `JournalFailed`), `Read` (decompress, decrypt).
- [ ] `src/OneNoteMcp.Core/Journal/JournalPruner.cs` -- retention, cap, temp leftovers, vanishing files.
- [ ] `src/OneNoteMcp.Core/Journal/PageSnapshot.cs`, `HierarchyWalk.cs`, `src/OneNoteMcp.Xml/ImageCount.cs` -- the step-6 snapshot.
- [ ] `WritePipeline.cs` -- step-6 failure → `JournalFailed`.
- [ ] Settings resolver, `UninstallCommand.cs`, `clean-install-check.ps1` -- decision Q1 override, shared patterns.
- [ ] `src/OneNoteMcp.Server/Journal/JournalPruneService.cs`, `ServerSetup.cs` -- startup and daily prune, logging.
- [ ] Tests -- every matrix row, the format round trip with unknown members ignored, and the decision-Q1 resolution in the server, the pruner and purge.

**Acceptance Criteria:**
- Given a test `WriteOperation` whose snapshot fails, when the pipeline runs, then the gateway records no write call and a second write can take the lock at once.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass.

## Design Notes

- `missingBinary[]` items: `{kind: "image", objectId, callbackId}`. `objectId` is the image's own `objectID`, else its nearest ancestor's. Match piBasic against piBinaryData by that ID with counts; when several images share one OE and some are missing, the last ones are listed.
- DPAPI entropy: `"OneNoteMcp.journal:" + entryId`, so a blob moved into another entry won't decrypt. The class carries `[SupportedOSPlatform("windows")]` for CA1416.
- `notebookName` is the displayed name, as in the grants.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `dotnet test tests/OneNoteMcp.Integration` (without `ONENOTE_MCP_INTEGRATION`) -- expected: pass or skip.
