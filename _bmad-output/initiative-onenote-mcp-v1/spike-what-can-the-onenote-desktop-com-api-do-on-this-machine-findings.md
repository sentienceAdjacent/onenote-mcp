---
title: 'Findings: what can the OneNote desktop COM API do on this machine?'
type: 'spike-findings'
findings_for: '1'
status: 'final'
created: '2026-11-03'
machine: 'ONENOTE.EXE 16.0.20527.20112 (M365 Click-to-Run; 64-bit inferred from the Program Files path), Windows 11 Pro 10.0.26300, not elevated'
evidence: 'spikes/results/*.jsonl'
---

# Findings: what can the OneNote desktop COM API do on this machine?

**Short answer:** the COM API works on M365 OneNote desktop here. Reads, writes, deletes, search, Publish and NavigateTo all work through `OneNote.Application`. Three results change the architecture, and I recommend C#:

1. **Late binding through IDispatch fails.** Clients have to call through the vtable, using a typed interop or comtypes. pywin32 and pwsh 7 can't call OneNote at all.
2. **The last-modified check isn't reliable on its own.** A page's `lastModifiedTime` stops following COM edits while the page isn't the one shown in OneNote. A stale date then still matches, and the write goes through.
3. **Indexed-only search can leave pages out.** A fresh page in a crawl-excluded location was missed for over 300 s in 2 of 3 runs. In a crawl-scope location it was found within 15 s in 1 run. Location probably matters, but one run doesn't settle it (row 28), so treat results as incomplete.

**Terms.** "Main" is the lead Claude session working with Phill; it relayed his observations and decisions. In the tables, "fails" means the capability doesn't do what the server would need, with the HRESULT where there is one. "Pass" means an expected rejection happened.

Evidence: each row cites a probe name. Search for it in `spikes/results/<log>.jsonl`. Probe code lives in `spikes/com-capabilities/` (PowerShell), `spikes/host-csharp/` and `spikes/host-python/`. The spike notebooks are `%LOCALAPPDATA%\OneNoteMcpSpike\MCP Spike` (Publish output goes to its `publish\` folder) and, for the search re-run, `%USERPROFILE%\OneNoteMcpSpikeIndexed\MCP Spike Indexed`.

**Open:** nothing. Phill accepted the findings on 2026-11-03 after the Fable final review (see the ticket's Notes).

## Constraints for the architecture

These are also logged in `architecture-onenote-mcp/.memlog.md`, where later entries supersede earlier ones.

1. **Binding.** Bind early through the vtable, using a hand-declared `[ComImport]` IApplication (see Stack). Never late-bind through IDispatch (B2, B3).
2. **Concurrency.** Don't rely on `dateExpectedLastModified` as the only concurrency guard. Per Microsoft's docs it rejects a date that doesn't match the page's current `lastModifiedTime`; a newer-than-current date was not tested. On a page that isn't displayed, `lastModifiedTime` didn't advance across COM edits within 8 s (row 18). Add a content read-compare-write, for example a hash of the target OE or the page XML, and document the guarantee as best-effort.
3. **Dates.** Pass the date as a UTC `DateTime` parsed from the XML attribute. UTC acceptance is logged; that local-kind dates are rejected is observed, not logged (U2).
4. **Search.** Indexed-only `FindPages` can omit pages, so treat results as incomplete.
   - In a crawl-scope location (`%USERPROFILE%\OneNoteMcpSpikeIndexed`), a fresh page was found at the first 15 s poll, after missing at 0 s (1 run).
   - In a crawl-excluded location (`%LOCALAPPDATA%`), it was missed for over 300 s in 2 of 3 runs (one log lost) and found at 0 s once.
   - So location probably matters, and even an indexed location can lag by seconds. This isn't proven from these runs: n=1 in scope, and the exclusion result isn't consistent.
   - Any notebook can sit anywhere, so the server can't rely on indexed-only search. Including unindexed pages returns unfiltered candidates, **recycle-bin pages among them** (row 29), so a post-filter must drop `isInRecycleBin="true"` and filter on hierarchy metadata first. A text post-filter at root scope costs about 5 s on the single STA thread (213 candidates × 22.5 ms average `GetPageContent`, derived from rows 5 and 29), so scope below the root where possible.
5. **Binary content.** Images in cloud notebooks may have no local binary: `GetBinaryPageContent` returns 0x8004200f, and piBinaryData drops the Image elements. Report "binary unavailable" instead of failing the read. Attachments (`InsertedFile`) were not probed.
6. **Recycle bin.** A soft-deleted page comes back from the recycle bin under a new ID; a section keeps its ID. A restore that resolves by name can be ambiguous when several deleted pages share a name, so it needs the user to choose.
7. **Threading.** One STA thread serialises every call, so a slow call (docx Publish, 2.9 s) blocks every other tool call. Time out hung calls (logged: failures `warmup-read`, `elevated-read`). Retry on RPC_E_CALL_REJECTED / RETRYLATER: that's standard COM practice, and the one sighting here was observed, not logged (U3).
8. **Hierarchy XML.** `UpdateHierarchy` with a partial Notebook XML adds sections and keeps siblings, and a rename can use the Section as the XML root.
9. **NavigateTo.** Never call `NavigateTo` unless the user explicitly asks. It takes keyboard focus (see "NavigateTo and keyboard focus").
10. **Modal dialog.** Map 0x80042030 hrAppInModalUI to a retryable error the user can act on ("close the open OneNote dialog"). Reads still work while a dialog is open.
11. **Locked sections.** Check a section's `locked` attribute before writing, and report "unlock in OneNote" (0x8004201b). Locked sections list no pages, so show them as locked, not empty.
12. **Integrity level.** Run at OneNote's integrity level and refuse to run on a mismatch. A server elevated over a non-elevated OneNote hangs instead of failing fast; CO_E_SERVER_EXEC_FAILURE only came after about 30–57 s. Give activation its own timeout. The reverse case (server not elevated, OneNote elevated) is untested.
13. **Reconnect.** Treat 0x800706ba RPC_S_SERVER_UNAVAILABLE (and RPC_E_DISCONNECTED) as "reconnect": drop the stale object, activate a new one, retry once.
14. **Idle release.** Release the COM reference after an idle timeout. This is a precaution: the launch blocking in the "OneNote killed" scenario is inferred, not proven, to have come from the held reference.
15. **Never kill OneNote.** The server must never kill or close OneNote; its only recovery is releasing its own reference.
16. **Silent activation.** Activation after OneNote has exited starts a windowless instance (511 ms). Don't activate silently when ONENOTE.EXE isn't running: report "OneNote is not running", or make silent activation a setting. Either way, activation needs the timeout from 12.

## Capability matrix

| # | Capability | Method | Result | Evidence (log / probe) | Notes |
|---|---|---|---|---|---|
| 1 | Create the COM object, OneNote running | `New-Object -ComObject` | works, 9 ms | read / `activation` | On an STA worker runspace |
| 2 | Hierarchy, every scope from the root | `GetHierarchy` hsSelf/Children/Notebooks/Sections/Pages | works, 2–27 ms | read / `hierarchy-root-*` | Full tree (4 notebooks, 31 sections, 205 pages): 57 KB in 27 ms |
| 3 | Schema namespace | `GetHierarchy` with xs2010 / xs2013 | works | read / `hierarchy-schema-*` | The default and xs2013 return `http://schemas.microsoft.com/office/onenote/2013/onenote`; xs2010 returns the 2010 namespace |
| 4 | Largest real notebook (143 pages, 13 sections, 2 locked) | `GetHierarchy(nb, hsPages)` | works, 6 ms, 37 KB | read / `largest-notebook-hierarchy` | Read-only |
| 5 | Page content, basic | `GetPageContent` piBasic | works, 25/25, 6–128 ms (avg 22.5) | read / `page-content-basic`, `largest-notebook-summary` | Real notebook, read-only, counts only |
| 6 | Page content with binary, **cloud notebook** | `GetPageContent` piBinaryData | the call works (25/25, avg 21 ms), **but images are missing** | read / `page-content-binary`, `largest-notebook-summary` (`notebookKind: cloud-url`) | The largest notebook is a cloud (OneDrive URL) notebook, which is logged. The other three were cloud too in an unlogged check. 4 of the 25 sampled pages had images (piBasic counts 2, 3, 1, 1), and piBinaryData returned 0 images for all four |
| 7 | Binary by callback ID, **cloud notebook** | `GetBinaryPageContent` | **fails 0x8004200f hrBinaryObjectDoesNotExist** | read / `binary-page-content` | The image isn't cached locally |
| 8 | Image insert + binary read, **local spike notebook** | `UpdatePageContent` + `GetPageContent` piBinaryData + `GetBinaryPageContent` | works | write / `image-insert`, `spike-page-content-*`, `spike-binary-page-content` | piBasic gives a CallbackID; piBinaryData inlines `one:Data`; the PNG is re-encoded, so the bytes don't round-trip |
| 9 | Create a notebook at a path | `OpenHierarchy` cftNotebook | works, 22 ms | write / `open-spike-notebook` | Reopening an existing path returns the same ID (focus / `OpenHierarchy`) |
| 10 | Create a section | `UpdateHierarchy` (Notebook root + new Section) | works, 12 ms | write / `section-create-updatehierarchy`, `section-create-verify` | Sibling sections are kept |
| 11 | Create a section | `OpenHierarchy` cftSection | works, 45 ms | write / `section-create-openhierarchy` | |
| 12 | Rename a section | `UpdateHierarchy` (Section as XML root) | works, 2 ms | write / `section-rename-*` | The ID stays the same |
| 13 | Create a page | `CreateNewPage` | works, 1–13 ms | write / `page-create` and `*-setup` | |
| 14 | Append an outline, no date | `UpdatePageContent` | works | write / `append-outline-nodate` | |
| 15 | Append an outline, with the current date | `UpdatePageContent(xml, date)` | works | write / `append-outline-withdate` | UTC date (constraint 3) |
| 16 | Edit existing text by `objectID`, with or without a date | `UpdatePageContent` | works | write / `edit-text-withdate-utc`, `edit-text-nodate`, `edit-verify` | |
| 17 | Stale-date conflict, **page displayed** | `UpdatePageContent(xml, staleDate)` | **pass: rejected 0x80042010 hrLastModifiedDateDidNotMatch** | focus / `lmt-displayed-stale-write`, `lmt-displayed-trace` (hands-off re-run, fresh read before every step); also write / `lmt-trace-displayed-stale-write` (possibly confounded by stray typing) | `lastModifiedTime` advanced on all 3 COM edits, and each edit with a freshly read UTC date was accepted. Per Microsoft's docs the date must match the current value; a newer-than-current date was not tested |
| 18 | Stale-date conflict, **page not displayed** | `UpdatePageContent(xml, staleDate)` | **fails: the stale write is applied** | write / `edit-stale-date`, `edit-stale-verify`, `lmt-trace-not-displayed*` | `lastModifiedTime` **did not advance within 8 s** across 3 COM edits (`lmtAdvances: 0`), so the "stale" date still matched. Whether it catches up later, and when, is **open**. *Observed, not logged:* on an older undisplayed page it moved backwards while the section's value advanced (U4) |
| 19 | Soft delete of a page | `DeleteHierarchy` (default) | works; the page goes to the recycle bin **under a new ID** | write / `delete-page-default`, `delete-page-recycle-check` | |
| 20 | Permanent delete of a page | `DeleteHierarchy(id, date, true)` | works; gone, not in the recycle bin | write / `delete-page-permanent`, `delete-permanent-check` | |
| 21 | Delete with a stale date, page not displayed | `DeleteHierarchy(id, staleDate, true)` | **fails: deleted anyway** | write / `delete-stale-date`, `delete-stale-check` | Same cause as 18 |
| 22 | Soft delete of a section | `DeleteHierarchy` (default) | works; the section keeps its ID with `isInRecycleBin="true"` | write / `delete-section-*` | |
| 23 | Publish to PDF / docx / MHTML | `Publish` | works: 95 ms / 2.9 s / 20 ms | write / `publish-pdf`, `publish-docx`, `publish-mht` | |
| 24 | Publish over an existing file | `Publish` | pass: rejected 0x8004201a hrFileAlreadyExists | write / `publish-existing-file` | |
| 25 | NavigateTo a page | `NavigateTo` | works, **and takes keyboard focus** | write / `navigate-to-page`; focus / `NavigateTo-focus` | See "NavigateTo and keyboard focus" |
| 25a | Calls that don't take focus | `GetHierarchy`, `CreateNewPage`, `UpdatePageContent`, `OpenHierarchy` (existing notebook), `GetHyperlinkToObject` | works; foreground unchanged | focus / `*-focus` | Also no focus change: `OpenHierarchy` creating a new notebook, and cftSection (search-indexed / `open-indexed-notebook-focus`, `create-indexed-section-focus`). Not measured: `Publish`, `DeleteHierarchy`, `FindPages` with `fDisplay=true` |
| 26 | Hyperlink to an object | `GetHyperlinkToObject` | works, `onenote:` URL | write / `get-hyperlink` | |
| 27 | Guard refuses foreign targets | `Invoke-GuardedWrite` | pass: refused before any COM call (page, notebook, empty ID, path outside the spike roots) | write / `guard-*` | The guarded code throws if it's ever reached; it never was |
| 28 | Search right after a write, indexed only | `FindPages(..., fIncludeUnindexedPages=false)` | **unreliable**, in run order: run 1 (about 12:51–12:56) missed for over 300 s, but its log was lost (L2; console output only); run 2 (12:56, 60 s window) found it at 0 s, logged; run 3 (12:57–13:02) missed for over 300 s, logged; **run 4, in the crawl-scope notebook `MCP Spike Indexed`** (14:26), missed at 0 s and **found at the first poll, 15.1 s**, logged | search / `search-immediate-indexed-only`, `search-poll-indexed-only`, `search-summary`; run 4: search-indexed / same names | WSearch was running. Runs 1-3 used `MCP Spike` under `%LOCALAPPDATA%`, which the crawl rules exclude; run 4's location is covered by the `file:///C:\Users\` include rule. The index also has a OneNote root (`ONEINDEX16://`). **Location probably matters**, but this rests on n=1 in scope, and run 2 found an excluded-location page at 0 s, so the mechanism isn't proven. The 15 s figure is bounded by the poll interval |
| 29 | Search including unindexed pages | `FindPages(..., true)` | finds the page at 0 s, but **the result isn't filtered** | search / `search-immediate-include-unindexed`, `search-common-word-root-unindexed-True`, `search-spike-page-count` | 8 hits for a unique token = all 8 pages in the spike notebook, **including the 3 in its recycle bin** (8 pages, 5 outside the bin). 213 at the root against 136 for indexed-only on a common word |
| 30 | Search with an empty query | `FindPages` | fails 0x80042019 hrInvalidQuery | search / `search-invalid-query` | |
| 31 | Search from a nonexistent start ID | `FindPages` | fails 0x80042014 hrObjectDoesNotExist | search / `search-bad-start-id` | *Observed, not logged:* `GetHierarchy` gave the same code (U5) |
| 32 | Real notebooks untouched | before/after `lastModifiedTime` snapshot | no change detected: 35 notebooks and sections, in both comparisons (13:07:30 and 13:50:08) | baseline / `untouched-check` (both rows) | See "AC3: real notebooks untouched" |

**About the JSONL `verdict` field.** `verdict: works` means the call returned without an error, not that the capability works. Read the detail fields instead. For example, `search-poll-indexed-only` rows log `works` with `targetFound: false`, and in `search.jsonl` the `search-summary` row logs `works` when *either* mode found the page. The tables above give the capability result.

### AC3: real notebooks untouched

Evidence, strongest first:
1. Every logged write went through `Invoke-GuardedWrite`, and every logged target is inside a spike notebook (write, search, failures and focus logs). The four `guard-*` refusals show it rejecting foreign targets.
2. Phill confirmed on 2026-11-03, in the main session, that he didn't edit any of his own notebooks during the spike.
3. The `lastModifiedTime` comparison found 0 of 35 changed, at 13:07:30 and again at 13:50:08, which was after `05-focus`, every `04-failure-modes` scenario (including the OneNote kill and the Shift+F9 sync) and the 13:45 host calls. This is **only a weak signal**, because `lastModifiedTime` can lag COM edits (rows 18 and 21).

Gaps: the writes in the lost 02-write runs (L1) and the ad hoc scripts (U1–U6) aren't logged; both targeted spike pages only. On this basis, AC3 is closed.

### NavigateTo and keyboard focus

`NavigateTo` brings OneNote to the foreground and takes keyboard focus (focus / `NavigateTo-focus`: foreground went from Code to ONENOTE). During the 12:48–12:50 write probes, it ran 3 times (logged) plus 2 ad hoc, and Phill's keystrokes landed in the MCP Spike notebook. No real notebook was touched (see AC3). The displayed-page result (row 17) was then re-run hands-off in `05-focus.ps1`. It also changes concurrency behaviour, because a displayed page tracks `lastModifiedTime` and an undisplayed one doesn't (rows 17/18). Constraint 9 follows.

## Binding and host-runtime findings

| # | Client | Result | Evidence | Cause (inferred) |
|---|---|---|---|---|
| B1 | Windows PowerShell 5.1, `New-Object -ComObject` | works | every PS probe | .NET Framework finds the registered PIA and calls through the vtable |
| B2 | pwsh 7.6.9, same call | **fails 0x80004005 E_FAIL**: it can't late-bind | read / `host-pwsh7-latebinding` | .NET late-binds through IDispatch, and `GetTypeInfo` fails (B3). Typed interop from pwsh 7 was not tested |
| B3 | pywin32 dynamic / makepy / IDispatch | **fails**: `GetTypeInfo` E_FAIL; `GetIDsOfNames` and Invoke 0x8002801d TYPE_E_LIBNOTREGISTERED | host / `pywin32-*` | The OneNote 15 type library `{0EA692EE-…}` 1.1 is registered under `Win32` only, with no `win64` key (host / `typelib-registration`), so OneNote's IDispatch can't load it for this (inferred 64-bit) process |
| B4 | comtypes 1.4.17, vtable through `GetModule(typelib)` | works | host / `comtypes-vtable` | |
| B5 | C# .NET 10, GAC interop `Microsoft.Office.Interop.OneNote` 15.0.0.0, vtable | works | host / `csharp*` | The interop can't be embedded (CS1756), so the DLL is copied to the output |

Not tested: C# `dynamic` (B2 predicts it fails the same way), and a hand-declared `[ComImport]` IApplication.

## Failure modes

| Scenario | Result | Evidence |
|---|---|---|
| Write to a foreign ID through the guard | pass: refused before any COM call | write / `guard-*` |
| Stale-date edit | Displayed page: rejected 0x80042010. Not displayed: accepted (rows 17/18) | write, focus |
| A hung COM call | **works (logged):** two real COM calls hung under the elevated scenario. The 30 s timeout abandoned the worker both times and the runner continued | failures / `warmup-read` (13:39:08, 30018 ms), `elevated-read` (13:40:35, 30010 ms) |
| Transient busy | **observed, not logged:** 0x80010001 RPC_E_CALL_REJECTED once on an `UpdatePageContent` (U3) | none in JSONL |
| OneNote killed | Closing the window didn't end ONENOTE.EXE (observed, not logged), so Phill ended it in Task Manager (`processGone: true`). The existing COM object then **fails 0x800706ba RPC_S_SERVER_UNAVAILABLE** (12 ms). A fresh `New-Object` **starts OneNote in 511 ms** as a windowless `ONENOTE.EXE -Embedding` (process details observed, not logged), and read (9 ms) and write (71 ms) against it work | failures / `onenote-closed-*` (13:24:49–50) |
| Launch blocked after the kill | **Observed, not logged.** While the probe still held its COM reference, every normal launch Phill tried hung ("cleaning up from the last time it was open", then "having trouble starting"; PIDs 5568, 26444). It cleared after the probe exited and the stuck launcher was ended. **Cause inferred, not proven:** an unclean kill can trigger OneNote's recovery on its own, so the held reference may not be what blocked it | main and Phill |
| Probe elevated, OneNote not | **Hangs rather than failing fast.** The warmup `GetHierarchy` timed out at 30 s. A fresh `New-Object` then failed **0x80080005 CO_E_SERVER_EXEC_FAILURE** after 32 s inside the call (about 57 s wall time). The follow-up read timed out at 30 s. The write step started, but its guard never completed and no `UpdatePageContent` was issued: Phill stopped the runner with Ctrl+C, so no write row was logged. Phill saw the OneNote window come to the front (observed, not logged) | failures / `warmup-read` (`elevated: true`), `elevated-activation`, `elevated-read` |
| Modal dialog open | Read works (5 ms). **Write fails 0x80042030 hrAppInModalUI** (19 ms, no hang) | failures / `modal-dialog-*` |
| Sync in progress | Read (215 pages, 40 ms) and write (8 ms, spike page) both work. The log can't prove a sync was running at the moment of the call, and the write went to the local spike notebook | failures / `sync-read`, `sync-write` |
| Password-protected section, locked | `locked="true"`, `encrypted="true"`, **0 pages listed**. `GetHierarchy(section, hsPages)` works (empty). **`CreateNewPage` fails 0x8004201b hrSectionEncryptedAndLocked** | failures / `locked-section-*` |
| Password-protected section, unlocked | `encrypted="true"`, no `locked` attribute, 2 pages. Hierarchy read, `GetPageContent` and write all work | failures / `unlocked-section-*` |
| Parallel tool calls | Works in both hosts: 2 and 8 concurrent calls all return, queued on one STA thread | host / `parallel-2`, `parallel-8` |

## HRESULTs observed

| HRESULT | Name | Where | Logged? |
|---|---|---|---|
| 0x80042010 | hrLastModifiedDateDidNotMatch | stale-date write, page displayed | yes |
| 0x8004200f | hrBinaryObjectDoesNotExist | `GetBinaryPageContent` on a cloud-notebook image | yes |
| 0x8004201a | hrFileAlreadyExists | `Publish` to an existing file | yes |
| 0x80042019 | hrInvalidQuery | `FindPages`, empty query | yes |
| 0x80042014 | hrObjectDoesNotExist | `FindPages` bad start ID (`GetHierarchy`: U5, not logged) | yes |
| 0x8004201b | hrSectionEncryptedAndLocked | `CreateNewPage` in a locked section | yes |
| 0x80042030 | hrAppInModalUI | `UpdatePageContent` with a modal dialog open | yes |
| 0x80080005 | CO_E_SERVER_EXEC_FAILURE | activation, server elevated and OneNote not | yes |
| 0x800706ba | RPC_S_SERVER_UNAVAILABLE | stale COM object after OneNote was killed | yes |
| 0x80004005 | E_FAIL | pwsh 7 late binding; `IDispatch::GetTypeInfo` | yes |
| 0x8002801d | TYPE_E_LIBNOTREGISTERED | pywin32 `GetIDsOfNames` / Invoke | yes |
| `timeout` | probe outcome, not an HRESULT | the runner abandoned the call after 30 s (elevated scenario) | yes |
| 0x80010001 | RPC_E_CALL_REJECTED | one `UpdatePageContent` (U3) | observed, not logged |

`OneNoteProbe.psm1` has the full OneNote list (0x80042000–0x80042031), copied from learn.microsoft.com "Error codes (OneNote)" on 2026-11-03. Take the names from that page: a list from memory had them shifted by one code from 0x80042010 onward.

## CLAUDE.md claims checked

These are the claims in CLAUDE.md's "OneNote COM constraints the design has to respect" section. Main drafts any CLAUDE.md changes.

| Claim | Status | Evidence |
|---|---|---|
| COM exists in OneNote desktop (M365) | **held** | the whole matrix |
| The UWP "OneNote for Windows 10" has no COM | untested | the app isn't installed |
| OneNote starts on demand when the COM object is created | **held**, with a nuance: it starts windowless (`-Embedding`) | failures / `onenote-closed-activation` |
| Reads return XML in the 2013 schema namespace | **held** | row 3 |
| Objects are addressed by ID, with name→ID resolution on top of `GetHierarchy` | **held** | rows 2, 19, 27 (IDs don't embed the parent; soft delete changes a page's ID) |
| `UpdatePageContent` / `DeleteHierarchy` fail if the page changed since the given date | **contradicted** for pages not displayed (the date still matches, so the stale write goes through); held for displayed pages | rows 17, 18, 21 |
| COM calls must run on an STA thread | **held as practice**: STA worked in every client; MTA wasn't tested | read / `activation`, host / `*` |
| Server and OneNote must run at the same integrity level | **held**, but the failure is a hang followed by 0x80080005, not a fast activation error | failures / elevated rows |
| A missing or bad registration shows as REGDB_E_CLASSNOTREG 0x80040154 | **untested** (registration is present). The binding failures actually seen were 0x8002801d and E_FAIL, from a type library registered for Win32 only | B2, B3 |
| OneNote HRESULTs 0x8004200x should be mapped | **held**; the range is 0x80042000–0x80042031 | HRESULT table |
| `FindPages` depends on Windows Search and can be incomplete | **held**: incomplete, and lagging even in a crawl-scope location; the location dependence is likely, not proven | rows 28, 29 |
| *Missing from CLAUDE.md:* late binding fails | new constraint | B2, B3, constraint 1 |

## Host comparison (Stack)

Both hosts are minimal stdio MCP servers with one tool, `get_hierarchy`, and one dedicated STA thread that owns the COM object, fed by a work queue. I drove them with `spikes/host-python/mcp_check.py`, an MCP client on the `mcp` 2.3.0 SDK, logged to `host.jsonl`.

| | C# (`spikes/host-csharp`) | Python (`spikes/host-python`) |
|---|---|---|
| SDK (checked 2026-11-03) | ModelContextProtocol 2.3.1 (latest stable on nuget.org), Microsoft.Extensions.Hosting 10.0.14 | mcp 2.3.0 (latest on PyPI; `FastMCP` is renamed `MCPServer` in 2.x), comtypes 1.4.17, pywin32 312 |
| COM binding | GAC interop: typed enums, `out string`, `COMException.HResult` | comtypes bindings generated from the type library at runtime; pywin32 doesn't work (B3) |
| STA handling | `Thread.SetApartmentState(STA)` + `BlockingCollection<Action>` + `TaskCompletionSource`, about 30 lines | `threading.Thread` + `CoInitializeEx(APARTMENTTHREADED)` + `queue.Queue` + `concurrent.futures.Future`, about 35 lines |
| Total server code | 86 lines | 92 lines |
| tools/list after spawn | 264 ms (built exe); 1.4 s through `dotnet run` | 693 ms (`uv run`) |
| Single call | 36–57 ms round trip | 377 ms the first time (COM object created on first use) |
| Parallel 2 / 8 (8 × 300 ms hold) | 2/2, 8/8, one STA thread, 2.5 s wall (serialised) | 2/2, 8/8, one STA thread, 2.4 s wall (serialised) |
| Gotchas | The interop can't be embedded; `dotnet run` needs the full environment or MSBuild errors land on stdout and break the stream | Python resolves to 3.14 without `.python-version`; the comtypes cache is written into the venv |

**Called from Claude Code (AC4), about 13:45. Observed, not logged** (recorded by main; no JSONL row). Main registered both hosts at local scope:
- `claude mcp add --scope local spike-csharp -- <built HostCsharp.exe>`
- `claude mcp add --scope local spike-python -- uv run --project spikes/host-python spikes/host-python/server.py`

`claude mcp list` showed both Connected. A headless `claude -p` run called each tool once, and both returned 5 notebooks. Both registrations were removed afterwards. The logged equivalent is the stdio checks in `host.jsonl`.

**Recommendation: C#. This is a judgment, not a measurement:** both hosts met every measured bar.
1. **The binding is typed, with no runtime code generation.** Python only works through comtypes, a third-party layer that generates bindings from a type library this machine registers incompletely.
2. **STA is a first-class runtime feature.** `SetApartmentState` plus a queue is the standard pattern.
3. **Startup and distribution.** A built exe answered tools/list in 264 ms and can be published self-contained.

**Binding decision: a hand-declared `[ComImport]` IApplication, not the GAC PIA.** It removes the dependency on how Office was installed. The cost is that the interface has to match OneNote's vtable order; the type library is the source for that. This route isn't tested yet; the spike used the PIA. With no PIA shipped, PIA redistribution is moot; if the hand-declared route fails, that question reopens.

## Not probed

| Area | Reason |
|---|---|
| `DeletePageContent` | Not in the plan's probe list; it's the likely primitive for removing an element |
| `CloseNotebook` | Would change Phill's open-notebook list; not needed for the spike |
| `SyncHierarchy` | A forced sync of a cloud notebook would touch real notebooks |
| `GetSpecialLocation` | Not needed to answer the spike question |
| `FindMeta` | Not in the plan; metadata search is a later feature |
| `UpdateHierarchy` move / reorder | Not in the plan; only create and rename were probed |
| Section groups (create, nest, move) | Not in the plan |
| `GetPageContent` piAll / piSelection | Only piBasic and piBinaryData were probed (piAll seen ad hoc only) |
| `DeleteHierarchy` on a whole notebook | Not in the plan, and risky to Phill's notebook list |
| `OpenHierarchy` on a real notebook path | Real notebooks are read-only in this spike |
| Attachments (`InsertedFile`) read and insert | Not probed; constraint 5 covers images only |

## Deviations from the plan's read-only list

The plan allows only `GetHierarchy` and `GetPageContent` against real notebooks. Two other read-only calls were used on them:
- `GetBinaryPageContent`, once, on one image in the largest real notebook (`01-read.ps1`, read / `binary-page-content`).
- Root-scope `FindPages` across all notebooks (`03-search.ps1`, `search-immediate-root-include-unindexed`, `search-common-word-root-unindexed-*`), logging hit counts only.

## Evidence gaps

**Lost logs:**
- **L1, two lost 02-write runs** (about 12:44–12:47, before the logged run at 12:49:59). I deleted `write.jsonl` before the clean re-run. They targeted the spike notebook only. Trace:
  - the logged run's `delete-page-recycle-check` found 2 pages and 2 sections already in the recycle bin;
  - their Publish output is still on disk: `%LOCALAPPDATA%\OneNoteMcpSpike\publish\page-20261103-124456.{pdf,docx,mht}` and `page-20261103-124712.{pdf,docx,mht}`, next to the logged run's `page-20261103-124959.*`.
- **L2, lost search run 1** (about 12:51–12:56). Its log was overwritten while I edited the script. Its console output showed indexed-only not found after 300 s.

**Observed, not logged:** the launch blocked after the kill, the process details in "OneNote killed", the OneNote window coming forward in the elevated run, U3, U6, and AC4.

**Ad hoc scratch scripts** (12:44–12:50, outside `spikes/`, spike-notebook pages only, no JSONL). No matrix row relies on them alone.
- U1: `MinValue` disables the date check; an exact UTC match is accepted (the accepted part is re-confirmed in focus / `lmt-displayed-edit`).
- U2: a local-kind `DateTime` holding the current value was rejected 0x80042010.
- U3: one 0x80010001 RPC_E_CALL_REJECTED on an `UpdatePageContent`.
- U4: on an older undisplayed page, `lastModifiedTime` moved backwards while the section's advanced.
- U5: `GetHierarchy` with a bogus ID returned 0x80042014.
- U6: the timeout wrapper abandoned a sleeping worker at 2 s and recovered.
- Two `NavigateTo` calls at about 12:48:30 and 12:49:19.
