---
title: "Release OneNote sooner so a restart doesn't show 'cleaning up'"
type: 'bugfix'
ticket: '3'
created: '2026-11-15'
status: 'ready-for-dev'
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

**Problem:** A server that made a call holds its `OneNote.Application` reference for `timeouts.idleReleaseSeconds` (300 s). That reference keeps `ONENOTE.EXE` alive after the user closes OneNote's window, so a restart in that time starts a second `ONENOTE.EXE` that shows "OneNote is cleaning up" and gives up (2026-11-16 sitting: reproduced; ending the server ended the old process within 1 s). A call made while a closed OneNote shuts down (about 12 s) also connects to it and pins it again.

**Approach:** Release the reference 5 s after the last COM call. Treat an `ONENOTE.EXE` with no open window as not running, unless `allowSilentActivation` is on, so the server neither connects to a closing OneNote nor keeps using one.

## Decisions (Phill, 2026-11-16)

1. Q1, the trigger: A, a short idle grace. Nothing releases per call or per tool, and there is no window watcher.
2. Q2: the default is 5 s, configurable through `timeouts.idleReleaseSeconds`. The README says so.
3. Q3: the key and its range stay as they are. The README says that values above about 10 s can bring the dialog back.
4. Q4: AD-3 is amended in this PR, and the amendment gets the Fable final review before merge.
5. Q5, fixed now: an `ONENOTE.EXE` with no open window counts as not running, unless `allowSilentActivation` is on. The signal is the most accurate one that is cheap to check (Design Notes). It must never refuse a normally open OneNote, and a minimised window counts as open.
6. Q6: the plan stays whole.

## Boundaries & Constraints

**Always:**
- The release stays on the worker's STA thread.
- Every re-activation runs AD-3's full rules.
- No reference is released while a call is in flight.
- `idleReleaseSeconds` accepts what it accepts today: the probes use 86400.
- The window rule applies to the connecting check and to a live reference's per-call check. When the per-call check fails, the reference is dropped, and the call then fails like an activation.
- If the window can't be read, the process counts as having one: fail open, never refuse.

**Never:**
- Kill, close or start OneNote.
- Match OneNote's window class, which is version-fragile.
- Call COM to judge OneNote's state.
- Change the disconnect retry, the busy backoff, the crash (WER) rules, or a poisoned worker's release.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Burst | Calls less than 5 s apart | One activation | None |
| Gap | Calls more than 5 s apart | Released between them; the second re-activates with every check | As first activation |
| Long call | A call longer than the grace | Held until it returns | None |
| Closing | `ONENOTE.EXE` alive with no window, silent activation off | No `CoCreateInstance`; `OneNoteNotRunning`, with a message that it is closing or starting | Refused |
| Closed under a live reference | Window gone, next call | Reference dropped, then `OneNoteNotRunning`; nothing is created | Refused |
| Silent on | No window, `allowSilentActivation` true | Connects as today | None |
| Minimised | OneNote minimised | Works | None |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:20-27` -- the `Timeouts.Default` `IdleRelease`, 300 s now.
- `src/OneNoteMcp.Com/IProcessProbe.cs` -- the probe seam.
  - `IsOneNoteRunning` is the connecting check. `IsOneNoteStillRunning` is the per-call check.
  - `ProcessProbe.SessionProcessIds` lists this session's processes.
  - Add `HasOneNoteWindow()`, with a default interface implementation that returns `true`, so `PinnedOneNoteProbe` and the test probes compile and fail open.
- `src/OneNoteMcp.Com/ComGateway.cs:730-823` -- `EnsureActivatedAsync`: the per-call check at line 733, the connecting check at line 771. `GatewaySettings.AllowSilentActivation` holds the setting. `CrashingMessage` is the pattern for a dedicated message.
- `ComGateway.cs:1061-1096` -- the idle timer; it doesn't change.
- `tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs` -- the idle tests, `TestProbe` and `Settings(idleMs:, silent:)`.
- `tests/OneNoteMcp.Tests/SettingsStoreTests.cs:128` -- asserts the 300 s default.
- `architecture-onenote-mcp.md` AD-3 -- the Activation bullet (line 90) and the Idle bullet (line 96).
- `README.md` -- Requirements (line 59) and "How it works" (line 314).
- `research-onemore-lessons.md` -- add the lesson.
- Not affected: `read_result` and the result store, the search cache, `once` grants and the write mutex. All of them are server memory and make no COM call.

## Tasks & Acceptance

**Execution:**
- [ ] `SettingsSnapshot.cs` -- set the `IdleRelease` default to 5 s.
- [ ] `IProcessProbe.cs` -- add `HasOneNoteWindow()`. It is true when a session `ONENOTE.EXE` has `MainWindowHandle != 0`, using a fresh `Process`, and when the read throws. Give it a pure helper over (pids, a window lookup) for the unit tests.
- [ ] `ComGateway.cs` -- when silent activation is off and there is no window:
  - per-call check: drop the reference and fall through to activation;
  - connecting check: release, then throw `OneNoteNotRunning` with a new `ClosingMessage`.
- [ ] `ComGatewayLifecycleTests.cs`, `SettingsStoreTests.cs`, and a probe-helper test -- cover every matrix row.
- [ ] The spine's AD-3, a memlog entry through `memlog.py append`, the README, and the OneMore research note.

**Acceptance Criteria:**
- Given OneNote running and a server that made a call less than 5 minutes ago, when the user closes OneNote's window, then `ONENOTE.EXE` exits within about 15 s and the next start shows no "cleaning up" dialog.
- Given OneNote open, normal or minimised, when any tool runs, then it is never refused for having no window.

## Design Notes

Release trigger (Q1). Re-activation costs about 15–20 ms:
- `CoCreateInstance` against a running OneNote takes 9 ms (spike 1, `read.jsonl` `activation`).
- About 2 ms per process enumeration, measured here as an upper bound.
- The integrity check.

The options weighed were: per COM call, which adds about 3–4 s to a 213-page search filter; per tool, which needs a port change; and a window watcher, which needs a timer. A 5 s grace keeps OneNote's exit within its own ~12 s shutdown.

Window signal (Q5):

| Signal | Accuracy | Cost | Verdict |
|---|---|---|---|
| (a) `MainWindowHandle != 0` | .NET: the first top-level window of the pid that is unowned and `IsWindowVisible` (`ProcessManager.Windows.cs` `MainWindowFinder`). Measured: a minimised test form gave a handle; once hidden, it gave 0. Windows on other virtual desktops stay `WS_VISIBLE`. | one `EnumWindows`, about 2 ms | **chosen** |
| (b) `EnumWindows` plus OneNote's frame class | Same as (a), plus a class name that isn't documented and could change between versions, which would refuse a normally open OneNote | same | rejected |
| (c) `-Embedding` on the command line | Tells how the process was started, not whether it is closing. OneMore notes "an embedded instance can be taken over by a later UI launch" (`Helpers/Office/Outlook.cs`) | about 1 ms | rejected |
| (d) The ROT / `GetActiveObject` | No evidence that OneNote registers. Binding returns a reference, which is the pin we're avoiding, and the method is absent on .NET 5+ | a COM call | rejected |
| (e) OneMore | `OneMoreCli/OneNoteLauncher.cs` and `OneMoreCalendar/OneNoteProvider.cs` judge "interactive" by `MainWindowHandle != IntPtr.Zero`, because "a headless server started first can prevent a subsequent interactive launch from ever taking over" | -- | agrees with (a) |

Residual risk: a closing OneNote that keeps some visible window would still count as open. That fails open, which is today's behaviour, and the sitting checks it.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.

**Manual checks (sitting, Phill; built server registered as `onenote-dev`):**
1. Without a server: close OneNote's window and note when `ONENOTE.EXE` ends (about 12 s). Start OneNote again; it must start normally.
2. With a connected server: call `get_hierarchy` (notebooks), then close the window within 2 s. `ONENOTE.EXE` must end within about 15 s of the close, and the next start must be normal.
3. Within the ~12 s after closing OneNote, call `get_hierarchy`. Expected: `OneNoteNotRunning` with the closing message. Task Manager must show no new `ONENOTE.EXE`, and the old one still ends on time.
4. Minimise OneNote, then call `get_hierarchy`. It works. Make another read more than 5 s later; it works too.
5. Read a page that has an image, wait more than 5 s, then call `get_binary_page_content` with its `callbackID`. The binary comes back, not `binaryUnavailable`.

## Implementation Notes

## Plan Change Log

## Review Triage Log
