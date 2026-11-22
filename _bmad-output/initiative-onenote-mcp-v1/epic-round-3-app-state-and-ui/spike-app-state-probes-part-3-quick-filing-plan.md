---
title: 'App-state probes, part 3: quick filing'
type: 'chore'
ticket: '13'
created: '2026-11-17'
status: done
baseline_revision: '55fd04930b009bb6bc7ae4d536fc77585b924de3'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/spike-app-state-probes-part-2-navigation-and-windows-plan.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Q9 builds `quick_filing` only if the picker's callback reaches our process and the wait can be bounded (A8), and entry 2 can't set A8's limit, the idle-release rule or the picker's cleanup until the epic Notes' "Entry 13" questions have measured answers: whether `QuickFiling` works early-bound from our 64-bit STA thread and `OnDialogClosed` arrives, whether `Run` returns at once, modality and focus, what other calls do while the picker is open, cancel, and what happens to the picker when our wait times out, our STA thread is replaced, or our reference is released.

**Approach:** Build on spikes 4.1 and 4.12's harness (this branch, `spike/4.13-quick-filing-probes`, is cut from `spike/4.12-navigation-windows-probes`, which stacks on 4.1's draft PR #60). Add the spike's own early-bound `IQuickFilingDialog` and `IQuickFilingDialogCallback`, a COM-visible callback object that records where and when it is called, and one opt-in hitl scenario `app-state-filing` that the main session runs in one sitting with Phill present. No event sink (Q10). Answers go into a findings doc and the spine memlog. No `src/` change.

## Boundaries & Constraints

**Always:**
- The picker is configured only through its own setters (`Title`, `Description`, `TreeDepth`, `ParentWindowHandle`, `AddButton`) and run with `Run`; nothing the user picks is acted on. `SelectedItem` and `PressedButton` are read and recorded, never used as a target.
- The dialog RCW and the callback object stay referenced from the session until the callback has fired or the step's limit has passed, except in the release probe (F7), whose point is to drop them.
- The callback only records (time, managed thread ID, apartment state, `SelectedItem`, `PressedButton`) and returns at once: OneNote's UI thread waits for it (OneMore row 26).
- Every step that shows the picker is preceded by a pause saying what Phill will see and what to do (pick, cancel, or leave it open), and followed by one where he reports what he saw. F/V (4.1's `ViewAsync`) is recorded around each `Run`.
- The crash breaker stays on, each COM call has 4.1's 60 s limit, and a blocked call stops the scenario. The guarded write in F4 goes through 5.1's guard to a probe page in MCP Test's Probes section only.
- 4.1's logging rules: test notebook names only, no window titles, the foreground process as a label; a selected ID outside the test notebooks is logged as `other`.

**Never:**
- Start, close or kill OneNote. Call `SetFilingLocation`, `NavigateTo`, `NavigateToUrl`, any window setter, or any event API.
- Leave a picker on screen at the end of a step without a pause asking Phill to close it.
- Retry a call automatically. Pass `force=true`.
- Change `src/` or the spine's text. Only the memlog gets lines.
- Commit real notebook names, paths holding the user name, or any probe output.

## I/O & Edge-Case Matrix

| Id | Area | Question |
|---|---|---|
| F1 | Binding | `QuickFiling()` from the gateway's STA thread: does the result cast to the early-bound `IQuickFilingDialog` (QI), and do its setters work? HRESULT per call |
| F2 | Run | `Run` with no `ParentWindowHandle`: time to return; F/V; Phill picks a section of MCP Test. Does `OnDialogClosed` arrive, after how long, on which thread and apartment, with which `SelectedItem` and `PressedButton`? |
| F3 | Modality | `Run` with `ParentWindowHandle` set to the current window's handle (read through 4.12's window copies, wrappers released): is the picker modal to OneNote's window, and does it take focus? Phill reports, then cancels |
| F4 | While open | With the picker open: `GetHierarchy` (notebooks), `GetPageContent` on a probe page, a guarded `UpdatePageContent` on that page, and a second `QuickFiling().Run`. HRESULT and time of each (is `0x80042030` returned, as under a modal dialog?), and whether the write applied |
| F5 | Cancel | Phill presses Cancel (and, in a second run, closes it with Esc): does the callback fire, and what are `SelectedItem` and `PressedButton`? |
| F6 | Our wait ends | The probe waits 10 s with no choice and marks its wait over (A8's bounded wait); then Phill picks. Does the late callback still arrive, and does anything else happen? |
| F7 | Reference released | With the picker open, the probe calls `Marshal.FinalReleaseComObject` on the dialog and drops the callback object (GC forced). Phill then picks: does the picker stay, does a callback arrive, does OneNote report an error or hang? |
| F8 | STA replaced | The picker runs on a side gateway (4.1's `CreateSideGateway`); with it open, the side gateway is disposed, ending its STA thread. Phill then picks: does the picker stay, does the callback arrive and where, and do the session gateway's calls still work? |
| H | HRESULTs | Every code seen, per method, with its trigger, mapped to AD-9 |

## Decisions (2026-11-17)

Answered overnight with the recommendation, per Phill's standing overnight rule (2026-11-17); Phill reviews in the morning.

- **F7 and F8: (a), probe both** as the last steps before `cleanup`, with Phill present and the breaker on. If OneNote hangs, the run stops, Phill deals with OneNote himself (the probe never closes or kills it), and the hang is the result. Reason: A8's rule that a picker left on screen stays and its answer is dropped is what these measure. Rejected: (b) logging them `notProbed`.
- **Plan size: (a), keep the full plan** (about 2,000 tokens), in one sitting. Rejected: (b) splitting F7 and F8 into another spike.

</frozen-after-approval>

## Code Map

- Stacks on 4.12 (and so 4.1). Reuse from `WriteProbes/ProbeSession.cs`: `StepAsync`, `ObservePauseAsync`, `ReportPauseAsync`, `RequireScenario`, `TryAsync`/`TryGuardedAsync`, `StopOnTimeoutAsync`, `ViewAsync`, `CreateSideGateway`, `McpTestIdAsync`, `SectionIdAsync`, `PageIdByTitleAsync`, `CreateProbePageAsync`, `UpdateAsync`/`PageXmlAsync`, `Echo`; 4.12's window copies (`ProbeWindowsInterop.cs`) and its window snapshot for F3's handle.
- 4.12 added `ProbeSession.LimitOf(gateway)` and an optional `callSeconds` on `CheckTimeoutAsync`/`TryGuardedAsync`: F8's side gateway passes its own limit. `ComGateway.Dispose` (`src/OneNoteMcp.Com/ComGateway.cs`) posts `ReleaseApplication` and disposes its `StaThread`, which is what F8 relies on; the session disposes its side gateways again at the end, so disposing one early must stay safe (check, don't assume).
- `src/OneNoteMcp.Com/Interop/IApplication.cs` slot 27 `QuickFiling()` returns `IDispatch`; cast to the spike's copy. `src/OneNoteMcp.Com/StaThread.cs` waits with `WaitHandle.WaitOne()`, which pumps COM on an STA. Don't edit either.
- Interop facts, from the comtypes module generated from this machine's type library (read 2026-11-17):
  - `IQuickFilingDialog` `{1D12BD3F-89B6-4077-AA2C-C9DC2BCA42F9}`, dual: 7/8 get/put `Title`, 9/10 `Description`, 11/12 `CheckboxText`, 13/14 `CheckboxState`, 15 get `WindowHandle` (`ulong`), 16/17 `TreeDepth` (`HierarchyElement`), 18/19 `ParentWindowHandle` (`ulong`), 20/21 `Position` (`POINT`), 22 `SetRecentResults(RecentResultType, bool, bool, bool)`, 23 `AddButton(bstr, HierarchyElement, HierarchyElement, bool)`, 24 `Run(IQuickFilingDialogCallback)`, 25 get `SelectedItem`, 26 get `PressedButton` (`uint`), 27 put `TreeCollapsedState`, 28 put `NotebookFilterOut`, 29 `AddInitialEditor(bstr)`.
  - `IQuickFilingDialogCallback` `{627EA7B4-95B5-4980-84C1-9D20DA4460B1}`, dual: 7 `OnDialogClosed(IQuickFilingDialog)`.
  - `HierarchyElement`: `heNone` 0, `heNotebooks` 1, `heSectionGroups` 2, `heSections` 4, `hePages` 8.
- OneMore at `c5c1008` (read 2026-11-17): `OneNote.cs:2142-2213` sets `Title`, `Description`, `ParentWindowHandle` (from `CurrentWindow`, wrappers released), `TreeDepth`, one `AddButton`, then `Run(new FilingCallback(...))`, keeping the dialog alive until the callback; `:2216-2247` the callback reads `SelectedItem`, then releases the dialog.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Integration/WriteProbes/ProbeQuickFilingInterop.cs` -- the spike's `IProbeQuickFilingDialog` and `IProbeQuickFilingDialogCallback` with the slots above, and `FilingRecorder`, a `[ComVisible(true)]` class implementing the callback that records time, thread ID, apartment, `SelectedItem` and `PressedButton` (each read in its own try) and signals a waiter -- a callback that throws or blocks would stall OneNote's UI thread.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs` -- add `RunPickerAsync(gateway, configure)` (QuickFiling, configure, Run, with timings and F/V; returns the dialog handle holder and recorder) and `WaitForPickAsync(recorder, limit)`; unit tests in `ProbeHelpersTests.cs` for the recorder (records and signals; a throwing `SelectedItem` is recorded, not rethrown) using a fake dialog, and a layout test of the two interfaces' method order.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/AppStateFilingProbes.cs` (scenario `app-state-filing`) -- every Matrix row in the Sitting script's order; `cleanup` asks Phill to close any picker still open and removes the probe page's marker.
- [ ] Live sitting (the main session's, with Phill, after 4.12's sitting) follows the Sitting script. Not part of implementation.
- [ ] `epic-round-3-app-state-and-ui/spike-app-state-probes-part-3-quick-filing-findings.md` (after the sitting) -- one row per Matrix question, HRESULTs, "Does the callback reach us", "Can the wait be bounded", Not probed, Evidence gaps, Relied on, Left behind.
- [ ] Spine `.memlog.md` (after the sitting) -- one `constraint` line per finding that changes an AD, naming it.

**Acceptance Criteria:**
- Given the recorder, when OneNote calls `OnDialogClosed`, then it returns without blocking and without rethrowing, whatever the dialog's getters do.
- Given F4's write, when it runs, then it targets only a probe page that 5.1's guard accepts.
- Given the cleanup step, when it ends, then no picker the run opened is on screen (Phill confirms) and the probe page is as before F4.

## Sitting script

Run from the repo root with OneNote running, `$env:ONENOTE_MCP_INTEGRATION='1'`, `$env:ONENOTE_MCP_SCENARIO='app-state-filing'`, then `dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~AppStateFiling`. It runs after 4.12's sitting (one probe sitting at a time).

**Before the sitting,** OneNote has one window showing MCP Test > Seed.

**One sitting (about 30–40 min), every step a hitl step:**

| Step | Rows | What Phill will see, and does |
|---|---|---|
| `bind-pick` | F1, F2 | A "Move or Copy"-style picker titled "MCP probe" appears. He picks MCP Test > Probes and presses the button, then reports what he saw |
| `modal` | F3 | The picker again, attached to OneNote's window. He tries clicking OneNote behind it, reports, then presses Cancel |
| `while-open` | F4 | The picker stays open while the probe makes four calls; a second picker may appear. He reports, then cancels each |
| `cancel` | F5 | Two pickers in turn: Cancel the first, Esc the second |
| `late` | F6 | He leaves the picker for 10 s until told, then picks Probes |
| `release` | F7 | He leaves it open until told, then picks Probes and reports anything odd |
| `thread` | F8 | Same as `release` |
| `cleanup` | - | He confirms no picker is open |

## Implementation Notes

- 2026-11-17, implemented (code and unit tests only; nothing ran against OneNote, and no `ONENOTE_MCP_*` variable was set). `dotnet build OneNoteMcp.slnx -warnaserror` passes with 0 warnings. `OneNoteMcp.Tests`: 2965 passed. `OneNoteMcp.Integration` with no integration variables: 409 passed, 96 skipped (`app-state-filing` among the skips). Judgement calls the plan didn't settle:
  - **The COM-facing types are public.** `IProbeQuickFilingDialog`, `IProbeQuickFilingDialogCallback`, `ProbeHierarchyElement` and `FilingRecorder` are public, unlike 4.12's internal window copies. Measured in-process (a CCW of our own object, no OneNote): while they were internal, `QueryInterface` on the recorder's CCW for the callback IID returned `0x80004002` (E_NOINTERFACE); public, it and `IDispatch` return S_OK. A unit test keeps this. Everything else stays internal (`FilingRecorder`'s constructor and `Calls` too).
  - **Callable members.** Callable: the `Title`, `Description`, `TreeDepth` and `ParentWindowHandle` getters and setters, `AddButton`, `Run`, `SelectedItem`, `PressedButton`, and `WindowHandle` (the picker's own handle, read once after `Run` for F3: whether it is a top-level window and the foreground root; no title). The checkbox, `Position`, `SetRecentResults`, `TreeCollapsedState`, `NotebookFilterOut` and `AddInitialEditor` are `[Obsolete(error: true)]` placeholders, so a call doesn't compile.
  - **The picker** is "MCP probe", sections only (`heSections`), one default button "File here" (`AddButton(text, heSections, heNone, true)`), no parent except in F3. F3's parent is `CurrentWindow.WindowHandle` from 4.12's `WindowsSnapshotAsync`, the value OneMore passes.
  - **`RunPickerAsync(label, config, gateway)`** makes `QuickFiling`, the QI, each setter, `Run` and the `WindowHandle` read in one STA delegate, each in its own try, so F1 gets one row (and one HRESULT for H) per call. Every other run rows each failed call before `Run` on its own, F3 rows all of them (its `ParentWindowHandle` setter included), and a run whose `Run` wasn't reached carries the first failed call's attempt, not the gateway's ok. A failed `get_WindowHandle`, and a failed `SelectedItem` or `PressedButton` inside the callback, get rows with their HRESULT too. Every exception keeps its HRESULT, type and message, not only `COMException`; a failed QI is recorded as `InvalidCastException`, whose HRESULT is always `0x80004002` (not the QI's own), with its message. `Run` is timed with `Stopwatch` timestamps, so a callback row gives ms after `Run` returned and after it started, and whether it came on the picker's STA thread.
  - **The callback's calls are kept apart from the callback object** (`FilingCalls`, which holds no reference to the recorder), so F7 can drop the recorder (a `WeakReference` records whether it was collected; OneNote's COM reference should keep the CCW alive) and still see a late call. The recorder's signal completes with `RunContinuationsAsynchronously`, so no probe code runs on OneNote's calling thread.
  - **Waits.** A step waits up to 180 s for Phill's pick or cancel after a `Run` that returned ok, 3 s otherwise. F6's bounded wait is 10 s. F7 and F8 wait 3 s after `Run` before dropping the reference or disposing the side gateway.
  - **Pauses.** A watch pause says what Phill will see and what to do in the picker. Every report pause also asks him to cancel any picker still open, and F5 asks between its two pickers when no callback came, so no step ends with a picker on screen without a pause asking him to close it. F6, F7 and F8 tell him when to pick with a pause of their own.
  - **F/V while a picker is open.** `PickerViewAsync` reads the foreground process label first, natively, then `ViewAsync`. A failed hierarchy read (perhaps `0x80042030`) is kept in the row and rowed for H instead of ending the step; a blocked read still stops the scenario.
  - **F4.** The probe page is `probe filing page` in MCP Test > Probes, found by title or created once through the guard (it stays for reruns; a row says whether this run created it). The write is a partial `UpdatePageContent` adding one outline with the text `probe filing marker <stamp>`, through 5.1's guard (`TryWriteAsync`) with the page's own date, plus a live check (read just before the picker opens, so it isn't itself one of F4's measured reads) that the target is a page titled `probe filing page`, outside the recycle bin, directly in the section named Probes directly in MCP Test; cleanup's delete repeats that check before each delete. Note that the guard makes two `GetHierarchy` reads before the write, so a failure while the picker is open may be the guard's read rather than the write. The row doesn't separate them (unverified whether its message does), so read it beside F4's own `GetHierarchy` row. Whether the write applied is read once both pickers are closed.
  - **F7** calls `Marshal.FinalReleaseComObject` on the dialog on the STA thread that obtained it, drops the recorder, then forces `GC.Collect`, `WaitForPendingFinalizers`, `GC.Collect`. **F8** runs the picker on a `CreateSideGateway(60)` gateway and disposes it while the picker is open; `ComGateway.Dispose` returns at once when already disposed (checked, `ComGateway.cs` `Dispose`), so the session's second dispose is safe. `StaThread.Dispose` joins for at most 5 s and ignores the result, and the gateway doesn't expose its worker to the test assembly, so the F8 row says the thread's end is `confirmed` only when the dispose took under 5 s, else `unconfirmed`. The dialog RCW of a disposed gateway is dropped, never released from another thread. F6, F7 and F8 each end with one session-gateway `GetHierarchy` (no retry) to see whether OneNote still answers.
  - **Selected IDs in rows** are named from sections-scope reads of MCP Test and MCP Test Cloud: the kind and path inside a test notebook, else `other` (no ID logged), or `none`.
  - **`cleanup`** asks Phill to cancel any picker still open and to confirm none is, waits 3 s, releases every dialog the run still holds (one row each, with its callback count), then deletes every outline on the probe page holding the marker text (so a rerun of `cleanup` alone finds it) and records whether the page's hash matches the one read before F4.
  - **For the findings doc's Left behind:** the probe page `probe filing page` in MCP Test > Probes (kept on purpose), and any picker Phill couldn't close.

## Plan Change Log

- 2026-11-17: the two open questions were answered overnight with the recommendation (Decisions above), per Phill's standing overnight rule; status `ready-for-dev`, then `in-progress` for the build. Phill reviews in the morning.

## Review Triage Log

- 2026-11-17, pass 1 (lens `quick`; 8 findings): high 0, medium 3, low 4, false 0, maybe-false 0, plus 1 low rejected. 7 patched, 0 deferred. After the patches: build 0 warnings; `OneNoteMcp.Tests` 2965 passed; `OneNoteMcp.Integration` 410 passed, 96 skipped (no integration variables).

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | A run whose `QuickFiling()` or QI failed rowed the gateway's ok, so F4's second picker (and other runs) could miss H | medium | patch | `RunCall` was null and the row fell back to `run.Invoke`. Now the first failed call's attempt |
| 2 | F3's `ParentWindowHandle` setter result was never rowed | medium | patch | Only `bind-pick` rowed steps. F3 now rows every step, other runs each failed one |
| 3 | The QI catch recorded `InvalidCastException.HResult` (always `0x80004002`) as if it were the QI's | low | patch | Comment corrected; the exception message is kept |
| 4 | Only `COMException` kept its HRESULT; E_INVALIDARG and similar lost theirs | low | patch | Every exception now keeps `HResult`, type and message, in `PickerCall` and `FilingRecorder.Read` |
| 5 | Callback getter failures never reached the HRESULT summary | low | patch | They sat in row detail with `attempt: null`. Each now gets its own row with an attempt |
| 6 | `OnDialogClosed`'s getters are unbounded COM calls, so "returns without blocking whatever the getters do" isn't guaranteed | low | reject | They are the same reads OneMore makes in its callback (`OneNote.cs:2231`); a bound would need another thread inside OneNote's call, more than a direct fix, for a hang not seen |
| 7 | `IsProbePage` compared the page ID with itself, so the Probes-only write check did nothing | medium | patch | Both call sites passed `f.PageId`. Now a live read: title, not in the recycle bin, directly in MCP Test > Probes; before F4 and before each cleanup delete |
| 8 | F8 assumed the side STA thread ended; `StaThread.Dispose` ignores its 5 s join result | low | patch | The row records the dispose time and `staThreadEnded` `confirmed` (under 5 s, no error) or `unconfirmed` |

## Design Notes

**Relied on, not re-probed:** OneMore's cross-process callback from an MTA (research row 26); `Run` shows the dialog from a new OneNote thread (Microsoft); under File > Options only the update methods return `0x80042030` (5.1 constraint 11), which F4 checks for a picker.

**Where the callback may land (inferred, F2 measures it).** .NET's COM-callable wrappers aggregate the free-threaded marshaller, so OneNote's call may arrive on an RPC (MTA) thread, not our STA. The recorder logs the thread and apartment; a callback on a pool thread still meets A8's need, because the wait is a signal, not an STA message.

**For the build (2026-11-17).** Implementation writes code and unit tests only. Never set `ONENOTE_MCP_INTEGRATION`, `ONENOTE_MCP_SCENARIO` or any `ONENOTE_MCP_PROBE_*` variable, never call OneNote or create a COM object, never run the server, and never read or list `%APPDATA%\OneNoteMcp`, `%LOCALAPPDATA%\OneNoteMcp` or `%LOCALAPPDATA%\OneNoteMcp-Test`; never run a command containing `OneNoteMcp grant`. Scratch output goes only under `<session scratchpad>\lane-4-12\` (the lane gives the full path). Don't commit. No bare `git stash`; no `rm -rf` with a variable path.

**Order hazards.** F7 and F8 run last before `cleanup`: either could leave the picker or OneNote in a state the later steps can't trust.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: succeeds.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` with no integration variables -- expected: pass, the recorder and layout tests run, the scenario skipped.

**Manual checks:**
- The diff has no call to `SetFilingLocation`, `NavigateTo`, `NavigateToUrl` or a window setter, and the callback does nothing but record and signal.
