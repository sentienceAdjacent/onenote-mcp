---
title: 'quick_filing'
type: 'feature'
ticket: '9'
created: '2026-11-23'
status: 'built'
route: 'full'
route_source: 'auto'
review: 'full'
review_source: 'auto'
lenses_ran: ['adversarial', 'edge-cases', 'verification-gaps']
baseline_revision: '3080ff0bb2e895b9d77b14d33dc29f0be7152d7f'
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/spike-app-state-probes-part-3-quick-filing-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/story-get-windows-and-navigate-to-in-a-chosen-window-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can't ask the user to point at a place in their notebooks; it has to guess from names. OneNote has its own location picker (`QuickFiling`), and spike 4.13 showed it works from our process: `Run` returns at once, the answer arrives by callback on an MTA thread, and the STA stays free while the user decides.

**Approach:** Declare `IQuickFilingDialog` and its callback early-bound in `OneNoteMcp.Com`; add a picker call to the port and both gateways that shows the picker and hands back a pending answer; ship a user-visible `quick_filing` tool on story 4.3's `AppStatePipeline` (no lock) whose wait for the user runs after the pipeline, bounded by a fixed 300 s and the client's cancel (spine AD-2, AD-3 Idle, AD-7 `AppState` `quick_filing`, AD-9, AD-11; epic A8).

## Boundaries & Constraints

**Always:**
- One STA delegate: `QuickFiling()`, the cast to `IQuickFilingDialog`, `Title`, `Description`, `TreeDepth = heSections`, one `AddButton` (sections, default), then `Run(callback)`. Never `ParentWindowHandle`, never another member (spine Coverage, QuickFiling dialog's members). `CallKind.AppState`: never retried after a disconnect, `ComBusy` while an abandoned write is outstanding, not an abandoned write itself.
- The callback follows AD-2: it may only read `SelectedItem` and `PressedButton` on the dialog it is given, record them, complete a `TaskCompletionSource` created with `RunContinuationsAsynchronously`, and return. It catches everything (never throws into OneNote), never touches the STA's `IApplication`, never blocks, and works on any thread.
- The gateway holds the picker from just before `Run` until the answer arrives or the caller lets go, whichever is first: while held, AD-3's idle release doesn't drop the reference. Letting go is idempotent, restarts the idle timer, releases the dialog wrapper on its STA if that worker is still live (otherwise drops it), and makes a later answer a no-op.
- Pipeline steps as the spine sets them: no lock, no grant, no basis or snapshot; step 7 is `Run` only. A step-7 `Timeout`, `ComUnavailable`, `Cancelled` or `0x800706BE` is `outcomeUnknown` (a picker may be on screen). The wait for the user is outside the 120 s deadline.
- One waiting call per process: a second `quick_filing` while one is waiting is `ComBusy`, retryable, before COM. The slot is freed when the wait ends (answer, 300 s, or the client's cancel), even if the old picker is still on screen.
- Outcomes: a pick (`SelectedItem` non-empty) is `ok` with the chosen section; Cancel or Esc (no item, `PressedButton` `0xFFFFFFFF`) is `ok` saying the user cancelled; the 300 s limit is `ok` with nothing chosen and a warning that the picker may still be on screen and a choice made in it now is ignored; a client cancel during the wait is `Cancelled`, retryable, never `outcomeUnknown`. Codes and result shape per Decision OQ1.
- `quick_filing` is user-visible (AD-11): its description says so, that it may bring OneNote forward, that it doesn't block OneNote, and that Claude calls it only when the user asks to choose a place. It promises no focus behaviour and no modality.
- Logs carry the outcome, the chosen ID, timings and the lengths of the title and description, never their text (AD-13). Contract snapshot checked (AD-14).

**Never:** a write lock; `ParentWindowHandle`; `WM_CLOSE` or any other attempt to close the picker; tree depths other than sections, or more than one button; `SetFilingLocation`; a setting for the 300 s; acting on a late answer; any spine change in this story beyond the AD-2 exception (Decision OQ2); setting the integration env var in the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Pick | user chooses a section | `ok`, the section's ID and its `one:Section` element | — |
| Cancel or Esc | no item, `0xFFFFFFFF` | `ok`, warning `pickerCancelled` | — |
| Wait ends | no answer in 300 s | `ok`, nothing chosen, warning `pickerLeftOpen` | slot and hold freed |
| Late answer | answer after the wait ended | dropped, logged | no error |
| Client cancel during wait | token cancelled after `Run` | slot and hold freed | `Cancelled`, retryable, not `outcomeUnknown` |
| Second call while waiting | one picker waiting | no COM | `ComBusy`, retryable |
| Abandoned write outstanding | — | no COM | `ComBusy` |
| `Run` times out or disconnects | step 7 | — | `outcomeUnknown`, slot and hold freed |
| Modal dialog | `0x80042030` from a setter or `Run` | — | `ModalDialogOpen` passes through |
| Getter fails in callback | `SelectedItem` throws | — | mapped error (`HResultMap`), not `outcomeUnknown` |
| Title or description | over 255 / 1024 chars, or control characters | no COM | `InvalidArgument` |
| Picker open, other tool | `get_hierarchy` during the wait | runs normally | — |

## Decisions (Phill, 2026-11-23, Inbox plan approval)

- **OQ1 (a):** a pick returns `meta.objectId` = the section's ID and `xml` = its own `one:Section` element from one `GetHierarchy(id, hsSelf)` read after the answer, as `close_notebook` returns its notebook's element (if that read fails, `ok` with the ID only). Cancel or Esc: `ok`, warning `pickerCancelled`. The wait ending: `ok`, warning `pickerLeftOpen`.
- **OQ2 (a):** only the callback interface (its dialog parameter typed `object`, cast to the internal `IQuickFilingDialog` inside) and a public sealed callback class with an internal constructor are public; the dialog interface stays internal. The runtime exposes a managed object to COM only through public interfaces (the spike's CCW answered `E_NOINTERFACE` while the callback interface was internal). A small spine amendment adds this exception to AD-2, with a memlog decision line, in its own commit.
- **OQ3 (a):** an opt-in integration test drives the gateway and use case with a 20 s wait override (sitting steps 3-4); Claude Code covers pick, cancel and the client cancel.
- **OQ4 (a):** the STA replacement with a picker open is skipped; the spine's Deferred row stays open, inferred safe since the callback never uses the STA.
- **OQ5 (a):** built now from main, in parallel with 4.7; whichever merges second resolves the tool list, snapshot and skill.
- **OQ6 (a):** the plan is kept whole (about 4,000 tokens, one goal).

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IApplication.cs:146-148` -- slot 27 `QuickFiling()` returns `object` (IDispatch); keep it, cast inside the delegate. Order frozen.
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeQuickFilingInterop.cs` -- spike 4.13's measured layout: `IProbeQuickFilingDialog` GUID `1D12BD3F-89B6-4077-AA2C-C9DC2BCA42F9`, dual, slots 7-29 (`Title` 7/8, `Description` 9/10, `WindowHandle` 15, `TreeDepth` put, `AddButton`, `Run` 24, `SelectedItem`, `PressedButton`; the rest `[Obsolete(error: true)]` placeholders); callback GUID `627EA7B4-95B5-4980-84C1-9D20DA4460B1`, slot 7 `OnDialogClosed`; `ProbeHierarchyElement` values; `FilingRecorder` (`ComVisible`, `ClassInterfaceType.None`, catches everything). Write our own from it in `Com/Interop`; don't reference the test project. Its doc comment records that the CCW answered `E_NOINTERFACE` while the callback interface was internal (Decision OQ2).
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs:1661-1738` (`RunPickerAsync`, `RunPicker`) -- the probed call order; `ProbeHelpersTests.cs:1078-1230` -- layout, CCW QI and recorder tests to mirror in `InteropLayoutTests`.
- `src/OneNoteMcp.Com/ComGateway.cs` -- `SyncHierarchyAsync` (394-415) for an `AppState` call; `BeginCall`/`EndCall`/`OnIdle` (1396-1430) gain the picker hold (a count under `gate`; `OnIdle` returns while it is above 0); `ThrowIfWriteOutstanding` (1288).
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- default-interface `AppState` methods refusing `NotSupported` (`SyncHierarchyAsync` 82); add the picker method the same way, returning a Core `QuickFilingPicker` (pending answer + idempotent `Release`).
- `src/OneNoteMcp.Core/AppState/AppStatePipeline.cs`, `AppStateOperation.cs` -- unchanged; the operation's `CallAsync` returns the picker, the use case waits after `RunAsync` returns.
- `src/OneNoteMcp.Core/UseCases/SyncHierarchyUseCase.cs`, `CloseNotebookUseCase.cs` (returns `ObjectId` and the node's XML) -- patterns for `QuickFilingUseCase`; `src/OneNoteMcp.Server/Tools/SyncHierarchyTool.cs` for the tool; `ServerSetup.cs:110-113` registration.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- add the codes (Decision OQ1); `ToolMeta.ObjectId` for the choice.
- Tests: `FakeGateway.cs` (`OnSyncCall`-style hook + scripted answer), `FakeApplication.cs:131` (`QuickFiling` throws today: return a fake dialog), `InteropLayoutTests.cs:84-130`, `AppStatePipelineTests`, `ComGatewayLifecycleTests`, `ToolContractTests.cs:86`, `Contract/tool-contract.json`, `McpSdkBindingTests.cs:443`, `PluginPackagingTests`; `plugin/skills/guide/SKILL.md` (tool table, line 8 count, retry rules, warnings table), `README.md` (tool table), `spikes/stress-harness/Calls.cs` (never called by the harness, as `get_windows`).

## Tasks & Acceptance

**Execution:**
- [x] `Com/Interop/IQuickFilingDialog.cs`, `IQuickFilingDialogCallback.cs`, `HierarchyElement` enum, `InteropLayoutTests.cs` -- early-bound declarations, visibility per Decision OQ2; layout, GUID and CCW QI tests.
- [x] `Core`: `QuickFilingPicker`/`QuickFilingAnswer` types and the port method; `ComGateway` (one STA delegate, the callback class, the picker hold, release on let-go); `FakeGateway`, `FakeApplication`.
- [x] `Core/UseCases/QuickFilingUseCase.cs` -- argument checks, the one-waiting-call slot, the pipeline call, the 300 s wait on an injected `TimeProvider` (a test override for the limit, as the pipeline's deadline), outcomes, and the read per Decision OQ1; `ToolWarnings`.
- [x] `Server/Tools/QuickFilingTool.cs`, `ServerSetup.cs` -- thin tool, user-visible description.
- [x] Unit tests: `QuickFilingToolTests` for every matrix row on `FakeGateway`; `ComGatewayLifecycleTests`: the call order, no idle release while held, idle release after let-go, a late answer is a no-op, the callback on a non-STA thread completes the answer.
- [x] `tests/OneNoteMcp.Integration/QuickFilingIntegrationTests.cs` -- opt-in, for the sitting (Decision OQ3).
- [x] Contract snapshot, tool list, binding count; skill, README, stress-harness rows.

**Acceptance Criteria:**
- Given a picker waiting, when idle time passes, then the COM reference is not released; given the wait ended, when idle time passes, then it is.
- Given the callback arrives on a thread other than the STA, when it completes, then the waiting call gets the answer and no STA work was queued by it.

## Design Notes

- Two-phase call: the pipeline runs step 7 (`Run`, ~150 ms) under its deadline and returns the picker; the use case then waits on the picker's answer with its own 300 s timer and the client's token, and always calls `Release` in a `finally`. That keeps the wait outside the deadline (AD-7) and gives the right error per phase: a cancel in step 7 is `outcomeUnknown`, a cancel in the wait is not.
- The ticket says "tree depth from the arguments" and "notebook, section or page ID"; the spine, written after spike 4.13, fixes `TreeDepth` to sections (the only depth probed) and one button. The spine is followed, so the result is always a section.
- Arguments: optional `title` (default "Choose a section") and `description` (default empty). The 255 / 1024 caps are ours, not OneNote's (unprobed), to keep a runaway string off the user's screen.
- Considered keeping the waiting slot in the gateway; going with the use case (a singleton), because the slot is a tool rule (AD-7) and the gateway's hold is a separate AD-3 rule.

## Live sitting (Phill, about 15 minutes; hitl)

Before: OneNote open on an MCP Test page with VS Code in front. The build session stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build.

1. In Claude Code he asks "let me pick a section in OneNote". The picker appears with the title Claude set. He chooses MCP Test > Probes and presses the button. **Expect** the result to name that section.
2. He asks again and presses Cancel. **Expect** "cancelled". Again with Esc: the same.
3. He runs `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter QuickFiling` (Decision OQ3): it opens a picker and, while it is open, reads the hierarchy and a page through the same gateway; both pass. Its short wait ends with the warning; the picker stays on screen.
4. With that old picker still open, he waits 10 s (past the 5 s idle release), then picks a section in it. **Expect** the picker to close with no error, hang or OneNote message; the test's final read passes (spine Deferred: last reference dropped with a picker on screen).
5. In Claude Code he asks for the picker and, while it is open, presses Esc in Claude Code to cancel the call. **Expect** the picker to stay; he closes it with Cancel. He asks for the picker once more: it opens (the slot was freed), and he cancels.

**Sitting record (Phill, 2026-11-25, against the merged 4.7 + 4.9 build):**

- Step 1 passed: the picker showed Claude's title; MCP Test Cloud > Probes was chosen in about 8 s and the result named that section. The callback's cast of OneNote's dialog proxy works.
- Step 2 passed: Cancel and Esc both returned `pickerCancelled`.
- Steps 3 and 4 passed (integration test, 20 s wait): reads with the picker open ok; the wait ended with `pickerLeftOpen`; the idle release with the picker on screen was harmless; the late answer was dropped; the final read passed.
- Step 5 passed: after Esc in Claude Code cancelled the call, the picker stayed on screen. The next picker call was accepted (the slot was freed) and the picker appeared; it went unanswered past the 300 s wait, so the call ended with `pickerLeftOpen`, and Phill then cancelled it.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Implementation Notes

- The spine amendment (AD-2's one public interop type, Decision OQ2) and its memlog line are their own commit, before the code.
- `QuickFilingCallback` lives in `OneNoteMcp.Com` (not `Interop`); `IQuickFilingDialog` declares `WindowHandle` and `ParentWindowHandle` as uncallable placeholders, so the one STA delegate can't touch them. The button text is "Choose".
- The gateway's hold (`PickerHold`) begins on the STA just before `Run`; if the caller already let go (a deadline or cancel while the delegate was still queued or setting up), `Run` is skipped and the dialog released there. A failed, timed-out or cancelled `ShowQuickFilingAsync` lets go itself. The answer ends the hold through a thread-pool continuation, so the callback takes no gateway lock and queues no STA work; `Release` posts the dialog's release to its STA only when that worker is still the live one.
- A pick whose self read returns a page (not expected with `heSections`) is reported with the ID only, so no page element leaves AD-21's checks.
- `spikes/stress-harness/StressHarness.csproj` compiles `Com/Interop/*.cs` into itself; it now excludes `IQuickFiling*.cs`, since the public callback interface would clash with the referenced assembly's (CS0436).
- Not done in the build session: the integration test (needs `ONENOTE_MCP_INTEGRATION=1` and Phill; sitting steps 3-4) and the live sitting. The callback's cast of OneNote's dialog (`object` → `IQuickFilingDialog`, a QI on the proxy OneNote passes) is untested against real OneNote: the spike passed the dialog typed, so step 1 of the sitting is its first proof.

## Plan Change Log

## Review Triage Log

Iteration 1 (2026-11-23, build session, lenses run inline: adversarial, edge cases, verification gaps; diff against origin/main).

- **patch** -- A cancel was told apart from a pick only by an empty `SelectedItem`. Spike 4.13 cancelled only with nothing selected, so whether OneNote names a highlighted section on Cancel or Esc is unprobed; if it does, a cancelled choice would have come back as a pick. `QuickFilingAnswer.Picked` now also needs `PressedButton` other than `0xFFFFFFFF`. Test `CancelWithAnItemStillNamed_IsPickerCancelled_NeverAPick`.
- **patch** -- A title of only spaces was shown as a blank title; it now gets the default, as an omitted one does. Test `ABlankTitle_ShowsTheDefaultTitle`.
- **patch** (verification gap) -- The let-go-before-`Run` path (the call's deadline ends while the STA call is still in `QuickFiling` or the setters) had no test. Added `QuickFiling_ACallThatTimesOutBeforeRun_NeverRuns_AndReleasesTheDialog`: `Run` is never called, the dialog is released, no hold is left.
- **dismissed** -- `ReadChosenAsync` swallows a client cancel during the self read and returns ok with the ID only. The pick already happened; per OQ1 a failed read is ok with the ID only, and a cancel there is that case.
- **dismissed** -- The dialog RCW OneNote passes to the callback (on its MTA thread) isn't released explicitly; it is dropped to the finalizer. Releasing it would be a COM call off the STA beyond the two getters AD-2 allows.
- **dismissed** -- Lock order: `PickerHold.sync` is taken before the gateway's `gate`, never the reverse (`ReleaseDialog` and `RestartIdleTimer` run outside `sync`). No deadlock path.
- **dismissed** -- The pipeline returns the picker straight from step 7 with nothing after it that can throw, and the use case's `finally` releases it, so no path leaves a hold the caller can't release.
- **defer (sitting)** -- The callback's cast of OneNote's own dialog proxy (`object` to `IQuickFilingDialog`) and the Cancel-with-highlight case are only provable live: sitting steps 1 and 2.
