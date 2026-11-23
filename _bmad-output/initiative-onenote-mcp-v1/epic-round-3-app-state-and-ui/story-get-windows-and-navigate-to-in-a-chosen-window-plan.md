---
title: 'get_windows, and navigate_to in a chosen window'
type: 'feature'
ticket: '6'
created: '2026-11-21'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/story-tracer-sync-hierarchy-through-the-appstate-pipeline-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can't tell which page the user is looking at, and `navigate_to` always moves OneNote's first window, not the one the user means (spike 4.12 W4).

**Approach:** Declare `IWindows` (with `CurrentWindow`) and `IWindow` early-bound in `OneNoteMcp.Com`; add a `GetWindowsAsync` read to the port and both gateways; ship a `get_windows` tool on story 4.3's `AppStatePipeline` (no lock, `ChangesState` false); and give `navigate_to` an optional `window` argument that calls `Window.NavigateTo` on that window (spine AD-7 `AppState` `get_windows`, `navigate_to`; AD-21).

## Boundaries & Constraints

**Always:**
- One STA call reads `Windows`, `Count`, `CurrentWindow` and each window's getters, walking `Item(0)`…`Item(Count - 1)`, never `Item(Count)` (W1); every `Window` wrapper, `CurrentWindow` and the collection released with `Marshal.ReleaseComObject` in a `finally` inside the STA delegate (OneMore row 10). `CallKind.Read`: retried once on a disconnect; `Timeout` retryable; never `outcomeUnknown`. `ComBusy` at the pipeline's start while an abandoned write is outstanding.
- Each window reports: `handle` (`WindowHandle`, the inner pane), `current` (it is `CurrentWindow`, matched by handle), `active`, `notebookId`, `sectionGroupId`, `sectionId`, `pageId`, `fullPageView`, `dockedLocation`, `sideNote` (a quick note, W3). An empty ID is reported as absent, never as an ID (W5). No window: an empty list, `ok` (W6).
- AD-21: a window on a page of a protected section without a read grant at the call's start (`ReadAccess.HoldsReadGrant`; a stored grant or a live `once` window, never prompted) reports the section and omits `pageId`, with the `readNotGranted` warning (Decision Q2).
- `get_windows` is not user-visible (AD-11, W2); its description says the skill calls it only when the user asks or refers to what they're looking at (Q5). Annotations: read-only, idempotent.
- `navigate_to(hierarchyId, pageContentObjectId?, window?)`, `window` an integer handle as `get_windows` returns it (Decision Q1): without `window`, unchanged (first window). With it, the same STA call finds the window by handle and calls its `NavigateTo`; a handle no window has → `InvalidArgument`, nothing navigated. No window setters (Q4); `fNewWindow` stays false.
- Logs carry IDs and handles only (AD-13). Contract snapshot checked (AD-14).

**Never:** `Active`, `DockedLocation`, `FullPageView`, `SideNote` setters or `SetDockedLocation`; `Window.NavigateToUrl` (4.7); a frame handle; the write lock; any spine change; setting the integration env var in the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| One window | MCP Test page shown | one entry, `current` true, its IDs | — |
| Two windows | main + Ctrl+M or quick note | two entries in `Item` order, one `current` | — |
| No window | `Count` 0 | empty list | — |
| Locked section | `CurrentPageId` `""` | section reported, no `pageId` | — |
| Protected page, no grant | unlocked protected section | section reported, no `pageId` | warning `readNotGranted`, once per call with the count |
| Protected page, grant held | stored or `once` grant | `pageId` reported | — |
| Abandoned write outstanding | — | no COM | `ComBusy` |
| navigate_to with `window` | a listed handle | only that window moves | — |
| navigate_to, unknown `window` | handle no window has | nothing moves | `InvalidArgument` |

## Decisions (Phill, 2026-11-22, Inbox plan approval)

- **Q1 (a):** `navigate_to`'s `window` argument is an integer: the `handle` `get_windows` returns (window handles fit in 32 bits, so JSON numbers are exact). It is a public contract (AD-14).
- **Q2 (a):** a page withheld under AD-21 is reported with the existing `readNotGranted` warning, once per call with the count of withheld windows, as broad reads and searches give it (backlog 15); its userAction says how to grant.
- **Q3:** the plan is kept whole.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IWindows.cs` -- today slots 7 `GetItem` (IDispatch), 8 `Count`; add 9 `_NewEnum` (placeholder) and 10 `CurrentWindow`, and type `GetItem` as the new `IWindow`.
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeWindowsInterop.cs` -- spike 4.12's measured layout: `IProbeWindow` GUID `8E8304B8-…`, slots 7–22 (`WindowHandle` ulong, the four IDs, `NavigateTo` 12, `FullPageView` 13, `Active` 15, `DockedLocation` 17, `SideNote` 20; setters, `Application`, `NavigateToUrl`, `SetDockedLocation` as `[Obsolete(error: true)]` placeholders). Write our own `IWindow` from it in `Com/Interop`; don't reference the test project.
- `src/OneNoteMcp.Com/Interop/IApplication.cs:160` slot 30 `Windows` -- order frozen; `tests/OneNoteMcp.Tests/InteropLayoutTests.cs:17` -- add `IWindows` and `IWindow` layouts.
- `src/OneNoteMcp.Com/ComGateway.cs` -- read pattern `GetHierarchyParentAsync` (139-148); `NavigateToAsync` (234-245, `CallKind.AppState`) gains the window path; `StaThread.cs:120-129` is the only release today.
- s43's `NavigateToUseCase.cs` (`Navigate : AppStateOperation<bool>`) and `NavigateToTool.cs` -- add the argument; s43's `AppStatePipeline` (`ChangesState` override for `get_windows`).
- `src/OneNoteMcp.Core/Access/ReadAccess.cs:219` `HoldsReadGrant(HierarchyNode, at?)` -- the no-prompt check; the section node from one `GetHierarchy(sectionId, self)` per window on a page.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- add `Windows` (list of a new `WindowListing` record), as `Grants` was added for `list_grants` (`ListGrantsUseCase.cs:110`).
- `tests/OneNoteMcp.Tests/FakeGateway.cs` (`NavigateCalls` with a window, a scripted window list), `FakeApplication.cs:135` (`Windows` throws today: add a fake collection for the gateway's own tests).
- Contract: `ToolContractTests.cs:81`, `Contract/tool-contract.json`, `McpSdkBindingTests.cs`; `PluginPackagingTests`; `plugin/skills/guide/SKILL.md`, `README.md`, `spikes/stress-harness/Calls.cs`.

## Tasks & Acceptance

**Execution:**
- [ ] `Com/Interop/IWindows.cs`, new `IWindow.cs`, `InteropLayoutTests.cs` -- early-bound declarations.
- [ ] Port (`GetWindowsAsync`, `NavigateToAsync` with an optional window), `ComGateway` (one STA delegate each, releases in `finally`), `FakeGateway`, `FakeApplication`.
- [ ] `Core/UseCases/GetWindowsUseCase.cs`, `Server/Tools/GetWindowsTool.cs`, `ServerSetup.cs`; `ToolMeta.Windows`.
- [ ] `NavigateToUseCase.cs`, `NavigateToTool.cs` -- `window` argument and description.
- [ ] Unit tests: `GetWindowsToolTests` and `NavigateToToolTests` for every matrix row; `ComGatewayLifecycleTests`: the walk stops at `Count - 1`, every wrapper released, an unknown handle navigates nothing.
- [ ] Contract snapshot, tool list, binding count; skill (`get_windows` when the user says "this page"), README, stress-harness rows.

**Acceptance Criteria:**
- Given an abandoned write outstanding, when `get_windows` is called, then it is `ComBusy` and no window is read.

## Design Notes

- The list goes in `meta`, not `xml`: it isn't OneNote XML (AD-4), and `list_grants` set the pattern.
- Finding the window and navigating it in one STA delegate keeps the handle check and the call together, so a window closed in between fails as `InvalidArgument` rather than a stale wrapper.
- Considered a string of digits for `window` (safe past 2^53); going with an integer (Decision Q1) because window handles fit in 32 bits and a number is the natural shape.

## Live sitting (Phill, about 10 minutes; hitl)

Before: OneNote open in one window on an MCP Test page, with another app (VS Code) in front. The build session stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build.

1. In Claude Code he asks "which OneNote page am I looking at?". He sees Claude name that MCP Test page; VS Code stays in front and OneNote doesn't change.
2. If MCP Test has a section inside a section group, he shows a page in it and asks again: the answer names the group too (spine Deferred row). Otherwise he skips this.
3. In OneNote he presses Ctrl+M for a second window and shows a different MCP Test page in it. He asks Claude to list his OneNote windows: two, the new one current.
4. He asks Claude to show a third MCP Test page "in the second window". Only the new window moves to it; the first keeps its page.
5. He asks to show a page without naming a window: the first window moves (as documented). He closes the second window.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Implementation Notes

## Plan Change Log

## Review Triage Log
