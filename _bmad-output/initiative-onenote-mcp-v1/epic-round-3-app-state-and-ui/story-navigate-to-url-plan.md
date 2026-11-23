---
title: 'navigate_to_url'
type: 'feature'
ticket: '7'
created: '2026-11-22'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
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
- Link kinds: page (`.one` with `page-id`, `object-id` allowed), section (`.one`, no `page-id`), notebook (the notebook's own folder).
- Step 7: `NavigateToUrl(url, fNewWindow: false)`, or with `window` the window found by handle and its `NavigateToUrl(url)` in one STA call (unknown handle: `InvalidArgument`, nothing sent, as 4.6). `CallKind.AppState`: never retried; a timeout, disconnect, cancel or `0x800706BE` is `outcomeUnknown`. `ComBusy` at the start while an abandoned write is outstanding.
- `0x80042014` stays `ObjectNotFound` with the spine's `userAction` (AD-9): the link may name something gone; if `navigate_to_url` was just called, wait a second and try once more; otherwise ask the user whether OneNote showed a message.
- Landing check, after an ok call, no settle: one windows read; the `window` argument's window, else `CurrentWindow`. Page link: `GetHyperlinkToObject` of that window's page, its `page-id` GUID against the link's; section link: `CurrentSectionId` against the matched section's ID; notebook link: `CurrentNotebookId`. A mismatch, or no current page for a page link: warning `landedElsewhere`. The reads failing, no window, or the `window` gone: the can't-confirm warning; the call stays ok.
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
- [ ] `Core/AppState/CloudLink.cs` (new) -- parse and classify a link; match it to the open notebooks and sections from one hierarchy XML; pure, so unit-tested on strings.
- [ ] Port, `ComGateway` (one STA delegate per path, wrappers released in `finally`), `IWindow`, `FakeGateway`, `FakeApplication` -- `NavigateToUrlAsync`. The window path is its own commit, so it can come out if the sitting's gate fails.
- [ ] `Core/UseCases/NavigateToUrlUseCase.cs` -- step 1 parse; step 3 the open-notebooks read (`GetHierarchy("", Sections)`) and match; step 7 the call; then the landing check outside the pipeline (Design Notes); log entry; warnings.
- [ ] `Server/Tools/NavigateToUrlTool.cs`, `ServerSetup.cs` -- `navigate_to_url(url, window?)`; annotations as `navigate_to`.
- [ ] Unit tests (`CloudLinkTests`, `NavigateToUrlToolTests`, `ComGatewayLifecycleTests`) -- every matrix row, plus: percent-encoded and plain notebook paths, a segment-boundary near miss (`MCP Test Cloud 2`), a section in a section group, a `base-path` into another notebook, double-encoded parts, no COM call on any refusal (FakeGateway records none), wrappers released, `fNewWindow` false.
- [ ] Contract snapshot, tool list, binding count; skill recipe (follow a pasted link only when the user asks; local links go through `navigate_to`), README, stress-harness rows, research note row 24.

**Acceptance Criteria:**
- Given a link refused in step 1, when the tool returns, then the log line holds the kind and rule and no part of the link.
- Given a page link whose landing matches, when the tool returns, then the result names the window's notebook, section and page IDs (page withheld without the read grant) and says "OneNote's current page is …".

## Design Notes

- The landing check runs after the pipeline returns ok, not inside step 7: the navigation's outcome is known by then, so a read that times out must not turn the call into `outcomeUnknown`. Its reads are `CallKind.Read` with the call timeout; any failure gives the can't-confirm warning.
- Matching reads the link once, decoded once, and requires the four-round decode to agree; the notebook's `path` is compared in both its reported (unencoded) and `UrlPath` forms, as AD-21's Web addresses rule reads it.
- Considered combining the call and the landing read in one STA delegate (no gap for the user to move); going with two calls because a failed read would otherwise be indistinguishable from a failed navigation.

## Live sitting (Phill, about 15 minutes; hitl)

Before: OneNote open on an MCP Test Cloud page, VS Code in front. The build session stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build. Claude makes one MCP Test Cloud page for step 4 beforehand and checks it with Sync Now.

1. **The gate (Window.NavigateToUrl).** Phill opens a second window (Ctrl+M) on another page. Claude lists the windows, then follows a cloud page link in the second window. Phill confirms only that window moved (pointing at OneNote if it hasn't repainted, N1 C). If it fails or moves the wrong window, the build stops and comes back to Phill before going on (ticket, Phill 2026-11-22).
2. With both windows open, Claude follows another link with no `window`. Phill says which window moved; the result should name that one (checks the spine's Deferred row on app-level `NavigateToUrl`). He closes the second window.
3. Claude follows a link `get_hyperlink_to_object` returned for an MCP Test Cloud page: that page is current and the result says so.
4. Claude soft-deletes the page made beforehand and follows its link: `landedElsewhere`.
5. Claude tries a web link (`get_web_hyperlink_to_object`) and a local MCP Test link: both refused; Phill confirms OneNote didn't move and no dialog appeared.
6. Claude makes two `navigate_to_url` calls back to back: the second gets the wait-a-second `userAction`. If it goes through instead, that is recorded and the unit test covers the mapping.
7. Phill runs Sync Now on MCP Test Cloud and reports any sync error.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Open Questions

**OQ1. The warning codes (public contract, AD-9, AD-14).** The ticket leaves the can't-confirm code to this story; the matched landing needs a carrier for "OneNote's current page is …".
- (a) `landingUnconfirmed` for can't-confirm, and an informational `landed` warning on every matched ok, as `syncRequested` and `notebookClosed` carry their result text.
- (b) `landingUnconfirmed`, and the matched case with no warning: the text goes in the description and `meta.windows` alone.
- (c) Other names Phill prefers.

Recommended: (a)

**OQ2. Business OneDrive and SharePoint links.** The spine's rule accepts any cloud link inside an open notebook, but only personal OneDrive links were probed for the Security Notice (spike 4.12; AD-3 Modal dialogs). A notice hidden behind other windows would hold the STA thread until the 120 s deadline (N1), then `outcomeUnknown`.
- (a) Follow the spine: any open cloud notebook's host.
- (b) Accept `d.docs.live.net` only until a probe covers business links; a narrowing that needs a spine Deferred note in a chore.

Recommended: (a)

**OQ3. Section-group links.** A link to a section group's folder inside an open notebook isn't one of the spine's three link kinds, and `CurrentSectionGroupId` was never seen non-empty (W1), so its landing can't be checked.
- (a) Refuse it with `InvalidArgument` and a `userAction` to use `navigate_to` with the group's ID.
- (b) Accept it and always give the can't-confirm warning.

Recommended: (a)

**OQ4. When the Window.NavigateToUrl gate runs.**
- (a) In the story's one sitting, as step 1, with the window path built as its own commit so it can come out if the gate fails.
- (b) A short sitting of its own before the window path is built (about 5 minutes more of Phill's time, a second setup).

Recommended: (a)

**OQ5. Plan size.** About 3,700 tokens (estimated from 14,200 characters), above bmad-build's 900-1,600 target, as 4.4-4.6 were. The window path could split into its own story, but it is one argument on the same call and the ticket keeps it.
- (a) Keep the plan whole.
- (b) Split the `window` argument into a follow-up story.

Recommended: (a)

## Implementation Notes

## Plan Change Log

## Review Triage Log
