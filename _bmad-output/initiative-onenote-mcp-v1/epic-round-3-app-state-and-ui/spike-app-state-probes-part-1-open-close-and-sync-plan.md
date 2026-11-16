---
title: 'App-state probes, part 1: open, close and sync'
type: 'chore'
ticket: '1'
created: '2026-11-16'
status: in-progress
baseline_revision: '62afdfff4e3cb3bbe916f2a0d67fc42aeca737fe'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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

## Decisions (Phill, 2026-11-16, after the build, relayed by the coordinator)

- **Sitting length: accepted.** As built, sitting A has about 24 pauses and sitting B about 19 (the plan said 14 and 13), because each step has a pause before it and a report pause after it.
- **"Only `cftNone`" covers notebook-level opens.** The guarded `cftSection` and `cftFolder` setup creates, in the run's notebooks and in throwaways `c` and Cloud, are allowed. The `probe pend <stamp>` sections they leave in `c` and Cloud go under Left behind.
- **O3: the probe closes what a probe URL opened.** This widens the close set under Always. `CloseNotebook` may also close a notebook whose `path` is this run's stamped probe URL (`https://d.docs.live.net/<MCP Test Cloud's cid>/Documents/probe … <stamp>`, with the run's exact stamp). The guard refuses every other notebook as before: a probe URL with another stamp, a non-probe URL, and both test notebooks. O3 uses a guarded close in place of the pause that asked Phill to close the notebook by hand. A pause remains only for when that close fails.

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
- [x] `tests/OneNoteMcp.Integration/TestNotebook.cs` and `TestNotebookGuardTests.cs`: add `CloseNotebookAsync`, `OpenExistingAsync`, `SyncHierarchyAsync` and `RunProbeRoot` to the guard, with the allowed sets under Always. Add a pure helper that maps `\\localhost\X$\…` to `X:\…`, and the optional root on `CreateThrowawayNotebookAsync`. Write one unit test per refusal and per look-alike: MCP Test, MCP Test Cloud, throwaway `a`, a name prefix with the wrong folder, a `..` path, a cid other than MCP Test Cloud's, a section ID passed to close, and `cftNone` that can't be changed.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs`: add these, with unit tests in `ProbeHelpersTests.cs` for the pure parts (redaction, fill-series stop rule, view chain, synthetic ID):
  - wrappers for the three calls;
  - `ViewAsync`, which returns the foreground handle and process, and the `isCurrentlyViewed` notebook, section and page with redacted names;
  - `FillSeriesAsync(notebookId, limit)`;
  - `OtherIdsKeptAsync`;
  - `MakeJunction` and `RemoveJunction`, which use `cmd /c mklink /J` and remove the link without following it;
  - `ProbeGrantsAsync`, which writes a second settings file in the session's temp folder and runs `ListGrantsUseCase` on it;
  - `ObservePauseAsync(step, whatYouWillSee)`.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/AppStateLocalProbes.cs` (scenario `app-state-local`) and `AppStateCloudProbes.cs` (`app-state-cloud`) cover every Matrix row in the order of the Sitting script. `cleanup` is its own step, and can be rerun with `ONENOTE_MCP_PROBE_STEPS=cleanup`.
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

Run from the repo root with OneNote already running, `$env:ONENOTE_MCP_INTEGRATION='1'` and `$env:ONENOTE_MCP_SCENARIO='<scenario>'`, then `dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~AppState`. To rerun steps of an earlier run, set `ONENOTE_MCP_PROBE_STEPS` and `ONENOTE_MCP_PROBE_STAMP=<stamp>` (the run's stamp is echoed at the start and given in any timeout pause). With `STEPS` and no `STAMP`, the run stops before any OneNote call and lists the run folders.

**Before either sitting,** Phill checks that `MCP Test Throwaway 1112-034716 b`, `… c` and `MCP Test Throwaway Cloud` are open, and that OneNote has a single window showing MCP Test > Seed.

**Sitting A, local (`app-state-local`, about 60–75 min).** As built, there are about 24 pauses on the normal path: 8 that say what is about to happen, 11 where Phill reports what he saw (most take seconds), and 5 where he acts (show a page, open and close Options). A shown-check pause or a by-hand reopen adds one where needed.

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

**Sitting B, cloud (`app-state-cloud`, about 60–75 min).** As built, there are about 19 pauses on the normal path: 2 that say what is about to happen, 5 report, 5 action (show a page, make the empty folder, check OneDrive on the web, network off, network on) and about 7 Sync Now checks. A by-hand close or reopen adds one where needed. A Sync Now check follows each step.
- `sync-cloud` (S1 C).
- `already-open-cloud` (O4 C): first he shows MCP Test > Seed, then reports whether OneNote switched.
- `url-create` (O3): he checks OneDrive on the web, and makes and later deletes `probe empty <stamp>` (Q4).
- `pending-cloud` (C2 C, C5): the cloud throwaway vanishes. He names any dialog, presses Cancel if one blocks, and checks Misplaced Sections.
- `reopen-url` (O2): the throwaway reappears, or he reopens it from OneNote's File > Open if the URL fails.
- `offline` (C3, Q3): he turns the network off, reports what happens, turns it back on, and checks Sync Now.
- `cleanup`.

## Implementation Notes

- 2026-11-16, implemented (code and unit tests only; nothing ran against OneNote, and no `ONENOTE_MCP_*` variable was set). `dotnet build OneNoteMcp.slnx -warnaserror` passes; `OneNoteMcp.Tests` 2965 passed; `OneNoteMcp.Integration` 339 passed, 94 skipped (both new scenarios among them). Judgement calls the plan didn't settle:
  - **Run notebooks in the guard.** `TestNotebookGuard.ThrowawayNotebookIds` now also accepts a throwaway-named notebook directly in a run's probe folder (`IsRunProbeThrowaway`: `…\probes\app-state\<MMdd-HHmmss>\`), so `o1`–`o4` take the guarded setup writes like any throwaway. The close and open guards key on the path instead (`IsUnderRunProbeRoot`, any depth, `\\localhost\X$` mapped back, any `..` segment refused), as Design Notes says. The run folder is passed in, never held in static state.
  - **`b` and `c`** are named in `TestNotebookGuard.ClosableAdoptedNames` (exact, with the folder of that name in the throwaway root); throwaway `a` is refused. A recorded path passes `OpenExistingAsync` only if it is also `b`'s or `c`'s folder or the cloud throwaway's URL.
  - **Call shapes.** `TestNotebook.CloseCall`, `OpenExistingCall` and `SyncCall` are the only COM call sites; a `DispatchProxy` stand-in for `IApplication` checks that COM gets `force=false` and `cftNone`, and reflection checks that neither wrapper has a `force` or create-type parameter.
  - **S2's closed-ID half** runs in `fresh`, after `o2`'s close: `sync-local` comes before any run notebook exists.
  - **One watch pause and one report pause per step**, not per call; the watch pause names every notebook the step closes or reopens, read live (name and folder). C4 and C2's reopen say in the report pause that the notebook comes back next. About 20 pauses in sitting A, not 14.
  - **C2's timing.** The other notebooks' IDs and the view are read before the edit; the edit and the close then run back to back (`CloseRowAsync`'s `justBefore`). A close that blocks to the timeout stops the scenario with the usual `call-timeout` row and Cancel pause; rerun the remaining steps.
  - **Misplaced Sections** are read as any hierarchy element whose name contains "Misplaced" (a full sections-scope read), with only probe-named children named; Phill also looks.
  - **O3.** The close guard's set doesn't include a notebook opened from a probe URL, so if one opens, a pause asks Phill to close it by hand; the same pause that has him delete `probe empty <stamp>` covers `probe missing <stamp>` if it appeared.
  - **Setup creates.** `o1` gets a section and page, `o3` a section group with a section (O8's folder), `o4` one section (O7), and `c` and the cloud throwaway a `probe pend …` section and page each (C2, C3), all through the existing guarded `cftSection`/`cftFolder` creates. Read "Never … a create type other than `cftNone`" as covering notebook-level opens only. The `probe pend …` sections stay in `c` and the cloud throwaway: Left behind.
  - **Reruns.** A step of an earlier run needs `ONENOTE_MCP_PROBE_STAMP=<stamp>` with `ONENOTE_MCP_PROBE_STEPS`, so it reuses that run's folder. `ONENOTE_MCP_PROBE_STEPS` without the stamp stops before any COM call, listing the run folders under `probes\app-state`. The stamp is echoed at the start and printed in the timeout pause. `cleanup` reopens only what was open when the run began (b, c; the cloud throwaway only from `app-state-cloud`) and names any earlier run folder that still has open notebooks.
  - **Shared settle.** `Round2MoveProbes.SettleAsync` moved to `ProbeSession.SettleAsync(after, question)`; 3.1's rows keep the question `Move`.
  - **Redaction.** `ProbeRecorder.Redact` now also writes this machine's `%LOCALAPPDATA%` as `%LOCALAPPDATA%`, its UNC form as `\\localhost\<drive>$\%LOCALAPPDATA%`, and any other `\\localhost\X$` as `\\localhost\<drive>$`, in plain and JSON-escaped text.
  - **View snapshot.** It reads the viewed notebook at sections scope and only the viewed section's pages (MCP Test's Ballast pages make a whole pages-scope read slow); the foreground process is `ONENOTE`, `test host`, `other` or `none`; no window title is read.

- 2026-11-16, after review pass 1: the 11 patches above were applied. `dotnet build OneNoteMcp.slnx -warnaserror` gives 0 warnings. `OneNoteMcp.Tests` passes 2965 of 2965, as at the baseline. `OneNoteMcp.Integration`, with no integration variables set, passes 340 and skips 94; the baseline was 217 passed and 92 skipped. The two new skips are the hitl scenarios.
  - **Matrix test audit.** Every Matrix row is a live probe step in `app-state-local` or `app-state-cloud`, and those skip by design without OneNote and Phill. The unit tests cover what can run here: the guard's refusals and look-alikes for close and open, the `force=false` and `cftNone` call shapes, redaction, the fill-series rule, the view snapshot, the synthetic ID, the grant file and the junction helpers. The rows themselves are checked at the sittings.
  - **Pauses** grew from the planned 14 and 13 to about 24 and 19 (Sitting script), because each step has a pause that says what is about to happen and a pause where Phill reports.

- 2026-11-16, Phill's O3 decision applied:
  - `TestNotebookGuard.CheckClose` now also allows a notebook whose `path` passes `IsProbeUrl` with MCP Test Cloud's cid and the run folder's exact stamp. Without a valid run folder it allows none.
  - `UrlCreateAsync` closes each notebook a probe URL opened through `CloseRowAsync`, which runs the cloud settle. The `url-create-close-by-hand` pause now runs only for a notebook whose close failed.
  - Nine new guard tests:
    - this run's stamped URL is allowed, both with a trailing slash and with the cid in another case;
    - six refusals: another stamp, a stamp look-alike, a non-probe URL, another account, a deeper folder, another host;
    - no run folder, MCP Test Cloud and MCP Test are refused.
  - Build: 0 warnings. `OneNoteMcp.Tests`: 2965 passed. `OneNoteMcp.Integration`: 349 passed, 94 skipped.

## Plan Change Log

- 2026-11-16: Phill approved the plan, choosing the recommended answer to each of Q1 to Q6. Status is `ready-for-dev`, and the build waits for 3.1 to merge.

## Review Triage Log

- 2026-11-16, pass 1 (lens `quick`; 12 findings): high 0, medium 5, low 6, false 0, maybe-false 0, plus 1 low rejected. 11 patched, 0 deferred.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | C5's grant file keys grants by the raw path; Core matches `NotebookKey` (trailing separator trimmed), so the cloud grant always reads `notebookNotOpen` | medium | patch | `OpenNotebooks.cs:21` keys open notebooks with `GrantKeys.NotebookKey`, and `GrantSettingsJson` wrote `path` as given. Patched: key with `NotebookKey`, plus a test |
| 2 | C5 cloud's after-reopen read used a new grant list built from the reopened path | medium | patch | `ReopenUrlAsync` called `GrantTargetsAsync(c, reopened)`, so a changed path would match by construction. Patched: reuse the list from `PendingCloudAsync` |
| 3 | The fill series stops at 16 s on three matching 0/0 reads, before a cloud notebook fills | medium | patch | `FillSchedule.Settled` counted the fixed reads. Patched: a read doesn't settle while it is empty or below the counts before the close, until the limit |
| 4 | D can close `c` with the shown check off while `c` is still on screen from C4 | medium | patch | Nothing between C4's reopen and D took `c` off screen. Patched: a shown check on `c` and `o1` before the Options pause |
| 5 | D's rows lack the focus-and-view snapshot that Always requires | low | patch | `d-sync`, `d-open` and `d-close` called the wrappers directly. Patched: snapshots before and after |
| 6 | A `cleanup` rerun without the stamp cleans a new empty folder and leaves the run's notebooks open | medium | patch | `Begin` made a fresh folder whenever `ONENOTE_MCP_PROBE_STAMP` was unset. Patched: a step-filtered run without the stamp stops before COM and names the variable, and the stamp appears in the echo and in the timeout pause |
| 7 | Cloud steps return early after a cloud write and skip the Sync Now check | low | patch | `PendingCloudAsync` and `OfflineAsync` returned after `PendingPageAsync`. Patched: the check runs on those paths too |
| 8 | Two pauses name a notebook from a convention or a constant, not a live read | low | patch | `JunctionUncAsync` used `RunName("o1")`; `ReopenUrlAsync` and cleanup used `CloudThrowawayName`. Patched: the live and recorded names |
| 9 | C2 records a guard refusal as OneNote refusing | low | patch | The result `refuses` fired on `Outcome == "refused"`. Patched: a separate `guardRefused` |
| 10 | The C6 row is filed under question `Close`, not `C6` | low | patch | `Recorder.Row("c6", "Close", …)`. Patched |
| 11 | Cleanup reopens `b`, `c` and the cloud throwaway whatever their state when found; the local run can reopen the cloud one without its safeguards | low | patch | `AdoptAsync` didn't keep the found state. Patched: only what was found open is reopened, and the local scenario never touches the cloud throwaway |
| 12 | The `SyncHierarchy` guard has no refusal unit test | low | reject | `SyncHierarchyAsync` reuses 5.1's `EnsureTargetsAsync` unchanged, and its pure parts are already tested. A refusal test would need a seam for the gateway's live read, which is more than a direct correction, and the gap is unlikely to bite |

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

**For the build (coordinator, 2026-11-16, after 3.1 merged as `7ce394f`).**
- Implementation writes code and unit tests only. Never set `ONENOTE_MCP_INTEGRATION` or `ONENOTE_MCP_SCENARIO`, never call OneNote, and never read or list `%APPDATA%\OneNoteMcp`, `%LOCALAPPDATA%\OneNoteMcp` or `%LOCALAPPDATA%\OneNoteMcp-Test` on this machine. The main session runs the sittings later, once the epic-3 gate clears.
- Reuse 3.1's harness as merged on `main` (Code Map line numbers came from its branch; find the symbols by name). That includes `CreateSideGateway`, the `ONENOTE_MCP_PROBE_*` switches (`STEPS`, `PAUSE_SECONDS`, and 3.1's opt-in pattern for risky cases, `Round2MoveProbes.cs` `crossMerge`), and the cross-storage settle (`Round2MoveProbes.SettleAsync`: 10 s, then a notebook-list read so that a crash trips the breaker there). Move the settle somewhere both can share rather than copy it.
- 3.1's lessons:
  - **Settle after every cloud close or open** (C2 C, C3, O2, O3, O4 C, cleanup) before the next write or close (3.1 findings, Implication 16: OneNote crashed about 2 s after a cross-storage move, and a cloud move straight after another blocked 180 s; with a 10 s settle neither recurred).
  - **Every pause names the exact notebook, read live** by its `name` (and its folder for local ones), never a name guessed from a convention: 3.1's closing pause named "-unpacked" notebooks that `OpenPackage` never made (3.1 findings, O1–O2).
  - **The crash breaker stops the scenario.** A tripped breaker ends the run at the next step (`StepAsync`); a guard read straight after each close and open (the settle's read) is where a crash is caught. The breaker's `IntegrityMismatch` text after a crash is the harness's, not OneNote's (3.1 findings, "What the next call reported").

**Order hazards.** O7's creates come first, so that L runs at least 5 min later. Every O-row that targets `o1` needs `o1` closed. D runs last before `cleanup`, because a close that blocks under the dialog stops the scenario.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`: expected to succeed.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` without the integration variables: expected to pass, with the guard and helper tests run and every scenario skipped.

**Manual checks (if no CLI):**
- Reading the diff confirms that no `CloseNotebook` call has a `force` other than `false`, that no create type other than `cftNone` reaches `OpenExistingAsync`, and that none of the calls under Never appears.
