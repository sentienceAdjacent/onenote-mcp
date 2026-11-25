---
title: 'navigate_to_url'
type: 'feature'
ticket: '7'
created: '2026-11-22'
status: 'built'
route: 'full'
route_source: 'auto'
review: 'full'
baseline_revision: 'f9608a6b6abf7982558eb0f34778cafa3917b09a'
review_source: 'auto'
lenses_ran: ['adversarial', 'edge-cases', 'verification-gaps', 'structure']
review_loop_iteration: 1
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/story-get-windows-and-navigate-to-in-a-chosen-window-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can show an object in OneNote only by ID (`navigate_to`); a `onenote:` link the user pastes, or one `get_hyperlink_to_object` returned, can't be followed, and OneNote's own `NavigateToUrl` would open closed notebooks and loose `.one` files (spike 4.12 N4, N5), block on hidden dialogs (N1, N2) and report ok for a page it never reached (N3).

**Approach:** A `navigate_to_url` tool on story 4.3's `AppStatePipeline` (no lock), as spine AD-7 `AppState` `navigate_to_url` sets it: the link checked before COM against one `GetHierarchy` of the open notebooks, `NavigateToUrl` (or `Window.NavigateToUrl` with a `window` argument) with `fNewWindow` false, then a landing check that reads the navigated window and says what OneNote's current page is.

## Boundaries & Constraints

**Always:**
- Step 1, before COM, every refusal `InvalidArgument` with a `userAction`: not `onenote:` (web links included); `onenote:` whose target isn't `http(s)://` (local, UNC, `///`, fragment-only), its `userAction` "call navigate_to with the object's ID"; control characters, a backslash, a query, a fragment parameter outside OneNote's own set (title, `section-id`, `page-id`, `object-id`, `base-path`, `end`, a number), or a part still changing after four rounds of percent-decoding (as `HtmlStopgap` checks links); an address not inside an open notebook's `NotebookKey` at a segment boundary (host and leading segments equal, case-insensitive, read with `UrlPath`); a `base-path` that isn't that same notebook; a `….one` that isn't the `path` of a section in that notebook; a folder that isn't the notebook itself.
- Link kinds: page (`.one` with `page-id`, `object-id` allowed), section (`.one`, no `page-id`), notebook (the notebook's own folder). A section group's folder is refused, its `userAction` "call navigate_to with the group's ID" (Decision OQ3).
- Step 7: `NavigateToUrl(url, fNewWindow: false)`, or with `window` the window found by handle and its `NavigateToUrl(url)` in one STA call (unknown handle: `InvalidArgument`, nothing sent, as 4.6). `CallKind.AppState`: never retried; a timeout, disconnect, cancel or `0x800706BE` is `outcomeUnknown`. `ComBusy` at the start while an abandoned write is outstanding.
- `0x80042014` stays `ObjectNotFound` with the spine's `userAction` (AD-9): the link may name something gone; if `navigate_to_url` was just called, wait a second and try once more; otherwise ask the user whether OneNote showed a message.
- Landing check, after an ok call, no settle: one windows read; the `window` argument's window, else `CurrentWindow`. Page link: `GetHyperlinkToObject` of that window's page, its `page-id` GUID against the link's; section link: `CurrentSectionId` against the matched section's ID; notebook link: `CurrentNotebookId`. A mismatch, or no current page for a page link: warning `landedElsewhere`. The reads failing, no window, or the `window` gone: warning `landingUnconfirmed`; the call stays ok. A match: warning `landed` (Decision OQ1).
- The result: `meta.windows` holds the one window read (4.6's `WindowListing`, `pageId` withheld under AD-21 as `get_windows` does) and the message says "OneNote's current page is …", never "OneNote shows …".
- User-visible (AD-11): the description says so, promises no focus behaviour and may bring OneNote forward; the skill calls it only when the user asks. Logs carry the link's kind, the refusing rule and the outcome, never the link (AD-13). Contract snapshot checked (AD-14).

**Never:** `fNewWindow` true; a retry; a settle wait; local links (even "safe" ones); a spine change; the write lock; reading or returning page content; setting the integration env var in the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Cloud page link | link from `get_hyperlink_to_object`, page there | ok, landed, window in `meta.windows` | — |
| Missing page | live section, unknown `page-id` (N3) | ok | `landedElsewhere` |
| Locked section's page | N6 | ok, no `pageId` | `landedElsewhere`, its message says a locked section shows no page |
| Section / notebook link | open section / notebook | ok, compared by ID | `landedElsewhere` on mismatch |
| Landing reads fail / no window / window gone | — | ok | can't-confirm warning |
| Local, web, non-`onenote:` link | — | nothing sent | `InvalidArgument` |
| Closed notebook / `.one` not a section / other host / bad `base-path` | — | nothing sent | `InvalidArgument` |
| Unknown `window` | handle no window has | nothing moves | `InvalidArgument` |
| Repeat inside a second | `0x80042014` | — | `ObjectNotFound`, the repeat `userAction` |
| Abandoned write outstanding | — | no COM | `ComBusy` |

## Decisions (Phill, 2026-11-23, Inbox plan approval)

- **OQ1 (a):** the can't-confirm warning is `landingUnconfirmed`; every matched ok carries an informational `landed` warning whose message says "OneNote's current page is …", as `syncRequested` and `notebookClosed` carry their result text.
- **OQ2 (a):** follow the spine: a link into any open cloud notebook's host is accepted (personal OneDrive, business OneDrive, SharePoint).
- **OQ3 (a):** a link to a section group's folder is refused with `InvalidArgument` and a `userAction` to call `navigate_to` with the group's ID.
- **OQ4 (a):** the `Window.NavigateToUrl` gate is step 1 of the story's one live sitting; the window path is built in its own commit so it can be dropped. If the gate fails in the sitting, the build stops and comes back to Phill.
- **OQ5 (a):** the plan is kept whole.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IWindow.cs` slot 21 `NavigateToUrlPlaceholder` -- becomes `void NavigateToUrl([MarshalAs(BStr)] string bstrUrl)` (no new-window flag: OneMore research row 25); `tests/OneNoteMcp.Tests/InteropLayoutTests.cs`.
- `src/OneNoteMcp.Com/Interop/IApplication.cs:94` slot 19 `NavigateToUrl(bstrUrl, fNewWindow)` -- already declared.
- `src/OneNoteMcp.Com/ComGateway.cs:237-390` -- `NavigateToAsync`/`NavigateInWindow`/`ReleaseAll`: generalise `NavigateInWindow` to take the per-window call; method names `NavigateToUrl`, `Window.NavigateToUrl`.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `NavigateToUrlAsync(url, int? window, ct)` as a default `NotSupported` method, as 4.6 did.
- `src/OneNoteMcp.Core/UseCases/NavigateToUseCase.cs` (pattern), `GetWindowsUseCase.cs:121-150` `PageReadableAsync` (move to a shared helper for the AD-21 page withholding), `CloseNotebookUseCase.cs` (one open-notebooks read in step 3), `OpenNotebookUseCase.cs:31-80` (log entry and `Rules`).
- `src/OneNoteMcp.Core/Writes/UrlPath.cs` (`TryParse`, `Readings`), `Access/GrantKeys.cs` (`NotebookKey`, `SectionKey`), `Access/OpenNotebooks.cs`, `Xml/HtmlStopgap.cs:205-260` (`IsAllowedOneNoteLink`, `FullyDecoded`: copy the discipline, not the class; it refuses `base-path`, which cloud page links carry).
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs:2465-2540` `LinkShape` and `ProbeHelpersTests.cs:776-815` -- the measured link shapes (cloud page link: `%7B…%7D` GUIDs and `&base-path=https://…/<notebook>`; notebook link: the folder with a trailing `/`). Test project: copy shapes, not code.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` (new codes), `ToolMeta.cs` (`Windows` reused), `Results/ToolErrors.cs`, `OneNoteHResults.cs`.
- Tool and contract: new `Server/Tools/NavigateToUrlTool.cs` (from `NavigateToTool.cs`), `ServerSetup.cs`, `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `ToolContractTests.cs`, `McpSdkBindingTests.cs`, `PluginPackagingTests`; `FakeGateway.cs`, `FakeApplication.cs:119,273`.
- Docs: `plugin/skills/guide/SKILL.md`, `README.md`, `spikes/stress-harness/Calls.cs`, `epic-read-only-release/research-onemore-lessons.md` row 24 (new: OneMore navigates by ID, not by link, because the link path shows a "searching for section" UI on a first jump and can fail for local notebooks: `Commands/Navigator/NavigatorWindow.cs`, `Commands/Tools/WhereAmIWindow.cs`; it never calls `Window.NavigateToUrl`; read 2026-11-22 with `gh search code`).

## Tasks & Acceptance

**Execution:**
- [x] `Core/AppState/CloudLink.cs` (new) -- parse and classify a link; match it to the open notebooks and sections from one hierarchy XML; pure, so unit-tested on strings.
- [x] Port, `ComGateway` (one STA delegate per path, wrappers released in `finally`), `IWindow`, `FakeGateway`, `FakeApplication` -- `NavigateToUrlAsync`. The window path is its own commit, so it can come out if the sitting's gate fails.
- [x] `Core/UseCases/NavigateToUrlUseCase.cs` -- step 1 parse; step 3 the open-notebooks read (`GetHierarchy("", Sections)`) and match; step 7 the call; then the landing check outside the pipeline (Design Notes); log entry; warnings.
- [x] `Server/Tools/NavigateToUrlTool.cs`, `ServerSetup.cs` -- `navigate_to_url(url, window?)`; annotations as `navigate_to`.
- [x] Unit tests (`CloudLinkTests`, `NavigateToUrlToolTests`, `ComGatewayLifecycleTests`) -- every matrix row, plus: percent-encoded and plain notebook paths, a segment-boundary near miss (`MCP Test Cloud 2`), a section in a section group, a `base-path` into another notebook, double-encoded parts, no COM call on any refusal (FakeGateway records none), wrappers released, `fNewWindow` false.
- [x] Contract snapshot, tool list, binding count; skill recipe (follow a pasted link only when the user asks; local links go through `navigate_to`), README, stress-harness rows, research note row 24.

**Acceptance Criteria:**
- Given a link refused in step 1, when the tool returns, then the log line holds the kind and rule and no part of the link.
- Given a page link whose landing matches, when the tool returns, then the result names the window's notebook, section and page IDs (page withheld without the read grant) and says "OneNote's current page is …".

## Design Notes

- The landing check runs after the pipeline returns ok, not inside step 7: the navigation's outcome is known by then, so a read that times out must not turn the call into `outcomeUnknown`. Its reads are `CallKind.Read` with the call timeout; any failure gives the can't-confirm warning.
- Matching reads the link once, decoded once, and requires the four-round decode to agree; the notebook's `path` is compared in both its reported (unencoded) and `UrlPath` forms, as AD-21's Web addresses rule reads it.
- The window path (`window` argument, `IWindow.NavigateToUrl`) is its own commit (Decision OQ4).
- Considered combining the call and the landing read in one STA delegate (no gap for the user to move); going with two calls because a failed read would otherwise be indistinguishable from a failed navigation.

## Live sitting (Phill, about 15 minutes; hitl)

Before: OneNote open on an MCP Test Cloud page, VS Code in front. The build session stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build. Claude makes the MCP Test Cloud pages for steps 4 and 7 beforehand and checks it with Sync Now.

1. **The gate (Window.NavigateToUrl).** Phill opens a second window (Ctrl+M) on another page. Claude lists the windows, then follows a cloud page link in the second window. Phill confirms only that window moved (pointing at OneNote if it hasn't repainted, N1 C). If it fails or moves the wrong window, the build stops and comes back to Phill before going on (ticket, Phill 2026-11-22).
2. With both windows open, Claude follows another link with no `window`. Phill says which window moved; the result should name that one (checks the spine's Deferred row on app-level `NavigateToUrl`). He closes the second window.
3. Claude follows a link `get_hyperlink_to_object` returned for an MCP Test Cloud page: that page is current and the result says so.
4. Claude soft-deletes the page made beforehand and follows its link: `landedElsewhere`.
5. Claude tries a web link (`get_web_hyperlink_to_object`) and a local MCP Test link: both refused; Phill confirms OneNote didn't move and no dialog appeared.
6. Claude makes two `navigate_to_url` calls back to back: the second gets the wait-a-second `userAction`. If it goes through instead, that is recorded and the unit test covers the mapping.
7. **Titles with "=" (Phill 2026-11-23: measure, then relax if safe).** Claude makes an MCP Test Cloud page titled `a=b test` beforehand, reads its `get_hyperlink_to_object` link and records whether OneNote writes the "=" as-is or encoded (`%3D`). Then it follows the link. If OneNote encodes it, the "=" refusal only blocks links OneNote never makes, and the build relaxes the rule (a unit test for an encoded title, the HTML link check left as is); if it writes "=" raw, the refusal stays and the result is recorded here.
8. Phill runs Sync Now on MCP Test Cloud and reports any sync error.

**Sitting record (2026-11-25).** Steps 1-7 passed with these results:
- Step 1, the gate: passed. Only window 1118364 moved; Phill confirmed.
- Step 2: the link with no `window` went to the current window, as the result said.
- Step 3: covered by steps 1 and 2.
- Step 4: the deleted page's link landed in the notebook's Recycle Bin, not `landedElsewhere` as planned. OneNote opened the page inside the `OneNote_RecycleBin` section group (`isRecycleBin="true"`); the page keeps its page-id GUID, so the check matched and returned only `landed`. Fix 1 (Phill): keep `landed` and add the informational warning `landedInRecycleBin`, whose userAction is to tell the user and offer `restore_from_recycle_bin`.
- Step 5: the web link and the local link were refused before OneNote; no dialog.
- Step 6: the second back-to-back call got ObjectNotFound `0x80042014` with the wait-a-second userAction.
- Step 7: OneNote writes "=" raw in the title part (`Probes.one#a=b%20test&section-id={…}&page-id={…}&end`) and drops "&" and "#" from titles entirely (title `x&y=z#q delete me` became `#xy=zq delete me`). Fix 2 (Phill approved): allow "=" in a title, raw or `%3D`; keep refusing a title holding "&" or "#", raw or encoded (`%26`, `%23`).
- Step 8: Sync Now pending.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Implementation Notes

- Commits: 5f6384e (the tool: `CloudLinks` parse and match, `NavigateToUrlUseCase`, app-level `NavigateToUrl`, landing check, docs, contract); e6ddbf7 (the window path alone, Decision OQ4: `IWindow` slot 21 `NavigateToUrl(bstr)`, `NavigateInWindow` taking the per-window call, `window` argument; revert that commit if the sitting's gate fails); a third with the review fixes and this plan.
- Port: `NavigateToUrlAsync(url, ct)` and `NavigateToUrlAsync(url, int? window, ct)` as default interface methods (`NotSupported`), as 4.6 did. `ComGateway` refuses an empty link before COM; app-level always passes `fNewWindow: false`; the window path finds the window by `HandleOf` and calls it in one STA delegate, wrappers released in `finally`. Both `CallKind.AppState`.
- `WindowPages` (new, internal) holds 4.6's AD-21 page withholding, now shared by `get_windows` and the landing check; `get_windows`' behaviour is unchanged. `HierarchyNode.NotebookTrees` gives each open notebook with its sections and section groups at any depth for step 3.
- The landing check runs after the pipeline returns, with plain gateway reads; any failure (or a cancel) there is `landingUnconfirmed` and the call stays ok. A page link compares the `page-id` GUID of `GetHyperlinkToObject` of the window's page; the GUID is compared, never returned, so a withheld page leaks nothing beyond the link the caller gave.
- Log line `navigate_to_url correlationId linkKind rule outcome`; refusals log `outcome=refused`.
- Contract version unchanged; the snapshot gains `navigate_to_url` (with `window`). Unit tests 4124 passed, 3 skipped (2026-11-23).

## Plan Change Log

- 2026-11-25, from the live sitting (Phill): (1) a link that lands in a notebook's Recycle Bin keeps `landed` and adds `landedInRecycleBin` (read from the window's section and section group at `self`; a failed read leaves it out and never fails the call); (2) a title may hold "=", raw or encoded, and a title holding "&" or "#", raw or encoded, is `fragmentParameter`. This replaces the 2026-11-23 rule (refuse "=" or "#", allow "&"). Tool description, guide skill and README updated; contract snapshot unchanged (descriptions are stripped, no schema change).

## Review Triage Log

- 2026-11-25, live sitting findings, both decided by Phill and fixed: step 4's Recycle Bin landing (new `landedInRecycleBin` warning; tests `ADeletedPagesLink_ThatLandsInTheRecycleBin_IsLanded_AndLandedInRecycleBin`, `TheRecycleBinGroup_Alone_IsEnoughToSayLandedInRecycleBin`, `ANormalPage_IsOnlyLanded`, `TheRecycleBinReadFailing_LeavesItLanded_AndTheCallOk`, `ARecycleBinReadThatIsNotXml_LeavesItLanded`); step 7's "=" measurement (tests `ATitleWithAnEquals_RawOrEncoded_IsStillAPageLink`; refused rows for `%26base-path=…`, `a&b=c`, `Q%26A`, `a#b`, `a%23b`). The 2026-11-23 "noted for Phill" row on "=" titles is resolved by the second.

- 2026-11-23, lenses adversarial, edge cases, verification gaps and structure, run by the build session itself (one reviewer, same model, no subagents) on the whole story diff against origin/main: 6 findings; 0 high, 1 medium, 5 low. 2 patched, 2 noted for the live sitting, 1 noted for Phill, 1 rejected; no loopback. Verification re-run after the patches (build, unit tests, stress harness, pack, plugin validate).
  - medium, patch -- step 1 decoded the title (the fragment's first part) and checked it only for control characters and backslashes, so `#Text%26base-path%3Dfile:%2F%2F%2FC:%2FUsers&page-id=…` passed: its title decodes to `&base-path=file:///C:/Users`. If OneNote decodes the fragment before splitting it, that names a local notebook, the case step 1 exists to refuse (spike 4.12 N1, N4). HtmlStopgap refuses a decoded title holding `&`, `=` or `#` for this reason. Fixed: a decoded title holding `=` or `#` is `fragmentParameter`; `&` alone stays allowed, since a parameter needs its `=` and titles such as "Q&A" are common. Tests: three new refused rows, `ATitleWithAnEncodedAmpersand_IsStillAPageLink`.
  - low, patch -- `NavigateToUrlUseCase.KindNoun` duplicated `CloudLinks.KindName`; removed.
  - low, noted (sitting step 1) -- `IWindow` slot 21's signature `NavigateToUrl(bstr)` comes from OneMore (research row 25) and the type-library order, not from a call; only the gate shows it moves the right window. The window path is in its own commit (e6ddbf7) for that reason.
  - low, noted (sitting step 2) -- without `window` the landing check reads `CurrentWindow`; if app-level `NavigateToUrl` moves another window, the result is a false `landedElsewhere`. The step records which window moved.
  - low, noted for Phill -- a page whose title holds `=` (encoded or not) can't be followed: step 1 refuses it as `fragmentParameter`, as HtmlStopgap refuses such links. It fails safe (nothing shown, `userAction` says to call `navigate_to` with the ID). Whether OneNote encodes `=` in a title is unmeasured.
  - low, rejected -- a cancel during the landing check returns ok with `landingUnconfirmed` rather than `Cancelled`. The Design Notes put the check after the navigation's outcome is known, so the call's outcome is ok; the warning says the check didn't run.

- 2026-11-23, automated security review, fixed: 2 findings, both checked against the code before fixing. Verification re-run (build with -warnaserror, unit tests, stress harness); tool descriptions unchanged, so the contract snapshot is unchanged.
  - fixed -- the cloud-link match ignored the scheme: `IsHttp` takes `http://` and `https://`, and `Inside`/`SameAddress` compared host and segments only, so an `http://` link matched an `https://` notebook. `CloudLink` now keeps the link's scheme and the base-path's, and `Forms` carries the reported path's scheme; the link body, any base-path, and the section or group it names must have the scheme of the open notebook's reported path (else `notInOpenNotebook` or `basePath`, InvalidArgument with a userAction). Rule chosen: match the notebook's own scheme rather than "https only". Every notebook path in the spike findings and fixtures is `https://`, so in practice only https is accepted, but whether an on-premises SharePoint notebook can report `http://` is unmeasured, and matching the reported scheme is as strict for https notebooks without refusing such a notebook outright. Tests: http body (page, section, notebook link) and http base-path refused in `Step3_Refuses_WithTheRule`; `AnHttpsLink_KeepsItsScheme_AndMatches_InAnyCase`; `AnHttpLink_ToAnHttpNotebook_Matches`; tool-level `AnHttpLink_ToAnOpenHttpsNotebook_IsInvalidArgument_AndNothingIsSent`.
  - fixed -- the landing check called `GetHyperlinkToObject` on the window's page even when that page was withheld under AD-21 (protected section, no read grant), and reported `landed` or `landedElsewhere`: a yes/no about a protected page. When withheld, the page's link is no longer read and the result is `landingUnconfirmed` (its message says the page is in a protected section with no read grant), with `readNotGranted` kept. Tests: `AProtectedPage_WithoutAReadGrant_IsLeftOut_WithReadNotGranted_AndLandingUnconfirmed` (replaces the `…AndStillLands` row) and `AProtectedPage_ElsewhereThanTheLink_IsStillLandingUnconfirmed_NotLandedElsewhere`, both asserting `FakeGateway.HyperlinkCalls` (new) stays empty.

- 2026-11-25, automated security review of 019960c (Medium, kept): a title such as `%62ase-path=file:…` or `base-path%3Dfile:…` decoded to a OneNote parameter but was read as a title. Fixed: the first part's key is read decoded; a title with an encoded "=" (OneNote writes it raw) or whose decoded text before its "=" is a parameter key is refused. Seven refused rows added.
