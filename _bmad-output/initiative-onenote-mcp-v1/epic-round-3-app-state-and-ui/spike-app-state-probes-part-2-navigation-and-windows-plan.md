---
title: 'App-state probes, part 2: navigation and windows'
type: 'chore'
ticket: '12'
created: '2026-11-16'
status: draft
baseline_revision: '23b4da658946d863809fec5747ad41f8f8a336e6'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/spike-app-state-probes-part-1-open-close-and-sync-plan.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Entry 2 can't write the rules for `navigate_to_url`, `get_windows` and the window argument on `navigate_to` until the epic Notes' "Entry 12" questions have measured answers: what `NavigateToUrl` does with each kind of link (and whether a link opens a closed notebook or a loose `.one`, which Q6 must keep out), how long the `0x80042014` repeat window is, whether and how a landing can be checked, and whether reading `Windows` changes focus or the view (Q5).

**Approach:** Build on spike 4.1's harness (this branch, `spike/4.12-navigation-windows-probes`, is cut from `origin/story/4.1-app-state-ui-probes`, draft PR #60, not from `main`). Add early-bound spike copies of `Windows` and `Window`, guarded `NavigateTo`, `NavigateToUrl`, `Window.NavigateTo` and window reads, a landing poll, and one opt-in hitl scenario `app-state-nav` that the main session runs in one sitting with Phill present. Answers go into a findings doc, one row per question, and the spine memlog. No `src/` change.

## Boundaries & Constraints

**Always:**
- Every navigation goes through the guard, which reads the hierarchy live first:
  - `NavigateTo` and `Window.NavigateTo` only on an ID inside MCP Test, MCP Test Cloud or this run's notebooks (4.1's run probe folder).
  - `NavigateToUrl` only on a URL this run produced itself: a link `GetHyperlinkToObject` or `GetWebHyperlinkToObject` returned for an allowed target, that link with its `page-id` replaced by a fresh synthetic GUID (the missing page), or `onenote:///<path>` for a `.one` this run published into the probe publish folder. The guard matches the exact string against the run's set; anything else is refused before COM.
  - `fNewWindow` is always `false`, hard-coded in the one call shape (Q7). There is no parameter for it.
- Window reads happen inside one `InvokeAsync` delegate on the STA thread, and every `Windows` and `Window` wrapper is released with `Marshal.ReleaseComObject` in a `finally` before the delegate returns (OneMore row 10).
- Every call that navigates is preceded by a pause that says OneNote will come to the front and what it will show, and followed by one where Phill reports what he saw. The first window read of the run is also preceded by a pause, because whether a read changes focus is the question. Each row records the focus-and-view snapshot (4.1's `ViewAsync`) before and after.
- The crash breaker stays on. Each call has 4.1's limit (60 s local, 180 s cloud), and a blocked call stops the scenario. A Sync Now check and 3.1's settle follow each cloud step.
- Rows follow 4.1's logging rules: test and run notebook names only, the foreground process as `ONENOTE`, `test host`, `other` or `none`, no window titles, the cid as `<cid>`, links redacted to their shape (scheme, path form, which IDs are present).
- The probes rely on earlier findings and don't re-probe them; each is named in Design Notes and cited in the findings doc.

**Never:**
- Start, close or kill OneNote. Call `NavigateToUrl` with `fNewWindow=true`, `Window.NavigateToUrl`, or any window setter (`Active`, `FullPageView`, `DockedLocation`, `SideNote`, `SetDockedLocation`); their vtable slots are declared only as placeholders that can't compile when called.
- Call `QuickFiling` (entry 13), `SetFilingLocation`, `Publish(pfOneNote2007)`, `MergeFiles`, or any `UpdateHierarchy` move. Pass `force=true` anywhere.
- Retry a call automatically. A repeat-window call is its own measured call, never a retry of the one before.
- Navigate to, or read page content of, any notebook but MCP Test, MCP Test Cloud and this run's notebooks. The Ballast Vault probe reads its page IDs from the hierarchy only, never content.
- Change `src/` or the spine's text. Only the memlog gets lines.
- Commit real notebook names, paths holding the user name or cid, or any probe output.

## I/O & Edge-Case Matrix

Each row is one findings-doc question. L = local (MCP Test), C = cloud (MCP Test Cloud). F/V is 4.1's snapshot before and after plus what Phill reports. Every navigation row records the HRESULT, time, F/V and a landing series (below).

| Id | Area | Question |
|---|---|---|
| N1 | Link | `NavigateToUrl` with a `GetHyperlinkToObject` link to a page, a section, a notebook, and a paragraph (an `OE` object ID), L and C |
| N2 | Link | A `GetWebHyperlinkToObject` link to a C page: the HRESULT, and whether a security dialog appears (Microsoft) or a browser opens. Phill dismisses any dialog with its safe choice |
| N3 | Link | A link to a missing page (an N1 page link with a synthetic `page-id`), L and C: HRESULT (OneMore: success) and where the window lands |
| N4 | Link | A link to a page of a closed run notebook `n1` (linked while open, then closed the way entry 1 does): does it open `n1`? HRESULT, notebook list after, F/V |
| N5 | Link | `onenote:///<path>` to a loose `.one` this run published into the probe publish folder, never opened: does it open it, and as what (Open Sections, a notebook)? |
| N6 | Link | A page in MCP Test's Ballast Vault while locked (its ID and link read while Phill has it unlocked, then he locks it): HRESULT, whether a password prompt shows, what the window's `CurrentPageId` reports |
| R1 | Repeat | Pairs of calls between two pages, the second sent 0, 1, 2, 3 and 5 s after the first returns, for `NavigateTo` and for `NavigateToUrl`, L and C: which delays give `0x80042014`. No reads between the two calls of a pair, at least 6 s between pairs |
| K1 | Landing | For N1, N3 and R1's settled calls: a landing series reading `CurrentWindow`'s page, section and notebook IDs at 0, 100, 250, 500, 750, 1000, 1500, 2000, 3000 and 5000 ms (10 s on C), stopping once unchanged for 1 s after at least 1.5 s. How long it takes to settle |
| K2 | Landing | Which comparison works, per link kind: the window's current ID against the target's ID; `GetHyperlinkToObject` on the current page against the link's `page-id` GUID (OneMore row 5); the hierarchy's `isCurrentlyViewed`. For N3, does any of them show the landing failed? |
| W1 | Windows | `Count`, `CurrentWindow`, and `Item(i)` for i = 0 … Count (the index base, and the HRESULT past the end); every getter on each window (`WindowHandle`, whether it is a top-level window, `CurrentPageId`, `SectionId`, `SectionGroupId`, `NotebookId`, `FullPageView`, `Active`, `DockedLocation`, `SideNote`) |
| W2 | Windows | Does reading W1 change focus or the view? F/V around a full W1 read, three times, with OneNote in the background and with it in front |
| W3 | Windows | A quick-note window open (Phill opens one): W1, and what `CurrentWindow` is |
| W4 | Windows | Two windows on one page (Phill opens a second with Ctrl+M): W1; which window app-level `NavigateTo` moves; then `Window.NavigateTo` on the second window to another page: which window moves, and F/V |
| W5 | Windows | A window showing a Vault page, unlocked then locked: what `CurrentPageId` and `CurrentSectionId` report each time |
| W6 | Windows | No OneNote window: see Open Questions |
| H | HRESULTs | Every code seen, per method, with its trigger, mapped to AD-9 |

</frozen-after-approval>

## Open Questions

1. **W6, no OneNote window.** The entry asks what `Windows` reports with no window, and story 1.21 says `NavigateTo` returns ok and shows nothing then, unmeasured. Making that state means Phill closes OneNote's last window by hand, and OneNote may then exit.
   - (a) Probe it as the run's last step, after `cleanup`: Phill closes the window by hand; the probe reads W1 and calls `NavigateTo` on an MCP Test page. If `ONENOTE.EXE` exits, the breaker records it, the run stops, and Phill restarts OneNote himself.
   - (b) Log W6 `notProbed`, and leave `navigate_to`'s no-window wording unmeasured.
   - Recommended: (a). The tool's description already makes the claim, and the step is last, so an exit costs nothing else.
2. **N5, a loose `.one` that the link opens.** If `NavigateToUrl` opens the published file, it lands as a section outside any notebook, which the close guard can't target (`CloseNotebook` takes a notebook ID).
   - (a) A pause asks Phill to close it by hand (right-click > Close This Section); if he can't, it goes under Left behind.
   - (b) Try `CloseNotebook` on the section's ID, recording the HRESULT, and fall back to (a).
   - Recommended: (a). (b) would probe `CloseNotebook` on a section, outside this entry's questions.
3. **Plan size.** The plan is about 3,000 tokens, over the 1,600 guide, as 4.1's was.
   - (a) Keep the full plan, in one sitting as the entry says.
   - (b) Split the windows rows (W1–W6) into a separate spike.
   - Recommended: (a). The entry puts all of these in one sitting, and K2 needs W1's reads.

## Code Map

- This branch stacks on 4.1 (draft PR #60). Reuse 4.1's harness as it is there, finding symbols by name:
  - `WriteProbes/ProbeSession.cs`: `StepAsync`, `PauseAsync`, `ObservePauseAsync`, `ReportPauseAsync`, `RequireScenario`, `StopOnTimeoutAsync`, `TryAsync`/`TryGuardedAsync`, `ViewAsync` (F/V), `Foreground`, `SettleAsync`, `SyntheticId`, `McpTestIdAsync`, `CloudIdAsync`, `SectionIdAsync`, `PageIdByTitleAsync`, `PublishOneAsync`/`PublishDir`, `CreateThrowawayAsync`, `MustCreateChildAsync`, `CreateProbePageAsync`, `NotebookInfoAsync`, `Echo`. Add the new wrappers and the landing poll here.
  - `WriteProbes/AppStateLocalProbes.cs`: `AppStateContext` and `AppState` (`Begin`, `AdoptAsync`, `CloseRowAsync`, `OpenRowAsync`, `CleanupAsync`, `Missing`). Reuse `Begin` for the run folder and stamp, and `CloseRowAsync` for N4's close. Its run tags are `o1`–`o4`; this scenario's notebook is `n1`, so either generalise the tag list or give the nav context its own.
  - `TestNotebook.cs`: `CloseNotebookAsync`, `OpenExistingAsync`, `SyncHierarchyAsync` and their `*Call` shapes show the pattern; `EnsureTargetsAsync`/`TestNotebookGuard.ThrowawayNotebookIds` (run notebooks accepted by `IsRunProbeThrowaway`) are the target check for `NavigateTo`.
  - `WriteProbes/PinnedOneNoteProbe.cs` (the breaker): unchanged. `WriteProbes/ProbeRecorder.cs`: add link redaction.
  - `CloudJamRepro.CheckSyncAsync` gives the Sync Now pause wording.
- Interop facts, from the comtypes module generated from this machine's type library (`spikes/host-python/.venv/.../comtypes/gen/_0EA692EE_..._0_1_1.py`, read 2026-11-16):
  - `Windows` `{6D4B9C3E-CC05-493F-85E2-43D1006DF96A}`, dual: slot 7 `Item(uint)` → `Window`, 8 `Count`, 9 `_NewEnum`, 10 `CurrentWindow`. `src/OneNoteMcp.Com/Interop/IWindows.cs` declares only 7–8 and is internal (AD-2): don't edit it.
  - `Window` `{8E8304B8-CBD1-44F8-B0E8-89C625B2002E}`, dual: 7 get `WindowHandle` (`ulong`), 8 `CurrentPageId`, 9 `CurrentSectionId`, 10 `CurrentSectionGroupId`, 11 `CurrentNotebookId`, 12 `NavigateTo(bstr hierarchyId, bstr objectId)`, 13/14 get/put `FullPageView`, 15/16 get/put `Active`, 17/18 get/put `DockedLocation`, 19 get `Application`, 20 get `SideNote`, 21 `NavigateToUrl(bstr)`, 22 `SetDockedLocation(DockLocation, POINT)`.
  - `IApplication.NavigateTo` (slot 18), `NavigateToUrl` (19), `GetHyperlinkToObject` (22), `GetWebHyperlinkToObject` (35) and `Windows` (30) are declared; call them through `ComGateway.InvokeAsync(..., retryOnDisconnect: false)`.
- `VtableSmokeTests.Slot30_Windows_Count` already reads `Count` (no focus record). `tests/OneNoteMcp.Tests/InteropLayoutTests.cs` shows the layout-test style.
- `BallastContent.VaultSectionName` (`Ballast Vault`) in MCP Test; `ProtectedSectionIntegrationTests` shows the hierarchy-only Vault read.
- OneMore at `c5c1008` (read 2026-11-16): `OneNote.cs:350-391` `WithCurrentWindow` releases `Windows` and `Window` in `finally`; `:1400-1520` `GetWindows` enumerates with `_NewEnum`, walks `WindowHandle` up to the top-level frame; `:2331-2345` calls `NavigateToUrl` through `InvokeWithRetry`, which gives up at once on `0x80042014` (`:447-452`); `NewWindowOpener.cs:23,30` waits 1500 ms for a window to settle; `NavigationService.cs:22,26` polls `CurrentPageId` every 1250 ms, never under 1000.

## Tasks & Acceptance

**Execution:**
- [ ] `tests/OneNoteMcp.Integration/WriteProbes/ProbeWindowsInterop.cs` -- the spike's own early-bound `IProbeWindows` and `IProbeWindow`, internal to the test assembly, with the slots above; setters and `Window.NavigateToUrl` declared as `[Obsolete(..., error: true)]` placeholders -- the order must match the type library, and a call to a setter must not compile.
- [ ] `tests/OneNoteMcp.Integration/TestNotebook.cs` and `TestNotebookGuardTests.cs` -- add `NavigateToAsync`, `NavigateToUrlAsync`, `WindowNavigateToAsync` and their single `*Call` shapes (`fNewWindow: false` fixed), a pure `TestNotebookGuard.CheckNavigateUrl(url, allowed)`, and unit tests for each refusal: a URL not in the run's set, one differing only in case or a trailing character, an `https` link not issued, a `file:` URL, an ID outside the allowed notebooks.
- [ ] `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs` -- add `WindowsSnapshotAsync` (W1, in one delegate, wrappers released in `finally`, each getter's HRESULT kept), `LinkAsync`/`WebLinkAsync` (record into the allowed set), `MissingPageLink`, `LooseFileLink`, `LandingSeriesAsync`, and the pure `LinkShape` (parse `page-id`, `section-id`, path form; redact) and landing stop rule; unit tests in `ProbeHelpersTests.cs` for the pure parts, and a layout test that the spike interfaces' method order matches the slot list.
- [ ] `tests/OneNoteMcp.Integration/WriteProbes/AppStateNavProbes.cs` (scenario `app-state-nav`) -- every Matrix row in the Sitting script's order; `cleanup` is its own step, rerunnable with `ONENOTE_MCP_PROBE_STEPS=cleanup` and the stamp.
- [ ] Live sitting (the main session's, with Phill, after 4.1's sittings) follows the Sitting script. Not part of implementation.
- [ ] `epic-round-3-app-state-and-ui/spike-app-state-probes-part-2-navigation-and-windows-findings.md` (after the sitting) -- 4.1's structure: one row per Matrix question, HRESULTs, "Can a link open a closed notebook or a loose .one", "Can a landing be checked", "Does reading the windows change focus or the view", Not probed, Evidence gaps, Relied on, Left behind.
- [ ] Spine `.memlog.md` (after the sitting) -- one `constraint` line per finding that changes an AD, naming it.

**Acceptance Criteria:**
- Given the guard, when a navigation names an ID outside the allowed notebooks or a URL outside the run's set, then it refuses before any COM call.
- Given the call shapes, when `NavigateTo` or `NavigateToUrl` is called, then COM receives `fNewWindow=false`, and no code path can pass `true`.
- Given a window read, when it returns or throws, then every `Window` and `Windows` wrapper it obtained has been released.
- Given the cleanup step, when it ends, then `n1` is closed, the run folder and the published `.one` are gone, and MCP Test, MCP Test Cloud, `b`, `c` and the cloud throwaway are open as found.
- Given the findings doc, when entry 2 reads it, then every Matrix row has a result or a stated reason, and every earlier finding relied on is named.

## Sitting script

Run from the repo root with OneNote running, `$env:ONENOTE_MCP_INTEGRATION='1'`, `$env:ONENOTE_MCP_SCENARIO='app-state-nav'`, then `dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~AppStateNav`. Reruns take `ONENOTE_MCP_PROBE_STEPS` and `ONENOTE_MCP_PROBE_STAMP`, as in 4.1. It runs after 4.1's sittings, because N4 closes a notebook the way entry 1 finds safe.

**Before the sitting,** OneNote has one window showing MCP Test > Seed, the Ballast Vault is locked, and MCP Test Cloud is open and synced.

**One sitting (about 60–75 min), every step a hitl step:**

| Step | Rows | What Phill will see, and does |
|---|---|---|
| `windows-read` | W1, W2 | Nothing expected. He reports any change in OneNote; then brings OneNote to the front for the second set of reads |
| `links-local` | N1 L, K1, K2 | OneNote comes forward four times: a page, a section, a notebook, a paragraph of MCP Test. He confirms each |
| `missing` | N3, K2 | OneNote comes forward; he reports what it shows |
| `repeat-local` | R1 L | Ten jumps between two MCP Test pages, in pairs. Watch only |
| `cloud` | N1 C, N2, N3 C, R1 C | As above on MCP Test Cloud; for N2 he names any dialog and takes its safe choice, closing any browser tab. Sync Now check after |
| `closed-notebook` | N4 | `n1` is made, linked, then vanishes; then the link is followed. He reports whether `n1` reappears |
| `loose-file` | N5 | He reports whether a section opens; if it does, he closes it by hand |
| `vault` | N6, W5 | He unlocks the Vault at a pause, shows a Vault page, then locks it at the next; the link is followed. He reports any password prompt |
| `quick-note` | W3 | He opens a quick note (Win+Alt+N) at a pause, then closes it |
| `two-windows` | W4 | He opens a second window on Seed (Ctrl+M) at a pause; two jumps; he reports which window moved, then closes the second window |
| `cleanup` | - | `n1` and the published file go. He confirms the notebook list |
| `no-window` | W6 | Per Open Question 1 |

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Relied on, not re-probed** (the findings doc cites each):
- `NavigateTo` takes focus (spike 1 row 25), makes OneNote download a page's missing images (story 1.21's probe), and, per story 1.21's review, does nothing visible with no window.
- A close and reopen changes every ID and keeps `path` (spike 2.2 constraint 9); 4.1 measures `CloseNotebook` itself, and N4 uses its close row.
- A published `.one` opened by `OpenHierarchy` lands as a hidden Open Section, in plaintext (5.1 constraints 7 and 17). N5 asks whether a link does the same.
- From OneMore (research rows 5, 10, 14, 24, 25): the link's page GUID survives moves; release window wrappers; the cloud repeat window is about 2 s, 3 s always worked; `NavigateToUrl` reports success for a missing page; `WindowHandle` can be an inner pane.

**Why the URL guard is an exact allow-set.** A link names a notebook by path or URL, in forms the guard would have to parse to check, and a parser is what N4 and N5 test OneNote's own handling of. Accepting only strings the run produced keeps the guard independent of that.

**Why the missing page is a rewritten link.** Replacing `page-id` with a synthetic GUID makes a link to a page that never existed, without deleting anything.

**Order hazards.** `windows-read` runs first, before anything has navigated, so W2 sees an untouched view. `closed-notebook` runs after the cloud steps, so a close that blocks stops nothing else. `no-window` is last, after `cleanup`.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: succeeds.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` with no integration variables -- expected: pass, the guard, helper and layout tests run, every scenario skipped.

**Manual checks:**
- The diff has no `fNewWindow: true`, no call to a window setter or `Window.NavigateToUrl`, and no `NavigateTo` outside the guarded wrappers.
