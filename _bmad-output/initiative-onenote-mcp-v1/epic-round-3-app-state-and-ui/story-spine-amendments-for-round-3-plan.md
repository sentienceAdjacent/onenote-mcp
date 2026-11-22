---
title: 'Spine amendments for round 3'
type: 'chore'
ticket: '2'
created: '2026-11-21'
status: 'draft'
baseline_revision: 'ffb64ed934336ccf671acfd9e96ea7c9a2f43ebf'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/epic-round-3-app-state-and-ui.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/spike-app-state-probes-part-1-open-close-and-sync-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/spike-app-state-probes-part-2-navigation-and-windows-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/spike-app-state-probes-part-3-quick-filing-findings.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Round 3's entries 3–10 build `sync_hierarchy`, `close_notebook`, `open_notebook`, `get_windows`, `navigate_to_url` and `quick_filing` on AD-7's `AppState` row, which says only "none / AD-11 / none", and on AD-11's user-visible list, which still names `Windows` and says nothing of open or close. Four source conflicts are open (epic Notes: AD-7 vs `navigate_to`, AD-7 step 2/AD-18 vs the lock, AD-11 vs a harmless `Windows` read, AD-14 vs the tool names). The three spikes (4.1, 4.12, 4.13, all final) now give the evidence, and several findings overturn the epic's assumptions: `cftNone` opens any folder as a notebook (4.1 O6, O3), a local close straight after a write lost the edit (4.1 C2), a local `onenote:` link can block on a Security Notice (4.12 N1), links open closed notebooks and loose `.one` files (4.12 N4, N5), `NavigateTo` didn't take focus (4.12), and the QuickFiling callback lands on an MTA thread, not our STA (4.13).

**Approach:** An architecture Update, as stories 5.2 and 3.2 did: append the decisions to the spine memlog, amend the affected ADs in place (IDs stable), add the coverage table and the dated Decisions for the members not exposed, resolve or add Deferred rows, reword the epic's A8 as Phill decided, run `lint_spine.py`. Then the orchestrating session runs the Fable final review at `effort: low`, Phill picks the fixes and approves, and only then is the spine's `updated` set. Docs only.

## Boundaries & Constraints

**Always:**
- Every amendment cites its evidence: a findings row or Implication (4.1, 4.12, 4.13) with its strength (one run, one pair, repeated), a memlog constraint line (339–364), a Phill Decision (epic Q1–Q11, 2026-11-22 A8), or an earlier spike.
- AD IDs never renumbered; a new rule goes into the AD that already binds the unit.
- Spine `status: final` stays; `updated` changes only when Phill approves the Fable-reviewed amendment.
- A rule resting on one run or one pair says so in the AD text.
- No real notebook names; probe items keep the names the findings use (`MCP Test`, throwaway `b`, `Ballast Vault` only where the findings already say it).

**Never:**
- Any `src/`, `tests/`, `plugin/` or README change; each rule names the entry that builds it.
- Writing `tickets.toml`; the orchestrating session refines entries through `bmad-ticket` if a rule here changes one (listed in Implementation Notes).
- Exposing `SetFilingLocation`, the window setters, events, `COMAddIns`, `LanguageSettings` or `Dummy1`; passing `force` to `CloseNotebook`; `fNewWindow=true` anywhere.
- Relaxing Q1–Q11 beyond what Phill answers below.

## Decisions already made (applied, not re-asked)

1. Q1 a: `close_notebook` never passes `force`. Q2 a: open and close need no grant.
2. Q3 a: `open_notebook` opens notebooks only; `.one` and `.onepkg` refused before COM; epic 3's Q11 rule on the canonical path. Its URL clause ("a OneDrive URL if entry 1 shows one opens and never creates") is **not met**: a URL turned an existing empty OneDrive folder into a notebook (4.1 O3). So URLs are refused, and a cloud notebook is reopened in OneNote (A3's fallback).
3. Q4 a: `get_windows` plus an optional window on `navigate_to` and `navigate_to_url`; setters not exposed. Q5 a holds: reading `Windows` changed neither focus nor view (4.12 W2, nine valid reads), so `get_windows` is an ordinary `AppState` read, not user-visible.
4. Q6 a: `navigate_to_url` only for `onenote:` links into an already-open notebook, matched by `NotebookKey` before COM; anything else `InvalidArgument`. Q7 a: `fNewWindow` always false.
5. Q8 b, Q10 c, Q11 a: `SetFilingLocation`, events, `COMAddIns`/`LanguageSettings` not exposed; dated Decisions here.
6. Phill 2026-11-22 (memlog l.364): A8's and Q9's condition becomes "the callback reaches our process"; `quick_filing` (entry 9) stays in scope; entry 2 rewords A8.
7. Phill 2026-11-20 (memlog l.344): C3 stays deferred; a close held behind a dialog or offline is designed as `Timeout` with `outcomeUnknown`.

</frozen-after-approval>

## Open Questions

Each needs Phill's answer; the recommendation and its evidence follow the options.

1. **Which `AppState` tools take the write lock** (AD-7 step 2, AD-18; epic Source conflict). (a) `open_notebook` and `close_notebook` take it; `sync_hierarchy`, `navigate_to`, `navigate_to_url`, `get_windows` and `quick_filing` don't; every `AppState` call still returns `ComBusy` while an abandoned write is outstanding (AD-3). (b) Every `AppState` tool takes it. (c) None does. *Recommended (a):* the open's notebook test must not race a create (A4), and a close must not run between another pipeline's write and its read-back (4.1 C2 lost such an edit); a picker held under the lock would stop every write for its whole wait, while an unparented picker blocked nothing (4.13 F4); navigation and sync change no content.
2. **What `close_notebook` does about pending writes** (4.1 C2, Implication 2; memlog l.340). (a) Probe first: a short local rerun of C2 with a delay and with a `SyncHierarchy` before the close, before the spine is final (a live step). (b) Rule now, measured in 4.4: under the lock, `SyncHierarchy` on the notebook, then a fixed 10 s settle (the move settle's precedent, AD-7), then `CloseNotebook`; the result says so; a Deferred row marks the length unmeasured, and 4.4's sitting must show a local edit surviving before 4.4 ships. (c) Refuse a close while this process wrote to that notebook within the last N minutes (the journal knows), and otherwise close at once. *Recommended (b):* 4.4 is hitl anyway, the cloud close already waits by itself (C2 C, 7.1 s), and (c) misses another process's writes. Whether `SyncHierarchy` saves a local notebook at all is unknown (inferred).
3. **The notebook test** (A4; 4.1 O6–O8, L). (a) The canonical folder exists, holds a `.onetoc2`, and no ancestor folder holds one (a section group's folder holds one too, O8); a missing path, a `.one`, a `.onepkg`, a UNC or device path and a folder failing the test are refused with `InvalidArgument` before COM; a notebook whose `.onetoc2` hasn't landed yet (up to 5 min, O7, 5.1 c.18) is refused with a `userAction` to wait. (b) As (a), plus at least one `.one` in the folder tree. *Recommended (a):* (b) refuses empty notebooks such as throwaway `b` (O1: 0 sections), which 4.4/4.5's tests reopen. Known gap: a plain folder OneNote once opened by mistake has a `.onetoc2` and passes (O6, L); it already is a notebook. The ancestor check is inferred, not probed.
4. **Local `onenote:` links in `navigate_to_url`** (4.12 N1 L, top finding; Implication 1). (a) Refuse a link whose target is a local path, with `InvalidArgument` and a `userAction` to use `navigate_to` with the object's ID; accept cloud (`onenote:https://…`) links only. (b) Accept them, and map a blocked call to `Timeout` (`outcomeUnknown`) whose `userAction` says a Security Notice may be waiting, possibly hidden. *Recommended (a):* a hidden notice held the STA, and so every other tool call, to the 60 s probe limit twice (runs 1, 2), and an AD-3 timeout poisons the STA thread; `NavigateTo` never raised the notice; cloud links never did.
5. **`quick_filing`'s bounds** (A8; 4.13 Implications 2–6). (a) A fixed 300 s wait limit (no new setting), and the client's cancel; unparented (F4 covers only unparented); no lock (Q1); a second call while one of this process's pickers is open is refused with `ComBusy`; a wait that ends is `ok: true` with no choice and a warning that the picker may still be on screen and a choice made in it now is ignored; a late answer is dropped. (b) As (a) but a `timeouts.quickFilingSeconds` setting (AD-10; a settings contract change). (c) As (a) but a second picker allowed (F4 showed two at once). *Recommended (a).*
6. **How a closed notebook's pages leave the search page-text cache** (AD-12; ticket text). (a) Left to eviction. (b) Drop the entries keyed by that `NotebookKey`. (c) Clear the whole cache. *Recommended (a):* the cache keys on page ID, every reopen gives new IDs (4.1 O1, O7, C2, C4), and an old ID reads `ObjectNotFound` and loses its entry (AD-12, 4.1 S2), so a dead entry can never be hit; it is memory only (AD-13).
7. **Plan size.** This plan is about 5,000 tokens, over the 1,600 guide, as rounds 2's and 5.2's spine plans were. (a) Keep one story. (b) Split the `quick_filing` and windows rules into a second story. *Recommended (a):* one document, one Fable review, one approval (the ticket's Verify); round 2 kept one story (J9).

## Code Map

- `architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine (920 lines). AD-2 l.59–65 (one STA owns every COM call), AD-3 l.67 (timeouts l.72–75, timed-out call l.76–83, modal dialogs l.102–107, idle l.108–119), AD-5 l.139–149, AD-6 l.151 (resolution l.170–173, key and path forms l.174–187), AD-7 l.190 (steps l.195–207, deadline l.209, class rule l.214 "`OpenHierarchy` from whether the target exists, checked under the write lock", table l.326–334, `AppState` row l.334), AD-9 l.403 (Codes l.423–442, table l.444–468), AD-11 l.497–506, AD-12 cache l.529–537, AD-13 l.539–548, AD-14 l.550–560, AD-18 l.657–668, AD-21 per tool l.761–772, Deferred l.856 (events l.866, modal dialogs l.902).
- `architecture-onenote-mcp/.memlog.md` -- l.339–344 (4.1), l.349–356 (4.12), l.357–363 (4.13), l.364 (A8 decision). Append with `uv run _bmad/scripts/memlog.py append --path <spine folder>/.memlog.md --type decision|event --text "..."`.
- `epic-round-3-app-state-and-ui/epic-round-3-app-state-and-ui.md` -- A1–A10 l.28–49; Decisions Q1–Q11 l.89–105; Source conflicts l.106–109; Assumptions l.140–150.
- The three findings docs -- 4.1 Short answers, table, Implications 1–11; 4.12 Short answer 1–14, Implications 1–14; 4.13 Implications 1–9.
- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- slots 7–35 for the coverage table (read only).
- `src/OneNoteMcp.Com/HResultMap.cs` l.102 (`0x80042006` → `Internal`), l.106 (`0x8004200A`), l.116 (`0x80042014` → `ObjectNotFound`), l.161 (`E_INVALIDARG` → `Internal`) -- read only.
- `src/OneNoteMcp.Core/UseCases/NavigateToUseCase.cs` (calls the gateway outside the pipeline; `ObjectNotFoundUserAction` "wait two seconds", OneMore inference) and `src/OneNoteMcp.Server/Tools/NavigateToTool.cs:24` ("takes keyboard focus", "shows and downloads nothing" without a window) -- read only; entry 3 changes them.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs:101–107` -- `OpenHierarchyAsync` (`cftNone`, `AppState`) exists since 3.8 and `open_package` already calls it inside its `ExternalWrite` pipeline; `open_notebook` reuses it.
- `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- deadlines l.49–95 (`DeadlineFor`); the `AppState` class has no pipeline yet.
- Shipped tool names: the `Name` constants in `src/OneNoteMcp.Server/Tools/*.cs` (check `publish`, `open_package`, `delete_hierarchy_object` there too).
- `~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace <spine folder>` -- `{pid}`/`{guid}` hits are known false positives.

## Tasks & Acceptance

**Execution:**
- [ ] `.memlog.md` -- one `decision` line per answered Open Question and per rule below that isn't already a constraint line; one `event` line when written.
- [ ] spine AD-7 -- **`AppState` pipeline**: steps 1 (argument checks; no grant, Q2), 2 (the lock per OQ1), 3–4 (the tool's checks), 7 (COM), 8; no basis (step 5) and no snapshot (step 6); the 120 s deadline; `outcomeUnknown` on step 7's ambiguous failures for every tool that changes state, never for `get_windows` (4.12 W2: it changes nothing; its `Timeout` is retryable as a read's); `ComBusy` while an abandoned write is outstanding, for every `AppState` call. `navigate_to` moves onto it (entry 3; resolves the AD-7 Source conflict). Rewrite the table row l.334: methods (`OpenHierarchy` on an existing notebook, `CloseNotebook`, `SyncHierarchy`, `NavigateTo`, `NavigateToUrl`, `QuickFiling`, `Windows`; `SetFilingLocation` dropped), access none, validation per tool (below), snapshot none. **`open_notebook` validation**: canonical path first (`GetFullPath`, links and junctions resolved; 4.1 O9, O10: OneNote keeps the path given), the canonical form sent to COM; already open by `NotebookKey` or canonical path → its ID, no COM call (O4: harmless either way; saves a call); the notebook test per OQ3 under the lock; URLs refused (Decision 2); the result's ID, name and the fill warning (local 2 s, cloud up to 16 s, OneNote's list up to a minute: O2, O8, C2). **`close_notebook` validation**: a live notebook ID, anything else `InvalidArgument` before COM (A3); the pending-write rule per OQ2; result: name, path, "every ID changes on reopen" (O1, S2), "the view may go blank" (C4), how to reopen (`open_notebook` locally; OneNote for a cloud notebook). **`sync_hierarchy`**: any hierarchy ID; ok means requested (S1, 1.15 quirk 3). **`navigate_to_url`**: `onenote:` only; the link's notebook matched by `NotebookKey` among open notebooks (N4), a link to a section path no open notebook holds refused (N5); OQ4's local-link rule; the landing check below. **`quick_filing`** per OQ5.
- [ ] spine AD-7 landing check (`navigate_to_url`, 4.12 K1, K2, Implications 4, 5) -- straight after the call, no settle: a page or paragraph link compares `GetHyperlinkToObject` of `CurrentWindow`'s page with the link's `page-id` (the only check that caught N3's miss); a section link compares `CurrentSectionId` with the open section whose `path` is the link's `.one`; a notebook link compares the notebook. A mismatch or `""` page warns `landedElsewhere`; the result says "OneNote's current page is …", not "OneNote shows …" (cloud repaint lag, N1 C).
- [ ] spine AD-3 -- per-call timeouts: 30 s for every `AppState` method (largest seen 7.1 s, cloud close with a pending edit, C2 C; 2.3 s URL open, O2); a close held behind a dialog or offline is `Timeout` with `outcomeUnknown` (Decision 7). Modal dialogs: under File > Options `SyncHierarchy`, `OpenHierarchy` and `CloseNotebook` went through (4.1 D, one run); the Security Notice and "can't open files from other programs" dialogs block `NavigateToUrl` (N1, N2); the cloud close's "syncing changes" box closed by itself (C2 C). Idle release never drops the COM reference while one of this process's pickers is open (A8; 4.13 F7 didn't exercise it).
- [ ] spine AD-2 -- the one COM code path off the STA: the QuickFiling callback arrives on an MTA thread of our process (4.13, 9/9, cause unknown); it may only read `SelectedItem`/`PressedButton` on the dialog passed in, record and signal, and return at once; it never touches the STA's `IApplication` (memlog l.358). Every `Window` and `Windows` wrapper is released in a `finally` (OneMore row 10).
- [ ] spine AD-11 -- user-visible: `NavigateTo`, `NavigateToUrl`, `QuickFiling`, `OpenHierarchy` on an existing notebook and `CloseNotebook` (4.1 C4 blanks the view, C2 C shows a modal box; memlog l.342), not `Windows` (Q5 a, W2) or `SyncHierarchy` (S1). The skill calls every `AppState` tool only when the user asks; `get_windows` when the user refers to what they're looking at (Q5); `navigate_to_url`'s landing check reads the windows internally (Q5). Descriptions promise no focus behaviour: `NavigateTo` didn't take focus in ten calls from the background (4.12 Focus finding) but opened a window in front when none was open (W6); the picker took the foreground (4.13 Implication 9). `navigate_to` without a window moves the first window, not the current one (W4).
- [ ] spine AD-9 -- no new code. `0x80042006` from `open_notebook` never reached (refused before COM, OQ3). `0x80042014` from `SyncHierarchy` (unknown or closed ID, S2) stays `ObjectNotFound`. `0x80042014` from `NavigateToUrl` is ambiguous (declined notice, web link, cloud repeat under 1 s; Implications 6, 7): it keeps `ObjectNotFound` with a method-specific `userAction`; `navigate_to`'s "wait two seconds" advice goes (R1: `NavigateTo` was never refused). `E_INVALIDARG` from `Windows.Item(Count)` is never reached (W1). `ComBusy` source gains a second picker (OQ5); `InvalidArgument` gains the `open_notebook`, `close_notebook` and `navigate_to_url` refusals.
- [ ] spine AD-12 -- the closed notebook's cache rule per OQ6.
- [ ] spine AD-13 -- path and URL arguments (`open_notebook`, `navigate_to_url`) carry notebook and section names, so they aren't logged; only their kind (local path or link) and the outcome are.
- [ ] spine AD-14 -- tool names `sync_hierarchy`, `close_notebook`, `navigate_to_url`, `quick_filing` by the rule; `open_notebook` (`OpenHierarchy`) and `get_windows` (`Windows`) as exceptions beside `create_page` (Source conflict); optional `window` argument on `navigate_to` and `navigate_to_url` (Q4).
- [ ] spine AD-18 -- the lock list per OQ1.
- [ ] spine AD-21 -- `get_windows` per tool: without the section's read grant, a window on a protected page reports the section and omits the page ID (W5, memlog l.352); `""` is "no page" (W5). What `open_notebook` opens follows What's protected on the canonical path (Q3).
- [ ] spine new section after AD-21, **Coverage of `IApplication`** -- a table of slots 7–35: method, released tool(s) and epic, or the dated Decision (MergeFiles: 3.9 dropped, 2026-11-16; SetFilingLocation Q8; the window setters Q4; events Q10; COMAddIns and LanguageSettings Q11; `Dummy1`: an undocumented placeholder). Entry 10 checks it against released tools.
- [ ] spine Deferred -- events row (l.866): the dated Decision (Q10: Microsoft's "Events cannot currently be added in managed code", OneMore polls, Claude Code channels are a gated research preview); revisit when channels leave the preview. Modal dialogs row (l.902): add the `AppState` evidence; other dialogs stay unprobed. Add: close's pending-write length (OQ2); C3 offline close; Cancel on the syncing box; the Security Notice's trigger; the QuickFiling callback thread's cause; a parented picker; `WM_CLOSE` on a picker; `NavigateTo` in an `-Embedding` instance; two windows' pick with OneNote in front.
- [ ] `epic-round-3-app-state-and-ui.md` -- reword A8 to "reaches our process" (Decision 6); a dated Decision per answered Open Question; A4's URL bullet marked "not met (4.1 O3)" by Decision 2.
- [ ] lint; fix findings.
- [ ] (orchestrating session) Fable final review at `effort: low`, Phill's fixes and approval, memlog `event`, `updated`.

**Acceptance Criteria:**
- Given the amended spine, when each of the four epic Source conflicts is traced, then each is settled in the AD it names, with a dated Decision.
- Given the Implications of 4.1 (1–11), 4.12 (1–14) and 4.13 (1–9), when each is traced, then it lands in an AD, a Deferred row, or a memlog line saying why not.
- Given slots 7–35 of `IApplication.cs`, when the coverage table is read, then every slot has a tool or a dated Decision.
- Given entries 3–10, when each reads its ADs, then no "entry 2 sets…" placeholder from the epic is left unowned.
- Given the Fable review at `effort: low` has run and Phill has approved, then the spine's `updated` is set (ticket Verify).

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Evidence strength is the findings' own ("one run", "one pair", "nine of nine"); a rule on one run says so, so Fable and later stories see what may move.
- No new error code: every refusal fits `InvalidArgument`, `ComBusy` or `NotSupported`, and a new code would change the public contract (AD-9, AD-14), as 3.2 found.
- Ticket effects for the orchestrating session's `bmad-ticket` pass, if Phill wants them recorded: entry 5 stays non-hitl (opens moved neither focus nor view, 4.1 O1/O4); entry 4's cloud close is hitl (C2 C modal box); entry 7 tests cloud links only under OQ4 (a); entry 9's Verify needs no change.

## Verification

**Commands:**
- `python ~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp` -- expected: only the known `{pid}`/`{guid}` false positives.
- `git diff --stat` -- expected: only the spine, its memlog and the epic file.

**Manual checks:**
- grep the spine for "entry 2", "4.2" and "epic 4's spine" placeholders: none left unowned.
