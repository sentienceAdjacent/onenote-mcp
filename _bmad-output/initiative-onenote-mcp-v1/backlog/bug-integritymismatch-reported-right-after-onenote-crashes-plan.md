---
title: 'Report OneNoteNotRunning when the integrity check finds no OneNote'
type: 'bugfix'
ticket: '4'
created: '2026-11-16'
status: 'draft'
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

**Problem:** After a crash in spike 3.1 sitting A, the next call returned `IntegrityMismatch` ("server=unreadable onenote=[]") instead of `OneNoteNotRunning`. That exact text is the integration harness's crash breaker (`PinnedOneNoteProbe`), which returns a fake `IntegrityReport(null, [])` to stop a reconnect. The production probe can't produce it, but production has the same gap in another form: if `ONENOTE.EXE` exits between the window check and the integrity check, the report is `server=Medium onenote=[]`. That counts as a match, so `CoCreateInstance` runs and may start a hidden `-Embedding` copy, which AD-3 forbids.

**Approach:** In the gateway's activation, an integrity report that lists no `ONENOTE.EXE` means OneNote isn't running. Unless `allowSilentActivation` is on, return `OneNoteNotRunning` (with the crashing message when WER is handling one), release the reference and never call COM. This check comes before the mismatch check. A real mismatch, where OneNote is listed at another level, is unchanged.

## Boundaries & Constraints

**Always:** COM is never called on this path. Keep `allowSilentActivation` behaviour as it is: with the setting on, an empty report with a readable server level still activates. Keep the existing not-running message and `userAction` (or the crashing pair). Unit tests use fakes only.

**Never:** Start, kill or close OneNote. Set `ONENOTE_MCP_INTEGRATION`. Change `IntegrityReport.Matches`, `ProcessProbe`'s Win32 reads, or the real-mismatch case. Retry or wait inside activation.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Breaker / unreadable server, none listed | running and window checks pass; report `(null, [])`; silent off | `OneNoteNotRunning`, 0 activations | not-running message |
| OneNote exited after the window check | report `(Medium, [])`; silent off | `OneNoteNotRunning`, 0 activations (today: activates) | not-running message; crashing pair if `IsOneNoteCrashing()` |
| Same, silent activation on | report `(Medium, [])`; silent on | activates, as today | none |
| Unreadable server, silent on | report `(null, [])`; silent on | `IntegrityMismatch`, as today | unchanged |
| Real mismatch | report `(Medium, [42:High])` | `IntegrityMismatch`, as today | unchanged |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/ComGateway.cs:777-803` -- `EnsureActivatedAsync`: the connecting check (777), the window check (789) and the integrity check (797-803). The fix goes between 797 and 798. Reuse the not-running throw at 780-786 (factor out a small helper, so both sites use the same message, `userAction` and `IsOneNoteCrashing()` choice) and `worker.Post(worker.ReleaseApplication)`.
- `src/OneNoteMcp.Com/ComGateway.cs:949-955` -- `CrashingMessage` and `CrashingUserAction`: reuse them, don't add new text.
- `src/OneNoteMcp.Com/IProcessProbe.cs:51-58` -- `IntegrityReport` and `Matches`. Don't change them. The `(null, [])` mismatch stays for silent activation and for the startup log (`Program.cs:82`).
- `tests/OneNoteMcp.Integration/WriteProbes/PinnedOneNoteProbe.cs:12-14,54-55` -- the source of "server=unreadable onenote=[]". Comment-only update: after the fix a tripped breaker reads as not running at the integrity check (silent activation is off in `ProbeSession.cs:76`), and it still stops before `CoCreateInstance`.
- `tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs:477-502` -- the integrity tests; add the new ones beside them. `TestProbe` (1660+) already has `Running`, `HasWindow`, `Crashing`, `Integrity` and `IntegrityChecks`; `Settings(silent: true)` exists.
- `tests/OneNoteMcp.Tests/ComGatewayTests.cs:135-140`, `tests/OneNoteMcp.Tests/GetHierarchyToolTests.cs:278-283` -- `FixedProbe(true)` returns `(0x2000, [])` and expects activation. After the fix that reads as not running, so list one Medium OneNote: `new(0x2000, [new OneNoteIntegrity(1, 0x2000)])`.

## Tasks & Acceptance

**Execution:**
- [ ] `tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs` -- add `IntegrityCheckFindsNoOneNote_IsOneNoteNotRunning_NothingCreated` as a theory over `(null, [])` and `(0x2000, [])`, with `Running = true` and `HasWindow = true`. Assert `OneNoteNotRunning`, `app.Activations == 0` and `IntegrityChecks == 1`. Add a `Crashing = true` case that expects `CrashingMessage`, and a silent-on case where `(0x2000, [])` activates. Run them first: both theory rows must fail today (`IntegrityMismatch`; activation) -- TDD red.
- [ ] `src/OneNoteMcp.Com/ComGateway.cs` -- after `CheckIntegrity()`, when `integrity.OneNote.Count == 0 && !settings.AllowSilentActivation`, release and throw the shared not-running failure. Update the `EnsureActivatedAsync` summary (726-734) to name the step.
- [ ] `tests/OneNoteMcp.Tests/ComGatewayTests.cs`, `tests/OneNoteMcp.Tests/GetHierarchyToolTests.cs` -- make `FixedProbe` list one matching OneNote.
- [ ] `tests/OneNoteMcp.Integration/WriteProbes/PinnedOneNoteProbe.cs` -- correct the class and line-54 comments.

**Acceptance Criteria:**
- Given a probe whose running and window checks pass and whose integrity report lists no OneNote, when a tool call activates with silent activation off, then it fails `OneNoteNotRunning` and the COM factory is never called.
- Given OneNote listed at a different level, when a call activates, then it still fails `IntegrityMismatch` with the same message.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Why the gateway, not just the harness: changing only `PinnedOneNoteProbe` would relabel the spike's message but leave the production race, `(Medium, [])`, where AD-3's "never start OneNote silently" is broken. Checking for no OneNote before `Matches` fixes both. The breaker stays safe with silent activation on, because `(null, [])` is still a mismatch there.

The ticket's Verify case ("a fake probe that returns no processes") already passes today when the connecting check itself sees none (`NotRunning_IsOneNoteNotRunning_NothingCreated_NoIntegrityCheck`). The failing case is the one where OneNote disappears after that check, which is what the new theory pins down. Checked at planning on the base revision, with a throwaway copy of the theory that was then reverted: `(null, [])` gave `IntegrityMismatch`, and `(0x2000, [])` threw nothing and activated. Both rows fail.

## Open Questions

1. **Spine wording.** AD-3 (line 89) already says not running gives `OneNoteNotRunning`, and AD-9's cause column says "process check (AD-3)". The fix enforces that rule at a second point and adds no new code or cause. Options: (a) no spine change; (b) add one sentence to AD-3 after line 101: "If the integrity check lists no `ONENOTE.EXE` (it exited after the running check), the call returns `OneNoteNotRunning` and COM isn't called, unless `allowSilentActivation` is set", and change AD-9's row to "process check (AD-3): not running, crashed, closing (no open window), or gone by the integrity check". (b) needs a Fable review and Phill's approval.
2. **Spike 3.1 findings.** Rows 83 and 147 of the draft findings, on `story/3.1-write-probes-part-3`, say a production race caused the misreport. The text actually came from the harness breaker. Options: (a) correct them on that branch before its findings are final; (b) leave them, with this plan as the correction.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, including the new theory (which fails before the `ComGateway.cs` change).
