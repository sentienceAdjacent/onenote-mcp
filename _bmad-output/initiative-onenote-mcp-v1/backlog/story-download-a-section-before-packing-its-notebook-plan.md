---
title: 'Probe: does SyncHierarchy download a section that is not fully downloaded'
type: 'feature'
ticket: '10'
created: '2026-11-17'
status: 'built'
baseline_revision: '2bdf598e004f07ca5dfced0ed45a75422ad17fd1'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context:
  - '{project-root}/spikes/stress-harness/README.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Story 3.7 refuses a notebook package when any section has `areAllPagesAvailable="false"`, because packing it raises OneNote's modal "cannot pack these sections … Continue anyway?" dialog and a Yes drops pages (3.7 sitting S3). The user must open each such section in OneNote by hand. Nobody knows whether `SyncHierarchy` on such a section downloads its pages: spike 1.15 found it returns S_OK at once and never fetched a missing image (page or section scope), and OneMore (lessons row 23) calls it fire-and-forget and saw "opening a `.one` and then syncing it didn't load its pages".

**Approach:** Measure first. Add a probe mode to the spike harness that watches one section the user names for a baseline minute, calls `SyncHierarchy` on it, and keeps watching its `areAllPagesAvailable`, page count, the foreground process and OneNote's window count, printing only codes, counts, times and attribute values. Run it in a sitting from the main session, on a section Phill approves, and record the findings. This plan covers the probe half of the ticket's Verify line; the ticket stays open, and the "download first" step is planned afterwards from the findings (Decisions, Q1).

## Boundaries & Constraints

**Always:** The probe calls only `GetHierarchy` and `SyncHierarchy` (an `AppState` call, AD-7/AD-11), never `NavigateTo`, a write, `Publish`, or closing/starting OneNote. It prints no names, paths, titles or IDs; the section ID is an argument and is never echoed. Its STA body returns exit codes and never throws: every COM call is caught and printed as an HRESULT only. Runs only in a sitting from the main session, with Phill's approval of the section, while he is at the machine. Same preconditions as `--probe-sync` (OneNote running, integrity match, own STA thread, compiled-in interop copy).

**Decisions (Phill, 2026-11-17, all as recommended):** Q1 (a): this ticket builds only the probe; the "download first" step is planned from the findings, likely over epic round 3's `sync_hierarchy` (A2). Q2 (a): probe first, then pack a second not-fully-downloaded section if one exists, else drop the pack step. Q3: one OneDrive for Business section, plus a OneDrive personal one if he has one. Q4 (a): no name or ID in any repo file, commit, plan, findings note or probe output; the session transcript and the command line may carry the ID.

**Never:** Run it from a build, a test or CI. Answer any OneNote dialog for the user (Phill answers it himself in the sitting). Record a section's or notebook's name or ID in any repo file, commit, plan, findings note or probe output. Change 3.7's refusal.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Downloads | a section with `areAllPagesAvailable` false | Baseline lines (60 s), "calling SyncHierarchy", the call's HRESULT and ms, then a line per 2 s poll until the attribute is absent or `true`, then the end state | Exit 0 |
| Downloads by itself | the attribute clears during the baseline | "cleared before the call"; no `SyncHierarchy` call | Exit 0; the finding is the result |
| Never downloads | same | Polls until `--max-seconds` (default 300) all false, then "not downloaded" | Exit 0 |
| Missing or empty `--section`, or another mode's option with it | arguments only | Usage refusal in `Options.Parse`, before any process or COM check | Exit 2 (usage) |
| Already available | a section with no such attribute | One line saying so, no `SyncHierarchy` call | Exit 4 (precondition) |
| Not a section, locked, protected, in the recycle bin, or ID not found | any other ID | Refused after `GetHierarchy`, before `SyncHierarchy`; no ID echoed | Exit 4, HRESULT only for a COM failure |
| A poll fails | e.g. `0x80042030` while a dialog is open | The poll line shows the HRESULT; polling goes on | Not fatal |
| Focus or dialog | foreground process changes, or ONENOTE gains a visible top-level window | Every line shows the foreground process (ONENOTE / other) and ONENOTE's visible top-level window count | Recorded, not acted on |

</frozen-after-approval>

## Code Map

- `spikes/stress-harness/ProbeSync.cs` -- the pattern: preconditions (`ProcessProbe.IsOneNoteRunning`, `CheckIntegrity`), own STA thread (l.34; its body has no catch, so this probe adds one), `Activator.CreateInstance` of the compiled-in `Interop.IApplication`, `SyncHierarchy` timed with `Stopwatch` (l.98), 2 s polling, `Hex()`, `FinalReleaseComObject`.
- `spikes/stress-harness/Options.cs` l.42 (usage), l.85, l.136 (parse), l.170 (the `--contract-only` exclusion list), l.188-208 (probe validation, before the notebook check) and `Program.cs` l.16-30 (main-thread handlers), l.41 (dispatch).
- `src/OneNoteMcp.Xml/HierarchyNode.cs` l.20, l.54-55 -- the element's own attributes; `areAllPagesAvailable` false only for `false` or `0`. `spikes/stress-harness/XmlFacts.cs` l.64-71 needs a notebook ID, so it doesn't fit a section-rooted read.
- `src/OneNoteMcp.Com/IProcessProbe.cs` -- `ProcessProbe`; its window helpers are internal, so the probe declares its own `GetForegroundWindow`, `GetWindowThreadProcessId`, `EnumWindows`, `IsWindowVisible`.
- `spikes/stress-harness/README.md` l.49-50 -- where probe modes are documented.
- `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md` row 23; 3.7 plan `epic-round-2-external-and-structural/story-publish-notebooks-and-section-groups-plan.md` (open item, S3); `epic-round-3-app-state-and-ui/epic-round-3-app-state-and-ui.md` l.34 (A2).

## Tasks & Acceptance

**Execution:**
- [x] `spikes/stress-harness/ProbeSectionSync.cs` -- new mode `--probe-section-sync --section <ID> [--max-seconds <n>]` (n 30-1800, default 300). Reads `GetHierarchy(sectionId, hsPages)`; the root must be `one:Section`, not `locked`, not `encrypted`, not `isInRecycleBin`/`isDeletedPages`, with `areAllPagesAvailable` false. Prints a "before" line (attribute, page count, foreground process, ONENOTE visible window count), polls the same line every 2 s for a 60 s baseline, prints "calling SyncHierarchy" before the call, then the HRESULT and ms, then a line per 2 s poll with elapsed seconds until the attribute clears or `--max-seconds`, then the end state. Every COM call caught; the body never throws. Never prints the ID. -- the measurement.
- [x] `spikes/stress-harness/Options.cs`, `Program.cs` -- parse `--probe-section-sync`, `--section`, `--max-seconds`; validate before the notebook check: `--section` required and non-empty; refuse `--notebook`, `--notebook-name`, `--out`, `--page`, and every other probe and diag mode with it; add it to the l.170 list and to `Usage`; dispatch in `Program.cs`. -- wiring.
- [x] `spikes/stress-harness/README.md` -- document the mode beside `--probe-sync`: the `AppState` warning, the approval rule, the baseline, Ctrl+C is safe (it only reads and requests a sync), and a hang after "calling SyncHierarchy" means look at OneNote.
- [ ] After the sitting: this plan's `## Sitting findings` (dated: storage kind, attribute and page counts before/after, baseline result, call and download times, focus and window counts, sync status, the pack step's result); add the lesson to `research-onemore-lessons.md` row 23; answer the 3.7 plan's open item. Send the ticket-Notes update to the team lead (a build never writes a ticket file).

**Acceptance Criteria:**
- Given the harness builds with no warnings, when the mode runs with no `--section`, an empty one, or another mode's option, then it refuses with exit 2 before any process or COM check.
- Given the sitting, when the probe runs on Phill's chosen section, then its output says whether the pages downloaded, whether it happened before or after the call, how long it took, and whether focus moved or a window appeared.

## Sitting steps (main session with Phill; never a build)

0. Build the probe from the checkout that holds it: `dotnet build spikes/stress-harness -c Release`, then `$h = "<that checkout>\spikes\stress-harness\bin\Release\net10.0-windows\StressHarness.exe"`.
1. Phill confirms OneNote is running at the server's integrity level, picks the section(s) (Decisions, Q3), and checks OneNote's sync status for that notebook (record: healthy, or the error code; stop on `0xE0000644`). He must not open or click the section in OneNote until the probe ends. The ID may appear in the chat and on the command line only (Decisions, Q4).
2. `& $h --probe-section-sync --section <ID>`. Phill watches for focus changes and dialogs and answers any dialog himself; record what he saw beside the output.
3. Sync status again (as step 1).
4. Optional, per Decisions Q2: pack a second not-fully-downloaded section as onepkg with `onenote-dev`'s `publish`, to a neutral file name under the export root. Record only: dialog yes/no, result code, size, seconds. Phill answers No to any dialog; the call can block for up to `publishSeconds` (300 s). Never copy the result's ID or `meta.outputPath` into a file. Phill deletes the file.
5. If the attribute cleared: `get_hierarchy` on the notebook confirms no section still reports it, and, only with Phill's explicit exception as for 3.7 Q2, a notebook `publish` that 3.7 refused now runs with no dialog, recorded as in step 4. If it didn't: record "not downloaded in N s", and whether opening the section in OneNote by hand then clears it.
6. Record the findings (task 4) and send them to the team lead for the "download first" follow-up.

## Verification

Unit tests can't cover the probe (it needs real OneNote); the harness has no test project.

**Commands:**
- `dotnet build spikes/stress-harness -c Release -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: unchanged, 0 warnings
- `& spikes/stress-harness/bin/Release/net10.0-windows/StressHarness.exe --probe-section-sync` -- expected: usage refusal, exit 2, OneNote untouched (likewise with `--section ""` and with `--section x --notebook y`)

## Implementation Notes

- 2026-11-17: built in worktree bl10. The probe reads the root with `HierarchyNode.Root` (kind, locked, encrypted, recycle bin) and its own `State` for the raw `areAllPagesAvailable` value (printed as `absent`, `true`, `false`, `1`, `0` or `other`) and the `one:Page` count. "Cleared" means a successful read whose value isn't `false` or `0`. The whole STA body is in a catch that prints the exception type and HRESULT only (exit 5); the first read's COM failure is exit 4 with its HRESULT. Window snapshot: `GetForegroundWindow` and `EnumWindows` + `IsWindowVisible`, matched against this session's `ONENOTE` process IDs.
- `--probe-section-sync` is validated first in `Options.Parse`, before the `--fake-server` and `--contract-only` branches, so no other mode's option rides along. `--section` and `--max-seconds` without the mode are also a usage refusal (not in the plan; it stops a stray ID being taken as nothing).
- Verified (2026-11-17): `dotnet build spikes/stress-harness -c Release -warnaserror` 0 warnings, 0 errors; `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings, 0 errors; the three usage refusals (`--probe-section-sync`; `--section ""`; `--section x --notebook y`) each exit 2 with the refusal text. The probe has not run against OneNote (task 4, the sitting).

## Plan Change Log

## Review Triage Log

Fable final review (low effort, 2026-11-17), 14 findings, all checked against the sources and applied: F1 (the ID rule vs the sitting) became Open Questions 4; F2 (pack step confounds the probe) reworked Open Questions 2 and step 4; F3 a 60 s baseline and "don't open the section"; F4 the STA body never throws, with matrix rows for a missing ID and a failed poll; F5 AC split into a usage refusal and a post-read refusal; F6 publish steps' file name, records and dialog answer; F7 sync status before and after; F8 epic round 3's A2 named in Open Questions 1; F9 `--max-seconds` and the "runs alone" list; F10 "calling SyncHierarchy" line and Ctrl+C note; F11 the read and attributes named; F12 findings also go to the lessons note and the 3.7 plan; F13 step 0 builds the probe; F14 the ticket stays open.

Quick lens, pass 1 (2026-11-17): 2 low, patched.

- **low, patched:** `--probe-section-sync` silently ignored options outside its refusal list (`--exe`, `--seed`, `--fake-variant`…). `Options.Parse` now refuses every option other than `--probe-section-sync`, `--section` and `--max-seconds` (checked: `--exe foo` and `--seed 3` exit 2); README says so.
- **low, patched:** the "calling SyncHierarchy" and result lines carried no foreground/window snapshot, so focus moved by the call showed only at the next poll; both now carry one.
- Note: the frozen block was edited only to record Phill's 2026-11-17 answers (Decisions), relayed by the team lead with his approval of the plan.
