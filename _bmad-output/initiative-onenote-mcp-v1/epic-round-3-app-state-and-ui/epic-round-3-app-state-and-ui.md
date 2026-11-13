---
type: epic
title: "Round 3: app state and UI"
parent: initiative-onenote-mcp-v1
covers: [R9]
after: [epic-round-2-external-and-structural]
assignee: ""
risk: medium
---

# Round 3: app state and UI

## Description

This epic adds the `AppState` class's pipeline and the rest of the COM surface:
- `OpenHierarchy` on an existing notebook, `CloseNotebook` and `SyncHierarchy`;
- the user-visible calls `NavigateToUrl` and `QuickFiling`, called only when the user explicitly asks (AD-11), and a `get_windows` read the skill uses when the user refers to what they're looking at (Q5). `NavigateTo` shipped in epic 1 as `navigate_to` (story 1.21); this epic moves it onto the `AppState` pipeline and lets it target a chosen window;
- dated Decisions not to expose events (Q10), `SetFilingLocation` (Q8), the window setters (Q4), `COMAddIns` and `LanguageSettings` (Q11), and the `Dummy1` slot.

It opens with three probe spikes for the COM behaviours no earlier spike settled, then amends the spine, and ships as a release with the skill and README updated. As the last epic, it also checks the initiative's Done when 4 across all five epics.

## Outcome

Claude can drive OneNote's app state when the user asks ("open that notebook", "close it", "sync it now", "show me this link", "what page am I on?"), without taking focus or changing what the user sees unasked, and without opening a way around the grants and AD-21. Every Application method has a released tool or a dated Decision.

## Requirements

- **A1 (R9) — The `AppState` pipeline.** Every `AppState` tool runs AD-7's pipeline as entry 2 sets it for this class:
  - no grant (AD-7); the write lock as entry 2 decides (Source conflict below); the 120 s deadline; a per-call timeout entry 2 sets for each method, `CloseNotebook` included, since a healthy close may outlast AD-3's 30 s default;
  - `outcomeUnknown` on the failures AD-7 step 7 lists, never retried (AD-3); entry 2 says whether a call that changes nothing, such as `get_windows`, can be `outcomeUnknown` at all;
  - path and URL arguments carry notebook and section names, so entry 2 says how they are logged (AD-13).

  `navigate_to` moves onto the pipeline and so gains its deadline, `outcomeUnknown` on a timeout, and `ComBusy` while an abandoned write is outstanding (AD-3). That changes its documented failures, so its description changes and the contract snapshot is checked (AD-14). Each tool's description says what it changes. The user-visible ones carry "user-visible" (AD-11); `open_notebook` and `close_notebook` join them if the spikes show either changes focus or the view. The skill calls every `AppState` tool only when the user asks (Assumption). (AD-7 `AppState`, AD-11)
- **A2 (R9) — `sync_hierarchy`.** `SyncHierarchy` on a notebook, section group, section or page. The result says the sync was requested, not that it finished: the call returns at once (spike 1.15) and OneNote gives no way to see a sync complete (OneMore row 23). (AD-7 `AppState`)
- **A3 (R9) — `close_notebook`.** `CloseNotebook` on a notebook ID, anything else refused before COM. It never passes `force` (Q1): when OneNote refuses or the close times out, the error's `userAction` says to let the notebook sync, or to close it in OneNote. It needs no grant (Q2). Its result gives the notebook's name and path and warns that every ID in it changes when it reopens. It says how to reopen it: through `open_notebook` for a local notebook, and in OneNote for a cloud notebook unless entry 1 shows `OpenHierarchy` opens a URL. Grants survive, because they are keyed by path (AD-6). Entry 2 picks how the closed notebook's pages leave the search cache (AD-12). A close that OneNote holds behind a dialog is expected to end in `Timeout` with `outcomeUnknown`, to be confirmed by entry 1. (AD-7 `AppState`)
- **A4 (R9) — `open_notebook`.** `OpenHierarchy` on an existing notebook, never creating anything (`cftNone`):
  - the server makes a local path canonical first (`GetFullPath`, junctions resolved), and every check, epic 3's Q11 rule included, uses the canonical form;
  - "exists" means, for a local path, a folder that entry 2's notebook test accepts, checked under the write lock (AD-7); that test is whatever entry 1 shows is needed, because a `.onetoc2` can land minutes after a create (5.1 constraint 18) and a section-group folder holds one too;
  - a URL is accepted only if entry 1 shows `cftNone` never creates at one; the server can't check a URL's existence itself, so OneNote's refusal is the check;
  - it opens notebooks only (Q3): a `.one` section file, a package (epic 3's `open_package` opens those) and a missing path are refused before COM;
  - a notebook already open returns its current ID from the hierarchy without calling COM (Assumption);
  - it gives no grant, though an existing grant for the same path applies (AD-6); what it opens follows AD-21 through epic 3's Q11 rule, applied to the canonical path (Q3);
  - the result gives the notebook's ID and name, and says a cloud notebook may take about 90 s to fill (OneMore row 22). (AD-7 `AppState`, AD-21)
- **A5 (R9) — `SetFilingLocation`: not exposed.** Entry 2 records a dated Decision (Q8): it changes a lasting OneNote option the server can't read or undo, one of its targets is Internet Explorer, and the user can set it in OneNote's options in seconds. Entry 8 is dropped.
- **A6 (R9) — Windows.** A `get_windows` tool lists OneNote's windows: handle, active state, the notebook, section group, section and page each shows, and its view. Without the section's read grant, a window showing a page in a protected section reports the section and omits the page's ID (AD-21, which already withholds such pages from reads). Reading the windows isn't user-visible (Q5): if entry 12 shows the read changes neither focus nor the view, the skill may call `get_windows` when the user refers to what they're looking at, and AD-11 lists only window changes as user-visible; if it does change them, entry 2 brings Q5 back to Phill. The window setters (`Active`, `DockedLocation`, `FullPageView`, `SideNote`, `SetDockedLocation`) get a dated Decision not to expose them (Q4). `navigate_to` takes an optional window. Every `Window` wrapper is released explicitly (OneMore row 10). (AD-11, AD-2, AD-21)
- **A7 (R9) — `navigate_to_url`.** A user-visible `NavigateToUrl` for `onenote:` links whose target lies inside a notebook that is already open, matched before COM against the open notebooks' `NotebookKey`s in AD-6's path forms (a local path made canonical first); any other link is refused with `InvalidArgument` (Q6). It takes the optional window from A6, and neither it nor `navigate_to` ever opens a new window (Q7). A repeated call inside the window entry 12 measures gets a `userAction` to wait. OneNote reports success even for a link to a missing page, so the tool checks where it landed: entry 12 looks for a way to compare the window's current location with the link, and entry 2 writes the rule. That check reads the windows, which Q5 allows. Without a check, the result warns that OneNote can't confirm where it landed. (AD-11)
- **A8 (R9) — `quick_filing`.** A user-visible location picker built on `QuickFiling`, built (Q9) if entry 13 shows its callback reaches our STA thread and the wait can be bounded as below; otherwise entry 2 records a dated Decision and entry 9 is dropped. The tool call waits for the user's choice with the STA queue free, its wait doesn't count against the pipeline deadline (as for elicitation, AD-6), and it is bounded by a limit entry 2 sets and by the client cancelling the call. While a picker is open, AD-3's idle release doesn't drop the COM reference. A picker left on screen after the call ends stays until the user closes it, and its answer is dropped. The result gives the chosen ID or says the user cancelled. (AD-11, AD-3)
- **A9 (R9) — Events: not shipped.** Entry 2 records a dated Decision (Q10) citing Microsoft ("Events cannot currently be added in managed code"), OneMore polling the current page instead, and Claude Code's channels, the only push route to the model, being a gated research preview; it adds a Deferred row to revisit when channels leave the preview. No spike probes an event sink.
- **A10 (R9) — Coverage.** Every `IApplication` member, slots 7 to 35 of `src/OneNoteMcp.Com/Interop/IApplication.cs`, has a released tool in one of the five epics or a dated Decision recording why it isn't exposed (the initiative's Done when 4). Entry 2 writes the table, and entry 10 checks it against the released tools.

## Done when

1. `sync_hierarchy`, `close_notebook` and `open_notebook` work against real OneNote through the `AppState` pipeline, and `navigate_to` runs on it too. A throwaway notebook closed and reopened by path keeps its grant.
2. Every tool on entry 2's amended AD-11 list says "user-visible" in its description. The skill calls them only when the user asks, and no use case calls them internally except as entry 2 allows. `get_windows` reports the page each window shows, within AD-21.
3. No `AppState` tool opens a notebook, section or file that `open_notebook`'s rules would refuse, whether directly or through a link.
4. A dated Decision records why events don't ship. Every OneNote Application member has a released tool, or a dated Decision, checked across all five epics.
5. It ships as a GitHub release through the epic 1 pipeline, with the skill and README updated.

## Boundaries

This epic owns:
- the `AppState` pipeline, and moving `navigate_to` onto it;
- `OpenHierarchy` on an existing path, `CloseNotebook`, `SyncHierarchy`, `SetFilingLocation`, `NavigateToUrl`, `QuickFiling`, `Windows` and any event sink, on the port and both gateways;
- these tools, the skill and README sections that cover them, and the initiative's coverage table.

Out of scope:
- anything that writes content;
- `OpenHierarchy` creating a section, section group or notebook (epic 5);
- `open_package` (epic 3), though this epic's `close_notebook` closes what it opened;
- starting, closing or restarting OneNote itself, which stays forbidden (AD-3).

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirement R9; Done when 4
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-2, AD-3, AD-5, AD-6, AD-7, AD-9, AD-11, AD-12, AD-13, AD-14, AD-16, AD-18, AD-21, Deferred (events; modal dialogs other than File > Options)
- rationale — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md, spike 1's focus entries and spike 5.1's modal, focus and notebook entries
- constraint — CLAUDE.md, section OneNote COM constraints (NavigateTo takes focus; never start, close or kill OneNote; `0x80042030` under a modal dialog)
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, rows 9 and 25a, and "Not probed"
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-do-the-read-tools-hold-up-on-a-large-real-notebook-findings.md, quirk 3 (`SyncHierarchy`)
- story plan — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-missing-binaries-xpsfile-download-guidance-and-opt-in-fetch-plan.md, the `navigate_to` design, its probe and review finding 2
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md, constraints 7, 11, 14, 17 and 18, "Failure modes" and "Left behind"
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural.md, E3 and Decisions Q11 and Q13
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/epic-safe-writes.md, the write pipeline and write lock the `AppState` pipeline sits beside
- prior art — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md, rows 4, 5, 10, 14 and 21–26, and CLAUDE.md, section "Prior art: OneMore"
- Microsoft — https://learn.microsoft.com/en-us/office/client-developer/onenote/application-interface-onenote (OpenHierarchy, CloseNotebook, SyncHierarchy, SetFilingLocation, NavigateToUrl, QuickFiling, Events), window-interfaces-onenote, quick-filing-dialog-box-interfaces-onenote, enumerations-onenote-developer-reference, error-codes-onenote (read at MicrosoftDocs/office-developer-client-docs@eedbd61c, 2026-11-12)
- Claude Code — https://code.claude.com/docs/en/mcp.md and https://code.claude.com/docs/en/channels.md, how a server could push events (Q10)
- code — src/OneNoteMcp.Com/Interop/IApplication.cs and IWindows.cs, src/OneNoteMcp.Core/UseCases/NavigateToUseCase.cs, src/OneNoteMcp.Server/Tools/NavigateToTool.cs, plugin/skills/guide/SKILL.md ("User-visible actions and limits")

## Notes

- Decision (Phill 2026-11-12, Q1, option a): `close_notebook` never passes `force`. When OneNote refuses, or the close times out, the error's `userAction` says to let the notebook sync, or to close it in OneNote. Reason: losing unsynced changes is the one way this epic could lose content, and the journal couldn't bring them back. Rejected: a `force` argument confirmed in an elicitation dialog (b), or unchecked (c). Entry 1 doesn't probe `force=true`.
- Decision (Phill 2026-11-12, Q2, option a): opening and closing a notebook need no grant, as AD-7 says. The descriptions say what the tools change, and the skill calls them only when the user asks. A cloud notebook the tool closes may be reopenable only in OneNote (entry 1 checks), and the close result says so. Rejected: the write grant for a close (b), which would block closing a notebook the user only reads; an elicitation confirmation per close (c).
- Decision (Phill 2026-11-12, Q3, option a): `open_notebook` opens notebooks only: a local folder that passes entry 2's notebook test, plus a OneDrive URL if entry 1 shows one opens and never creates. A `.one` and a `.onepkg` are refused before COM, and epic 3's Q11 rule applies to the canonical path. Entry 2 sets what happens to a section-group folder, from entry 1's probes. Rejected: `.one` files as protected (b); any path with Q11's rule alone (c).
- Decision (Phill 2026-11-12, Q4, option a): `get_windows`, plus an optional window on `navigate_to` and `navigate_to_url`. `Active`, `DockedLocation`, `FullPageView`, `SideNote` and `SetDockedLocation` get a dated Decision not to expose them, which entry 2 records. Rejected: the setters as user-visible tools (b); `get_windows` only (c).
- Decision (Phill 2026-11-12, Q5, option a): if entry 12 shows that reading the windows changes neither focus nor the view, `get_windows` is an ordinary `AppState` read. The skill may call it when the user refers to what they're looking at ("this page", "the page I'm on"), `navigate_to_url` uses it for its landing check, and entry 2 amends AD-11 to list only window changes as user-visible. A protected page's ID stays withheld without a read grant (A6). If entry 12 shows the read does change focus or the view, entry 2 brings this back to Phill. Rejected: keeping AD-11 as written (b).
- Decision (Phill 2026-11-12, Q6, option a): `navigate_to_url` accepts only `onenote:` links whose target lies inside a notebook that is already open, matched before COM against the open notebooks' `NotebookKey`s in AD-6's path forms (a local path made canonical first). Anything else is refused with `InvalidArgument`. Reason: a link can point at a closed notebook, a loose `.one` or a remote URL, and following it could get around `open_notebook`'s rules and the plaintext backup route. Rejected: links outside the open notebooks that pass `open_notebook`'s rules (b); any `onenote:` link plus web links (c); any URL (d).
- Decision (Phill 2026-11-12, Q7, option a): `navigate_to` and `navigate_to_url` always pass `fNewWindow=false`, as story 1.21 decided for `navigate_to`; the window argument (Q4) covers a second window. Rejected: a `newWindow` argument (b).
- Decision (Phill 2026-11-12, Q8, option b): `SetFilingLocation` isn't exposed, and entry 2 records the dated Decision: it changes a lasting OneNote option the server can't read or undo, one of its targets is Internet Explorer, and the user can set it in OneNote's options in seconds. Rejected: a `set_filing_location` tool (a).
- Dropped: entry 8 (`set_filing_location`), by Q8 option b (Phill 2026-11-12). It had no file and no plan, so it is deleted from `tickets.toml`, and entry 9's `after` moves to entry 7. Its id is never reused.
- Decision (Phill 2026-11-12, Q9, option a): build `quick_filing` as a user-visible location picker if entry 13 shows the callback reaches our STA thread and the wait can be bounded as A8 says; otherwise entry 2 records a dated Decision and entry 9 is dropped. Rejected: a Decision now (b).
- Decision (Phill 2026-11-12, Q10, option c): events aren't probed or shipped. Entry 2 records the dated Decision, citing Microsoft's statement, OneMore's polling and Claude Code's channels preview, and adds a Deferred row to revisit when channels leave the preview. Entry 13 probes only QuickFiling. Rejected: probing a sink and shipping channel events (a), or probing without shipping (b).
- Decision (Phill 2026-11-12, Q11, option a): `COMAddIns` and `LanguageSettings` aren't exposed; entry 2 records the dated Decision. Rejected: a read-only add-in listing (b).
- Decision (Phill 2026-11-12): the Fable final review of the first draft found 20 issues, and all 20 are fixed in this revision.
- Decision (Phill 2026-11-12): keep the whole-epic gate. The spikes (entries 1, 12 and 13) run after epic 3, as round 2's spike runs after epics 2 and 5. This file's `after` gates every entry on epic 3 being done, which brings in epics 2 and 5, which epic 3 waits on: this epic uses epic 2's write lock and pipeline runner, and shares the class dispatch, tool registration, contract snapshot and skill with epic 3. Entries name other epics' stories only as what they build on, never in `after`. This follows Phill's 2026-11-12 decision, relayed by the team lead, to plan rounds 2 and 3 now and build them only after epics 2 and 5 land.
- Source conflict: AD-7 — "Every non-read use case runs these steps in order … no write path exists outside the pipeline" vs `navigate_to` (story 1.21), an `AppState` use case that calls the gateway directly, because it shipped before epic 2 built the pipeline. Entry 2 sets the `AppState` pipeline, and entry 3 moves `navigate_to` onto it.
- Source conflict: AD-7 step 2 and AD-18 — every non-read pipeline takes the write lock vs this plan's assumption (below) that `sync_hierarchy`, `get_windows`, `navigate_to`, `navigate_to_url` and `quick_filing` don't. Entry 2 settles it with a dated Decision; entries 3, 6, 7 and 9 wait on it.
- Source conflict: AD-11 — "`NavigateTo`, `NavigateToUrl`, `QuickFiling` and `Windows` tools carry 'user-visible'" vs a `Windows` read that may change nothing the user sees. Q5 (option a) settles it, and entry 2 amends AD-11.
- Source conflict: AD-14 — "Tool names are the `snake_case` of the COM method" vs `open_notebook` (`OpenHierarchy`) and `get_windows` (`Windows`). Entry 2 records both names in AD-14 as exceptions to that rule, with `create_page` (`CreateNewPage`), which AD-14 doesn't list either.
- Answered before this epic, so the spikes don't repeat them:
  - `NavigateTo` brings OneNote forward and takes keyboard focus (spike 1 row 25). `OpenHierarchy` with `cftNotebook` at the path of an already-open notebook returned that notebook's ID (spike 1 row 9; 5.1 constraint 18, `sameId`) and left the foreground window unchanged (spike 1 row 25a); opening a closed notebook was never measured. `OpenHierarchy` creates usually kept the foreground window, with three unexplained exceptions (5.1 constraint 14).
  - With `cftNotebook`, `OpenHierarchy` makes a notebook in any existing folder, even a non-empty one, and its `.onetoc2` showed up five minutes later (5.1 constraint 18).
  - `NavigateTo` on a page makes OneNote download its missing images within 2 s (story 1.21's probe). With no OneNote window it returns ok and shows nothing (story 1.21's review, finding 2, now in `navigate_to`'s description; not separately measured).
  - `SyncHierarchy` on a page and on a section returns `S_OK` in 0 ms and downloads no missing images (spike 1.15, quirk 3).
  - `OpenHierarchy` opens a published `.one` in 42–55 ms, as a hidden Open Section. A pages-scope read straight after lists no pages. The opened copy has no `encrypted` marker and is plaintext (5.1 constraints 7 and 17).
  - Under File > Options and under a section password dialog, `GetHierarchy` and `OpenHierarchy` creates go through (an open of an existing path wasn't tried), and only the update methods return `0x80042030` (5.1 constraint 11).
  - Two dialogs block calls instead of returning an error, both surfacing as our `Timeout` (5.1 constraint 11, Failure modes): "OneNote is syncing changes", which in run 1 on local MCP Test blocked calls for about 3 minutes until Phill pressed Cancel; and "We couldn't find that file", raised by `Publish` into a missing folder.
  - Separately, during 5.1's cloud-jam recovery Phill closed MCP Test Cloud by hand, not through COM: the close hung, needed Cancel and a retry, and OneNote moved the unsynced sections to Misplaced Sections (5.1, Failure modes). No COM `CloseNotebook` has been tried.
  - Closing and reopening a notebook keeps every `path` and gives every notebook, section and section group a new ID (spike 2.2 constraint 9, closed by hand). OneMore measured the same through the API, page and paragraph IDs included; windows left open kept dead IDs and showed blank, and a 376-page cloud notebook took about 90 s to fill (row 22).
  - COM can't delete a notebook, so 5.1's throwaway notebooks (`MCP Test Throwaway 1112-034716 a`, `b`, `c` and `MCP Test Throwaway Cloud`) are still open (5.1 constraint 18, "Left behind").
  - The interop declares every `IApplication` slot, with `QuickFiling`, `Windows`, `COMAddIns` and `LanguageSettings` returned as `IDispatch`, and `IWindows` with `Count` and `GetItem` only. `CurrentWindow`, the `Window` interface, `IQuickFilingDialog` and its callback interface aren't declared. Late binding fails here, so entry 6 declares the window interfaces and entry 9 the QuickFiling ones, early-bound; the spikes use their own copies.
- From OneMore and Microsoft's docs (checked 2026-11-12; rows 21–26 of `../epic-read-only-release/research-onemore-lessons.md`):
  - OneMore isn't in-process with OneNote: it runs in `dllhost.exe`, in an MTA, and reaches OneNote through `IApplication` across processes as we do (we use an STA). Its COM evidence therefore transfers better than an in-process add-in's would.
  - Microsoft: a section group outside any open notebook opens as a notebook, and a section outside one opens under Recent Opened Sections. A section group or section already in an open notebook is left as it is ("nothing happens"), and the ID is returned either way. OneMore reports that OneNote navigates to a notebook that is already open, and that the hierarchy can be empty straight after an open (it retries up to three times, 0.5–1.5 s apart).
  - `NavigateToUrl` reports success for a link to a page that doesn't exist. On cloud notebooks, a second call within about 2 s fails `0x80042014`, and at 3 s or more it always worked (Defects page). OneMore treats `0x80042014` as not retryable.
  - `Window.WindowHandle` can be an inner pane, not the frame, and the index base of `Windows[i]` isn't documented. OneMore only reads windows, never sets them.
  - `QuickFiling`'s callback runs while OneNote's UI thread waits for it to return, so it must return at once, and the dialog object must outlive `Run`. Microsoft: `Run` shows the dialog from a new thread.
  - No error code is specific to these calls. The nearest are `hrNotebookDoesNotExist` `0x80042015`, `hrObjectDoesNotExist` `0x80042014`, `hrNavigatingToPage` `0x8004200A`, `hrOpeningSection` `0x80042003`, `hrFolderDoesNotExist` `0x80042018`, `hrNotYetSynchronized` `0x8004201D` and `hrAppInModalUI` `0x80042030`.
- Unknown, settled by the spikes. They run on MCP Test, MCP Test Cloud, the throwaway notebooks `b`, `c` and `Cloud`, and notebooks they make in a fresh probe folder (closed, and the folder deleted, when done). There is a Sync Now check after each cloud step, and Phill is present for every call that takes focus or shows a dialog.
  - Entry 1 (open, close and sync):
    - `OpenHierarchy` with `cftNone` on a closed local notebook: focus and the view, what path OneNote reports for a notebook opened through a junction or a UNC path, and how long the hierarchy takes to fill, local and cloud.
    - The same on: a OneDrive notebook URL, which Microsoft doesn't document (does it open, and can it ever create?); an already-open notebook (does the view change, as OneMore reports?); a missing path; a folder that isn't a notebook; a notebook just created and closed, before its `.onetoc2` lands; a section-group subfolder of a closed and of an open notebook. For each: the HRESULT, and whether anything is created.
    - `CloseNotebook` on a local and a cloud throwaway, without `force` (Q1): how long a close with changes waiting to sync takes, and whether it refuses, blocks behind a dialog until our `Timeout`, or leaves sections in Misplaced Sections; offline; focus; what a window showing the notebook does; closing the last open notebook, and the notebook the only window shows; under File > Options; and whether `list_grants` shows the grant active again after a reopen.
    - `SyncHierarchy` on a notebook, local and cloud, and the HRESULT for an unknown or closed notebook's ID.
    - Each of these methods under File > Options, and the HRESULT each returns.
  - Entry 12 (navigation and windows):
    - `NavigateToUrl` with: a `onenote:` link from `get_hyperlink_to_object` to a page, a section, a notebook and a paragraph; a web link from `get_web_hyperlink_to_object`; a link to a missing page; links to a page of a closed throwaway notebook and to a loose probe `.one` (does it open them?); and a page in a locked protected section. The repeat window that gives `0x80042014`, for `NavigateTo` and `NavigateToUrl`, local and cloud.
    - The landing check: how long the window takes to settle, and how to compare where it landed with the link (the window's current IDs, or `GetHyperlinkToObject` on the current page against the link's page GUID, OneMore row 5).
    - `Windows`: `Count`, `CurrentWindow`, the index base and each `Window` property; whether reading them changes focus or the view; with no window, a quick-note window, two windows on one page, and a window showing a page in a protected section; `Window.NavigateTo` in the second window.
  - Entry 13 (QuickFiling):
    - `QuickFiling`: whether our STA thread gets the dialog early-bound and receives `OnDialogClosed`; whether `Run` returns at once; modality and focus; what other COM calls do while it's open; cancel; and what happens to the picker when our call times out, our STA thread is replaced, or our COM reference is released.
- Assumption (unconfirmed): the skill calls every `AppState` tool only when the user asks, not just the user-visible ones, because each changes the user's OneNote; for `get_windows`, the user referring to what they're looking at counts as asking (Q5). It never calls `sync_hierarchy` on its own after a write.
- Assumption (unconfirmed): `open_notebook` first looks for the canonical path among the open notebooks and returns that notebook's ID without calling COM, because OneMore reports that OneNote navigates to an already-open notebook. Entry 1 checks it.
- Assumption (unconfirmed): the write lock is taken by `open_notebook` and `close_notebook`, so a close can't run during another session's write and an open's existence check can't race a create. Entry 2 decides (Source conflict above).
- Assumption (unconfirmed): `Dummy1` (slot 31), a placeholder with no documented behaviour, gets a dated Decision not to expose it.
- Assumption (unconfirmed): test fixtures.
  - The integration tests close and reopen 5.1's throwaway notebooks `b` and `c`, never MCP Test or MCP Test Cloud; epic 3 uses `a`. They do so only if entry 1 shows that closing and opening a closed notebook change neither focus nor the view; otherwise those steps run live with Phill, and entry 5 becomes hitl.
  - `MCP Test Throwaway Cloud` is closed only in live checks with Phill, who reopens it in OneNote straight after and checks it with Sync Now, reporting `0xE0000644` if it appears.
  - The test harness makes the notebook under the test export root itself, through its own COM seam, in a fresh folder. It waits, within a limit, until entry 2's notebook test accepts the folder, closes the notebook, and lets `open_notebook` open it; afterwards it closes it and deletes the folder.
  - The integration guard, which epic 3 opens to closing `-unpacked` notebooks, gains these throwaways.
  - Calls that take focus or show a dialog aren't in the automated suite. Unit tests on `FakeGateway` cover their logic, and they run live with Phill, batched into as few sittings as the build order allows.
- Assumption (unconfirmed): no release ticket. Phill tags the release after the refactor sweep, as in epics 2, 3 and 5.
- Assumption (unconfirmed): no opening refactor, because epic 3's refactor sweep (3.11) closes just before this epic starts.
- Assumption (unconfirmed): closing 5.1's leftover throwaway notebooks once `close_notebook` ships is Phill's housekeeping, not a story.
- Decision (Phill 2026-11-12, accepting the plan by answering its questions; drafted in an autonomous inception and revised after its Fable review the same day): the breakdown is the entries in `tickets.toml` (1, 12, 13, 2–7, 9–11; entry 8 dropped by Q8), in one lane, because every story after the spine amendment touches the gateway port, the tool registration and contract snapshot, and the skill.
  - The probes are split in three so no spike outgrows a sitting: entry 1 open, close and sync; entry 12 navigation and windows; entry 13 QuickFiling. Entry 12 follows entry 1 because its closed-notebook link probe closes and reopens a throwaway the way entry 1 found safe. Entry 13 follows entry 12 because Phill can be present for one probe sitting at a time. All three use the probe harness 2.2 and 5.1 built, which entry 1 extends.
  - The tracer bullet is entry 3: `sync_hierarchy` through the new `AppState` pipeline, with `navigate_to` moved onto it. It is the thinnest call that crosses every layer and takes no focus.
  - `close_notebook` (entry 4) follows as the slice least known, and `open_notebook` (entry 5) reopens what it closed.
  - If entry 9 (`quick_filing`) is dropped because entry 13 finds no working callback, entry 10's `after` moves to entry 7.
  - bmad-ticket's draft check ran in a fresh subagent on each draft; its findings were checked against the sources and applied.
