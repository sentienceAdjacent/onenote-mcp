---
title: 'Settings (read side) and logging'
type: 'feature'
ticket: '4'
created: '2026-11-04'
status: done
route: 'full'
route_source: 'auto'
baseline_revision: '9b7ef5d8c5d2d0ee772f80d943b88f9f8f0fd7b9'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The server has no settings and logs only to stderr. Later stories need one Core type that owns `settings.json` (AD-10): story 5 reads the timeouts and `allowSilentActivation`, the journal stories read `journal`, and exports read `exportRoots`. The logs also vanish with the process, so a failed run can't be diagnosed afterwards (AD-13).

**Approach:** Add a Core `SettingsStore` that reads `%APPDATA%\OneNoteMcp\settings.json` at `schemaVersion` 1 into an immutable `SettingsSnapshot`. A missing file gives the defaults. A malformed file gives the defaults with status `Invalid`, so reads continue and anything that writes gets `SettingsInvalid`. A newer `schemaVersion` gives status `NewerSchema`, which means the server runs read-only. Add a rolling-file log under `%LOCALAPPDATA%\OneNoteMcp\logs` (10 MB per file, 5 files) beside the existing stderr log. Logs carry IDs, HRESULTs and timings only.

## Boundaries & Constraints

**Always:**
- **Snapshots:** each `Read()` reads the file again and returns a new snapshot, with no caching (AD-10: re-read at every access check). The store never writes the file.
- **Defaults** (AD-3, AD-8, AD-11):
  - timeouts: call 30 s, `Publish` 300 s, activation 10 s, idle release 300 s;
  - `allowSilentActivation: false`;
  - journal: path `%LOCALAPPDATA%\OneNoteMcp\journal`, `retentionDays` 30, `maxSizeMB` 2048;
  - `exportRoots`: `[Documents\OneNote Exports]`.
- **Paths:** the settings file path and the log directory each have a test-only environment override, `ONENOTE_MCP_SETTINGS_PATH` and `ONENOTE_MCP_LOG_DIR` (Conventions: "env vars only for test overrides of the paths").
- **Invalid files:**
  - Invalid JSON, a wrong type, or an out-of-range value (a timeout ≤ 0, `retentionDays` < 1, `maxSizeMB` < 1, an `exportRoots` entry that isn't an absolute path) gives `Invalid`, with a reason naming the field.
  - The reason never quotes the file's content.
  - Unknown keys are ignored.
  - A missing `schemaVersion` counts as invalid.
- **Log content:** the file log uses the same records as stderr. No logging call adds XML, page content or exception messages from XML parsing.
- **Startup:** the server logs one startup line with the settings status and path, but never the settings values.
- **File logger (Phill, 2026-11-04): Serilog.**
  - Packages: `Serilog.Extensions.Logging` 10.0.0 and `Serilog.Sinks.File` 7.0.0, plugged in beside the existing stderr console provider through `AddSerilog`.
  - Each process writes its own file, `onenote-mcp-{yyyyMMddTHHmmss}-{pid}.log`, with `shared: false`, `fileSizeLimitBytes` 10 MB, `rollOnFileSizeLimit` and `retainedFileCountLimit` 5. This avoids `shared: true`, whose behaviour with size rolling across processes isn't documented.
  - Serilog's own retention only counts one process's files, so a small startup prune keeps the newest 5 `onenote-mcp-*.log` files in the folder. It skips files that are locked.

**Never:**
- No writes to `settings.json`, no `Local\OneNoteMcp.Settings` mutex, and no `notebooks{}` grant parsing. Those belong to the access stories.
- The gateway doesn't consume the timeouts yet; story 5 does.
- No write tools, so read-only mode is only a property on the snapshot for now.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Absent | no file | `Status=Absent`, all defaults | No error |
| Valid | `schemaVersion:1` + some fields | those fields, defaults for the rest, `Status=Ok` | No error |
| Malformed | not JSON, wrong type, or out of range | defaults, `Status=Invalid`, reason names the field | `EnsureWritable()` throws `SettingsInvalid`; reads continue |
| Newer schema | `schemaVersion:2` | defaults, `Status=NewerSchema`, `ReadOnly=true` | `EnsureWritable()` throws `SettingsInvalid` with a "newer version" message |
| Unreadable | file locked or access denied | treated as Invalid | same as Malformed |
| Logs | server runs one call | a file under the log dir holds the call line (correlationId, tool, ms) and no XML | logging failures never fail a tool call |
| Roll | log passes 10 MB | rolls to a new file; at most 5 files kept | pruning tolerates files locked by another process |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/` -- new `Settings/SettingsStore.cs`, `SettingsSnapshot.cs` (records: `Timeouts`, `JournalSettings`, `ExportRoots`), `SettingsStatus` enum. Core is `net10.0`, so use `System.Text.Json` with `JsonDocument` for field-by-field validation, not attribute deserialisation, so each reason names the field.
- `src/OneNoteMcp.Core/ErrorCode.cs` -- `SettingsInvalid` already exists; `OneNoteException(code, message)` (story 3) carries it.
- `src/OneNoteMcp.Core/Results/ToolErrors.cs` -- the existing mapping of `SettingsInvalid` to its userAction text; reuse it, don't redefine it.
- `src/OneNoteMcp.Server/Program.cs` -- logging is set up here (stderr console). Add the file provider and the startup settings line here.
- `src/OneNoteMcp.Server/ServerSetup.cs` -- register `SettingsStore` as a singleton.
- `src/OneNoteMcp.Server/ToolRunner.cs:87-91` -- existing `LogCall`/`LogInternal` source-generated messages: IDs, HRESULT and ms only. Keep them.
- `tests/OneNoteMcp.Tests/` -- xunit v3; test names use `Subject_Behaviour` (for example `PluginManifest_HasNoVersion`). Use temp dirs, never the real `%APPDATA%`.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Settings/*` -- store, snapshot, status, defaults, path resolution with the env override, `EnsureWritable()`.
- [x] `Directory.Packages.props`, server csproj -- add the two Serilog packages.
- [x] `src/OneNoteMcp.Server/Logging/*` -- Serilog file-log setup (path from the env override, file name, limits) and the startup prune.
- [x] `src/OneNoteMcp.Server/Program.cs`, `ServerSetup.cs` -- register both; log the startup status line.
- [x] `tests/OneNoteMcp.Tests/SettingsStoreTests.cs` -- every matrix row from Absent to Unreadable, plus one per validated field.
- [x] `tests/OneNoteMcp.Tests/FileLoggingTests.cs` -- writes go to the override dir, roll at the size cap (the cap is injectable for the test), the prune keeps the newest 5 and skips a locked file, and an unwritable log dir doesn't stop startup.

**Acceptance Criteria:**
- Given the repo, when the Release build and the unit tests run, then both pass with 0 warnings.
- Given the server registered in Claude Code, when `get_hierarchy` runs once, then `%LOCALAPPDATA%\OneNoteMcp\logs` holds a file with the startup line and the call line (correlationId, tool, ms) and no XML.

## Implementation Notes

- Build and test with `-c Release` only: a running `onenote` MCP session locks the Debug output folder.

## Plan Change Log

## Review Triage Log

| # | Lens | Finding | Verdict | Route | Evidence |
|---|------|---------|---------|-------|----------|
| 1 | quick | `FileLog`: folder can exceed 5 files (startup-only prune; Serilog retention is per pid) | medium | patch | Serilog's retention matches only its own base file name; a rolling process keeps 5 of its own on top of the 4 the startup prune left. Matrix row Roll says at most 5. Fix: prune on every file open via `FileLifecycleHooks`. |
| 2 | quick | `EnsureWritable` for `NewerSchema` maps to `SettingsInvalid` → `retryable: true`, "Fix settings.json, then retry" | medium | defer | Real: `ToolErrors.cs:39,62` give that advice for every `SettingsInvalid`. Nothing calls `EnsureWritable` yet; the fix (per-case userAction/retryable) belongs with the first write tool. |
| 3 | quick | `SettingsStore.Read`: `File.Exists` is false on permission errors, so an unreadable file reads as Absent (writable) | medium | patch | `File.Exists` returns false on any error, including access denied on the parent folder; matrix row Unreadable says Invalid. Fix: open directly, only not-found is Absent. |
| 4 | quick | Timeouts have no upper bound; > 2,147,483 s throws in `CancelAfter` | low | patch | `CancelAfter` limit is int.MaxValue ms; a file with a larger value would be `Ok` now and fail at runtime in story 5. Direct one-line bound. |
| 5 | quick | `schemaVersion` above int.MaxValue reads as Invalid, not NewerSchema | low | patch | `TryGetInt32` fails above int.MaxValue. One-word fix (`TryGetInt64`). |
| 6 | quick | `ToolRunner.LogInternal` logs the whole exception, now persisted to the file log; a future XML parse error could quote content | medium (unverified until XML parsing exists) | defer | Pre-existing call; no XML parsing exists in `src` yet. The story that adds XML parsing must keep parse-exception messages out of logs. |

## Design Notes

- The timeouts' JSON shape is `"timeouts": {"callSeconds": 30, "publishSeconds": 300, "activationSeconds": 10, "idleReleaseSeconds": 300}`. AD-10 leaves `timeouts{}` open, and these names keep the unit in the key.
- `SettingsStatus` is `Ok | Absent | Invalid | NewerSchema`. `ReadOnly` is true for `NewerSchema`. `EnsureWritable()` throws for both `Invalid` and `NewerSchema`: writes fail with `SettingsInvalid` in both cases (AD-10).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests -c Release` -- expected: all pass.

**Manual checks:**
- After Phill disables the `onenote` server in `/mcp` and re-enables it once the build is done, run one `get_hierarchy` (read-only). Then check that the newest file in `%LOCALAPPDATA%\OneNoteMcp\logs` contains `correlationId=` and no `<one:`.
