---
title: 'Findings: app-state probes, part 1: open, close and sync'
type: 'spike-findings'
findings_for: '1'
status: 'final'
created: '2026-11-19'
machine: 'Windows 11 Pro 10.0.26300, OneNote desktop (M365), not elevated; the OneNote build was not recorded by these runs'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state-local-20261119-191413.jsonl, app-state-local-20261119-194035.jsonl, app-state-cloud-20261120-032732.jsonl and app-state-cloud-20261120-044350.jsonl (outside the repo); Phill''s reports and screenshots per pause, in the coordinator''s sitting logs'
---

# Findings: app-state probes, part 1: open, close and sync

**Final** (approved by Phill on 2026-11-20). Sitting A (local) ran on 2026-11-19 and sitting B (cloud) on 2026-11-20 (UTC). C3 (close offline) was deferred by Phill and isn't probed. The Fable final review ran and its 13 confirmed findings were fixed; the memlog lines are in the spine's `.memlog.md`.

**Top finding: `OpenHierarchy` with `cftNone` creates a notebook in any existing folder.** An empty folder and a folder holding only `probe-marker.txt` both opened as notebooks (O6), each named after its folder. Both then held an `Open Notebook.onetoc2` (6,080 bytes) that the probe never wrote (L). Only a missing path is refused (`0x80042006`, O5). So `cftNone` doesn't protect a folder that isn't a notebook: `open_notebook` has to apply entry 2's notebook test before COM, and that test can't trust a `.onetoc2` that an earlier open may have written.

**Second finding, verified: an edit made just before a local close is lost for good.** In C2, `UpdatePageContent` returned ok in 273 ms and `CloseNotebook` returned ok in 109 ms right after, with no dialog and nothing in Misplaced Sections. After the reopen, the page was there, but its text didn't hold the edit's marker (`editSurvived: false`). It never came back (re-read 2026-11-19 at about 20:35 UTC, about 38 min after C2's close at 19:57:24, by the coordinator through the onenote-dev server's `get_page_content` and `find_pages`; that read is recorded here and in the 4.1 Inbox thread, not in a JSONL log: the page `probe pend local p 1119-191413` holds only its title and `probe text`, and `find_pages` from the root for `probe pending edit local 1119-191413` doesn't match it. Its `lastModifiedTime` was unchanged, but that proves nothing: it doesn't advance after COM edits to a page that isn't open). The section file grew before C4's reopen (11,504 to 13,712 bytes; `c2-local-reopen` and `c4-reopen` disk listings); inferred from the later re-read, that growth wasn't the edit. One run; whether a delay before the close saves the edit is open (Open before final). **A cloud close didn't lose it** in one online run (C2 C): `CloseNotebook` took 7.1 s while OneNote showed a modal "OneNote is syncing changes." box with a progress bar and Cancel, which closed on its own; the edit was on the page in OneNote for the web and after the reopen (`editSurvived: true`).

**Short answer (local):**

1. **Close** returns ok in 38–126 ms, with no dialog reported, even with an edit just made (C1, C2) and even with File > Options open (D). Every other open notebook kept every ID (C1 and each later close). Closing the notebook on screen leaves OneNote viewing nothing, as COM sees it (C4).
2. **Open of a closed notebook** returns in 26–40 ms. The notebook gets a new ID and keeps its `name` and `path` (O1, O7, C2, C4). Its sections and pages show in the hierarchy within 2 s, but Phill saw two notebooks take about a minute to appear in OneNote's list (O8, C2).
3. **Open of an already-open notebook** returns its own ID, and the view and focus stay where they were (O4: MCP Test and `c`, confirmed by Phill). OneMore's "OneNote navigates to it" didn't happen with `cftNone`.
4. **A section group's folder** returns the group's ID while its notebook is open. Once the notebook is closed, the folder opens as a notebook of its own (O8), as Microsoft documents.
5. **Junction and UNC paths open the notebook under the path given.** OneNote reports the junction or `\\localhost\<drive>$` path, not the target folder, and names the junction notebook after the junction (O9, O10).
6. **`SyncHierarchy`** on MCP Test returns ok in 293–367 ms with no visible effect (S1, D). An unknown ID and a closed notebook's old ID both return `0x80042014` hrObjectDoesNotExist, in 606–654 ms (S2).
7. **No call moved the foreground window.** In every row, the window and process were the same before and after the call. The view changed only in C4. Phill reported no dialog at any pause; at O7, C1, C4 and D he didn't say either way.
8. **A write grant survives a close and reopen** (C5): it read `active`, then `notebookNotOpen`, then `active` again, keyed by the path.

**Short answer (cloud, sitting B):**

1. **Sync** of MCP Test Cloud and the cloud throwaway returns ok in 377 and 212 ms, with the view unchanged. Every Sync Status check read "Up to date" for both test notebooks, with no `0xE0000644` (S1 C).
2. **Close with an edit pending waits for the sync**: ok in 7,104 ms (the edit took 239 ms), while OneNote showed its modal "OneNote is syncing changes." box, and **the edit survived** (C2 C). The foreground window changed across the call, the only call in either sitting where it did, but to a window that wasn't OneNote's (`Process: other` before and after). That the box took the focus is inferred from Phill's report of a modal box.
3. **Open by OneDrive URL** reopens a closed cloud notebook in 2.3 s with **a new ID**, the same name and the same URL path. Its 11 sections and 13 pages appeared between 4 and 16 s (O2).
4. **A URL can't create at a missing folder** (`0x80042006` hrFileDoesNotExist, 274 ms, and no OneDrive folder appeared), **but it turns an existing empty OneDrive folder into a notebook**: ok in 2.3 s, and the folder then held `Open Notebook.onetoc2` (O3).
5. **Open of an already-open cloud notebook** returns its own ID in 239 ms. Nothing appeared, and the view stayed on MCP Test > Seed (O4 C).
6. **The cloud write grant** read `active` → `notebookNotOpen` → `active` across C2's close and O2's reopen, keyed by its URL (C5 C).

**How it ran.** Scenario `app-state-local` in `tests/OneNoteMcp.Integration/WriteProbes/AppStateLocalProbes.cs`, run by the main session with Phill present. Run stamp `1119-191413`. Notebooks: MCP Test (local), MCP Test Cloud (shown at O4, not touched), `MCP Test Throwaway 1112-034716 b` and `… c` (adopted by exact name), and the run's own `MCP Test Throwaway 1119-191413 o1`–`o4`, made in `%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\1119-191413\`. Every call went through `TestNotebook`'s guard, and every shown check came back empty. Each row cites a probe name; search for it in the JSONL logs. Times are UTC. "Phill" marks what he saw in OneNote and reported; it isn't in the logs.

**Sitting B** ran scenario `app-state-cloud` (`AppStateCloudProbes.cs`) with Phill present, run stamp `1120-032732`, on MCP Test Cloud and `MCP Test Throwaway Cloud`. A first start (`-023405`) was killed for low memory at its second pause and recorded nothing past S1; `-032732` reran from the top. Its O4 C step timed out at its first pause (no answer in 1,800 s) and was rerun alone as `-044350` (`ONENOTE_MCP_PROBE_STAMP=1120-032732`, `ONENOTE_MCP_PROBE_STEPS=already-open-cloud`), which passed. `-032732` contributes S1 C, O3, C2 C, O2, C5 C, C3 (skipped) and cleanup.

**Which run counts.**
- **`-191413`** (19:14–19:40): the setup, S1 L, S2, `make`, O7, C1, O1, C5 L, then O4 on MCP Test. It was killed for low memory at the report pause after O4 (`already-open-mcp-test-report`). It contributes everything before that pause.
- **`-194035`** (19:40–20:26): the rerun from `already-open`, with `ONENOTE_MCP_PROBE_STAMP=1119-191413`, so it reused the run folder (`reused: true`). It contributes O4 (MCP Test again, same result, and `c`), O5, O6, O8, C2 L, C4, O9, O10, D, L and cleanup. It passed: 8 passed and 1 skipped (the cloud scenario).

## Results, one row per Matrix question

| Id | Question | Result, local | Result, cloud | Probes |
|---|---|---|---|---|
| S1 | `SyncHierarchy` on a notebook | **ok, 367 ms**, on MCP Test. Window, process and view were the same before and after (MCP Test > Seed). Phill noticed nothing. D's sync of MCP Test under Options also returned ok, in 293 ms. Whether a sync ran isn't observable: the call returns before any sync is done. | **ok, 377 ms** on MCP Test Cloud and **212 ms** on the cloud throwaway; window and view unchanged. Phill saw nothing ("sync can happen fast"). Sync Status then read "Up to date" for both, and MCP Test Cloud > Probes showed in OneNote for the web. | `s1-local-mcp-test` (`-191413`); `d-sync` (`-194035`); `s1-cloud-*` (`-032732`) |
| S2 | `SyncHierarchy` on an unknown ID, and on a closed notebook's old ID | **`0x80042014` hrObjectDoesNotExist for both**: a fresh `{GUID}{1}{B0}` in 654 ms, and `o2`'s ID from before its close in 606 ms. Both are slower than a successful sync. | (local only) | `s2-synthetic-id`, `s2-closed-run-notebook` (`-191413`) |
| C1 | Close `b`, with nothing pending and nothing shown | **ok, 89 ms.** Every other open notebook kept all its IDs: 8 notebooks, from MCP Test (1,144 IDs) to the cloud throwaway (24), with none lost or gained. Window and view unchanged. Phill: OneNote didn't get the focus. He didn't say whether the page shown changed. | (local only) | `c1-close-b` (`-191413`) |
| C2 | Close with an edit waiting | **Closes: ok, 109 ms**, sent straight after the edit returned ok (the edit took 273 ms). No dialog (Phill). The hierarchy read found no Misplaced Sections element (`misplaced: []`). The reopen was ok, 38 ms, with a new ID and the same name and path, and showed 1 section and 1 page at 2 s. **The edit was lost for good:** the page was found, but its text didn't hold the marker (`editSurvived: false`), and it still didn't 38 min later (re-read 2026-11-19 at about 20:35 UTC, about 38 min after C2's close at 19:57:24, by the coordinator through the onenote-dev server's `get_page_content` and `find_pages`; that read is recorded here and in the 4.1 Inbox thread, not in a JSONL log: the page `probe pend local p 1119-191413` holds only its title and `probe text`, and `find_pages` from the root for `probe pending edit local 1119-191413` doesn't match it. Its `lastModifiedTime` was unchanged, but that proves nothing: it doesn't advance after COM edits to a page that isn't open). Phill saw `c` come back after about a minute. He also saw a Misplaced Sections entry in OneNote, holding `probe s1/s2/s3/nested 1111-165147`. Those sections come from 5.1's cloud run of 2026-11-11 (5.1's Failure modes), not from this close. | **Closes: ok, 7,104 ms**, sent straight after the edit returned ok (239 ms). Phill: a modal "Microsoft OneNote / OneNote is syncing changes." box with a progress bar and Cancel appeared and **closed on its own** (screenshot). The foreground window changed across the call, to a window that wasn't OneNote's (`Process: other`). `misplaced: []`. **The edit survived**: Phill saw `probe pending edit cloud 1120-032732` on the page in OneNote for the web, and after O2's reopen `editSurvived: true`. The Misplaced Sections entry Phill saw holds only 5.1's `1111-165147` sections. | `c2-local-close`, `c2-local`, `c2-local-reopen`, `c2-local-edit-survived` (`-194035`); `c2-cloud-close`, `c2-cloud`, `c2-cloud-edit-survived` (`-032732`) |
| C3 | The same as C2 offline | cloud only | **Not probed**: deferred by Phill, and the pause was skipped (`notProbed`). Its setup section `probe pend offline 1120-032732` and its page synced (Phill); no edit was sent, because the edit comes after the skipped pause. | `c3`, `offline-network-off` (`-032732`) |
| C4 | Close `c` while its page is the one shown in OneNote's only window | **ok, 116 ms.** Before the close, the view was `c` > `probe pend local …` > its page. After it, **nothing was viewed**: no notebook, section or page had `isCurrentlyViewed`. That stayed so through the reopen (ok, 40 ms, new ID, filled at 2 s) and every later row. Phill: "I believe it jumped to where I was last. Not 100% on that though." After the reopen: "the window is the same, but with c back in the list". He didn't mention a dialog. | (local only) | `c4-close-shown`, `c4-reopen` (`-194035`) |
| C5 | `list_grants` across a close and reopen | **`active` → `notebookNotOpen` → `active`** for `b`'s write grant, keyed by its path (`NotebookKey`), so the new ID didn't matter. The cloud throwaway's grant stayed `active` throughout. | **`active` → `notebookNotOpen` → `active`** for the cloud throwaway's write grant, keyed by its URL, across C2's close and O2's reopen with a new ID. | `c5-local` (`-191413`); `c5-cloud` (`-032732`) |
| C6 | Closing the last open notebook | **Not probed**, by design (Q2 a). C4 is the nearest probe. | — | `c6` |
| O1 | Closed `b` by its path | **ok, 26 ms. New ID** (`{7EB2913A…}` → `{3DB18FCA…}`), same `name` and `path`. Window and view unchanged. The fill series read 0 sections and 0 pages throughout, which matches the counts before the close (0/0). So `Settled: false` here means only that an empty notebook never passes the settle rule. Phill: `b` came back, and OneNote didn't take the focus. | see O2 | `o1-open-b` (`-191413`) |
| O2 | Closed cloud throwaway by its URL | cloud only | **ok, 2,341 ms. New ID** (`{BF324CB6…}` → `{D28015BA…}`), same `name` and URL `path`. Fill: 0 sections up to 4 s, 7 sections and 11 pages at 8 s, and all 11 and 13 (the counts before the close) at 16 s, settled. Phill: it came back at the bottom of the list within about 2 min, with no dialog; Sync Status read "Up to date", and its sections showed on the web. | `o2-reopen-url`, `o2-result` (`-032732`) |
| O3 | Can a URL create? | cloud only | **At a missing folder, no:** `0x80042006` hrFileDoesNotExist, 274 ms, nothing opened, and Phill found no `probe missing 1120-032732` folder in OneDrive. **At an existing empty folder Phill made, it opens a notebook:** ok, 2,326 ms, a new notebook named after the folder, with 0 sections through the 180 s fill. Phill then found `Open Notebook.onetoc2` in the folder (screenshot, about 5 min after the open and after the close; whether it landed at the open or at the close isn't known). Its close was ok, 86 ms, and every other notebook kept its IDs. | `o3-missing`, `o3-empty`, `o3-empty-close` (`-032732`) |
| O4 | An already-open notebook | **Same ID, no view change, no focus change.** MCP Test: ok, 31–36 ms (twice, once per run), `sameId: true`, and the view stayed on MCP Test Cloud > Probes > Text Page. `c`: ok, 26 ms, `sameId: true`, view unchanged. Phill: "No, still on MCP Test Cloud" for MCP Test, and "No visible change" for `c`. No notebook appeared. | **Same ID, no view change.** MCP Test Cloud by its URL: ok, 239 ms, `sameId: true`, nothing appeared, and the view stayed on MCP Test > Seed (Phill: "Still MCP Test › Seed"). Its 75 sections and 93 pages read the same from 0 to 16 s. | `o4-local-mcp-test` (both runs), `o4-local-c` (`-194035`); `o4-cloud-mcp-test-cloud` (`-044350`) |
| O5 | A missing path, and one under a missing parent | **`0x80042006` hrFileDoesNotExist for both**, in 39 and 25 ms. Nothing opened, and nothing was on disk afterwards (`kind: absent`). Under AD-9 this maps to `Internal` ("Paths, not objects"). | (local only) | `o5-missing`, `o5-missing-parent` (`-194035`) |
| O6 | A folder that isn't a notebook | **Opens as a notebook.** The empty folder: ok, 36 ms, opened as notebook `probe empty 1119-191413`. The folder holding only `probe-marker.txt`: ok, 37 ms, opened as `probe marker 1119-191413`. Both showed 0 sections through the fill series, and neither folder had any new file when listed straight after the open. At L, 34 min later, each held an `Open Notebook.onetoc2` of 6,080 bytes (the marker folder kept its `.txt`). Phill saw both in the notebook list. The plan expected nothing to open. | (local only) | `o6-empty-folder`, `o6-marker-folder`, `l-probe empty <stamp>`, `l-probe marker <stamp>` (`-194035`) |
| O7 | A notebook created and closed before its `.onetoc2` lands | **Both opened, but the condition wasn't met.** At `o2`'s create (no section) its folder was empty. At its close, 76 s later, it already held `Open Notebook.onetoc2` (6,080 bytes). `o4` (one section) had its `.onetoc2` at create. Both closed ok (41 and 38 ms) and reopened ok with `cftNone` (29 and 30 ms), with a new ID and the same name and path. `o4` showed its section at 2 s. Since O6 shows that `cftNone` opens any folder, a `.onetoc2` that hasn't landed doesn't stop an open. Phill: both came back; he didn't mention a dialog. | (local only) | `make-o2`, `make-o4`, `o7-close-o2`, `o7-close-o4`, `o7-open-o2`, `o7-open-o4` (`-191413`) |
| O8 | A section-group folder of `o3` | **While `o3` is open:** ok, 30 ms, and it returned **the group's own ID** (`returnedGroupId: true`). No notebook appeared. **After `o3`'s close** (ok, 126 ms): ok, 32 ms, a **new notebook** `probe sg 1119-191413` at the group's folder, with a new ID, showing its 1 section at 2 s. Window and view unchanged. Phill: `o3` went, and the group appeared as a notebook about a minute later. | (local only) | `o8-group-o3-open`, `o8-group-o3-open-id`, `o8-close-o3`, `o8-group-o3-closed` (`-194035`) |
| O9 | Closed `o1` through a junction | **ok, 27 ms.** The notebook's `path` is **the junction's path**, and its `name` is the junction's folder name (`probe junction 1119-191413`), not `o1`'s. New ID, 1 section and 1 page at 2 s. Its close was ok, 42 ms. `mklink /J` worked without elevation. Phill didn't track the names; these come from the hierarchy reads. | (local only) | `o9-close-o1`, `o9-junction-made`, `o9-junction`, `o9-close-junction` (`-194035`) |
| O10 | Closed `o1` through `\\localhost\<drive>$\…` | **ok, 34 ms.** The share was reachable without elevation. The notebook's `path` is **the UNC path as given**, and its `name` is `o1`'s folder name. New ID, filled at 2 s. Its close was ok, 39 ms. | (local only) | `o10-unc`, `o10-close-unc` (`-194035`) |
| L | The O6 and O7 folders, listed again later | At 66.7 min, `o2` held `Open Notebook.onetoc2` (6,080 bytes), and `o4` held that file (6,224 bytes) and its section's `.one`. At 34 min, `probe empty` and `probe marker` each held an `Open Notebook.onetoc2` (6,080 bytes), one OneNote wrote. All four were open as notebooks. **What marks a folder as a notebook:** `Open Notebook.onetoc2`, but OneNote writes that file into any folder it opens, and a section group's folder holds one too (`o3`'s group: 5,168 bytes). | (local only) | `l-*` (`-194035`), `o8-setup` |
| D | Sync, open and close with File > Options open | **All three applied:** `SyncHierarchy` on MCP Test ok, 293 ms. `OpenHierarchy` `cftNone` on closed `o1` ok, 39 ms (opened). `CloseNotebook` on `o1` ok, 52 ms (`stillOpen: false`). No `0x80042030` and no blocking; Phill sent only a screenshot. `o1` was missing from the list after the step because D's own close closed it. | (local only) | `d-sync`, `d-open`, `d-close` (`-194035`) |

## HRESULTs observed

| HRESULT | Name | Method | Trigger | AD-9 code |
|---|---|---|---|---|
| `0x80042014` | hrObjectDoesNotExist | `SyncHierarchy` | an ID no notebook has, and a closed notebook's old ID (S2) | `ObjectNotFound` |
| `0x80042006` | hrFileDoesNotExist | `OpenHierarchy` (`cftNone`) | a missing folder, and a folder under a missing parent (O5); a OneDrive URL with no folder behind it (O3) | `Internal` ("Paths, not objects") |

Each run's `hresult-summary` row lists only that run's codes. The table merges all runs. No call returned `0x80042030`, timed out, or tripped the crash breaker.

## What entry 2's notebook test needs (local)

- **`cftNone` isn't a guard.** It opens any existing folder as a notebook and writes `Open Notebook.onetoc2` into it (O6, L). Only a missing path fails (O5). So the test has to run before COM, under the write lock as A4 says.
- **A `.onetoc2` alone isn't enough.**
  - It lands in any folder OneNote has once opened, even by mistake (O6).
  - A section group's folder holds one (O8, `o8-setup`). Opened while its notebook is closed, that folder becomes a notebook of its own.
  - A notebook just created without sections had none at create; it was there 76 s later (O7). 5.1 constraint 18 saw 5 min.
  - Inferred, not probed: a test that also refuses a folder whose parent holds an `Open Notebook.onetoc2` would catch the section-group case. Entry 2 decides.
- **Paths need canonicalising before the test, and before any key match.** OneNote keeps the path it was given: a junction or a `\\localhost\X$` form becomes the notebook's `path` (O9, O10). Inferred: a notebook opened that way would match no grant or `NotebookKey` held under its real path, and the same folder could be open twice under two paths. That wasn't probed.
- **An already-open notebook needs no pre-COM shortcut for the user's sake.** `cftNone` returned the same ID and moved neither view nor focus (O4). The epic's unconfirmed assumption, that OneNote navigates there, didn't hold with `cftNone`. A shortcut still saves a call, and entry 2 can keep it or drop it.

## Can a URL open, and can it create

**A URL opens a closed cloud notebook** (O2): ok in 2.3 s, with a new ID and its contents listed within 16 s.

**A URL doesn't create at a missing folder, but it does turn an existing folder into a notebook** (O3), just as a local path does (O6). So A4's condition, that `cftNone` never creates at a URL, **doesn't hold**: an existing OneDrive folder the URL names becomes a notebook (one empty folder probed), and OneNote writes `Open Notebook.onetoc2` into it. A URL needs the same notebook test as a path, before COM. Inferred, not probed: the server can't list a OneDrive folder through COM, so entry 2 has to choose between refusing URLs, testing them some other way, or accepting only a URL that was a notebook's own `path`.

## Implications for entry 2

1. **`open_notebook` refuses before COM** anything entry 2's notebook test rejects. A missing path should get its own refusal, such as `InvalidArgument` naming the path, rather than AD-9's `Internal` for `0x80042006` (O5). Entry 2 decides.
2. **`close_notebook` must not drop pending writes (a hard input to 4.4).** C2 lost, for good, an edit that returned ok straight before a close that also returned ok, with no dialog or error. So the close has to wait for, or force, a save of pending writes to that notebook before it calls `CloseNotebook`. What wait is enough, and whether `SyncHierarchy` forces the save, are open (Open before final). A cloud close did wait: C2 C took 7.1 s behind a modal "syncing changes" box and kept the edit. On that one online run, the gap is local only.
3. **The close result warns that IDs change, and the view may go blank.** Every reopen gave a new ID (O1, O7, C2, C4, O8–O10), and old IDs then return `ObjectNotFound` (S2). Closing the notebook on screen left COM reporting no viewed page (C4).
4. **Neither close nor open needed a modal check under File > Options** (D, one local run): both went through, and unlike `UpdateHierarchy` they returned no `0x80042030`. Other dialogs weren't probed, and 5.1 found two that block instead (Relied on), so a blocked open or close still ends in `Timeout`.
5. **Timeouts.** Locally, closes took at most 126 ms and opens at most 40 ms. Cloud closes took up to 7.1 s (with an edit pending) and URL opens 2.3 s, plus up to 16 s for the contents to list. All are inside AD-3's 30 s, but a cloud close with a large pending sync may not be (inferred).
6. **Fill.** A reopened local notebook listed its contents within 2 s (0 at 1 s, full at 2 s, in every non-empty case), but each held at most 1 section and 1 page; a cloud notebook of 11 sections took 16 s (O2). OneNote's own list can lag about a minute (Phill, O8 and C2). So `open_notebook` may say the notebook is open before the user sees it.
7. **Grants.** Write grants are keyed by path, so they survive a close and reopen (C5).
8. **Integration tests (epic Notes, entry 5).** Locally, closing and reopening `b` and `c` changed neither focus nor the view, when they weren't on screen (C1, O1, C2). So, locally, those tests needn't be hitl. The cloud close with a pending edit is different: it showed a modal box (C2 C, Phill), so a cloud close test is hitl unless nothing is pending.
9. **A cloud close may show a modal box.** OneNote's "syncing changes" box has a Cancel button. Inferred, not probed: pressing Cancel would end the wait early, and what `CloseNotebook` then returns is unknown.
10. **A1: `open_notebook` and `close_notebook` should carry "user-visible".** The epic adds them if either changes focus or the view. A close of the notebook on screen blanks the view (C4), and a cloud close with an edit pending shows a modal box (C2 C, Phill). Opens moved neither (O1, O2, O4).
11. **A3: a close held behind a dialog ending in `Timeout` with `outcomeUnknown` stays unconfirmed.** No close blocked in either sitting (D's close went through under File > Options), and C3 (offline) was deferred. Phill decided on 2026-11-20 that C3 stays deferred and entry 2 designs for A3's assumption.

## Not probed

- C6: closing the last open notebook (Q2 a, by design).
- A section group's folder open as a notebook while its parent notebook is open too (O8 opened it only while `o3` was closed).
- Opening the same folder twice at once under two paths (junction or UNC, plus direct). O9 and O10 opened `o1` only while its direct form was closed.
- C3: close a cloud notebook with an edit pending while offline (deferred by Phill, 2026-11-20).
- Pressing Cancel on the cloud close's "syncing changes" box.

## Evidence gaps

- **C2's lost edit is one run**, re-read 38 min later. The probe didn't read the edit back between `UpdatePageContent` and `CloseNotebook` (`CloseRowAsync`), so "applied, then lost" rests on the ok return, and on the cloud control, where the same code path's edit survived. No run tried a delay or a sync between the write and the close, so what saves the edit isn't known.
- **When the `.onetoc2` lands after a `cftNone` open of a plain folder.** The folders were listed straight after the open (no new file) and at L (file present), with nothing in between.
- **Misplaced Sections isn't in the hierarchy read.** Phill saw a Misplaced Sections entry, yet `ProbeView.Misplaced` found no element named "Misplaced" in a full sections-scope read. Either OneNote doesn't expose it through `GetHierarchy`, or exposes it under another name. Sitting B's C2 relied on Phill's look as well, and the read again found none.
- **Three non-test notebooks went to "Incomplete sync" during sitting B**: "Up to date" at 22:29 local, "Incomplete sync" at 23:23 local. No probe targets them, but the change falls in the window of O3's URL opens and C2 C's close. Cause unknown, not probed; recovery not recorded.
- **C4's window.** COM reports nothing viewed, and Phill thinks the window "jumped to where I was last", but isn't certain.
- **C1's page shown.** Phill wasn't asked again whether the page changed after close-open-b. The view snapshot says it didn't.

## Relied on, not re-probed

- `cftNotebook` at an open notebook's path returns its ID and leaves the foreground alone (spike 1 rows 9 and 25a; 5.1 constraint 18 `sameId`). O4 adds the `cftNone` case.
- `cftNotebook` makes a notebook in any existing folder, and its `.onetoc2` can land 5 min later (5.1 constraint 18).
- `SyncHierarchy` on a page and a section returns `S_OK` in 0 ms and downloads nothing (spike 1.15 quirk 3).
- Under File > Options, reads and `OpenHierarchy` creates go through, and only update methods return `0x80042030` (5.1 constraint 11; spike 3.1 Implication 13).
- A close and reopen keeps every `path` and changes every ID (spike 2.2 constraint 9). C1–C4 and O1 agree.
- A published `.one` opens as a hidden Open Section (5.1 constraints 7 and 17). O-rows don't reopen backups.
- Under File > Options only the update methods return `0x80042030`; two other dialogs block instead, and surface as `Timeout` (5.1 constraint 11).
- A hand close of MCP Test Cloud with unsynced sections hung and moved them to Misplaced Sections (5.1, Failure modes). That is where the `1111-165147` sections Phill saw come from.

## Open before final

- C2 with a delay (and with a `SyncHierarchy`) between the edit and the close: does the edit survive, and after how long? A quick local rerun; sitting B shows that the cloud close already waits. This is what 4.4's wait needs.
- The spine memlog `constraint` lines, one per finding that changes an AD (the plan's Tasks), written after the Fable review.
- The Fable final review.

## Left behind

- `c` keeps the section `probe pend local 1119-191413` and its page, by design (plan Decisions). `b` and `c` are open, each with a new ID: `b` is `{3DB18FCA…}`, and `c` is `{5F85BF5B…}` after C4's reopen.
- The run folder `%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\1119-191413\` is deleted. Cleanup's state row lists no run notebook open, `b`, `c` and the cloud throwaway open, and no folder. The junction was removed as a link, without being followed.
- Misplaced Sections still holds 5.1's `probe s1/s2/s3/nested 1111-165147` sections, which were there before this run.
- Sitting B: `MCP Test Throwaway Cloud` is open with a new ID (`{D28015BA…}`) and keeps the sections `probe pend cloud 1120-032732` and `probe pend offline 1120-032732`, by design. Phill deleted the OneDrive folder `probe empty 1120-032732` (to the OneDrive recycle bin). The run folder `…\app-state\1120-032732\` is deleted; cleanup's state row lists no run notebook open.
