---
title: 'quick_filing'
type: 'feature'
ticket: '9'
created: '2026-11-23'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
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
- Outcomes: a pick (`SelectedItem` non-empty) is `ok` with the chosen section; Cancel or Esc (no item, `PressedButton` `0xFFFFFFFF`) is `ok` saying the user cancelled; the 300 s limit is `ok` with nothing chosen and a warning that the picker may still be on screen and a choice made in it now is ignored; a client cancel during the wait is `Cancelled`, retryable, never `outcomeUnknown`. Codes and result shape per Open Question 1.
- `quick_filing` is user-visible (AD-11): its description says so, that it may bring OneNote forward, that it doesn't block OneNote, and that Claude calls it only when the user asks to choose a place. It promises no focus behaviour and no modality.
- Logs carry the outcome, the chosen ID, timings and the lengths of the title and description, never their text (AD-13). Contract snapshot checked (AD-14).

**Never:** a write lock; `ParentWindowHandle`; `WM_CLOSE` or any other attempt to close the picker; tree depths other than sections, or more than one button; `SetFilingLocation`; a setting for the 300 s; acting on a late answer; any spine change in this story; setting the integration env var in the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Pick | user chooses a section | `ok`, the section's ID (Q1) | — |
| Cancel or Esc | no item, `0xFFFFFFFF` | `ok`, says cancelled (Q1) | — |
| Wait ends | no answer in 300 s | `ok`, nothing chosen, warning (Q1) | slot and hold freed |
| Late answer | answer after the wait ended | dropped, logged | no error |
| Client cancel during wait | token cancelled after `Run` | slot and hold freed | `Cancelled`, retryable, not `outcomeUnknown` |
| Second call while waiting | one picker waiting | no COM | `ComBusy`, retryable |
| Abandoned write outstanding | — | no COM | `ComBusy` |
| `Run` times out or disconnects | step 7 | — | `outcomeUnknown`, slot and hold freed |
| Modal dialog | `0x80042030` from a setter or `Run` | — | `ModalDialogOpen` passes through |
| Getter fails in callback | `SelectedItem` throws | — | mapped error (`HResultMap`), not `outcomeUnknown` |
| Title or description | over 255 / 1024 chars, or control characters | no COM | `InvalidArgument` |
| Picker open, other tool | `get_hierarchy` during the wait | runs normally | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IApplication.cs:146-148` -- slot 27 `QuickFiling()` returns `object` (IDispatch); keep it, cast inside the delegate. Order frozen.
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeQuickFilingInterop.cs` -- spike 4.13's measured layout: `IProbeQuickFilingDialog` GUID `1D12BD3F-89B6-4077-AA2C-C9DC2BCA42F9`, dual, slots 7-29 (`Title` 7/8, `Description` 9/10, `WindowHandle` 15, `TreeDepth` put, `AddButton`, `Run` 24, `SelectedItem`, `PressedButton`; the rest `[Obsolete(error: true)]` placeholders); callback GUID `627EA7B4-95B5-4980-84C1-9D20DA4460B1`, slot 7 `OnDialogClosed`; `ProbeHierarchyElement` values; `FilingRecorder` (`ComVisible`, `ClassInterfaceType.None`, catches everything). Write our own from it in `Com/Interop`; don't reference the test project. Its doc comment records that the CCW answered `E_NOINTERFACE` while the callback interface was internal (Open Question 2).
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs:1661-1738` (`RunPickerAsync`, `RunPicker`) -- the probed call order; `ProbeHelpersTests.cs:1078-1230` -- layout, CCW QI and recorder tests to mirror in `InteropLayoutTests`.
- `src/OneNoteMcp.Com/ComGateway.cs` -- `SyncHierarchyAsync` (394-415) for an `AppState` call; `BeginCall`/`EndCall`/`OnIdle` (1396-1430) gain the picker hold (a count under `gate`; `OnIdle` returns while it is above 0); `ThrowIfWriteOutstanding` (1288).
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- default-interface `AppState` methods refusing `NotSupported` (`SyncHierarchyAsync` 82); add the picker method the same way, returning a Core `QuickFilingPicker` (pending answer + idempotent `Release`).
- `src/OneNoteMcp.Core/AppState/AppStatePipeline.cs`, `AppStateOperation.cs` -- unchanged; the operation's `CallAsync` returns the picker, the use case waits after `RunAsync` returns.
- `src/OneNoteMcp.Core/UseCases/SyncHierarchyUseCase.cs`, `CloseNotebookUseCase.cs` (returns `ObjectId` and the node's XML) -- patterns for `QuickFilingUseCase`; `src/OneNoteMcp.Server/Tools/SyncHierarchyTool.cs` for the tool; `ServerSetup.cs:110-113` registration.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- add the codes (Q1); `ToolMeta.ObjectId` for the choice.
- Tests: `FakeGateway.cs` (`OnSyncCall`-style hook + scripted answer), `FakeApplication.cs:131` (`QuickFiling` throws today: return a fake dialog), `InteropLayoutTests.cs:84-130`, `AppStatePipelineTests`, `ComGatewayLifecycleTests`, `ToolContractTests.cs:86`, `Contract/tool-contract.json`, `McpSdkBindingTests.cs:443`, `PluginPackagingTests`; `plugin/skills/guide/SKILL.md` (tool table, line 8 count, retry rules, warnings table), `README.md` (tool table), `spikes/stress-harness/Calls.cs` (never called by the harness, as `get_windows`).

## Tasks & Acceptance

**Execution:**
- [ ] `Com/Interop/IQuickFilingDialog.cs`, `IQuickFilingDialogCallback.cs`, `HierarchyElement` enum, `InteropLayoutTests.cs` -- early-bound declarations, visibility per Q2; layout, GUID and CCW QI tests.
- [ ] `Core`: `QuickFilingPicker`/`QuickFilingAnswer` types and the port method; `ComGateway` (one STA delegate, the callback class, the picker hold, release on let-go); `FakeGateway`, `FakeApplication`.
- [ ] `Core/UseCases/QuickFilingUseCase.cs` -- argument checks, the one-waiting-call slot, the pipeline call, the 300 s wait on an injected `TimeProvider` (a test override for the limit, as the pipeline's deadline), outcomes, and the read per Q1; `ToolWarnings`.
- [ ] `Server/Tools/QuickFilingTool.cs`, `ServerSetup.cs` -- thin tool, user-visible description.
- [ ] Unit tests: `QuickFilingToolTests` for every matrix row on `FakeGateway`; `ComGatewayLifecycleTests`: the call order, no idle release while held, idle release after let-go, a late answer is a no-op, the callback on a non-STA thread completes the answer.
- [ ] `tests/OneNoteMcp.Integration/QuickFilingIntegrationTests.cs` -- opt-in, for the sitting (Q3).
- [ ] Contract snapshot, tool list, binding count; skill, README, stress-harness rows.

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
3. He runs `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter QuickFiling` (Q3): it opens a picker and, while it is open, reads the hierarchy and a page through the same gateway; both pass. Its short wait ends with the warning; the picker stays on screen.
4. With that old picker still open, he waits 10 s (past the 5 s idle release), then picks a section in it. **Expect** the picker to close with no error, hang or OneNote message; the test's final read passes (spine Deferred: last reference dropped with a picker on screen).
5. In Claude Code he asks for the picker and, while it is open, presses Esc in Claude Code to cancel the call. **Expect** the picker to stay; he closes it with Cancel. He asks for the picker once more: it opens (the slot was freed), and he cancels.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Open Questions

1. **The result's shape and the warning codes.** The spine leaves the codes to this story (AD-9) and asks the result to "give the chosen ID, or say the user cancelled"; the ticket's sitting wants the result to name the section.
   - (a) A pick: `meta.objectId` = the section's ID and `xml` = its own `one:Section` element from one `GetHierarchy(id, hsSelf)` read after the answer, as `close_notebook` returns its notebook's element (if that read fails, `ok` with the ID only). A cancel: `ok`, warning `pickerCancelled`. The wait ending: `ok`, warning `pickerLeftOpen`.
   - (b) The same codes, but the ID only, no read after the answer: Claude calls `get_hierarchy` itself to name it.
   - (c) A new `meta.pickerOutcome` field (`chosen`, `cancelled`, `waitEnded`) instead of warnings.
   Recommended: (a)
2. **The callback must be public, against AD-2's "interop types are internal".** The runtime exposes a managed object to COM only through public interfaces; the spike's CCW answered `E_NOINTERFACE` while its callback interface was internal.
   - (a) Make only the callback interface public, with the dialog parameter typed `object` (cast to the internal `IQuickFilingDialog` inside), plus a public sealed callback class with an internal constructor; the dialog interface stays internal. A spine chore adds the exception to AD-2.
   - (b) Make both interfaces and the class public, as the spike did.
   - (c) Keep everything internal and build the CCW with `ComWrappers` (source-generated COM), which nothing here has tried.
   Recommended: (a)
3. **How the sitting checks the wait's end, idle release with a picker on screen, and another call during a wait.** The tool's 300 s limit has no setting.
   - (a) An opt-in integration test drives the gateway and use case with a 20 s limit override (steps 3-4); Claude Code covers pick, cancel and the client cancel. About 15 minutes.
   - (b) Everything through Claude Code with the real 300 s, and a second Claude call during the wait. About 25 minutes, and the "other call" depends on Claude Code running two tool calls at once, which isn't certain.
   Recommended: (a)
4. **The STA replacement with a picker open (spine Deferred).** The spine says 4.9 tries it "if a sitting can poison the STA safely".
   - (a) Skip it: the row stays open, inferred safe since the callback never uses the STA.
   - (b) Add a test-only hang to the integration test to poison the STA while a picker is open, then pick.
   Recommended: (a)
5. **Build order.** Entry 9's `after` is entry 7 (`navigate_to_url`), which has no plan or branch yet; nothing in this story uses its code.
   - (a) Build 4.9 now from main; whichever of 4.7 and 4.9 merges second takes the tool-list, snapshot and skill merge.
   - (b) Wait for 4.7.
   Recommended: (a)
6. **Plan size.** About 4,000 tokens, over bmad-build's 1,600 target, as 4.4-4.6 were. It is one goal (one tool and the interop it needs).
   - (a) Keep the plan whole.
   - (b) Split the interop and gateway into their own story.
   Recommended: (a)
