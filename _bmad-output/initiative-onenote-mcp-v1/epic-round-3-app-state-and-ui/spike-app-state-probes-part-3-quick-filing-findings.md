---
title: 'Findings: app-state probes, part 3: quick filing'
type: 'spike-findings'
findings_for: '13'
status: 'final'
created: '2026-11-22'
machine: 'Windows 11 Pro 10.0.26300, OneNote desktop (M365), not elevated; the OneNote build was not recorded by this run'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state-filing-20261122-024248.jsonl (outside the repo; one run, 2026-11-22 02:42–03:09 UTC, test run successful); Phill''s reports and screenshots per pause, in the coordinator''s sitting notes and the 4.13 Inbox thread (the screenshots show notebooks outside the test set and stay out of the repo)'
---

# Findings: app-state probes, part 3: quick filing

One sitting on 2026-11-22 (UTC), one run (stamp `20261122-024248`), every step of the Sitting script in order: `bind-pick`, `modal`, `while-open`, `cancel`, `late`, `release`, `thread`, `cleanup`. Every pause was answered by Phill; none was skipped. OneNote started on MCP Test › Probes (every F/V `before` read), not on Seed as the Sitting script asked; no row depends on the starting page. No quick-filing call failed and no call hit the 60 s limit. The only HRESULT in the run is the deliberate `Windows.Item(Count)` read in F3's parent lookup (H); the run's `hresult-summary` row is empty (`{}`) because it doesn't count that read.

**Top finding: the callback reaches our process, but in this run never on our STA thread.** All nine `OnDialogClosed` calls arrived on an MTA thread of our process (managed thread IDs 15, 15, 30, 34, 23, 22, 22, 16, 16), never on the STA thread that called `Run` (9, or 24 for the side gateway in F8). `Run` returns in 134–199 ms, before the user has chosen anything, and the STA thread is free while an unparented picker is open: it served `GetHierarchy`, `GetPageContent`, a guarded `UpdatePageContent` and a second `Run` (F4). So epic A8's condition as written ("its callback reaches our STA thread") isn't met, but what A8 needs is: the answer reaches the server, and the STA queue stays free while the user decides. Phill has since reworded the condition to "reaches our process" (Implication 1). Why the call lands on an MTA thread isn't known; one machine, one run. A guess, unchecked: .NET's COM-callable wrapper for our callback is agile, so OneNote's call isn't marshalled to the STA.

**Second finding: none of the three things we tried on our side closed the picker or stopped its answer.** A wait that ended (F6), a `FinalReleaseComObject` on the dialog plus dropping the callback object (F7), and disposing the side gateway that ran it (F8) all left the picker on screen, and the user's later pick still called back, with the section, on an MTA thread, and with no error or hang in OneNote. The interface has no close method (slots 7–29, plan's Code Map); posting `WM_CLOSE` to the picker's window wasn't tried (Not probed). So, as far as probed, a picker left on screen stays until the user closes it, as A8 assumes, and dropping its answer is our code's job.

**Third finding: the parented picker wasn't modal to OneNote's clicks.** With `ParentWindowHandle` set to the inner pane OneMore passes (F3), Phill reports he could still click in OneNote and change pages behind the picker, though he couldn't type. Only that parent was tried; the top-level frame wasn't, and modality without a parent wasn't asked. With an unparented picker open, COM calls, including a write, returned `ok`, never `0x80042030`, and two pickers were open at once (F4).

**Short answer:**

1. **Binding works early-bound** from our 64-bit STA thread: `QuickFiling()` (0–2 ms) casts to the spike's `IQuickFilingDialog` (QI ok), and `Title`, `Description`, `TreeDepth`, `ParentWindowHandle` and `AddButton` all return S_OK in 0–5 ms (F1).
2. **`Run` returns at once** (134–199 ms over nine runs) and the picker's own `WindowHandle` is a live top-level window (2–6 ms to read) (F2).
3. **`OnDialogClosed` arrives** for every way the picker closed here: a pick, Cancel, Esc, a pick after our wait ended, after we released the dialog, after the side gateway was disposed (F2, F5–F8). In this run always on an MTA thread, never the STA (top finding).
4. **A pick gives** `SelectedItem` = the section's ID (MCP Test › Probes) and `PressedButton` = 0, the index of our one added button. **A cancel** (Cancel button or Esc) gives no selected item and `PressedButton` = `0xFFFFFFFF` (F5). Both getters answered without an error from the MTA thread, on the dialog OneNote passed in.
5. **The picker** shows the title and description we set, a search box, and an All Notebooks tree of open notebooks down to sections; "File here" is disabled until a section is chosen (F2, Phill's screenshots). One throwaway notebook (`b`) was missing from the tree while `a` and `c` were listed; unexplained (Evidence gaps).
6. **The picker takes the foreground.** Every F/V `before` read had another app in front (`other`, nine of nine). Straight after `Run` the foreground was `ONENOTE` in six (`modal`, both `cancel` runs, `late`, `release`, `thread`) and still `other` in three (`bind-pick` and both F4 runs). Phill reported focus only for F2's picker, which came to the front with keyboard focus, so F2's `other` read was probably too early (Evidence gaps).
7. **With an unparented picker open, other calls work** (F4): `GetHierarchy` 16 ms, `GetPageContent` 32 ms, guarded `UpdatePageContent` 293 ms (applied: the marker was on the page afterwards), and a second `Run` ok in 161 ms, its picker on top of the first. No `0x80042030`, no message, no busy cursor. With a parented picker open, nothing was called (Not probed).
8. **The wait can be bounded** (F6): a 10 s wait ended with nothing (10,002 ms), and Phill's later pick still called back 23.0 s after `Run`. The session gateway's `GetHierarchy` (21 ms) ran after that late callback, so it says the gateway was fine afterwards, not while the picker was open; only F4 covers calls with a picker open.
9. **Error codes:** none from any quick-filing call. The only HRESULT was `E_INVALIDARG` from `Windows.Item(Count)`, a OneNote call the probe makes past the end on purpose to find F3's parent handle (4.12's W1) (H).

## Results, one row per Matrix question

| Id | Result |
|---|---|
| F1 | From the session gateway's STA thread (managed ID 9, apartment STA): `QuickFiling` ok 2 ms (an `__ComObject`), QI to `IQuickFilingDialog` ok 0 ms, `put_Title` ok 5 ms, `put_Description`, `put_TreeDepth` (`heSections`) and `AddButton` ("File here", `heSections`, `heNone`, default) ok 0 ms each. F3's run repeated the set with `put_ParentWindowHandle` added, all ok, 0–1 ms. No HRESULT on any call. |
| F2 | `Run` with no parent: ok, returned in 152 ms; picker window a live top-level window. F/V straight after: foreground still `other`, view unchanged (MCP Test › Probes). Phill: the picker appeared at once, came to the front with keyboard focus, showed the title and description, a search box and an All Notebooks tree of open notebooks down to sections (MCP Test's sections listed; throwaway `b` missing, `a` and `c` listed); "File here" was disabled until he selected a section; it closed on "File here". `OnDialogClosed` arrived once, 30.5 s after `Run` returned (Phill's time to pick), on managed thread 15, apartment MTA, not the STA thread; `SelectedItem` = MCP Test › Probes (a section ID), `PressedButton` 0; both getters ok. |
| F3 | Parent = `CurrentWindow.WindowHandle` from 4.12's window read (the value OneMore passes): a live window but not top-level (an inner pane, as 4.12's W1 found). `Run` ok 155 ms; picker top-level, its root the foreground window; F/V after: foreground `ONENOTE`, view unchanged. Phill: **not modal to clicks**: he could click around OneNote and change pages behind it (he didn't try notebooks or sections) but couldn't type in OneNote; Cancel closed it. He wasn't asked about focus here; the foreground comes from the probe's read. Callback 36.5 s after `Run`, thread 15, MTA; nothing selected, `PressedButton` `0xFFFFFFFF`. Only the inner pane was tried as parent; the frame (the pane's root) wasn't, and whether it would make the picker modal is unknown. |
| F4 | All unparented. First picker `Run` ok 139 ms. Then, session gateway, each with no callback yet from the first picker (`firstCallbacks` 0 recorded on these three): `GetHierarchy` (notebooks) ok 16 ms; `GetPageContent` on `probe filing page` ok 32 ms; guarded `UpdatePageContent` (5.1's guard, the page's own date, one outline with a marker) ok 293 ms, which includes the guard's two hierarchy reads. Then a second `QuickFiling().Run` ok 161 ms; that row has no `firstCallbacks`, but the first picker's callback came 33.8 s after its `Run`, so it was still open. No `0x80042030`. The write applied: a read after the pickers closed found the marker (`markerOnPage` true, 45 ms). Phill: a second picker appeared on top; no message, no busy cursor. He cancelled both; callbacks on threads 30 and 34, both MTA, nothing selected, `PressedButton` `0xFFFFFFFF` each. F/V after both `Run`s: foreground `other`, view unchanged. |
| F5 | Cancel button: `Run` ok 156 ms; callback 2.3 s after `Run`, thread 23, MTA. Esc: `Run` ok 134 ms; callback 1.4 s after `Run`, thread 22, MTA. Both: `SelectedItem` none, `PressedButton` `0xFFFFFFFF`, getters ok; Phill saw each close. Foreground `ONENOTE` after each `Run` (probe's read). |
| F6 | `Run` ok 153 ms. The probe's bounded wait ended with no callback after 10,002 ms (limit 10,000 ms) and marked itself over; the dialog and recorder stayed referenced. Phill then picked MCP Test › Probes: the callback still arrived, 23.0 s after `Run`, thread 22, MTA, with the section and `PressedButton` 0. Session gateway `GetHierarchy` after the callback ok 21 ms. Phill: the picker closed on the button; nothing else (no message, error or slowness). |
| F7 | `Run` ok 140 ms; 3 s later, before any callback, `Marshal.FinalReleaseComObject(dialog)` ok (returned 0) and the callback object dropped with a forced GC; the recorder was still alive afterwards (`WeakReference` not collected; presumably OneNote's reference keeps the wrapper alive). Phill: the picker stayed open; he picked Probes and it closed; no error, message or hang. Callback arrived 21.5 s after `Run`, thread 16, MTA, with the section and `PressedButton` 0. Session `GetHierarchy` afterwards ok 20 ms. This releases the dialog, not the gateway's `IApplication` reference: AD-3's idle release wasn't exercised (Not probed). |
| F8 | `Run` on a side gateway's STA thread (managed ID 24) ok 199 ms. 3 s later, before any callback, the side gateway was disposed in 2 ms with no dispose error; that its STA thread ended is inferred from the dispose's join returning within its 5 s limit. Phill: the picker stayed open; he picked Probes and it closed; no error, message or hang. Callback arrived 22.5 s after `Run`, thread 16, MTA, with the section and `PressedButton` 0. Session gateway `GetHierarchy` afterwards ok 18 ms. This was a clean dispose of a healthy gateway, not AD-3's replacement of a poisoned STA (Not probed). |
| H | No HRESULT from any quick-filing call: `QuickFiling`, the QI, every setter, `Run`, `get_WindowHandle`, `SelectedItem`, `PressedButton`, and F4's reads and write all returned S_OK. One OneNote call failed, on purpose: `Windows.Item(1)` (= `Item(Count)`) in F3's parent read gave `0x80070057` E_INVALIDARG (4.12 W1; no AD-9 row, so `Internal`). The run's `hresult-summary` row is `{}` because it doesn't count that read. |

## HRESULTs observed

| HRESULT | Where | AD-9 code |
|---|---|---|
| `0x80070057` E_INVALIDARG | `Windows.Item(Count)`, F3's parent read (the probe's deliberate past-the-end read) | none stated, so `Internal`; never reached if a tool stops at `Count - 1` |

No other code. The quick-filing calls produced none, so this spike adds nothing to AD-9's map. In particular `0x80042030` (modal dialog open) wasn't returned with an unparented picker open (F4).

## Does the callback reach us

Yes, every time (nine of nine), including after our wait ended (F6), after we released the dialog (F7) and after the side gateway that ran it was disposed (F8). In this run it arrived on an MTA thread, a different one most times, never on the STA thread that called `Run`. Why isn't known, and it is one machine and one run, so a tool must not count on any particular thread. So:

- The callback must be safe on any thread, including the STA and a thread running at the same time as it: it may only record the answer and signal (as the probe's recorder does), never touch the STA's `IApplication` reference or wait on the STA.
- Reading `SelectedItem` and `PressedButton` on the dialog passed to the callback worked from the calling thread (nine of nine).
- OneNote waits for the callback (OneMore row 26), so it must return at once; the recorder did, and no stall was seen.

## Can the wait be bounded

Yes (F6). A wait of our own, with the STA queue free, ended cleanly, and the gateway worked after the late callback. What the bound didn't do is close the picker: it stays until the user acts, and a later answer still arrives (F6, F7, F8). So the tool must mark its call finished when the wait ends, and the callback must drop an answer that arrives after that. AD-3's idle release (of the gateway's `IApplication` reference) wasn't exercised: the harness sets `idleReleaseSeconds` to 86400 (`ProbeSession.cs:77`, `:823`). A8's rule that idle release doesn't drop the COM reference while a picker is open stands.

## Implications for entry 2

1. **Decision (Phill, 2026-11-22): A8's and Q9's condition is reworded from "the callback reaches our STA thread" to "reaches our process", and `quick_filing` (entry 9) stays in scope.** This supersedes the STA wording of his Q9 decision (2026-11-12). Entry 2, the round-3 spine amendments, rewords A8 itself; entry 9 builds the tool.
2. **The callback is thread-agnostic and minimal:** record `SelectedItem` and `PressedButton`, complete a task with continuations run asynchronously, return. Nothing in it touches the STA or blocks, whichever thread it lands on.
3. **Cancel is `PressedButton` `0xFFFFFFFF` with no selected item**, for both Cancel and Esc; a pick is the added button's index (0 here) with the chosen ID. The result reports "cancelled" for the first and the ID for the second.
4. **Bound the wait in our code, and drop late answers** (F6): none of the three ways tried closes the picker from our side (F6, F7, F8; no close method), so the tool's result after a timeout says the picker may still be on screen and that a choice made in it now is ignored. Whether `WM_CLOSE` would close it is untried.
5. **An unparented picker doesn't block COM** (F4): reads and a guarded write went through while it was open. This is one input to the AD-7/AD-18 source conflict on whether `quick_filing` holds the write lock; a write with a parented picker open wasn't tried, so if the tool sets a parent, entry 2 can't rely on F4 for it.
6. **Two pickers can be open at once** (F4). Entry 2 decides whether `quick_filing` refuses a second call while one of ours is open; nothing in OneNote prevented it.
7. **No new HRESULTs for AD-9.** Entry 2 still maps the generic ones (`Timeout`, disconnects) for `QuickFiling` and `Run`.
8. **`ParentWindowHandle` set to the inner pane didn't make the picker modal to clicks** (F3, Phill's report); the description shouldn't promise the picker blocks OneNote.
9. **The picker takes the foreground from another app:** another app was in front before all nine `Run`s and OneNote after six (Short answer 6), and Phill saw F2's take keyboard focus. This supports AD-11's "user-visible" label for `QuickFiling`.

## Not probed

- `ParentWindowHandle` set to OneNote's top-level frame (the inner pane's root) rather than the inner pane, and whether the unparented picker is modal (F3).
- Any COM call, or a write, while a parented picker is open (F4 ran unparented only).
- AD-3's idle release of the gateway's `IApplication` reference while a picker is open: the harness's `idleReleaseSeconds` is 86400 (`ProbeSession.cs:77`, `:823`). F7 released the dialog only.
- AD-3's replacement of a poisoned STA while a picker is open; F8 disposed a healthy side gateway cleanly.
- Closing the picker from our side by posting `WM_CLOSE` (or another message) to its window.
- The checkbox, `Position`, `SetRecentResults`, `TreeCollapsedState`, `NotebookFilterOut`, `AddInitialEditor` and `TreeDepth` values other than `heSections` (plan: placeholders that don't compile).
- More than one added button, or `AddButton` for pages or notebooks.
- Picking an item outside MCP Test (the plan logs it as `other`; Phill only picked Probes).
- Closing OneNote or its window while a picker is open (plan, Never).
- A picker on the cloud notebook, a locked section or a protected section in the tree.

## Evidence gaps

- **Throwaway `b` missing from the picker's tree**, while `a` and `c` were listed (Phill's screenshot, F2). `b` was open at the 4.12 cleanup (the team lead's `get_hierarchy` check, about 02:36–02:40 UTC), shortly before this run, but wasn't checked at picker time. 4.1 had left `b` with a new ID after a close and reopen. Unexplained; seen in F2's picker only.
- **Focus was reported by Phill for F2 only.** F3's report covers clicking and typing, not focus; the other runs rest on the probe's foreground read, which comes milliseconds after `Run` returns and read `other` in three runs, including F2's, where Phill saw focus taken. The picker may take focus a moment later (inferred).
- **F3's "could change pages behind it"** rests on Phill's report alone; the probe recorded no view change, because the F/V read came before he clicked. Where his keystrokes went when he couldn't type (the picker's search box, presumably) wasn't recorded.
- **The callback's delay after the user's click** isn't measured: the probe times from `Run`, and most of each figure is Phill's time. The two cancels (1.4 s and 2.3 s after `Run`, both including his reaction) bound it from above.
- **The callback's thread** is one machine and one run; the cause isn't known.
- F7 and F8 are one sample each; F8's STA end is inferred from the dispose's join.

## Relied on, not re-probed

- OneMore research row 26: the callback only records `SelectedItem` and returns, because OneNote's UI thread waits for it; keep the dialog alive past `Run`. Consistent with every row here, and the probe kept to it.
- 4.1's harness (steps, pauses, F/V, the crash breaker, the 60 s limit, side gateways), 4.12's window copies and snapshot (F3's parent handle), and 5.1's guard (F4's write).
- 4.12 W1: `Window.WindowHandle` is an inner pane, and `Item(Count)` gives `E_INVALIDARG`; both seen again in F3's parent read.

## Open before final

- Phill's sign-off on the findings: approved 2026-11-22 (Inbox).
- The OneNote build wasn't recorded by this run.
- Entry 2's spine amendments (A8's wording per Implication 1, AD-7/AD-18 for `quick_filing`) are entry 2's, not this spike's.
- Spine memlog: constraint lines and Phill's A8 decision added 2026-11-22.

## Left behind

- The probe page `probe filing page` in MCP Test › Probes, created by this run and kept for reruns; cleanup removed F4's marker (`DeletePageContent` ok 249 ms, `markersLeft` 0, `asBeforeF4` true).
- No picker: every picker the run opened got its callback (nine of nine), and Phill confirmed none open at cleanup.
