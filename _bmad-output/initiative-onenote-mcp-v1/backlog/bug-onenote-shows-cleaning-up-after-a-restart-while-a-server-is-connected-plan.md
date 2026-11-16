---
title: "Release OneNote sooner so a restart doesn't show 'cleaning up'"
type: 'bugfix'
ticket: '3'
created: '2026-11-15'
status: 'draft'
baseline_revision: 'cb6eb3a672b12b36c3a6d2c4521c2686e343ebd1'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A server that made a call holds its `OneNote.Application` reference for `timeouts.idleReleaseSeconds` (300 s). That reference keeps `ONENOTE.EXE` alive after the user closes OneNote's window, so a restart in that window starts a second `ONENOTE.EXE` that shows "OneNote is cleaning up" and gives up (2026-11-16 sitting: reproduced; ending the server ended the old process within 1 s).

**Approach:** Release the reference within a few seconds of the last COM call, short enough that a connected server never keeps OneNote alive past its own shutdown (about 12 s with no server). The strategy and value are Q1–Q3.

## Boundaries & Constraints

**Always:** The release stays on the worker's STA thread (`StaThread.ReleaseApplication`, `FinalReleaseComObject`). Every re-activation runs AD-3's full rules: settings re-read, connecting process check, integrity check, `CoCreateInstance` under `timeouts.activation`. No reference is released while a call is in flight. `idleReleaseSeconds` still accepts the values it does today: the write probes pin one activation with 86400 (`ProbeSession.cs:76`).

**Never:** Kill, close or start OneNote. Release a poisoned worker's reference early (it stays until its hung call returns, AD-3). Change the disconnect retry, busy backoff or crash rules. Touch Core or the port (`IOneNoteGateway`) unless Q1 picks per-tool release.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Burst | Calls less than the grace apart (one tool's COM calls, a search post-filter) | One activation for the burst | None |
| Gap | Two calls more than the grace apart | Reference released between them; the second re-activates (connecting check and integrity check run again) | As first activation |
| Long call | One call runs longer than the grace | Not released until it returns, then after the grace | None |
| Race | A call starts as the release is queued | The call re-activates without spending the read's one reconnect retry | Existing `ReconnectRequiredException(null)` path |
| OneNote closed meanwhile | Released, then the window closes and the process exits | Next call: `OneNoteNotRunning` | As today |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:20-27` -- `Timeouts.Default`; the 4th value is `IdleRelease` (300 s).
- `src/OneNoteMcp.Core/Settings/SettingsStore.cs:271,440` -- reads `idleReleaseSeconds`: whole seconds, 1 to `int.MaxValue/1000`. Leave the range alone (Q3).
- `src/OneNoteMcp.Com/ComGateway.cs:1061-1096` -- `BeginCall`/`EndCall` arm the idle timer when `inFlight` reaches 0. `OnIdle` posts `ReleaseApplication`. `inFlight` counts gateway calls, not tool calls, so a grace of 0 would release between the COM calls of one tool.
- `ComGateway.cs:730-823` -- `EnsureActivatedAsync`. With a live reference it runs one process check (`IsOneNoteStillRunning`). Without one it re-reads settings, then runs `IsOneNoteRunning`, `CheckIntegrity` and `CoCreateInstance`. Reuse it as is.
- `ComGateway.cs:687` -- a work item that finds the reference released throws `ReconnectRequiredException(null)`. That retries without spending the read's retry (`MaxAttempts` 4).
- `tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs:409-440, 1340` -- idle tests and `Settings(idleMs:)`. `TestProbe.IntegrityChecks` and `FakeApplication.Activations` count activations.
- `tests/OneNoteMcp.Tests/SettingsStoreTests.cs:128` -- asserts the 300 s default.
- `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:96` -- AD-3 "Idle: release the COM reference after 5 minutes idle." Needs amending (Q4). Memlog lines 26, 36 and 41 record why idle release exists.
- Not affected: `read_result` and the result store (no COM), the search cache, `once` read grants and the write mutex. All of them live in the server, not in the reference.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs` -- set the `IdleRelease` default to the Q2 value.
- [ ] `src/OneNoteMcp.Com/ComGateway.cs` -- only if Q1 is not A: the chosen release trigger. Update the class summary's idle line either way.
- [ ] `tests/OneNoteMcp.Tests/SettingsStoreTests.cs` -- update the default assertion.
- [ ] `tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs` -- one test per matrix row: burst (`Activations == 1`), gap (`Activations == 2`, `IntegrityChecks == 2`), long call (`HasApplication` stays true past the grace while the fake blocks), race (the call succeeds and `Activations == 2`).
- [ ] `architecture-onenote-mcp.md` and its `.memlog.md` -- amend AD-3 Idle with the value and the reason (citing the 2026-11-16 sitting), and add a decision entry (Q4).
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- if Q5 is deferred, add the closing-OneNote race as an entry.

**Acceptance Criteria:**
- Given OneNote running and a server that made a call less than 5 minutes ago, when the user closes OneNote's window, then `ONENOTE.EXE` exits within about 15 s and the next start shows no "cleaning up" dialog.
- Given the defaults, when a tool makes several COM calls in a row, then OneNote is activated once for that tool.

## Design Notes

Measured or cited costs. "Pin" means how long the server keeps a closed OneNote alive.

| Option | Pin after last call | Extra cost | Code |
|---|---|---|---|
| A. Short idle (default N s) | ≤ N s + ~1 s | ~15–20 ms per re-activation, only after a gap > N | 1 constant |
| B. Release after each COM call | ~0 | the same per COM call; search post-filter of 213 pages (spike 1, ~5 s) gains ~3–4 s | timer path and new semantics for 0 |
| C. Release after each tool call | ~0 between tools | one re-activation per tool | new port method, `FakeGateway`, `ToolRunner` |
| D. A plus a window watcher | ≤ poll interval | ~2 ms enumeration per poll, while a reference is held | new probe method, timer, Win32 heuristics |

Where the costs come from:
- `CoCreateInstance` against a running OneNote: 9 ms (spike 1 row 1; `read.jsonl` `activation`).
- Against a closed OneNote, with silent activation on: 511 ms.
- The process check: measured on this machine, about 2 ms per process enumeration (483 processes, PowerShell, so an upper bound). The connecting check enumerates 3 names, about 6 ms. The integrity check is one more enumeration plus a token read.
- With a live reference, every call already pays about 6 ms for `IsOneNoteStillRunning`. The net cost of a re-activation is therefore the integrity check, the settings read and the 9 ms: about 15–20 ms, against a 36–57 ms round trip per call (spike 1 host table).

Prior art: OneMore activates a short-lived `Application` for each command and releases it with `FinalReleaseComObject`. Its `OneNote.Dispose` carries the comment "DO NOT call this otherwise OneNote will not shutdown properly" (Design – COM Layer). Its docs don't mention the "cleaning up" dialog.

Unknown, checked at the sitting: whether a `CallbackID` read under one activation still works under the next. It has the same `{GUID}{n}{B0}` form as an object ID, so it is probably stored, not session-scoped. The current 300 s idle and every reconnect already cross activations.

## Open Questions

1. **Release trigger.** A, B, C or D, as in the table above. Recommended: A. It adds no code path, and any N ≤ 10 already keeps the pin inside OneNote's own ~12 s shutdown.
2. **Default grace.** 2 s, 5 s or 10 s. Recommended: 5 s. It covers one tool's COM calls with a wide margin and keeps the pin well under 12 s. 2 s re-activates more often between tool calls; 10 s leaves little margin.
3. **Setting range.** (a) Keep `idleReleaseSeconds` and its range, and document that above about 10 s the dialog can return. (b) Cap it, for example at 30: a larger value makes `settings.json` `SettingsInvalid`, and `ProbeSession` changes. (c) Remove the key. Recommended: (a). The probes need 86400.
4. **Spine amendment.** (a) Amend AD-3 in this PR and run the Fable review before merge. (b) Amend it without the Fable review, since it changes one value. (c) Leave it to the next spine sync. Recommended: (a). The global rule forbids skipping the review for planning docs.
5. **Closing-OneNote race.** A call made in the ~12 s while a closed OneNote shuts down re-activates against it, or may start a hidden `-Embedding` copy (unprobed). (a) Defer it to a backlog entry, and watch for it at the sitting. (b) Fix it now, by counting a windowless `ONENOTE.EXE` as not running unless `allowSilentActivation` is on (AD-3 change). Recommended: (a). With a short grace, the pin stays bounded anyway.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the new idle tests included.

**Manual checks (sitting, Phill; built server registered as `onenote-dev`):**
1. Without a server: close OneNote's window. Record when `ONENOTE.EXE` ends (expected about 12 s). Start OneNote again; the start must be normal.
2. With one connected server: call `get_hierarchy` (notebooks), then close the window within 2 s. `ONENOTE.EXE` must end within about 15 s of the close, and the next start must be normal (no "cleaning up").
3. Make two reads more than the grace apart. Both succeed, and the log shows no error.
4. Read a page that has an image (`get_page_content`). Wait longer than the grace, then call `get_binary_page_content` with its `callbackID`. The binary is returned, not `binaryUnavailable`.
