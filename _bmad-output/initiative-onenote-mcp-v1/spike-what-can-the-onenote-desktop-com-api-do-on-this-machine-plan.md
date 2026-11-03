---
title: 'Spike: what can the OneNote desktop COM API do on this machine?'
type: 'chore'
ticket: '1'
created: '2026-11-03'
status: done
baseline_revision: 'be551c4ce51d292e8b1deeac4a536fcf3a71a1b4'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The architecture spine assumes the OneNote desktop COM API works on M365, but nothing current confirms it, and we don't know its failure modes or which MCP host language handles its STA threading best.

**Approach:** Run probes against the installed OneNote: PowerShell probes for what the API can do, then a minimal C# and Python MCP server that each call `GetHierarchy`. Record the results in a findings doc, and log the constraints they set in the architecture memlog.

## Boundaries & Constraints

**Always:** Every write, rename, delete or publish call goes through one guard. The guard re-reads the spike notebook's hierarchy and refuses any target ID that isn't inside it. The spike notebook lives in `%LOCALAPPDATA%\OneNoteMcpSpike\`, which is outside the repo and outside OneDrive. Real notebooks get read-only calls only (`GetHierarchy`, `GetPageContent`). Each probe appends a result row (method, args, outcome, HRESULT, ms) to a JSONL log. Confirm SDK versions on nuget.org and pypi.org before pinning them.

**Never:** Change, move or delete anything in a pre-existing notebook. Close or kill OneNote without asking Phill first. Build a production server shape: no tool surface design, no packaging. Commit any notebook content from real notebooks; logs record IDs and counts only, never page text.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Read hierarchy | OneNote running | XML for every scope, plus the namespace | Log the HRESULT on failure |
| Edit with stale date | Page modified after the date was read | `UpdatePageContent` is rejected | Record the HRESULT returned; the rejection itself is a pass |
| Guarded write to a foreign ID | A target ID from a real notebook | The guard refuses before any COM call | The probe passes when the refusal is logged |
| OneNote closed | Phill closes OneNote | Record whether creating the object starts it, and the time it takes | Log the HRESULT |
| Elevated probe | Phill runs the probe elevated, OneNote not elevated | Record the activation outcome | Log the HRESULT |
| Modal dialog / sync / locked section | Phill sets up each state | Record the behaviour of one read and one write | Log the HRESULT; time out the call at 30 s |
| Parallel tool calls | Two MCP calls at once, in each host | Both return, or the failure is recorded | Log it per host |

</frozen-after-approval>

## Code Map

The repo has no code yet. Tooling confirmed on 2026-11-03:
- pwsh 7.6.9; Windows PowerShell 5.1 at `C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe`, the fallback if pwsh late binding fails on `[ref]` out-params
- `Microsoft.Office.Interop.OneNote` is present in GAC_MSIL, with `HierarchyScope`, `PageInfo` and `CreateFileType` enums
- ONENOTE.EXE 16.0.20527.20112 (M365 Click-to-Run); CLSID `{DC67E480-C3CB-49F8-8232-60B0C2056C8E}`
- .NET SDK 10.0.103; Python 3.13.14; uv 0.9.15; the WSearch service is running; the session is not elevated
- `CLAUDE.md`, section "OneNote COM constraints": the claims under test
- `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md`: write `constraint` entries with `uv run _bmad/scripts/memlog.py append` only

## Tasks & Acceptance

**Execution:**
- [x] `spikes/com-capabilities/OneNoteProbe.psm1` -- COM object helper, the ID guard, JSONL result logger, and a timeout wrapper -- shared by every probe
- [x] `spikes/com-capabilities/01-read.ps1` -- hierarchy at every scope, page content with and without binary data, timing on the largest real notebook (read-only) -- read capability
- [x] `spikes/com-capabilities/02-write.ps1` -- create the spike notebook, then section create/rename, page create, append and edit, a stale-date conflict, delete and a recycle-bin check, Publish to PDF/docx, NavigateTo -- write capability
- [x] `spikes/com-capabilities/03-search.ps1` -- FindPages straight after a write and again after a delay -- search versus index state
- [x] `spikes/com-capabilities/04-failure-modes.ps1` (run by Phill 2026-11-03, 13:24–13:40) -- an interactive runner that asks Phill to set each state, then runs one read and one write -- failure modes (hitl)
- [x] `spikes/host-csharp/` -- minimal stdio MCP server (ModelContextProtocol), one `get_hierarchy` tool, with COM on a dedicated STA thread -- host comparison
- [x] `spikes/host-python/` -- minimal stdio MCP server (`mcp`, run with uv) with the same tool, using pywin32 on an STA worker thread -- host comparison
- [x] `spikes/results/*.jsonl` -- the probe logs, IDs and counts only -- evidence
- [x] `_bmad-output/initiative-onenote-mcp-v1/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md` -- capability matrix, failure-mode table, HRESULT list, host comparison and stack recommendation -- the deliverable
- [x] the architecture `.memlog.md` -- one `constraint` entry per finding that binds the architecture -- feeds the paused spine

**Acceptance Criteria:**
- Given the probes have run, when the findings doc is read, then every row has a result (works / fails with a HRESULT / not tested with a reason) backed by a JSONL log line
- Given a real notebook's hierarchy before and after the spike, with Phill not editing in between, when they're compared, then their `lastModifiedTime` values are unchanged
- Given each host server is registered in Claude Code, when its `get_hierarchy` tool is called, then it returns the notebook list
- Given the findings doc is drafted, when the Fable final review has run and its confirmed findings are resolved, then Phill's acceptance is recorded in the ticket's Notes

## Implementation Notes

- 2026-11-03: probes run under Windows PowerShell 5.1. pwsh 7 fails every late-bound OneNote call with E_FAIL; each script re-launches itself under `powershell.exe` when started from pwsh, so the `pwsh -File ...` verification commands still work.
- 2026-11-03: Python host uses **comtypes 1.4.17** (vtable) rather than pywin32 for COM; pywin32 fails because OneNote's IDispatch cannot load its Win32-only-registered type library for 64-bit callers (`pywin32_probe.py`, `host.jsonl`). pywin32 312 stays as a dependency for that probe. This deviates from the Code Map's "pywin32 on an STA worker thread".
- 2026-11-03: C# host references the GAC OneNote 15 interop with `EmbedInteropTypes=false` (CS1756 blocks embedding). A local `nuget.config` limits restore to nuget.org (the machine's Telerik feed made restore take ~8 min).
- 2026-11-03: added `00-baseline.ps1` (before/after `lastModifiedTime` snapshot + compare, IDs only) for the untouched-notebooks check, and `spikes/host-python/mcp_check.py` (MCP client that logs tools/list, single and parallel calls to `host.jsonl`).
- 2026-11-03: `mcp_check.py` passes the full environment to the server; the SDK's reduced default env makes `dotnet run` emit MSBuild errors on stdout.
- 2026-11-03: search run 1's JSONL was overwritten while the script was being edited; its console output (indexed-only not found after 300 s) is cited in the findings. Runs 2 and 3 are logged.
- 2026-11-03 (reported by Phill via main): a human typed into the MCP Spike notebook during the probes, after a COM call brought OneNote to the foreground and took keyboard focus. The candidate window is 12:48-12:50 local: the logged NavigateTo calls at 12:50:04, 12:50:12 and 12:50:23 (all spike page IDs), plus ad hoc NavigateTo calls at about 12:48:30 and 12:49:19 while I was investigating `lastModifiedTime`. Main confirmed no real notebook was touched. Consequence: the "page displayed" `lastModifiedTime` result (`lmt-trace-displayed`) could be confounded by that typing. `05-focus.ps1` re-runs it with a fresh read before every step, and also records which calls take the foreground. From now on, no call that shows or focuses OneNote runs outside `05-focus.ps1`, and that script waits for main's go-ahead.
- Open: `04-failure-modes.ps1` scenarios need Phill in the main session (closing OneNote, elevated shell, modal dialog, sync, locked section). Hosts not yet registered in Claude Code (`claude mcp add`); both were driven over stdio by `mcp_check.py` instead.

## Plan Change Log

## Review Triage Log

**Pass 1 (quick lens, 2026-11-03).** Verdicts: 0 high, 7 medium, 8 low, 1 false, 0 maybe-false. 11 patched; 5 rejected (4 low, 1 false).

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | The after-snapshot predates the later probes | medium | patch | `baseline.jsonl` snapshot-after is at 13:07:30. The focus, failure-mode and host runs came after it. Re-run `00-baseline -Label after` and `-Compare`, and explain any change (a Shift+F9 sync of the cloud notebooks can legitimately move `lastModifiedTime`). |
| 2 | Earlier 02-write runs were lost and not disclosed | medium | patch | Before the logged run's first delete, `delete-page-recycle-check` already shows 2 pages and 2 sections in the recycle bin. Disclose the lost runs in the findings doc. |
| 3 | Some findings rows and memlog constraints have no JSONL line behind them | medium | patch | Headless-block, U3, U6, the AC4 registration and "2 of 3" have no log line. Label each one as observed rather than logged, restate search as "1 logged miss + 1 lost run", and supersede the memlog constraints that rest on unlogged evidence. |
| 4 | The hung-call row cites the wrong evidence | medium | patch | The modal-dialog write failed in 19 ms. The logged hangs are the elevated `warmup-read` (30018 ms) and `elevated-read` (30010 ms). Cite those instead. |
| 5 | The elevated row says no write was attempted | low | patch | `04-failure-modes.ps1` lines 78–80 call `Write-Once` after `Read-Once`, but there's no write row because Ctrl+C stopped the run. Record that. |
| 6 | Plan Implementation Notes are stale on host registration | low | reject | Fixing it means editing this build's plan. The findings doc already records the main session's registration. |
| 7 | AC4/AC6 unmet: no Fable review yet | false | reject | The Fable review and Phill's acceptance are the close-out step after this review, not a defect in the diff. |
| 8 | The JSONL `verdict` contradicts the search findings | medium | patch | Indexed-only polls log `works` even with `targetFound:false`. Add a note to the findings doc on what the verdict field means. Don't rewrite the evidence logs. |
| 9 | Row 6 understates the image loss | low | patch | `read.jsonl` has 4 pages with images lost (1/0, 1/0, 2/0, 3/0). Direct correction. |
| 10 | The memlog keeps a withdrawn constraint live | medium | patch | Append a superseding constraint marking local-kind rejection unverified (the memlog is append-only). |
| 11 | Undisclosed read-only calls on real notebooks | low | patch | `GetBinaryPageContent` and root `FindPages` are read-only but aren't on the plan's list. Note them in the findings deviations. |
| 12 | `05-focus.ps1` has no confirmation prompt | low | reject | Spike script with no further runs planned. The fix adds a guard. |
| 13 | The untouched check relies on `lastModifiedTime` | medium | patch | The spike itself shows `lastModifiedTime` lags COM edits. Restate row 32's evidence as guard + logged targets + comparison, with the comparison marked weak. |
| 14 | The Python host hangs at startup if the typelib fails | low | reject | Spike code. The typelib loads on this machine, and the fix adds a timeout. Carry it into the real server's design instead. |
| 15 | 02-write step 12 doesn't check `.ok` | low | reject | Spike script, unlikely path, and the fix adds a branch. |
| 16 | `Skip` logs the wrong method for the locked scenarios | low | patch | `04-failure-modes.ps1` line 41 is hard-coded to `UpdatePageContent`. Direct correction: log the scenario's real write method. |

## Design Notes

The guard can't check ID prefixes, because OneNote child IDs don't embed the parent's ID. It looks for the target ID inside `GetHierarchy(spikeNotebookId, hsPages)` output instead. The hitl steps (closing OneNote, running elevated, opening a dialog, locking a section) run in the main session with Phill, not in a subagent.

## Verification

**Commands:**
- `pwsh -File spikes/com-capabilities/01-read.ps1` -- expected: exit 0, and `results/read.jsonl` has a row per scope
- `dotnet run --project spikes/host-csharp` / `uv run --project spikes/host-python server.py` -- expected: each starts and answers MCP `tools/list`

**Manual checks (if no CLI):**
- Before and after the spike, compare `lastModifiedTime` values in `GetHierarchy("", hsNotebooks)` for every real notebook -- expected: identical
