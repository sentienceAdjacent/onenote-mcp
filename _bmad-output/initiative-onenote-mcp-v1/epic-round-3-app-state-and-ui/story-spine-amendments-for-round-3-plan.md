---
title: 'Spine amendments for round 3'
type: 'chore'
ticket: '2'
created: '2026-11-21'
status: 'built'
baseline_revision: 'ffb64ed934336ccf671acfd9e96ea7c9a2f43ebf'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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

## Decisions (Phill, 2026-11-22, Inbox; plan approved with every recommendation)

The plan's questions are referred to below as OQ1–OQ7.

- **OQ1 (a).** `open_notebook` and `close_notebook` take the write lock; `sync_hierarchy`, `navigate_to`, `navigate_to_url`, `get_windows` and `quick_filing` don't. Every `AppState` call returns `ComBusy` while an abandoned write is outstanding (AD-3). Why: the open's notebook test must not race a create (A4); a close must not run between another pipeline's write and its read-back (4.1 C2); an unparented picker blocked nothing (4.13 F4), and one held under the lock would stall every write.
- **OQ2 (b).** Under the lock, `close_notebook` runs `SyncHierarchy` on the notebook, waits a fixed settle of about 10 s, then calls `CloseNotebook`; the result says so. The length is unmeasured (a Deferred row) and is measured in story 4.4's sitting, which must show a local edit surviving before 4.4 ships. Whether `SyncHierarchy` saves a local notebook is unknown.
- **OQ3 (a).** The notebook test: the canonical folder exists, holds a `.onetoc2`, and no ancestor folder holds one. A missing path, a `.one`, a `.onepkg`, a UNC or device path and a folder failing the test are refused with `InvalidArgument` before COM; a notebook whose `.onetoc2` hasn't landed yet gets a `userAction` to wait. The ancestor check is inferred, not probed.
- **OQ4 (a).** `navigate_to_url` refuses a link whose target is a local path, with `InvalidArgument` and a `userAction` to use `navigate_to` with the object's ID; it accepts cloud (`onenote:https://…`) links only.
- **OQ5 (a).** `quick_filing`: a fixed 300 s wait (no setting) that the client can cancel; unparented; no lock; a second call while one of this process's pickers is open is refused with `ComBusy`; a wait that ends is `ok: true` with nothing chosen, warning that the picker may still be on screen and a later choice is ignored; a late answer is dropped.
  - Superseded in part (Phill, 2026-11-22, review finding 3): the hold on a picker ends when the call's wait ends (the 300 s limit or the client's cancel). The `ComBusy` for a second call ends then, AD-3's idle release applies again, and a late answer is still dropped.
- **OQ6 (a).** A closed notebook's pages are left to the search cache's eviction.
- **OQ7 (a).** One story.
- **Accepted as applied:** `open_notebook` refuses URLs (Q3's condition not met, 4.1 O3); a 30 s per-call limit for every `AppState` call; `get_windows` stays class `AppState`, is never `outcomeUnknown`, and its `Timeout` is retryable.

</frozen-after-approval>

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
- [x] `.memlog.md` -- one `decision` line per answered Open Question and per rule below that isn't already a constraint line; one `event` line when written.
- [x] spine AD-7 -- **`AppState` pipeline**: steps 1 (argument checks; no grant, Q2), 2 (the lock per OQ1), 3–4 (the tool's checks), 7 (COM), 8; no basis (step 5) and no snapshot (step 6); the 120 s deadline; `outcomeUnknown` on step 7's ambiguous failures for every tool that changes state, never for `get_windows` (4.12 W2: it changes nothing; its `Timeout` is retryable as a read's); `ComBusy` while an abandoned write is outstanding, for every `AppState` call. `navigate_to` moves onto it (entry 3; resolves the AD-7 Source conflict). Rewrite the table row l.334: methods (`OpenHierarchy` on an existing notebook, `CloseNotebook`, `SyncHierarchy`, `NavigateTo`, `NavigateToUrl`, `QuickFiling`, `Windows`; `SetFilingLocation` dropped), access none, validation per tool (below), snapshot none. **`open_notebook` validation**: canonical path first (`GetFullPath`, links and junctions resolved; 4.1 O9, O10: OneNote keeps the path given), the canonical form sent to COM; already open by `NotebookKey` or canonical path → its ID, no COM call (O4: harmless either way; saves a call); the notebook test per OQ3 under the lock; URLs refused (Decision 2); the result's ID, name and the fill warning (local 2 s, cloud up to 16 s, OneNote's list up to a minute: O2, O8, C2). **`close_notebook` validation**: a live notebook ID, anything else `InvalidArgument` before COM (A3); the pending-write rule per OQ2; result: name, path, "every ID changes on reopen" (O1, S2), "the view may go blank" (C4), how to reopen (`open_notebook` locally; OneNote for a cloud notebook). **`sync_hierarchy`**: any hierarchy ID; ok means requested (S1, 1.15 quirk 3). **`navigate_to_url`**: `onenote:` only; the link's notebook matched by `NotebookKey` among open notebooks (N4), a link to a section path no open notebook holds refused (N5); OQ4's local-link rule; the landing check below. **`quick_filing`** per OQ5.
- [x] spine AD-7 landing check (`navigate_to_url`, 4.12 K1, K2, Implications 4, 5) -- straight after the call, no settle: a page or paragraph link compares `GetHyperlinkToObject` of `CurrentWindow`'s page with the link's `page-id` (the only check that caught N3's miss); a section link compares `CurrentSectionId` with the open section whose `path` is the link's `.one`; a notebook link compares the notebook. A mismatch or `""` page warns `landedElsewhere`; the result says "OneNote's current page is …", not "OneNote shows …" (cloud repaint lag, N1 C).
- [x] spine AD-3 -- per-call timeouts: 30 s for every `AppState` method (largest seen 7.1 s, cloud close with a pending edit, C2 C; 2.3 s URL open, O2); a close held behind a dialog or offline is `Timeout` with `outcomeUnknown` (Decision 7). Modal dialogs: under File > Options `SyncHierarchy`, `OpenHierarchy` and `CloseNotebook` went through (4.1 D, one run); the Security Notice and "can't open files from other programs" dialogs block `NavigateToUrl` (N1, N2); the cloud close's "syncing changes" box closed by itself (C2 C). Idle release never drops the COM reference while one of this process's pickers is open (A8; 4.13 F7 didn't exercise it).
- [x] spine AD-2 -- the one COM code path off the STA: the QuickFiling callback arrives on an MTA thread of our process (4.13, 9/9, cause unknown); it may only read `SelectedItem`/`PressedButton` on the dialog passed in, record and signal, and return at once; it never touches the STA's `IApplication` (memlog l.358). Every `Window` and `Windows` wrapper is released in a `finally` (OneMore row 10).
- [x] spine AD-11 -- user-visible: `NavigateTo`, `NavigateToUrl`, `QuickFiling`, `OpenHierarchy` on an existing notebook and `CloseNotebook` (4.1 C4 blanks the view, C2 C shows a modal box; memlog l.342), not `Windows` (Q5 a, W2) or `SyncHierarchy` (S1). The skill calls every `AppState` tool only when the user asks; `get_windows` when the user refers to what they're looking at (Q5); `navigate_to_url`'s landing check reads the windows internally (Q5). Descriptions promise no focus behaviour: `NavigateTo` didn't take focus in ten calls from the background (4.12 Focus finding) but opened a window in front when none was open (W6); the picker took the foreground (4.13 Implication 9). `navigate_to` without a window moves the first window, not the current one (W4).
- [x] spine AD-9 -- no new code. `0x80042006` from `open_notebook` never reached (refused before COM, OQ3). `0x80042014` from `SyncHierarchy` (unknown or closed ID, S2) stays `ObjectNotFound`. `0x80042014` from `NavigateToUrl` is ambiguous (declined notice, web link, cloud repeat under 1 s; Implications 6, 7): it keeps `ObjectNotFound` with a method-specific `userAction`; `navigate_to`'s "wait two seconds" advice goes (R1: `NavigateTo` was never refused). `E_INVALIDARG` from `Windows.Item(Count)` is never reached (W1). `ComBusy` source gains a second picker (OQ5); `InvalidArgument` gains the `open_notebook`, `close_notebook` and `navigate_to_url` refusals.
- [x] spine AD-12 -- the closed notebook's cache rule per OQ6.
- [x] spine AD-13 -- path and URL arguments (`open_notebook`, `navigate_to_url`) carry notebook and section names, so they aren't logged; only their kind (local path or link) and the outcome are.
- [x] spine AD-14 -- tool names `sync_hierarchy`, `close_notebook`, `navigate_to_url`, `quick_filing` by the rule; `open_notebook` (`OpenHierarchy`) and `get_windows` (`Windows`) as exceptions beside `create_page` (Source conflict); optional `window` argument on `navigate_to` and `navigate_to_url` (Q4).
- [x] spine AD-18 -- the lock list per OQ1.
- [x] spine AD-21 -- `get_windows` per tool: without the section's read grant, a window on a protected page reports the section and omits the page ID (W5, memlog l.352); `""` is "no page" (W5). What `open_notebook` opens follows What's protected on the canonical path (Q3).
- [x] spine new section after AD-21, **Coverage of `IApplication`** -- a table of slots 7–35: method, released tool(s) and epic, or the dated Decision (MergeFiles: 3.9 dropped, 2026-11-16; SetFilingLocation Q8; the window setters Q4; events Q10; COMAddIns and LanguageSettings Q11; `Dummy1`: an undocumented placeholder). Entry 10 checks it against released tools.
- [x] spine Deferred -- events row (l.866): the dated Decision (Q10: Microsoft's "Events cannot currently be added in managed code", OneMore polls, Claude Code channels are a gated research preview); revisit when channels leave the preview. Modal dialogs row (l.902): add the `AppState` evidence; other dialogs stay unprobed. Add: close's pending-write length (OQ2); C3 offline close; Cancel on the syncing box; the Security Notice's trigger; the QuickFiling callback thread's cause; a parented picker; `WM_CLOSE` on a picker; `NavigateTo` in an `-Embedding` instance; two windows' pick with OneNote in front.
- [x] `epic-round-3-app-state-and-ui.md` -- reword A8 to "reaches our process" (Decision 6); a dated Decision per answered Open Question; A4's URL bullet marked "not met (4.1 O3)" by Decision 2.
- [x] lint; fix findings.
- [ ] (orchestrating session) Fable final review at `effort: low`, Phill's fixes and approval, memlog `event`, `updated`.

**Acceptance Criteria:**
- Given the amended spine, when each of the four epic Source conflicts is traced, then each is settled in the AD it names, with a dated Decision.
- Given the Implications of 4.1 (1–11), 4.12 (1–14) and 4.13 (1–9), when each is traced, then it lands in an AD, a Deferred row, or a memlog line saying why not.
- Given slots 7–35 of `IApplication.cs`, when the coverage table is read, then every slot has a tool or a dated Decision.
- Given entries 3–10, when each reads its ADs, then no "entry 2 sets…" placeholder from the epic is left unowned.
- Given the Fable review at `effort: low` has run and Phill has approved, then the spine's `updated` is set (ticket Verify).

## Implementation Notes

2026-11-22, built (every Execution task but the last, which is the orchestrating session's):
- Verification: `lint_spine.py` reports only the known `{pid}` low finding; `git diff --stat` touches only the spine, its memlog, the epic and this plan; no "entry 2" / "4.2" / "epic 4's spine" placeholder in the spine. The memlog script also stamped the memlog's own `updated`; the spine's `updated` is unchanged (2026-11-19).
- Implication trace: every Implication of 4.1, 4.12 and 4.13 lands in an AD or a Deferred row, except 4.1 Implication 8 (test hitl-ness), which gets a memlog line as a ticket effect.
- Small choices made while writing, for the Fable review and Phill to check (each inferred from an approved decision, none new policy):
  - The warning codes for `open_notebook`'s fill lag and `quick_filing`'s ended wait aren't named; stories 4.5 and 4.9 name them (AD-9 says each warning is defined by the AD that raises it). Only `landedElsewhere` is named, as the plan says.
  - A client cancel during `quick_filing`'s wait is `Cancelled` without `outcomeUnknown` (`Run` has already returned).
  - A `window` argument that names no current window is `InvalidArgument` before COM (from Q4); `close_notebook` given an ID no open notebook has is `InvalidArgument`, per "anything else".
  - Extra Deferred row: `Window.NavigateToUrl` (4.12 Not probed), which `navigate_to_url`'s `window` argument needs; story 4.7 tries it first. The `NavigateTo`-windowless row notes, inferred, that AD-3's no-window rule probably refuses W6's state before COM.
  - AD-6 gains the C5 close/reopen evidence; AD-14 names epic 5's `create_notebook`, `create_section` and `create_section_group` as existing name exceptions beside `create_page`; the spine's `sources` gains the three round-3 findings.
  - AD-21 says `navigate_to` and `navigate_to_url` need no read grant (as shipped) and that a result naming the current page follows `get_windows`' rule.
- Ticket effects for the orchestrating session's `bmad-ticket` pass (Design Notes): entry 5 stays non-hitl; entry 4's cloud close is hitl; entry 7 tests cloud links only and tries `Window.NavigateToUrl`; entry 4's Verify needs the local-edit-survives check (OQ2).

2026-11-22, Fable final review fixes (approved by Phill, Inbox; docs only, applied by a subagent):
- Phill's decisions: D1 `open_notebook` refuses a folder inside a OneDrive-synced folder (step 1 and under the lock); D2 `close_notebook` given an ID no open notebook has is `ObjectNotFound`, a wrong-kind ID stays `InvalidArgument` (this replaces the `InvalidArgument` choice listed above); D3 the small choices listed above, and the review fixes in the memlog's "not Phill's" line, are accepted. Recorded in the spine (AD-7 `AppState`, AD-9, AD-13, AD-21), the epic's Notes and three memlog lines.
- Fable findings 2–4, 6–19, F1, F2 and the editorial ones applied to the spine (AD-2, AD-3, AD-7 `AppState`, AD-9, AD-11, AD-14, Coverage, Consistency Conventions, Deferred), the epic (A1, A3, A4, OQ5, the four Source conflicts, Answered-before lines), this plan's frozen OQ5 (one dated note, finding F2) and the 4.12 and 4.13 findings ("Draft." removed). Two memlog lines (the fixes, and 4.1's Incomplete sync gap).
- Design Notes' refusal-code line was then corrected by the build session to `InvalidArgument`, `ObjectNotFound` or `ComBusy`, matching AD-9. Not changed: the frontmatter's local `created` date.
- Verification: `lint_spine.py` reports only the known `{pid}` low finding; the spine's `status` and `updated` unchanged.

## Plan Change Log

## Review Triage Log

- 2026-11-22, quick lens (one reviewer, same model), pass 1: 6 findings; verdicts 0 high, 3 medium, 3 low, 0 false, 0 maybe-false. Routes: 5 patch (applied by the build session, the implementer not re-engaged), 1 intent_gap (not reverted, see below).
  - medium, patch -- AD-7 `AppState`/AD-18: whether an abandoned `CloseNotebook` or `OpenHierarchy` counts as an abandoned write was unsaid; `ComGateway.cs` tracks only `CallKind.Write` (l.964, 983) and runs `NavigateTo` as `AppState` (l.244), so a timed-out close could release the lock while it may still finish (4.1 C2). Fixed: they count, the other `AppState` calls don't; `get_windows` is a read for the disconnect retry.
  - low, patch -- AD-9 table: `ComUnavailable` and `Cancelled` rows didn't place `AppState` tools, and `Cancelled` read as contradicting `quick_filing`'s cancel rule. Fixed in both rows.
  - medium, intent_gap -- AD-7/AD-3 `quick_filing`: "open until its callback arrives" has no other end, so a picker never closed (or OneNote closed under it) holds the COM reference and refuses later calls until the server exits. A bound changes epic A8's "while a picker is open, idle release doesn't drop the reference", which is Phill's. Deviation from step 4: not reverted (a docs-only change, one sentence at stake); a Deferred row and a memlog line record it, and the question goes to Phill with the Fable review. Recommended answer: end the hold and the `ComBusy` when the call's wait ends (4.13 F8: a disposed gateway left a later pick working).
  - medium, patch -- epic A3 and A7 contradicted the new decisions (A3's URL clause, A7's local-path matching and can't-confirm warning), and three Assumptions (already-open shortcut, the lock, `Dummy1`) stayed "unconfirmed". Fixed: superseded wording in A3 and A7, the three Assumptions marked decided or confirmed.
  - low, patch -- AD-7 class rule (and AD-5) called `open_package`'s `OpenHierarchy` `AppState`, while it runs inside its `ExternalWrite` pipeline and `ComGateway` sends it as a write (l.398–407). Fixed both to match the shipped code.
  - low, patch -- 4.12 Implication 11 ("the result says which window moved") landed nowhere. Fixed with a memlog line: naming the window is a result field (AD-14), left to stories 4.6 and 4.7.
- Verification after the patches: `lint_spine.py` only the known `{pid}` false positive.
- 2026-11-22, the intent_gap above resolved by Phill (Inbox, review finding 3): the server lets go of a picker when the call's wait ends (300 s or the client's cancel); idle release applies again, a new picker is allowed, a late answer is still ignored. Applied to epic A8, spine AD-3 Idle, AD-7 `AppState`, AD-9 `ComBusy` and the table row; the Deferred row resolved; one memlog decision line by Phill.

## Design Notes

- Evidence strength is the findings' own ("one run", "one pair", "nine of nine"); a rule on one run says so, so Fable and later stories see what may move.
- No new error code: every refusal fits `InvalidArgument`, `ObjectNotFound` or `ComBusy`, and a new code would change the public contract (AD-9, AD-14), as 3.2 found.
- Ticket effects for the orchestrating session's `bmad-ticket` pass, if Phill wants them recorded: entry 5 stays non-hitl (opens moved neither focus nor view, 4.1 O1/O4); entry 4's cloud close is hitl (C2 C modal box); entry 7 tests cloud links only under OQ4 (a); entry 9's Verify needs no change.

## Verification

**Commands:**
- `python ~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp` -- expected: only the known `{pid}`/`{guid}` false positives.
- `git diff --stat` -- expected: only the spine, its memlog and the epic file.

**Manual checks:**
- grep the spine for "entry 2", "4.2" and "epic 4's spine" placeholders: none left unowned.
