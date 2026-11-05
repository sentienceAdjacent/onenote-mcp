---
title: 'Gateway lifecycle, read-side port and integration harness'
type: 'feature'
ticket: '5'
created: '2026-11-04'
status: 'built'
baseline_revision: '116c53856deb44652a3d37bb6e6f496763514cef'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `ComGateway` is the minimal actor from story 2: no timeouts, so one hung COM call blocks the server forever; no reconnect, busy retry, integrity check or idle release (AD-3). The port has only `GetHierarchy`, so stories 7–9 can't start, and there is no integration harness or test notebook to run AD-3 against real OneNote.

**Approach:** Rebuild `ComGateway` as a supervisor over replaceable STA workers (one thread + one COM reference each) that applies every AD-3 rule, reading timeouts and `allowSilentActivation` from `SettingsStore` at each activation. Add the Read-class COM methods to `IOneNoteGateway`. Add an opt-in integration harness that creates an **MCP Test** notebook in a scratch folder through a guarded write path that exists only in the integration suite.

## Boundaries & Constraints

**Always:**
- Only Com references interop types (AD-2). Core gets its own `PageInfo` and `SpecialLocation` enums, like `HierarchyScope`.
- One live worker at a time. The call deadline covers the queue wait plus the call; busy backoff stops before the deadline and returns `ComBusy`, so busy never poisons a thread.
- Reconnect triggers: `0x800706BA`, `RPC_E_DISCONNECTED`, `InvalidComObjectException`. Not `0x800706BE` (it can mean bad XML; OneMore row 7). Reconnection re-runs every activation rule. Reads retry once.
- Activation: process check (`OneNoteNotRunning` unless `allowSilentActivation`), then integrity check (`IntegrityMismatch`, COM never created), then `CoCreateInstance` on the worker under the activation timeout.
- `FindPages` always passes `fDisplay=false` (AD-11); the port has no display parameter.
- The guarded write path lives in `tests/OneNoteMcp.Integration` only. It refuses any target that isn't inside the MCP Test notebook, checked against a live `GetHierarchy`.
- Never kill or close OneNote. No `NavigateTo`. Integration reads of Phill's notebooks stay read-only.
- **Decision (Phill, 2026-11-04): nine Read methods, no `FormatDate`.** It isn't in the OneNote 15 type library (our 29-slot `IApplication` and the PIA both lack it), and nothing needs it: dates are UTC ISO-8601 at the boundary. A deferred-work entry routes the spine (AD-7 table), R1 and story 7 to `bmad-correct-course`.
- **Decision (Phill, 2026-11-04): plan kept whole** (about 2,400 tokens); the harness is this story's verification.

**Never:**
- No write methods on the port, so the "abandoned write → non-read `ComBusy`" and write `outcomeUnknown` rules wait for epic 2's first write (record in deferred-work).
- No tools: stories 7–9 add them. No `FakeGateway` fixtures: story 6.
- No `IMessageFilter` registration; busy is handled by retry.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Hang | COM call exceeds call timeout | `Timeout`; worker poisoned; next call gets a fresh worker | calls queued behind it fail `ComBusy` |
| Abandoned bound | 2 abandoned workers outstanding | every call `ComUnavailable`, COM not touched | clears when an abandoned call returns (it releases its reference and exits) |
| Disconnect | `0x800706BA` / `RPC_E_DISCONNECTED` / `InvalidComObjectException` | drop ref, re-activate, retry the read once | second failure `ComUnavailable` |
| Busy | `RPC_E_CALL_REJECTED` / `RETRYLATER` | retry with backoff until just before the deadline | then `ComBusy` |
| Not synced | `0x8004201D` | `ComBusy` at once, no retry | — |
| Not running | no `ONENOTE.EXE` in session | `OneNoteNotRunning`, nothing created | `allowSilentActivation: true` activates instead |
| Integrity | OneNote at a different integrity level | `IntegrityMismatch` in < 1 s, COM never created | startup logs the levels |
| Activation hang | create exceeds activation timeout | `Timeout`, worker poisoned | as Hang |
| Idle | no call for `idleRelease` | COM reference released | next call re-activates |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/ComGateway.cs` -- rewrite as supervisor; keep `MapScope`, the `HResultMap.ToException` mapping, the per-call process check and the test-seam constructor style (`Func<object>` activation). Add an `internal` raw seam `InvokeAsync<T>(Func<IApplication,T>)` used by unit tests and by the integration suite's guarded writes; it applies the same lifecycle.
- `src/OneNoteMcp.Com/StaThread.cs` -- becomes the worker: add poison (fail queued items with `ComBusy`), owned COM reference, self-release-and-exit when an abandoned call returns. Keep the pumping `WaitOne` loop.
- `src/OneNoteMcp.Com/IProcessProbe.cs` -- add integrity: current process vs every session `ONENOTE.EXE` (token `TokenIntegrityLevel` RID via `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`).
- `src/OneNoteMcp.Com/HResultMap.cs` -- reuse; already maps busy/disconnect/`0x8004201D`/`0x80080005`. Add nothing unless a test shows a gap.
- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- read-only; slots are fixed (CI guard).
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `GetHierarchyParentAsync`, `GetPageContentAsync(id, PageInfo)`, `GetBinaryPageContentAsync(pageId, callbackId)`, `FindPagesAsync(start, query, includeUnindexed)`, `FindMetaAsync(start, name, includeUnindexed)`, `GetHyperlinkToObjectAsync`, `GetWebHyperlinkToObjectAsync` (hierarchyId, pageContentObjectId), `GetSpecialLocationAsync(SpecialLocation)`.
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs` -- `Timeouts`, `AllowSilentActivation` consumed as-is.
- `src/OneNoteMcp.Server/ServerSetup.cs`, `Program.cs` -- pass `SettingsStore` to `ComGateway`; startup integrity log line (IDs and levels only).
- `tests/OneNoteMcp.Tests/FakeApplication.cs` -- make programmable per method (hang on a gate, throw sequences, count activations); `FakeGateway.cs` gets throwing stubs.
- `tests/OneNoteMcp.Integration/` -- `VtableSmokeTests.OneNoteFixture.Enabled` is the existing opt-in gate (`ONENOTE_MCP_INTEGRATION=1`); reuse it.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/{IOneNoteGateway,PageInfo,SpecialLocation}.cs` -- port methods + Core enums.
- [x] `src/OneNoteMcp.Com/{StaThread,ComGateway,IProcessProbe}.cs` -- worker, supervisor, AD-3 rules, integrity probe, idle timer; implement every port method with `xs2013`.
- [x] `src/OneNoteMcp.Server/{ServerSetup,Program}.cs` -- wiring + startup integrity line.
- [x] `tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs` -- every matrix row with the fake (Timeouts injected in ms), plus each port method's argument mapping.
- [x] `tests/OneNoteMcp.Integration/TestNotebook.cs` -- guarded write path: create-or-reuse `MCP Test` at the scratch folder, seed one section and one page; refuse foreign targets (unit-tested guard logic where possible).
- [x] `tests/OneNoteMcp.Integration/LifecycleScenarioTests.cs` -- unattended: forced hang (raw seam sleeps past a 2 s timeout via a temp `settings.json`), replacement read succeeds, abandoned worker released; read-method smoke over MCP Test. Hitl, each gated by `ONENOTE_MCP_SCENARIO=<name>`: `not-running`, `modal-dialog` (reads work; guarded write → `ModalDialogOpen`), `restart` (poll up to 120 s for a new OneNote pid, then read succeeds via reconnect), `elevated` (`IntegrityMismatch`, no activation).
- [x] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- two entries: the abandoned-write rule (epic 2), and `FormatDate` removal from AD-7/R1/story 7 via `bmad-correct-course`.

**Acceptance Criteria:**
- Given the repo, when Release build and unit tests run, then both pass with 0 warnings.
- Given OneNote running and `ONENOTE_MCP_INTEGRATION=1`, when the integration suite runs, then the unattended tests pass and MCP Test exists in the scratch folder with no change to any other notebook.
- Given Phill sets up each hitl state, when its scenario runs, then it passes.

## Implementation Notes

- Build and test with `-c Release` only: a running `onenote` MCP session locks the Debug output folder.
- Don't run the integration suite: it creates the MCP Test notebook in Phill's OneNote, so the lead runs it with him. Make sure it compiles.

## Plan Change Log

## Review Triage Log

Pass 1 (quick): high 1, medium 2, low 4, false 0, maybe-false 0. All patch.

| # | Lens | Finding | Verdict | Route | Evidence |
|---|------|---------|---------|-------|----------|
| 1 | quick | `ComGateway.AwaitAsync`: a caller cancelling a started, hung call never poisons the worker; later calls time out "waiting for an earlier call" forever, `_abandoned` stays 0 | high | patch | Confirmed: the `OperationCanceledException` branch only calls `TryAbandon`, which fails for a started item, and the queued-timeout path never reaches `Poison`. Fix: on cancel of a started item, keep watching it to its deadline and poison if still running. |
| 2 | quick | `_activationLock.WaitAsync` has no bound | low | patch | Confirmed. Each COM call and activation is bounded (AD-3), but a waiter behind the lock isn't. One-line bound by the activation timeout. Per-attempt deadlines on reconnect are per COM call, as AD-3 states; no change there. |
| 3 | quick | `InvokeAsync` doc says writes are never retried, but busy retry applies to writes | low | patch | Confirmed: `AttemptAsync` busy loop ignores `retryOnDisconnect`. A rejected call never ran, so the retry is safe; fix the comment and add it to the abandoned-write deferred note for epic 2. |
| 4 | quick | `TestNotebookGuard` identifies MCP Test by folder only; an override pointing at a real notebook lets the harness write there; `EnsureFolderIsOurs` accepts any folder with one `.onetoc2` | medium | patch | Confirmed at `TestNotebook.cs:55,123,206`. Fix: also require notebook `name` = `MCP Test` on ensure and on every write; creation accepts only an empty folder. |
| 5 | quick | `ProcessProbe.CheckIntegrity`: an `ONENOTE.EXE` that exits between listing and opening reads as unreadable → `IntegrityMismatch` | medium | patch | Confirmed: `OpenProcess` failure returns null (`IProcessProbe.cs:107-110`). Fix: skip a process that no longer exists; if none remain, report no mismatch (the process check decides not-running). |
| 6 | quick | Matrix "Activation hang … as Hang" lacks a queued-call `ComBusy` assertion; startup integrity log line untested | low | patch | Confirmed in `ComGatewayLifecycleTests.cs:315`; no `StartupLog.Integrity` test. Add both. |
| 7 | quick | Modal scenario: missing `Seed` section fails with a misleading guard error | low | patch | Confirmed: `FindSectionId(...)!`. Add a `SkipWhen` like the notebook-missing case. |

## Design Notes

- **Integrity unreadable = mismatch.** If OneNote's token can't be queried, treat it as a mismatch: the only known cause is OneNote running higher, and calling COM then hangs 30–57 s (spike row "Probe elevated").
- **Scratch folder:** `%LOCALAPPDATA%\OneNoteMcp-Test\MCP Test` (override `ONENOTE_MCP_TEST_DIR`), outside `OneNoteMcp\` so `uninstall --purge` never touches it. MCP Test stays open in OneNote's notebook list for story 6's fixtures. Creating it doesn't take focus (spike row 25a).
- **Abandoned count** is outstanding abandoned workers, not a lifetime total, so "until OneNote is reachable again" means an abandoned call came back.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests -c Release` -- expected: all pass.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration -c Release` -- expected: unattended tests pass, hitl ones skip.

**Manual checks:**
- Phill runs each hitl scenario once (not-running needs `ONENOTE.EXE` ended in Task Manager; elevated runs from an elevated shell).
