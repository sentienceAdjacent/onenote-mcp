---
title: 'Findings: what can the OneNote desktop COM API do on this machine?'
ticket: '1'
status: 'draft'
created: '2026-11-03'
machine: 'ONENOTE.EXE 16.0.20527.20112 (M365 Click-to-Run, 64-bit), Windows 11 Pro 10.0.26300, not elevated'
evidence: 'spikes/results/*.jsonl'
---

# Findings: what can the OneNote desktop COM API do on this machine?

**Short answer:** the COM API works on M365 OneNote here. Reads, writes, deletes, search, Publish and NavigateTo all work through `OneNote.Application`. Four results change the architecture:

1. **Late binding (IDispatch) is broken for 64-bit clients.** Calls have to go through the vtable, using a typed interop or comtypes. pywin32 and pwsh 7 can't call OneNote at all.
2. **The last-modified check can't be trusted on its own.** A page's `lastModifiedTime` stops tracking COM edits once the page isn't the one shown in OneNote, so a stale date can match and the write goes through.
3. **`FindPages` over the Windows Search index is unreliable.** In indexed-only mode, one logged run didn't find a fresh page within 300 s, one logged run found it at 0 s, and a third run (log lost) also missed it. `fIncludeUnindexedPages=true` finds it immediately, but it also returns every unindexed page, so the caller has to filter the results.
4. **Both hosts work. I recommend C#.**

Where the evidence lives: each row below cites the probe name. Search for that name in `spikes/results/<log>.jsonl`. Probe code is in `spikes/com-capabilities/` (PowerShell), `spikes/host-csharp/` and `spikes/host-python/`. The spike notebook is at `%LOCALAPPDATA%\OneNoteMcpSpike\MCP Spike`, with Publish output in `%LOCALAPPDATA%\OneNoteMcpSpike\publish\`.

**Human edit during the spike:** between 12:48 and 12:50, `NavigateTo` brought OneNote to the foreground and Phill's keystrokes went into the MCP Spike notebook. Phill confirmed no real notebook was touched; row 32 gives the full evidence and its limits. The displayed-page result in row 17 was re-run hands-off in `05-focus.ps1`.

**Rows still open:** none in the failure-mode table; Phill ran every `04-failure-modes.ps1` scenario. Still open outside it: the Fable review. Host registration in Claude Code is done (AC4, see Stack).

## Capability matrix

| # | Capability | Method | Result | Evidence (log / probe) | Notes |
|---|---|---|---|---|---|
| 1 | Create the COM object, OneNote running | `New-Object -ComObject` | works, 9–23 ms | read / `activation` | Runs on an STA worker runspace |
| 2 | Hierarchy, every scope from the root | `GetHierarchy` hsSelf/Children/Notebooks/Sections/Pages | works, 2–44 ms | read / `hierarchy-root-*` | Full tree (4 notebooks, 31 sections, 205 pages): 57 KB in 27–44 ms |
| 3 | Schema namespace | `GetHierarchy` with xs2010 / xs2013 | works | read / `hierarchy-schema-*` | The default and xs2013 both return `http://schemas.microsoft.com/office/onenote/2013/onenote`; xs2010 returns the 2010 namespace |
| 4 | Largest real notebook (143 pages, 13 sections, 2 locked) | `GetHierarchy(nb, hsPages)` | works, 6 ms, 37 KB | read / `largest-notebook-hierarchy` | Read-only |
| 5 | Page content, basic | `GetPageContent` piBasic | works, 25/25, 6–128 ms (avg 22.5) | read / `page-content-basic`, `largest-notebook-summary` | Real notebook, read-only, counts only |
| 6 | Page content with binary, **cloud notebook** | `GetPageContent` piBinaryData | call works, 25/25, avg 21 ms, **but images are missing** | read / `page-content-binary` | Every notebook here is a cloud (OneDrive URL) notebook. In piBinaryData output, image elements without a local binary are dropped; 4 of the 25 sampled pages had images (piBasic counts 2, 3, 1, 1), and piBinaryData returned 0 images for all four |
| 7 | Binary by callback ID, **cloud notebook** | `GetBinaryPageContent` | **fails 0x8004200f hrBinaryObjectDoesNotExist** | read / `binary-page-content` | The image isn't cached locally. An MCP server can't assume it will get image bytes from cloud notebooks |
| 8 | Image insert + binary read, **local spike notebook** | `UpdatePageContent` + `GetPageContent` piBinaryData + `GetBinaryPageContent` | works | write / `image-insert`, `spike-page-content-*`, `spike-binary-page-content` | piBasic gives a CallbackID, piBinaryData gives inline `one:Data`. OneNote re-encodes the PNG, so the bytes don't round-trip |
| 9 | Create a notebook at a path | `OpenHierarchy` cftNotebook | works, 22–38 ms | write / `open-spike-notebook` | Reopening an existing path returns the same ID |
| 10 | Create a section | `UpdateHierarchy` (Notebook root + new Section) | works, 12–19 ms | write / `section-create-updatehierarchy`, `section-create-verify` | Other sections are kept; a partial Notebook XML doesn't delete siblings |
| 11 | Create a section | `OpenHierarchy` cftSection | works, 45 ms | write / `section-create-openhierarchy` | |
| 12 | Rename a section | `UpdateHierarchy` (Section as XML root) | works, 2–6 ms | write / `section-rename-*` | The ID stays the same |
| 13 | Create a page | `CreateNewPage` | works, 1–43 ms | write / `page-create` and `*-setup` | |
| 14 | Append an outline, no date | `UpdatePageContent` | works | write / `append-outline-nodate` | |
| 15 | Append an outline, with the current date | `UpdatePageContent(xml, date)` | works | write / `append-outline-withdate` | The date must be **UTC** (see 17) |
| 16 | Edit existing text by `objectID`, with or without a date | `UpdatePageContent` | works | write / `edit-text-withdate-utc`, `edit-text-nodate`, `edit-verify` | |
| 17 | Stale-date conflict, **page displayed** | `UpdatePageContent(xml, staleDate)` | **pass: rejected 0x80042010 hrLastModifiedDateDidNotMatch** | focus / `lmt-displayed-stale-write`, `lmt-displayed-trace` (the re-run, hands-off, fresh read before every step); also write / `lmt-trace-displayed-stale-write` (that run may have been confounded by stray typing) | On a displayed page, `lastModifiedTime` advanced on all 3 COM edits, and each edit with a freshly read UTC date was accepted. The check is an exact match against the page's UTC `lastModifiedTime`. *Unlogged:* a local-kind date was rejected even when its value was current (see U2) |
| 18 | Stale-date conflict, **page not displayed** | `UpdatePageContent(xml, staleDate)` | **fails: the stale write is applied** | write / `edit-stale-date`, `edit-stale-verify`, `lmt-trace-not-displayed*` | The page's `lastModifiedTime` stayed frozen across 3 COM edits (`lmtAdvances: 0`), so the "stale" date still matched. With the page displayed, all 3 edits advanced it. *Unlogged:* on an older page not displayed, the value even moved backwards, while the section's `lastModifiedTime` advanced (see U4) |
| 19 | Soft delete of a page | `DeleteHierarchy` (default) | works; the page goes to the recycle bin **under a new ID** | write / `delete-page-default`, `delete-page-recycle-check` | Original ID gone; recycle-bin page count +1 |
| 20 | Permanent delete of a page | `DeleteHierarchy(id, date, true)` | works; gone, not in the recycle bin | write / `delete-page-permanent`, `delete-permanent-check` | |
| 21 | Delete with a stale date (page not displayed) | `DeleteHierarchy(id, staleDate, true)` | **fails: deleted anyway** | write / `delete-stale-date`, `delete-stale-check` | Same cause as 18 |
| 22 | Soft delete of a section | `DeleteHierarchy` (default) | works; the section keeps its ID with `isInRecycleBin="true"` | write / `delete-section-*` | |
| 23 | Publish to PDF / docx / MHTML | `Publish` | works: 95 ms / 2.9 s / 20 ms | write / `publish-pdf`, `publish-docx`, `publish-mht` | docx is slow |
| 24 | Publish over an existing file | `Publish` | pass: rejected 0x8004201a hrFileAlreadyExists | write / `publish-existing-file` | |
| 25 | NavigateTo a page | `NavigateTo` | works, **and brings OneNote to the foreground, taking keyboard focus** (foreground: Code → ONENOTE) | write / `navigate-to-page`; focus / `NavigateTo-focus` | During the 12:48–12:50 probes this let Phill's keystrokes land in the spike notebook. The calls in row 25a don't take focus |
| 25a | Calls that don't take focus | `GetHierarchy`, `CreateNewPage`, `UpdatePageContent`, `OpenHierarchy` (existing notebook), `GetHyperlinkToObject` | works; foreground unchanged (Code → Code) | focus / `*-focus` | Not measured: `OpenHierarchy` creating a *new* notebook, `Publish`, `DeleteHierarchy`, `FindPages` with `fDisplay=true` |
| 26 | Hyperlink to an object | `GetHyperlinkToObject` | works, `onenote:` URL | write / `get-hyperlink` | |
| 27 | Guard refuses foreign targets | `Invoke-GuardedWrite` | pass: refused before any COM call (page, notebook, empty ID, path outside the spike root) | write / `guard-*` | The guarded code block throws if it's ever reached; it never was |
| 28 | Search right after a write, indexed only | `FindPages(..., fIncludeUnindexedPages=false)` | **unreliable**: 1 logged miss over 300 s (run 3, 12:57–13:02), 1 lost run, also a miss (run 1, lost log; see L2), and 1 logged find at 0 s (run 2, 12:56, 60 s window) | search / `search-immediate-indexed-only`, `search-poll-indexed-only`, `search-summary` | WSearch was running. **About `verdict`:** in the JSONL, `verdict: works` means the call returned without an error, not that the capability works. Read the detail fields instead: `search-poll-indexed-only` rows log `works` with `targetFound: false`, and `search-summary` logs `works` when *either* mode found the page (`foundIndexedOnly` or `foundIncludeUnindexed`) |
| 29 | Search including unindexed pages | `FindPages(..., true)` | finds the page at 0 s, but **the result isn't filtered** | search / `search-immediate-include-unindexed`, `search-common-word-root-unindexed-True`, `search-spike-page-count` | Returned 8–9 hits for a unique token, which was every page in the spike notebook, and 213 at the root against 136 for indexed-only on a common word. The caller has to filter the candidates itself |
| 30 | Search with an empty query | `FindPages` | fails 0x80042019 hrInvalidQuery | search / `search-invalid-query` | |
| 31 | Search from a nonexistent start ID | `FindPages` | fails 0x80042014 hrObjectDoesNotExist | search / `search-bad-start-id` | *Unlogged:* GetHierarchy with a bogus ID gave the same code (U5) |
| 32 | Real notebooks untouched | before/after `lastModifiedTime` snapshot | no change detected: 35 notebooks and sections, 0 changed, in both comparisons (after-snapshots at 13:07:30 and 13:50:08; the second comes after `05-focus`, every `04-failure-modes` scenario including the OneNote kill and the Shift+F9 sync, and the 13:45 Claude Code host calls) | baseline / `untouched-check` (both rows) | Evidence for AC3, strongest first: (1) every logged write went through `Invoke-GuardedWrite` and every logged target is inside the spike notebook (write, search, failures and focus logs; the four `guard-*` refusals show it refusing foreign targets); (2) Phill confirmed on 2026-11-03, in the main session, that he didn't edit any of his own notebooks; (3) the `lastModifiedTime` comparison, which is **only a weak signal** because this spike shows `lastModifiedTime` can lag COM edits (rows 18 and 21). Gaps: the write calls in the lost 02-write runs (L1) and the ad hoc scripts (U1–U6) aren't logged; both targeted spike pages only. On that basis, ticket AC3 is closed |

## Binding and host-runtime findings

| # | Client | Result | Evidence | Cause (inferred) |
|---|---|---|---|---|
| B1 | Windows PowerShell 5.1, `New-Object -ComObject` | works | every PS probe | .NET Framework finds the registered PIA and calls through the vtable |
| B2 | pwsh 7.6.9, same call | **fails 0x80004005 E_FAIL** | read / `host-pwsh7-latebinding` | .NET (Core) late-binds through IDispatch, and `GetTypeInfo` fails (B3) |
| B3 | pywin32 dynamic / makepy / IDispatch | **fails**: `GetTypeInfo` E_FAIL, `GetIDsOfNames` and Invoke 0x8002801d TYPE_E_LIBNOTREGISTERED | host / `pywin32-*` | The OneNote 15 type library `{0EA692EE-…}` 1.1 is registered under `Win32` only, with no `win64` key (host / `typelib-registration`). OneNote's IDispatch can't load its own type library for 64-bit callers |
| B4 | comtypes 1.4.17, vtable through `GetModule(typelib)` | works | host / `comtypes-vtable` | |
| B5 | C# .NET 10, GAC interop `Microsoft.Office.Interop.OneNote` 15.0.0.0, vtable | works | host / `csharp*` | The interop can't be embedded (CS1756: IApplication lacks `ComImport`), so the DLL is copied to the output |

Not tested: C# `dynamic`. B2 predicts it fails the same way.

## Failure modes

| Scenario | Result | Evidence |
|---|---|---|
| Write to a foreign ID through the guard | pass: refused before any COM call | write / `guard-*` |
| Stale-date edit | Displayed page: rejected 0x80042010. Not displayed: accepted (see 17/18) | write |
| A hung COM call | **works (logged):** two real COM calls hung under the elevated scenario and were cut off by the 30 s timeout. In both, the worker was abandoned and the runner went on to its next step | failures / `warmup-read` (13:39:08, 30018 ms, "worker abandoned"), `elevated-read` (13:40:35, 30010 ms, "worker abandoned"). Also U6 (observed, not logged) |
| Transient busy | **observed, not logged:** 0x80010001 RPC_E_CALL_REJECTED seen once on an `UpdatePageContent` (U3) | none in JSONL |
| OneNote closed | **Closing the window doesn't end ONENOTE.EXE**; Phill had to end it in Task Manager (observed by main; then `processGone: true`). Afterwards the existing COM object **fails 0x800706ba RPC_S_SERVER_UNAVAILABLE** (12 ms). A fresh `New-Object` **starts OneNote in 511 ms** as a windowless `ONENOTE.EXE -Embedding` (parent: DCOM launcher), and read (9 ms) and write (71 ms) against it work | failures / `onenote-closed-process`, `onenote-closed-stale-object-read`, `onenote-closed-activation`, `onenote-closed-read`, `onenote-closed-write`; process details observed by main |
| Headless instance blocks the user | **While the probe still held its COM reference, every normal launch Phill tried hung** at "OneNote is cleaning up from the last time it was open", then "OneNote is having trouble starting". The stuck launchers piled up (PIDs 5568, 26444) while the `-Embedding` instance sat idle (about 2 s CPU in total). It cleared only once the probe exited: the reference was released, the `-Embedding` instance went away, and the stuck launcher was ended. After that OneNote launched normally | **observed, not logged** (main and Phill); timeline matches the failures rows at 13:24:49–50 |
| Probe elevated, OneNote not | **Hangs rather than failing fast.** The warmup `GetHierarchy` timed out at 30 s and the worker was abandoned. A fresh `New-Object` then **failed 0x80080005 CO_E_SERVER_EXEC_FAILURE** after 32 s inside the call (about 57 s wall time from the warmup timeout). The follow-up read timed out at 30 s too. **The write was attempted:** `Write-Once` runs after `Read-Once` (`04-failure-modes.ps1`, elevated block), and its guard's `GetHierarchy` calls were in flight when Phill stopped the runner with Ctrl+C. No write row was logged. Phill saw the existing OneNote window come to the front at that point (observed, not logged) | failures / `warmup-read` (13:39:08, `elevated: true`), `elevated-activation`, `elevated-read` |
| Modal dialog open | Read works (5 ms). **Write fails 0x80042030 hrAppInModalUI** (19 ms), returned immediately with no hang | failures / `modal-dialog-read`, `modal-dialog-write` |
| Sync in progress | Read (full hierarchy, 215 pages, 40 ms) and write (8 ms, spike page) both work | failures / `sync-read`, `sync-write` | The log can't prove a sync was running at the moment of the call, only that Phill had started one (Shift+F9). The write went to the local spike notebook, not a syncing cloud notebook |
| Password-protected section, locked | The hierarchy shows `locked="true"` and `encrypted="true"` with **0 pages listed**. `GetHierarchy(section, hsPages)` works (empty). **Write (`CreateNewPage`) fails 0x8004201b hrSectionEncryptedAndLocked** | failures / `locked-section-state`, `locked-section-read`, `locked-section-write` |
| Password-protected section, unlocked | `encrypted="true"` with no `locked` attribute; 2 pages listed. Hierarchy read, `GetPageContent` and write all work | failures / `unlocked-section-*` |
| Parallel tool calls | works in both hosts: 2 and 8 concurrent calls all return, queued on one STA thread | host / `parallel-2`, `parallel-8` |

## HRESULTs observed

| HRESULT | Name | Where |
|---|---|---|
| 0x80042010 | hrLastModifiedDateDidNotMatch | stale-date write, page displayed |
| 0x8004200f | hrBinaryObjectDoesNotExist | `GetBinaryPageContent` on a cloud notebook image |
| 0x8004201a | hrFileAlreadyExists | `Publish` to an existing file |
| 0x80042019 | hrInvalidQuery | `FindPages` with an empty query |
| 0x80042014 | hrObjectDoesNotExist | bad start ID (`FindPages`, `GetHierarchy`) |
| 0x80004005 | E_FAIL | pwsh 7 late binding; `IDispatch::GetTypeInfo` |
| 0x8002801d | TYPE_E_LIBNOTREGISTERED | pywin32 `GetIDsOfNames` / Invoke |
| 0x80010001 | RPC_E_CALL_REJECTED | observed, not logged (U3) |

`OneNoteProbe.psm1` has the full OneNote list (0x80042000–0x80042031), taken from learn.microsoft.com "Error codes (OneNote)" on 2026-11-03. Note: names I wrote from memory were off by one from 0x80042010 on. Map codes from the doc, not from memory.

## Host comparison (Stack)

Both hosts are minimal stdio MCP servers with one tool, `get_hierarchy`, and one dedicated STA thread that owns the COM object, fed by a work queue. I drove them with `spikes/host-python/mcp_check.py`, an MCP client from the `mcp` 2.3.0 SDK, which logs to `host.jsonl`.

| | C# (`spikes/host-csharp`) | Python (`spikes/host-python`) |
|---|---|---|
| SDK (checked 2026-11-03) | ModelContextProtocol 2.3.1 (nuget.org latest stable, published 2026-11-03), Microsoft.Extensions.Hosting 10.0.14 | mcp 2.3.0 (pypi latest; `FastMCP` is renamed `MCPServer` in 2.x), comtypes 1.4.17, pywin32 312 |
| COM binding | GAC interop, typed enums, `out string`, `COMException.HResult` | comtypes generated from the registered type library at runtime; pywin32 doesn't work (B3) |
| STA handling | `Thread.SetApartmentState(STA)` + `BlockingCollection<Action>` + `TaskCompletionSource`, about 30 lines | `threading.Thread` + `CoInitializeEx(APARTMENTTHREADED)` + `queue.Queue` + `concurrent.futures.Future` / `asyncio.wrap_future`, about 35 lines |
| Total server code | 86 lines | 92 lines |
| tools/list after spawn | 264 ms (built exe); 1.4 s through `dotnet run` | 693 ms (`uv run`) |
| Single call | 36–57 ms round trip | 377 ms the first time (COM object created on first use) |
| Parallel 2 / 8 (8 × 300 ms hold) | 2/2, 8/8; one STA thread; wall 2.5 s (serialised) | 2/2, 8/8; one STA thread; wall 2.4 s (serialised) |
| Gotchas | Interop not embeddable (copy it, or hand-declare a `[ComImport]` IApplication); `dotnet run` needs the full environment or MSBuild errors land on stdout and break the MCP stream | The default Python on this machine resolves to 3.14 without `.python-version`; the comtypes gen cache is written into the venv; pywin32 is a dead end |

**Called from Claude Code (ticket AC4), about 13:45 on 2026-11-03.** Main registered both hosts at local scope:
- `claude mcp add --scope local spike-csharp -- <built HostCsharp.exe>`
- `claude mcp add --scope local spike-python -- uv run --project spikes/host-python spikes/host-python/server.py`

`claude mcp list` showed both as Connected. A headless `claude -p` run then called each tool once. `mcp__spike-csharp__get_hierarchy` and `mcp__spike-python__get_hierarchy` both returned the notebook list (5 notebooks). Both registrations were removed afterwards. **Observed, not logged** (recorded by main; no JSONL row). The logged equivalent is the stdio checks in `host.jsonl`.

**Recommendation: C#.** Reasons:

1. **The COM binding is first-party and typed.** The early-bound interop gives enums, `out` params and HRESULTs through `COMException` with no runtime code generation. Python only works through comtypes, a third-party layer that generates bindings from a type library this machine has registered incompletely.
2. **STA is a first-class runtime feature.** `SetApartmentState` plus a queue is the textbook pattern, and the spike needed nothing extra.
3. **Startup and distribution.** A built exe answered tools/list in 264 ms, and it can be published self-contained, so there's no uv or venv on the user's machine.
4. **Python's real advantages don't outweigh that.** Faster iteration and nicer XML tooling matter, but the binding risk above is the deciding factor for a COM-first server.

The case against C#: it depends on the Office interop assembly in the GAC (present here, but tied to how Office was installed), or on a hand-declared `[ComImport]` interface that has to match the vtable order. The architecture should pick one of those. Python's comtypes route needs only the type library, which comes with OneNote itself.

## Constraints for the architecture (also logged in `architecture-onenote-mcp/.memlog.md`)

1. Bind early through the vtable (interop or `ComImport`). Never late-bind through IDispatch: it fails for 64-bit clients on this M365 build.
2. Don't treat `dateExpectedLastModified` as the only concurrency guard. It's an exact UTC match against a `lastModifiedTime` that doesn't move for COM edits to pages that aren't displayed. Add read-compare-write on content (for example, a hash of the target OE or the page XML), and say plainly that the guarantee is best-effort.
3. Pass dates as UTC `DateTime`s parsed from the XML attribute. UTC acceptance is logged (focus / `lmt-displayed-edit`). That local-kind dates are rejected rests on unlogged evidence only (U2).
4. Search: `FindPages` indexed-only can miss fresh pages for minutes (1 logged miss over 300 s, 1 lost run that also missed, 1 logged find at 0 s). Including unindexed pages returns unfiltered candidates. Plan for post-filtering or a server-side text scan, and say that search may be incomplete.
5. Images and attachments in cloud notebooks may have no local binary (0x8004200f, and missing in piBinaryData). Report "binary unavailable" rather than failing the whole read.
6. A soft-deleted page comes back from the recycle bin under a new ID; a section keeps its ID. Any undo or "restore" feature has to resolve by name or position, not by the old ID.
7. The COM thread must time out hung calls (logged: failures / `warmup-read`, `elevated-read`), and should retry on RPC_E_CALL_REJECTED / RETRYLATER. The retry is standard COM practice; the only local sighting is observed, not logged (U3). One STA thread serialises everything, so a slow call (docx Publish, 2.9 s) blocks every other tool call.
8. `UpdateHierarchy` with a partial Notebook XML adds sections and doesn't delete siblings, and a rename can use the Section as the XML root.
9. `NavigateTo` brings OneNote to the foreground and takes keyboard focus (focus / `NavigateTo-focus`); it also changes how concurrency checks behave. An MCP tool must never call it without the user asking for it explicitly. The reads and writes measured in row 25a don't take focus.

10. Map 0x80042030 hrAppInModalUI to a retryable error the user can act on ("close the open OneNote dialog"). Reads still work while a dialog is open.
11. Check a section's `locked` attribute before writing, and report locked sections as "unlock in OneNote" (0x8004201b). Locked sections list no pages, so reads must show them as locked, not empty.
12. A mismatched integrity level hangs instead of failing fast (CO_E_SERVER_EXEC_FAILURE only after about 30–57 s). The server must run at the same integrity level as OneNote, detect elevation at startup and refuse to run elevated, and put its own timeout on activation as well as on calls.
13. Treat 0x800706ba RPC_S_SERVER_UNAVAILABLE (and RPC_E_DISCONNECTED) as "reconnect": drop the stale object, activate a new one, and retry once.
14. Don't hold the COM reference while idle. Release it after an idle timeout. A held reference to a headless `-Embedding` instance stopped Phill from opening OneNote at all.
15. The server must never kill or close OneNote. Recovery is only ever releasing its own reference.
16. Activating after OneNote has exited or crashed starts a windowless instance (about 0.5 s). The user doesn't see it, so the server shouldn't assume OneNote is visibly running.

## Deviations from the plan's read-only list

The plan allows only `GetHierarchy` and `GetPageContent` against real notebooks. Two other calls were made against them, and both are read-only:
- `GetBinaryPageContent`, once, on one image in the largest real notebook (`01-read.ps1`, read / `binary-page-content`).
- Root-scope `FindPages`, with the start node empty so it covers all notebooks (`03-search.ps1`, `search-immediate-root-include-unindexed` and `search-common-word-root-unindexed-*`). Only hit counts were logged.

## Lost and unlogged evidence

- **L1, lost 02-write runs:** two full `02-write.ps1` runs at about 12:44–12:47 local, before the logged run at 12:49:59. I deleted `write.jsonl` before that clean re-run, so their rows are gone. They targeted the spike notebook only. Trace: the logged run's `delete-page-recycle-check` shows 2 pages and 2 sections already in the spike notebook's recycle bin, which are the soft deletes from those two runs.
- **L2, lost search run 1:** a 300 s `03-search.ps1` run at about 12:51–12:56. Its log was overwritten while I edited the script. Its console output showed indexed-only not found after 300 s.
- **Observed, not logged:** the headless-instance-blocks-user row, U3 (transient busy), U6 (timeout on a sleeping worker), and the AC4 Claude Code registration and tool calls.

## Unlogged ad hoc tests (2026-11-03, 12:44–12:50 local)

While investigating the stale-date result, I ran scratch scripts (outside `spikes/`) against spike-notebook pages only. They wrote nothing to JSONL. **No matrix row relies on them as its only evidence.** Where they add detail, the row is marked *Unlogged*, and any claim that rests on them alone is marked unverified.

- U1: date-argument semantics. `MinValue` disables the check, and UTC exact match is accepted. The accepted part is re-confirmed in focus / `lmt-displayed-edit`.
- U2: a local-kind `DateTime` holding the current value was rejected 0x80042010.
- U3: one 0x80010001 RPC_E_CALL_REJECTED on an `UpdatePageContent` shortly after `GetPageContent`.
- U4: on an older page not displayed, `lastModifiedTime` moved backwards while the section's advanced.
- U5: `GetHierarchy` with a bogus ID returned 0x80042014.
- U6: the timeout wrapper abandoned a sleeping worker at 2 s and recovered.
- Two `NavigateTo` calls at about 12:48:30 and 12:49:19, which were also candidates for the focus theft.
