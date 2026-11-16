---
title: "Release OneNote sooner so a restart doesn't show 'cleaning up'"
type: 'bugfix'
ticket: '3'
created: '2026-11-15'
status: 'built'
baseline_revision: 'cb6eb3a672b12b36c3a6d2c4521c2686e343ebd1'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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

- Implemented directly, not by a subagent. The lane's hard rules apply: never touch OneNote, and never run the integration tests or the server. A fresh subagent wouldn't carry them, and the dispatch may not add them.
- `IProcessProbe.HasOneNoteWindow()` defaults to `true`, so `PinnedOneNoteProbe` (the integration probes) keeps today's behaviour. `ProcessProbe` uses `AnyWithWindow` over `HasMainWindow`; a process that has gone gives `false`, and one that can't be read gives `null`, which counts as open.
- `ComGateway`: `LiveReferenceUsable(settings)` serves the per-call check and the re-check after the activation lock. A windowless OneNote is refused with `OneNoteNotRunning`, `NoWindowMessage` and `NoWindowUserAction`, after the crash check and before the integrity check.
- The minimised-window evidence is a real test: a minimised `STATIC` tool window of the test process makes `HasMainWindow` true. Before that, a PowerShell check gave a WinForms form a handle while minimised and 0 once hidden.
- Race test: a 1 ms grace never fired between back-to-back calls, because of the timer resolution. It now uses a 15 ms grace with 0–24 ms gaps.
- Docs: AD-3 (Activation and Idle), the spine memlog (decision 276, through `memlog.py`), the README (Requirements, How it works), and the OneMore research note. The skill's `OneNoteNotRunning` row is unchanged, because the error's own `userAction` covers the closing case.
- Verified: `dotnet build OneNoteMcp.slnx -warnaserror` gave 0 warnings and 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 2957 passed.

**Live check (2026-11-16 sitting, Phill): passed.** Built server (ccbfa6b), no other OneNoteMcp server running.
- Check 1 (no server): from the same evening's sitting A run: ONENOTE.EXE ended about 12 s after the close; normal start.
- Checks 2-3 (a server reading every second, the worst case): last ok read 23:25:42; the 23:25:43 read found no window and refused (`OneNoteNotRunning`, the closing message) without connecting; ONENOTE.EXE ended 23:25:44, about 1 s after the close (sitting A, before the fix: still alive 2.5 min later). No new ONENOTE.EXE appeared; later reads got plain "not running". Phill restarted OneNote at 23:27:22: normal start, and the next read was ok.
- Check 4: two reads 7 s apart with OneNote minimised: both ok (121 ms, 47 ms).
- Check 5: `get_page_content` then, 7 s later, `get_binary_page_content` with the image's `callbackID`: ok, no warnings. (The release itself isn't observed directly: the server's logs are off-limits to the session.)

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens, 2026-11-15): 6 findings. Verdicts: high 0, medium 1, low 4, false 1, maybe-false 0.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | `LifecycleScenarioTests.Scenario_Restart_ReadSucceedsViaReconnect` no longer reaches the reconnect path | low | patch | True. The 5 s idle release, or the window check, drops the reference before the restart. The reconnect path is still covered by the unit tests. Renamed to `..._ReadSucceedsAfterOneNoteRestarts` and the summary says why. Not run (integration). |
| 2 | Class summary says the reference is released "at once" when the window goes | low | patch | True: it is checked only at the next call. Comment corrected. |
| 3 | `EnsureActivatedAsync` summary omits the window checks | low | patch | True. Summary now names `LiveReferenceUsable` and the window check's place in the order. |
| 4 | The window check and the running check use different process sets (two-process and crashed-window cases) | medium | defer | True and fails open, as Q5 decided. Literally consistent with the intent's per-process wording; which process COM binds to is unprobed. Entry added to `deferred-work.md`. The Fable review raised the same point (F3). |
| 5 | No evidence of the Fable review; Execution boxes unticked | false | reject | The Fable review ran in parallel with this lens; its findings go to Phill. Ticking the boxes would be an edit to this plan, which the triage rules reject. |
| 6 | README "For agents" step 1 checks only `Get-Process ONENOTE` | low | patch | True. Now asks for a window (`MainWindowHandle -ne 0`, minimised is fine). |

Fable final review of the AD-3 amendment (`model: fable`, `effort: low`, read-only, bmad-review with also_consider and the reviewer-gate checklist). 15 findings: 0 high, 5 medium, 10 low. I verified each, confirmed 14 and dropped 1. Phill's picks, 2026-11-16: apply all 14 confirmed; F4 is fixed in code.

| # | Finding | Verdict | Phill | Applied |
|---|---|---|---|---|
| F1 | "Never keeps OneNote alive past its own shutdown" was unmeasured and too strong | medium | apply | Now reads "Expected to". Names the two exceptions: a call in flight, and an abandoned thread. A Deferred row covers the live check. |
| F2 | The spine said nothing about a `callbackID` across a release | medium | apply | Deferred row added (manual check 5), with the `binaryUnavailable` fallback. |
| F3 | The spine read per process; the code checks the whole session | medium | apply | The rule now says "some `ONENOTE.EXE` in this session has an open window". A Deferred row covers the two-process and crashed-window cases. |
| F4 | A server on another desktop sees no windows and would refuse an open OneNote | medium | fix in code | `ProcessProbe.CanSeeAnyWindow` (`EnumWindows`): when no OneNote has a window and this process sees no windows at all, every OneNote counts as having one. Tests through the probe seam (`AnyWithWindow` with `canSeeWindows`). Spine and README worded to match. Memlog entry 277. |
| F5 | Silent activation under a 5 s release wasn't addressed | medium | apply | The cost figure is scoped to "against a running OneNote", with a silent-activation sentence. The risk row now covers the 5 s default and a leftover instance. |
| F6 | The 15–20 ms figure was an estimate | low | apply | Now marked "estimated"; only the 9 ms is logged. |
| F7 | The per-call window check's cost wasn't stated | low | apply | Estimated 2–3 ms per call while a reference is held, not measured. |
| F8 | "Start a hidden copy" was stated as fact | low | apply | "Might … unprobed (see Deferred)". |
| F9 | AD-9's `OneNoteNotRunning` row named only the process check | low | apply | The source is now "not running, crashed, or closing (no open window)". |
| F10 | The skill row contradicts `NoWindowUserAction` | low | apply | `SKILL.md`: "or follow the error's `userAction`". |
| F11 | The upper cap of the range was missing | low | apply | AD-3 gives 1–2,147,483. New AD-10 `timeouts{}` range line. |
| F12 | Dates (2026-11-16 against local 11-15); spine `updated` field | -- | dropped | The ticket's Notes and Phill's decisions say 2026-11-16. `updated` is bumped when Phill approves an amendment, as the memlog shows. |
| F13 | The sitting had no citation or evidence level | low | apply | Cites the backlog 3 ticket's Notes: "one machine, one run, observed with clock times, not logged". |
| F14 | Prevents didn't name the new purpose | low | apply | Adds "OneNote kept running after the user closes it". |
| F15 | Dense bullet, fragments, vague wording | low | apply | Split into a heading bullet with sub-bullets. The rejected signals point to the memlog. The message's content and "exits" are named. |
