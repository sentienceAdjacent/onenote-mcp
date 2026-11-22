---
title: 'Findings: app-state probes, part 3: quick filing'
type: 'spike-findings'
findings_for: '13'
status: 'draft'
created: '2026-11-22'
machine: 'Windows 11 Pro 10.0.26300, OneNote desktop (M365), not elevated; the OneNote build was not recorded by this run'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state-filing-20261122-024248.jsonl (outside the repo; one run, 2026-11-22 02:42–03:09 UTC, test run successful); Phill''s reports and screenshots per pause, in the coordinator''s sitting notes and the 4.13 Inbox thread (the screenshots show notebooks outside the test set and stay out of the repo)'
---

# Findings: app-state probes, part 3: quick filing

**Draft.** One sitting on 2026-11-22 (UTC), one run (stamp `20261122-024248`), every step of the Sitting script in order: `bind-pick`, `modal`, `while-open`, `cancel`, `late`, `release`, `thread`, `cleanup`. Every pause was answered by Phill; none was skipped. No call failed and no call hit the 60 s limit; the run's `hresult-summary` row is empty (`{}`).

**Top finding: the callback reaches our process, but never on our STA thread.** All nine `OnDialogClosed` calls arrived on an MTA thread of our process (managed thread IDs 15, 15, 30, 34, 23, 22, 22, 16, 16), never on the STA thread that called `Run` (9, or 24 for the side gateway in F8). `Run` returns in 134–199 ms, before the user has chosen anything, and the STA thread is free while the picker is open: it served `GetHierarchy`, `GetPageContent`, a guarded `UpdatePageContent` and a second `Run` with the picker open (F4). So the condition in epic A8 ("its callback reaches our STA thread") is not met as written, but what A8 needs from it is: the answer reaches the server, and the STA queue stays free while the user decides. Inferred, not checked: .NET's COM-callable wrapper for our callback is agile (free-threaded marshaller), so OneNote's call lands on an RPC/MTA thread instead of being marshalled to the STA.

**Second finding: nothing we do on our side closes the picker or stops its answer.** A wait that ended (F6), a `FinalReleaseComObject` on the dialog plus dropping the callback object (F7), and ending the STA thread that ran it (F8) all left the picker on screen, and the user's later pick still called back, with the section, on an MTA thread, and with no error or hang in OneNote. The interface has no close method (slots 7–29, plan's Code Map). So a picker left on screen stays until the user closes it, as A8 assumes, and dropping its answer is our code's job: the callback must find its call already finished and discard the answer.

**Third finding: the picker isn't modal to OneNote, even with a parent.** With `ParentWindowHandle` set (F3), Phill could still click in OneNote and change pages behind the picker, though he couldn't type. And with a picker open, COM calls, including a write, went through with `ok`, never `0x80042030` (F4). Two pickers can be open at once (F4).

**Short answer:**

1. **Binding works early-bound** from our 64-bit STA thread: `QuickFiling()` (0–2 ms) casts to the spike's `IQuickFilingDialog` (QI ok), and `Title`, `Description`, `TreeDepth`, `ParentWindowHandle` and `AddButton` all return S_OK in 0–5 ms (F1).
2. **`Run` returns at once** (134–199 ms over nine runs) and the picker's own `WindowHandle` is a live top-level window (2–6 ms to read) (F2).
3. **`OnDialogClosed` arrives** for every way the picker closed: a pick, Cancel, Esc, a pick after our wait ended, after our release, after our STA thread ended (F2, F5–F8). Every time on an MTA thread, never the STA (top finding).
4. **A pick gives** `SelectedItem` = the section's ID (MCP Test › Probes) and `PressedButton` = 0, the index of our one added button. **A cancel** (Cancel button or Esc) gives no selected item and `PressedButton` = `0xFFFFFFFF` (F5). Both getters answered without an error from the MTA thread, on the dialog OneNote passed in.
5. **The picker** shows the title and description we set, a search box, and an All Notebooks tree of every open notebook down to sections; "File here" is disabled until a section is chosen (F2, Phill's screenshots). One open throwaway notebook (`b`) was missing from the tree while the other throwaways (`a`, `c`) were listed; unexplained (Evidence gaps).
6. **Focus:** Phill saw every picker come to the front with keyboard focus. The probe's foreground read straight after `Run` agreed in seven runs (`ONENOTE`) and didn't in F2's and F4's (`other`), which may be timing (Evidence gaps).
7. **With a picker open, other calls work** (F4): `GetHierarchy` 16 ms, `GetPageContent` 32 ms, guarded `UpdatePageContent` 293 ms (applied: the marker was on the page afterwards), and a second `Run` ok in 161 ms, its picker on top of the first. No `0x80042030`, no message, no busy cursor.
8. **The wait can be bounded** (F6): a 10 s wait ended with nothing (10,002 ms), the session gateway kept working (`GetHierarchy` 21 ms), and Phill's later pick still called back 23.0 s after `Run`.
9. **Error codes:** none from any OneNote call in this run. The only HRESULT was the probe's own `Windows.Item(Count)` `E_INVALIDARG`, read on purpose while finding F3's parent handle (4.12's W1) (H).

## Results, one row per Matrix question

| Id | Result |
|---|---|
| F1 | From the session gateway's STA thread (managed ID 9, apartment STA): `QuickFiling` ok 2 ms (an `__ComObject`), QI to `IQuickFilingDialog` ok 0 ms, `put_Title` ok 5 ms, `put_Description`, `put_TreeDepth` (`heSections`) and `AddButton` ("File here", `heSections`, `heNone`, default) ok 0 ms each. F3's run repeated the set with `put_ParentWindowHandle` added, all ok, 0–1 ms. No HRESULT on any call. |
| F2 | `Run` with no parent: ok, returned in 152 ms; picker window a live top-level window. F/V straight after: foreground still `other`, view unchanged (MCP Test › Probes). Phill: the picker appeared at once, came to the front with keyboard focus, showed the title and description, a search box and an All Notebooks tree of every open notebook down to sections (MCP Test's sections listed; throwaway `b` missing although open, `a` and `c` listed); "File here" was disabled until he selected a section; it closed on "File here". `OnDialogClosed` arrived once, 30.5 s after `Run` returned (Phill's time to pick), on managed thread 15, apartment MTA, not the STA thread; `SelectedItem` = MCP Test › Probes (a section ID), `PressedButton` 0; both getters ok. |
| F3 | Parent = `CurrentWindow.WindowHandle` from 4.12's window read (the value OneMore passes): a live window but not top-level (an inner pane, as 4.12's W1 found). `Run` ok 155 ms; picker top-level, its root the foreground window; F/V after: foreground `ONENOTE`, view unchanged. Phill: the picker was in front with focus; **not modal**: he could click around OneNote and change pages behind it (he didn't try notebooks or sections) but couldn't type in OneNote; Cancel closed it. Callback 36.5 s after `Run`, thread 15, MTA; nothing selected, `PressedButton` `0xFFFFFFFF`. Inferred, not probed: an inner pane as parent may be why it isn't modal; the frame (the pane's root) wasn't tried. |
| F4 | First picker `Run` ok 139 ms, no callback yet when the calls began (`firstCallbacks` 0 on each). Then, session gateway: `GetHierarchy` (notebooks) ok 16 ms; `GetPageContent` on `probe filing page` ok 32 ms; guarded `UpdatePageContent` (5.1's guard, the page's own date, one outline with a marker) ok 293 ms, which includes the guard's two hierarchy reads; second `QuickFiling().Run` ok 161 ms. No `0x80042030`. The write applied: a read after the pickers closed found the marker (`markerOnPage` true, 45 ms). Phill: a second picker appeared on top; no message, no busy cursor. He cancelled both; callbacks on threads 30 and 34, both MTA, nothing selected, `PressedButton` `0xFFFFFFFF` each. F/V after both `Run`s: foreground `other`, view unchanged. |
| F5 | Cancel button: `Run` ok 156 ms; callback 2.3 s after `Run`, thread 23, MTA. Esc: `Run` ok 134 ms; callback 1.4 s after `Run`, thread 22, MTA. Both: `SelectedItem` none, `PressedButton` `0xFFFFFFFF`, getters ok; Phill saw each close. Foreground `ONENOTE` after each `Run`. |
| F6 | `Run` ok 153 ms. The probe's bounded wait ended with no callback after 10,002 ms (limit 10,000 ms) and marked itself over; the dialog and recorder stayed referenced. Phill then picked MCP Test › Probes: the callback still arrived, 23.0 s after `Run`, thread 22, MTA, with the section and `PressedButton` 0. Session gateway `GetHierarchy` afterwards ok 21 ms. Phill: the picker closed on the button; nothing else (no message, error or slowness). |
| F7 | `Run` ok 140 ms; 3 s later, before any callback, `Marshal.FinalReleaseComObject(dialog)` ok (returned 0) and the callback object dropped with a forced GC; the recorder was still alive afterwards (`WeakReference` not collected: OneNote's reference keeps the wrapper alive). Phill: the picker stayed open; he picked Probes and it closed; no error, message or hang. Callback arrived 21.5 s after `Run`, thread 16, MTA, with the section and `PressedButton` 0. Session `GetHierarchy` afterwards ok 20 ms. |
| F8 | `Run` on a side gateway's STA thread (managed ID 24) ok 199 ms. 3 s later, before any callback, the side gateway was disposed in 2 ms; its STA thread ended (join returned within its 5 s limit); no dispose error. Phill: the picker stayed open; he picked Probes and it closed; no error, message or hang. Callback arrived 22.5 s after `Run`, thread 16, MTA, with the section and `PressedButton` 0. Session gateway `GetHierarchy` afterwards ok 18 ms. Disposing the side gateway early was safe: the session's end-of-run dispose raised nothing. |
| H | No HRESULT from any OneNote call: `QuickFiling`, the QI, every setter, `Run`, `get_WindowHandle`, `SelectedItem`, `PressedButton`, and F4's reads and write all returned S_OK. `0x80070057` E_INVALIDARG from `Windows.Item(Count)` in F3's parent read is the probe reading past the end on purpose (4.12 W1; no AD-9 row, so `Internal`). The run's `hresult-summary` row is `{}`. |

## HRESULTs observed

| HRESULT | Where | AD-9 code |
|---|---|---|
| `0x80070057` E_INVALIDARG | `Windows.Item(Count)`, F3's parent read (the probe's past-the-end read) | none stated, so `Internal`; never reached if a tool stops at `Count - 1` |

No other code. The quick-filing calls produced none, so this spike adds nothing to AD-9's map. In particular `0x80042030` (modal dialog open) wasn't returned with a picker open (F4).

## Does the callback reach us

Yes, every time (nine of nine), including after our wait ended (F6), after we released the dialog (F7) and after the STA thread that ran it ended (F8). It arrives on an MTA thread, a different one most times, never on the STA thread that called `Run`. So:

- The callback code runs concurrently with the STA and must be thread-safe: it may only record the answer and signal (as the probe's recorder does), never touch the STA's `IApplication` reference.
- Reading `SelectedItem` and `PressedButton` on the dialog passed to the callback works from that MTA thread (nine of nine).
- OneNote waits for the callback (OneMore row 26), so it must return at once; the recorder did, and no stall was seen.

## Can the wait be bounded

Yes (F6). A wait of our own, with the STA queue free, ended cleanly and the gateway kept working. What the bound can't do is close the picker: it stays until the user acts, and a later answer still arrives (F6, F7, F8). So the tool must mark its call finished when the wait ends, and the callback must drop an answer that arrives after that. AD-3's idle release while a picker is open did no harm in the one sample (F7: releasing the dialog and the callback object changed nothing the user saw, and the answer still arrived), so A8's rule that idle release doesn't drop the COM reference while a picker is open isn't needed for the callback; keeping the dialog referenced until the callback (OneMore's practice) costs nothing and is the safer default.

## Implications for entry 2

1. **A8's condition needs rewording:** the callback reaches our process on an MTA thread, not our STA thread. What `quick_filing` needs holds: the answer arrives and the STA queue stays free while the user decides. Entry 2 rewrites A8 to say "reaches our process" and builds Q9.
2. **The callback is thread-safe and minimal:** record `SelectedItem` and `PressedButton`, complete a task with continuations run asynchronously, return. Nothing in it touches the STA or blocks.
3. **Cancel is `PressedButton` `0xFFFFFFFF` with no selected item**, for both Cancel and Esc; a pick is the added button's index (0 here) with the chosen ID. The result reports "cancelled" for the first and the ID for the second.
4. **Bound the wait in our code, and drop late answers** (F6): the picker can't be closed from our side (F7, F8; no close method), so the tool's result after a timeout says the picker may still be on screen and that a choice made in it now is ignored.
5. **The picker isn't modal** (F3): OneNote stays clickable behind it, and COM calls and writes go through while it is open (F4). `quick_filing` therefore needn't hold the write lock or block other tools while it waits (one input to the AD-7/AD-18 source conflict entry 2 settles).
6. **Two pickers can be open at once** (F4). Entry 2 decides whether `quick_filing` refuses a second call while one of ours is open; nothing in OneNote prevents it.
7. **No new HRESULTs for AD-9.** Entry 2 still maps the generic ones (`Timeout`, disconnects) for `QuickFiling` and `Run`.
8. **`ParentWindowHandle` doesn't make the picker modal**, at least with the inner pane OneMore passes; the description shouldn't promise the picker blocks OneNote.

## Not probed

- `ParentWindowHandle` set to OneNote's top-level frame (the inner pane's root) rather than the inner pane (F3).
- The checkbox, `Position`, `SetRecentResults`, `TreeCollapsedState`, `NotebookFilterOut`, `AddInitialEditor` and `TreeDepth` values other than `heSections` (plan: placeholders that don't compile).
- More than one added button, or `AddButton` for pages or notebooks.
- Picking an item outside MCP Test (the plan logs it as `other`; Phill only picked Probes).
- Closing OneNote or its window while a picker is open (plan, Never).
- A picker on the cloud notebook, a locked section or a protected section in the tree.

## Evidence gaps

- **Throwaway `b` missing from the picker's tree** although it was open, while `a` and `c` were listed (Phill's screenshot, F2). Unexplained; the run didn't read the hierarchy at that moment to compare, and it was seen in F2's picker only (not checked in later pickers).
- **F2's and F4's F/V say the foreground stayed `other`** straight after `Run`, while Phill saw F2's picker take focus; the other seven `Run`s read `ONENOTE`. The read comes milliseconds after `Run` returns, so the picker may take focus a moment later (inferred); F4's focus wasn't reported.
- **The callback's delay after the user's click** isn't measured: the probe times from `Run`, and most of each figure is Phill's time. The two cancels (1.4 s and 2.3 s after `Run`, both including his reaction) bound it from above.
- F7 and F8 are one sample each.
- **"Couldn't type"** in F3 rests on Phill's report alone; where the keystrokes went (the picker's search box, presumably) wasn't recorded.

## Relied on, not re-probed

- OneMore research row 26: the callback only records `SelectedItem` and returns, because OneNote's UI thread waits for it; keep the dialog alive past `Run`. Consistent with every row here, and the probe kept to it.
- 4.1's harness (steps, pauses, F/V, the crash breaker, the 60 s limit, side gateways), 4.12's window copies and snapshot (F3's parent handle), and 5.1's guard (F4's write).
- 4.12 W1: `Window.WindowHandle` is an inner pane, and `Item(Count)` gives `E_INVALIDARG`; both seen again in F3's parent read.

## Open before final

- Phill's sign-off on the findings.
- Spine memlog constraint lines for these findings (proposed, not yet added).
- The OneNote build wasn't recorded by this run.
- Entry 2's spine amendments (A8's wording, AD-7/AD-18 for `quick_filing`) are entry 2's, not this spike's.

## Left behind

- The probe page `probe filing page` in MCP Test › Probes, created by this run and kept for reruns; cleanup removed F4's marker (`DeletePageContent` ok 249 ms, `markersLeft` 0, `asBeforeF4` true).
- No picker: every picker the run opened got its callback (nine of nine), and Phill confirmed none open at cleanup.
