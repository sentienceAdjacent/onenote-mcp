---
title: 'App-state probes, part 1: open, close and sync'
type: 'chore'
ticket: '1'
created: '2026-11-16'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/spike-write-probes-part-3-exports-move-merge-and-recycle-bin-resto-plan.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Entry 2 (round 3's spine amendments) can't write the `AppState` rules for `close_notebook`, `open_notebook` and `sync_hierarchy` until the epic Notes' "Entry 1" questions have measured answers: what `OpenHierarchy` with `cftNone` does on a closed, open, missing, non-notebook, half-made, section-group, junction, UNC and OneDrive-URL path (and whether it can ever create), what `CloseNotebook` without `force` does with unsynced changes, offline, on screen and under a dialog, and what `SyncHierarchy` returns on a notebook and on a bad ID.

**Approach:** Extend the probe harness in `tests/OneNoteMcp.Integration/WriteProbes/` (no fork) with guarded `CloseNotebook`, `OpenHierarchy`-`cftNone` and `SyncHierarchy` calls, a focus-and-view snapshot and a fill poll that entries 12 and 13 reuse, and two opt-in scenarios that the main session runs live with Phill. Answers go into a findings doc, one row per question, and into the spine memlog. No `src/` change.

## Boundaries & Constraints

**Always:**
- Every call goes through `TestNotebook`'s guard, which reads the hierarchy live before it:
  - `CloseNotebook` only on throwaway `b` or `c` (adopted by exact name), `MCP Test Throwaway Cloud`, or a notebook whose path lies in this run's probe folder. The wrapper has no `force` parameter and always passes `false` (Q1).
  - `OpenHierarchy` only with `cftNone`, hard-coded, only on: a path in the run's probe folder, or its `\\localhost\<drive>$` or junction form; the recorded paths of `b`, `c` and the cloud throwaway; MCP Test's folder and MCP Test Cloud's URL while they are open (O4 only); or a `https://d.docs.live.net/<MCP Test Cloud's cid>/Documents/probe … <stamp>` URL (O3).
  - `SyncHierarchy` only on IDs in MCP Test, MCP Test Cloud, the throwaways or the run's objects, plus S2's synthetic ID (checked absent from every notebook) and a closed run notebook's old ID.
- The probe folder is new and empty: `%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\<stamp>\`. Its notebooks are named `MCP Test Throwaway <stamp> o<n>`. When done, they are closed, then the junction link is removed (never followed), then the folder is deleted, only after a live read shows no open notebook under it.
- Each probe writes one JSONL row per question: method, argument shape, HRESULT, time, and the focus-and-view snapshot before and after. Each open also records the notebook's `name`, `path` and ID, and a fill series. Each close also records whether the other open test notebooks kept their IDs. The snapshot logs a notebook's name only if it's a test or throwaway notebook ("other notebook" otherwise), and the foreground process only if it is `ONENOTE` or the test host ("other"). It never logs window titles. The cid is written `<cid>`.
- A Sync Now check after every cloud step. Report `0xE0000644` at once. The shown check (`EnsureNothingShownAsync`) runs before every close, except C4, which needs the notebook on screen. Each call has a 60 s limit (180 s on cloud), and the crash breaker stays on.
- Phill is present for the whole of each sitting. Every close and every open of a closed notebook is preceded by a pause that says what he will see, and is followed by one where he reports what he saw.
- The probes rely on earlier findings and don't re-probe them. Each one is named in Design Notes and cited in the findings doc.

**Never:**
- Start, close or kill OneNote. Call `NavigateTo`, `NavigateToUrl`, `Windows`, `QuickFiling`, `SetFilingLocation`, `Publish(pfOneNote2007)`, `MergeFiles`, or any `UpdateHierarchy` move (spike 3.1 sitting A: the last three end `ONENOTE.EXE`). Pass `force=true`.
- Close MCP Test, MCP Test Cloud, throwaway `a` or any notebook outside the allowed set. Open MCP Test or MCP Test Cloud except while it is open (O4), or open any other notebook. Open anything with a create type other than `cftNone`, except the run's own notebooks, which are made in the probe folder with 5.1's `cftNotebook` create.
- Retry a call automatically, or send `DateTime.MinValue`.
- Change `src/` or the spine's text. Only the memlog gets lines.
- Commit real notebook names, paths holding the user name or cid, or any probe output.

## I/O & Edge-Case Matrix

Each row is one findings-doc question. L = local, C = cloud. F/V means the foreground window and its process before and after, the `isCurrentlyViewed` chain before and after, and what Phill reports.

| Id | Area | Question |
|---|---|---|
| S1 | Sync | `SyncHierarchy` on a notebook: MCP Test (L), MCP Test Cloud and the cloud throwaway (C). HRESULT, time, F/V |
| S2 | Sync | `SyncHierarchy` on an unknown ID (a fresh `{GUID}{1}{B0}`) and on a closed run notebook's old ID: the HRESULT for each |
| C1 | Close | Throwaway `b` (L), nothing pending, nothing shown: HRESULT, time, F/V, and whether the other test notebooks kept their IDs (OneMore: untouched) |
| C2 | Close | A notebook with changes waiting: a probe page in `c` (L) and in the cloud throwaway (C) is edited, then closed at once. HRESULT and time, then one of four results: it closes, it refuses, a dialog blocks it until our `Timeout` (Phill names the dialog and presses Cancel), or sections land in Misplaced Sections (read from the hierarchy, and Phill looks). After the reopen, does the edit survive? |
| C3 | Close | The same as C2 on the cloud throwaway, with the network off (Phill), then back on, then a reopen and a Sync Now check |
| C4 | Close | `c` while its page is the one shown in OneNote's only window: what that window shows afterwards (Phill), and F/V |
| C5 | Close | `list_grants` (Core's `ListGrantsUseCase`, run against the probe's own settings file holding grants for `b`'s path and the cloud throwaway's URL): the grant's status before the close, after it, and after the reopen |
| C6 | Close | Closing the last open notebook: see Open Questions |
| O1 | Open | Closed `b` by its path: HRESULT, whether the ID is new, `name` and `path` against before the close, F/V, and a fill series (section and page counts read at 0, 0.5, 1, 2, 4, 8 and 16 s, then every 10 s until three reads match, up to 30 s), compared with the counts before the close |
| O2 | Open | Closed cloud throwaway by its URL: does it open? Then O1's records, with a fill series up to 180 s. If it doesn't open, Phill reopens it in OneNote and the HRESULT is the result |
| O3 | Open | Can a URL create? `cftNone` at `…/Documents/probe missing <stamp>/` (missing), and at an empty OneDrive folder `probe empty <stamp>` (see Open Questions): HRESULT, whether any notebook opened, and Phill checks OneDrive on the web |
| O4 | Open | An already-open notebook: MCP Test (L), `c` (L) and MCP Test Cloud (C), with Phill showing a page of the other test notebook. Is the ID the same? Does the view move (OneMore: OneNote navigates to it)? F/V |
| O5 | Open | A missing local path, and one under a missing parent: HRESULT; nothing on disk or in the hierarchy afterwards |
| O6 | Open | A folder that isn't a notebook (empty, and one holding `probe-marker.txt`): HRESULT; nothing opened; disk listed straight after and again at L |
| O7 | Open | A notebook just created and closed before its `.onetoc2` lands: `o2` with no section, `o4` with one. Disk is listed at create, at close and at open. Does `cftNone` open it, and with what HRESULT? |
| O8 | Open | A section-group subfolder of `o3`, while `o3` is open (Microsoft: "nothing happens", and the ID is returned) and after `o3` is closed (Microsoft: it opens as a notebook). HRESULT, ID, and what opened (`name`, `path`, a notebook or not); F/V |
| O9 | Open | Closed `o1` through a junction in the probe folder (`mklink /J`, made without elevation): HRESULT, and the `path` OneNote reports (the junction or its target) |
| O10 | Open | Closed `o1` through `\\localhost\<drive>$\…`: HRESULT and the reported `path`. If the share isn't reachable without elevation (`Directory.Exists` is false), the row is logged `notProbed` before COM |
| L | Files | The O6 and O7 folders listed again at least 5 min after their creates (5.1 constraint 18: the `.onetoc2` landed 5 min late). What files mark a folder as a notebook, for entry 2's notebook test |
| D | Modal | With File > Options open (L): `SyncHierarchy` (MCP Test), `OpenHierarchy` `cftNone` on closed `o1`, then `CloseNotebook` on `o1` if it opened, else on `c`. HRESULT, and whether each was applied |
| H | HRESULTs | Every code seen, per method, with its trigger, mapped to AD-9 |

</frozen-after-approval>

## Open Questions

None open. Phill answered all six on 2026-11-16 (the 4.1 Inbox thread), each with the recommended choice, and approved the plan:

1. **When to build:** (a). The plan is approved now. It is implemented once 3.1 merges into `main`, and the sittings wait until the epic-3 gate clears.
2. **C6, closing the last open notebook:** (a). It is logged `notProbed` by design, and C4 is the nearest probe.
3. **C3, a close while offline:** (a). It is probed in the cloud sitting, with the network off for about 2 min and only on the cloud throwaway.
4. **O3's empty OneDrive folder:** (a). Phill makes `probe empty <stamp>` at a pause and deletes it afterwards.
5. **Sittings:** (a). Two of about 60 min each, local then cloud.
6. **Plan size:** (a). The full plan is kept.

## Code Map

- `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs` (3.1's branch). Reuse as they are:
  - steps and pauses: `StepAsync`, `PauseAsync` (the pause file and `skip`), `RequireScenario`, `StopOnTimeoutAsync`, `CheckTimeoutAsync`, `SetupAsync`;
  - checks and snapshots: `EnsureNothingShownAsync` (with `targetId` set to the notebook), `Foreground`, `Disk`;
  - setup and lookups: `CreateThrowawayAsync`, `AdoptThrowawayAsync`, `AdoptCloudThrowawayAsync`, `MustCreateChildAsync`, `CreateProbePageAsync`, `MustUpdateAsync`, `NotebooksAsync`, `TreeAsync`, `McpTestIdAsync`, `CloudIdAsync`, `Created`, `Throwaways`, `Echo`.
  Add the new wrappers and snapshots here.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` (3.1's branch):
  - `CreateThrowawayNotebookAsync` (:409) gains an optional root, so it can create in the probe folder;
  - `ReopenThrowawayNotebookAsync` (:449) shows how to guard by live path;
  - `OpenBackupAsync` (:357) shows the `cftNone` call shape;
  - the guard pieces are `IsLocalThrowaway` (:647), `IsCloudThrowaway`, `IsCloudPathNamed`, `ThrowawayNotebookIds` (:674), `FindThrowawayByExactName` (:727), `EnsureUnder`, `EnsureFolderIsOurs` and `NotebookPath`.
- `WriteProbes/HierarchyProbes.cs`: `Stamp` (:88), `SyncChecked` (:126) and `ModalAsync` (:713, the close pause sits in a `finally`). `CloudJamRepro.CheckSyncAsync` gives the Sync Now pause wording.
- `WriteProbes/PinnedOneNoteProbe.cs` is the crash breaker; leave it unchanged. `ProbeRecorder.cs` writes the rows and redacts `<cid>`; extend the redaction to `%LOCALAPPDATA%` and `\\localhost\<drive>$` paths.
- `src/OneNoteMcp.Com/Interop/IApplication.cs`: `OpenHierarchy` (:32), `CloseNotebook(id, force)` (:51), `SyncHierarchy` (:151). `Enums.cs` `CreateFileType.cftNone` (:42). Call them through `ComGateway.InvokeAsync(..., retryOnDisconnect: false)`, and don't edit them.
- `src/OneNoteMcp.Core/UseCases/ListGrantsUseCase.cs` (`ListGrantsUseCase(gateway, settings, clock)`), with `src/OneNoteMcp.Core/Settings/SettingsStore.cs`. For the grant file's shape, see `tests/OneNoteMcp.Tests/SettingsStoreTests.cs` and `GrantCommandTests.cs`.
- `_bmad/scripts/memlog.py append --path <spine>/.memlog.md --type constraint --text "..."` adds the memlog lines, at the end only.

## Tasks & Acceptance

**Execution:**
- [ ] `tests/OneNoteMcp.Integration/TestNotebook.cs` and `TestNotebookGuardTests.cs`: add `CloseNotebookAsync`, `OpenExistingAsync`, `SyncHierarchyAsync` and `RunProbeRoot` to the guard, with the allowed sets under Always. Add a pure helper that maps `\\localhost\X$\…` to `X:\…`, and the optional root on `CreateThrowawayNotebookAsync`. Write one unit test per refusal and per look-alike: MCP Test, MCP Test Cloud, throwaway `a`, a name prefix with the wrong folder, a `..` path, a cid other than MCP Test Cloud's, a section ID passed to close, and `cftNone` that can't be changed.
- [ ] `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs`: add these, with unit tests in `ProbeHelpersTests.cs` for the pure parts (redaction, fill-series stop rule, view chain, synthetic ID):
  - wrappers for the three calls;
  - `ViewAsync`, which returns the foreground handle and process, and the `isCurrentlyViewed` notebook, section and page with redacted names;
  - `FillSeriesAsync(notebookId, limit)`;
  - `OtherIdsKeptAsync`;
  - `MakeJunction` and `RemoveJunction`, which use `cmd /c mklink /J` and remove the link without following it;
  - `ProbeGrantsAsync`, which writes a second settings file in the session's temp folder and runs `ListGrantsUseCase` on it;
  - `ObservePauseAsync(step, whatYouWillSee)`.
- [ ] `tests/OneNoteMcp.Integration/WriteProbes/AppStateLocalProbes.cs` (scenario `app-state-local`) and `AppStateCloudProbes.cs` (`app-state-cloud`) cover every Matrix row in the order of the Sitting script. `cleanup` is its own step, and can be rerun with `ONENOTE_MCP_PROBE_STEPS=cleanup`.
- [ ] Live sittings (the main session's, with Phill, after review) follow the Sitting script. They aren't part of implementation.
- [ ] `epic-round-3-app-state-and-ui/spike-app-state-probes-part-1-open-close-and-sync-findings.md` (after the sittings) follows 3.1's structure: one row per Matrix question, then HRESULTs, "What entry 2's notebook test needs", "Can a URL open, and can it create", Not probed, Evidence gaps, "Relied on, not re-probed", Open before final, and Left behind.
- [ ] Spine `.memlog.md` (after the sittings): one `constraint` line per finding that changes an AD, naming the AD.

**Acceptance Criteria:**
- Given the guard, when a close, open or sync names a notebook, path or ID outside the allowed sets, then it refuses before any COM call.
- Given `CloseNotebookAsync`, when it is called, then COM receives `force=false`, and no code path can pass `true`.
- Given the cleanup step, when it ends, then no run notebook is open, `b`, `c` and the cloud throwaway are open as they were found, and the probe folder is gone with the junction's target untouched.
- Given the findings doc, when entry 2 reads it, then every Matrix row has a result or a stated reason it wasn't probed, and every earlier finding relied on is named.
- Given the staged diff, when it is grepped for the cid, the user name and real notebook names, then nothing matches.

## Sitting script

Run from the repo root with OneNote already running, `$env:ONENOTE_MCP_INTEGRATION='1'` and `$env:ONENOTE_MCP_SCENARIO='<scenario>'`, then `dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~AppState`. To rerun one step, set `ONENOTE_MCP_PROBE_STEPS`.

**Before either sitting,** Phill checks that `MCP Test Throwaway 1112-034716 b`, `… c` and `MCP Test Throwaway Cloud` are open, and that OneNote has a single window showing MCP Test > Seed.

**Sitting A, local (`app-state-local`, about 60 min, about 14 pauses):**

| Step | Rows | What Phill will see, and does |
|---|---|---|
| `sync-local` | S1 L, S2 | Probably nothing. He reports any sync glyph or dialog |
| `make` | (setup) | Notebooks `o1` to `o4` appear in the notebook list. A create may bring OneNote forward (5.1 constraint 14) |
| `fresh` | O7 | `o2` and `o4` vanish from the list, then may reappear. He reports dialogs |
| `close-open-b` | C1, C5, O1 | `b` vanishes, then reappears. He reports whether OneNote came forward or the page shown changed |
| `already-open` | O4 L | First he shows MCP Test Cloud > Probes > Text Page. He reports whether OneNote switched to MCP Test or `c` |
| `missing`, `not-notebook` | O5, O6 | Nothing is expected. He reports any notebook that appears |
| `section-group` | O8 | `o3` vanishes. Its group may appear as a notebook of its own |
| `pending-local` | C2 L | `c` vanishes and reappears. He names any dialog and presses Cancel if one blocks |
| `close-shown` | C4 | First he opens a page of `c`. He reports what the window shows after `c` closes. Then `c` reopens |
| `junction-unc` | O9, O10 | `o1` appears and vanishes twice |
| `modal` | D | He opens File > Options at the first pause and closes it at the second. If a call blocks, he closes Options, and `cleanup` is rerun |
| `late-files`, `cleanup` | L | The run's notebooks vanish. He confirms `b` and `c` are open |

**Sitting B, cloud (`app-state-cloud`, about 60 min, about 13 pauses).** A Sync Now check follows each step.
- `sync-cloud` (S1 C).
- `already-open-cloud` (O4 C): first he shows MCP Test > Seed, then reports whether OneNote switched.
- `url-create` (O3): he checks OneDrive on the web, and makes and later deletes `probe empty <stamp>` (Q4).
- `pending-cloud` (C2 C, C5): the cloud throwaway vanishes. He names any dialog, presses Cancel if one blocks, and checks Misplaced Sections.
- `reopen-url` (O2): the throwaway reappears, or he reopens it from OneNote's File > Open if the URL fails.
- `offline` (C3, Q3): he turns the network off, reports what happens, turns it back on, and checks Sync Now.
- `cleanup`.

## Implementation Notes

## Plan Change Log

- 2026-11-16: Phill approved the plan, choosing the recommended answer to each of Q1 to Q6. Status is `ready-for-dev`, and the build waits for 3.1 to merge.

## Review Triage Log

## Design Notes

**Relied on, not re-probed** (the findings doc cites each):
- `cftNotebook` at an open notebook's path returns its ID and leaves the foreground window alone (spike 1 rows 9 and 25a; 5.1 constraint 18 `sameId`). O4 differs: it uses `cftNone`, and it also records the view.
- `cftNotebook` makes a notebook in any existing folder, and its `.onetoc2` can land 5 min later (5.1 constraint 18). That is why O6 and L exist.
- `SyncHierarchy` on a page and on a section returns `S_OK` in 0 ms and downloads nothing (spike 1.15 quirk 3). S1 covers only notebooks.
- Under File > Options, `GetHierarchy` and `OpenHierarchy` creates go through, and only the update methods return `0x80042030` (5.1 constraint 11). Two dialogs block instead, and surface as `Timeout`.
- A close and reopen keeps every `path` and changes every ID (spike 2.2 constraint 9). A hand close of MCP Test Cloud with unsynced sections hung, needed Cancel, and moved the sections to Misplaced Sections (5.1, Failure modes).
- A published `.one` opens as a hidden Open Section (5.1 constraints 7 and 17). O-rows don't reopen backups.

**Prior art (OneMore at `baa00f0`, checked 2026-11-16; research-note rows 4 and 21–23):**
- `OneNote.OpenHierarchy` (`OneNote.cs:2368-2377`) always passes `cftNotebook`, never `cftNone`. `OpenFolderCommand.cs:45-47` relies on OneNote "navigating directly to the notebook" when it's already open, "also true for subfolders". That is O4's and O8's view question. It retries an empty hierarchy up to three times, 0.5–1.5 s apart (`:66-72`), which O1's fill series measures properly.
- `SyncHierarchy` is fire-and-forget (`Commands/Tools/SyncCommand.cs:14-15`, `OneNote.cs:1964-1968`). The CLI command now syncs one notebook or all of them.
- OneMore never calls `CloseNotebook` (only `OneMoreTests/MockApplication.cs` declares it).
- The ID Stability TechNote: closing and reopening one notebook left the other notebooks' 604 pages with unchanged IDs, which C1 checks here. A cloud notebook refilled 0 → 376 pages over about 90 s, and a local one in seconds. A window left open kept dead IDs and showed blank, which is what C4 looks for.
- The Defects page: `NavigateToUrl` seems to set off a notebook sync that blocks later calls. That belongs to entry 12, but it is why S1 records F/V.

**Why the guard keys on the probe folder, not on names.** A notebook opened from a section-group folder, a junction or a UNC path gets a name OneNote picks. Its path, mapped back from the UNC form, still lies in the probe folder, so the guard's rule for closing it doesn't depend on that name.

**Order hazards.** O7's creates come first, so that L runs at least 5 min later. Every O-row that targets `o1` needs `o1` closed. D runs last before `cleanup`, because a close that blocks under the dialog stops the scenario.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`: expected to succeed.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` without the integration variables: expected to pass, with the guard and helper tests run and every scenario skipped.

**Manual checks (if no CLI):**
- Reading the diff confirms that no `CloseNotebook` call has a `force` other than `false`, that no create type other than `cftNone` reaches `OpenExistingAsync`, and that none of the calls under Never appears.
