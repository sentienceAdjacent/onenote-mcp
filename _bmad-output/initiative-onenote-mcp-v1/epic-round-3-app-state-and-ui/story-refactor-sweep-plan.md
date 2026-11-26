---
title: 'Refactor sweep (round 3)'
type: 'refactor'
ticket: '11'
created: '2026-11-23'
status: 'built'
baseline_revision: 'ee89b5c133bcabe3cfebb0719eed608ad7a1b1a5'
route: 'full'
route_source: 'pinned'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-refactor-sweep-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Round 3's builds (4.3-4.7, 4.9) left one deferred item and no other `deferred:` cleanup; reading the code finds small copies the `AppState` tools introduced: the empty-`hierarchyId` refusal written twice (`sync_hierarchy`, `navigate_to`), the `0x80042014` userAction rewrap twice (`navigate_to`, `navigate_to_url`), a `window is { }` branch in both navigate use cases that the gateway overload already makes (null falls back to the app-level call in `ComGateway`, `FakeGateway` and the port's default), `CloudLink.Unescape` a copy of `UrlPath.Decode`, `CloudLink.IsHttp` repeated inline in `CloseNotebookUseCase`, and `GetWindowsUseCase` holding two helpers that `WindowPages` and `navigate_to_url` use.

**Approach:** Code motion only, identical in behaviour for every input: no message, userAction, error code, retryability, warning, COM call, call order, tool contract, spine or setting changes (except as Q1 decides). The existing suites are the guard (same counts before and after, all green), with the harness's contract check and plugin validation. It starts after 4.7 (PR #131), 4.9 (PR #132) and entry 10 (ticket `after = [10]`) are merged.

## Boundaries & Constraints

**Always:**
1. `AppStateChecks` (new, `src/OneNoteMcp.Core/AppState/`): `RequireHierarchyId(id)`, the one empty-ID refusal, used by `SyncHierarchyUseCase` and `NavigateToUseCase`; `WithUserAction(ex, userAction)`, the `0x80042014` rewrap (code, message, HRESULT, inner, `outcomeUnknown` kept; `retryable` left unset, as both copies do today), used by both navigate use cases with their own userAction constants.
2. `NavigateToUseCase` and `NavigateToUrlUseCase` step 7 call the windowed overload with `window` as given (null included); no branch.
3. `UrlPath.IsHttp(text)` (public): `CloudLink.IsHttp` goes, and `CloseNotebookUseCase`'s cloud test uses it. `UrlPath.Decode` becomes `internal`; `CloudLink.Unescape` goes.
4. `WithheldMessage(int)` and `DockedLocationName(int)` move, unchanged, from `GetWindowsUseCase` to `WindowPages`; callers and `GetWindowsToolTests` follow.
5. At the start, entry 10's plan Review Triage Log and any `deferred-work.md` entry added since this plan are read: a code-motion item joins under these rules; anything else goes to "Not in this sweep".
6. `deferred-work.md`: the 4.6 `navigate_to` description entry (l.653) marked as Q1 decides.

**Never:** a change to any message, userAction, code, retryability, warning text, COM call or tool contract (Q1 aside); `CloseNotebookUseCase`'s own empty-ID check (its text and userAction differ); `OpenNotebookUseCase.IsUrl` (a wider rule: `http:` without `//`, trimmed); the log-entry wiring of `open_notebook`, `navigate_to_url` and `quick_filing`; `ComGateway` (its window paths already share `NavigateInWindow`); test-helper consolidation; spine, settings, CLAUDE.md, `tickets.toml`; the integration variable set by the build session; touching OneNote.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected (as on the baseline) | Error Handling |
|---|---|---|---|
| Empty ID | `sync_hierarchy` or `navigate_to`, `hierarchyId: ""` | no COM call | `InvalidArgument`, today's text |
| Stale ID | `0x80042014` from `NavigateTo` / `NavigateToUrl` | — | `ObjectNotFound`, each tool's own userAction, `outcomeUnknown` kept |
| No window | `navigate_to` / `navigate_to_url` without `window` | app-level call, `FakeGateway` records a null window | — |
| Window | either with a handle | `Window.*` call | unknown handle `InvalidArgument` |
| Close reopen advice | path `https://…`, `HTTP://…`, `C:\…` | cloud, cloud, local userAction | — |
| Escaped link | `navigate_to_url` with `%`-escapes, an invalid escape | same parse and refusal | `CloudLinkTests` unchanged |
| Withheld page | `get_windows`, landing check | same `readNotGranted` text, same `dockedLocation` names | — |

## Decisions (Phill, 2026-11-23, Inbox plan approval)

- **Q1 (b):** `navigate_to`'s description is fixed in entry 10 (Phill chose that there, 4.10 OQ3 (a)); this sweep marks the deferred entry resolved and keeps its "no contract change" rule.
- **Q2 (b):** the 10-minute live sitting below: the harness on MCP Test, the round-3 integration filters that take no focus, and one sync and one navigate through Claude.

</frozen-after-approval>

## Code Map

Line numbers from a trial merge of 4.7 and 4.9 into `eb3a5d4`; recheck after the real merges.

- `src/OneNoteMcp.Core/UseCases/SyncHierarchyUseCase.cs:38`, `NavigateToUseCase.cs:58` -- the identical empty-ID refusal.
- `NavigateToUseCase.cs:44-46`, `NavigateToUrlUseCase.cs:102-104` -- the `0x80042014` rewrap (same constructor call).
- `NavigateToUseCase.cs:64`, `NavigateToUrlUseCase.cs:279` -- the `window` branches; `IOneNoteGateway.cs:58-61, 81-84` (null falls back), `ComGateway.cs:235-236, 262-263` and `tests/OneNoteMcp.Tests/FakeGateway.cs:168-169, 205-206` (3-argument forwards to 4-argument with null).
- `src/OneNoteMcp.Core/AppState/CloudLink.cs:471-481` (`Unescape`) = `Writes/UrlPath.cs:153` (`Decode`); `CloudLink.cs:490` `IsHttp` (callers 134, 144, 179, 244) = `CloseNotebookUseCase.cs:67`.
- `GetWindowsUseCase.cs:58, 64`; users `WindowPages.cs:48`, `NavigateToUrlUseCase.cs:198`, `tests/OneNoteMcp.Tests/GetWindowsToolTests.cs:125, 174, 267` (the test project sees Core's internals, as `OpenNotebookToolTests` does).
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md:653`; `src/OneNoteMcp.Server/Tools/NavigateToTool.cs:24, 26-27` (Q1).
- OneMore: not consulted; no COM call, order or argument changes.

## Tasks & Acceptance

**Execution:**
- [x] Record unit and integration test counts on the baseline (main with 4.7, 4.9 and entry 10 merged); do item 5's read.
- [x] `src/OneNoteMcp.Core/AppState/AppStateChecks.cs` and the two navigate and sync use cases -- items 1-2.
- [x] `Writes/UrlPath.cs`, `AppState/CloudLink.cs`, `UseCases/CloseNotebookUseCase.cs` -- item 3.
- [x] `UseCases/WindowPages.cs`, `GetWindowsUseCase.cs`, `NavigateToUrlUseCase.cs`, `GetWindowsToolTests.cs` -- item 4.
- [x] Unit tests: `AppStateChecksTests` for each matrix row the helpers carry (texts compared with today's literals); one `CloseNotebookToolTests` row for `HTTP://` if none exists.
- [x] Q1's outcome; `deferred-work.md` -- item 6.

**Acceptance Criteria:**
- Given the branch, when the unit and integration suites run, then every test passes and the counts equal the baseline's plus the new helper tests.
- Given `git diff` of `src/`, when every changed string literal is listed, then none differs from the baseline's (Q1's description aside).
- Given the branch, when the harness's `--contract-only` runs against a `contract-ok` fake, then it exits 0; and `claude plugin validate --strict dist/stage/onenote` passes.

## Design Notes

- Route `full` is pinned by the coordinator (plan gate first); the estimate is about 90 changed lines in 10 files, all mechanical.
- Why not wider: the test files' `Ok`/`Error`/`BuildServer` copies are in 28 files from every epic; a round-3-only helper would leave two patterns. The three log-entry shapes carry different fields.

## Live sitting (Phill, about 10 minutes; per Q2)

Before: OneNote open in a window with MCP Test and throwaway b; nothing else on b's pages. The build session packs the plugin, stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build.

1. Phill runs the harness on MCP Test (harness `README.md`, "Run it": `pwsh -File scripts/pack-plugin.ps1`, `dotnet build spikes/stress-harness -c Release`, then `StressHarness.exe --notebook-name "MCP Test" --out "$env:TEMP\stress-run"`). Expect exit 0 and 0 mismatches; nothing changes on screen.
2. He runs `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter "FullyQualifiedName~SyncHierarchy|FullyQualifiedName~CloseNotebook|FullyQualifiedName~OpenNotebook"`. Expect all pass; b closes and reopens (about 10 s each, as in 4.4's sitting).
3. In Claude Code he asks "sync MCP Test", then "show me an MCP Test page". Expect `ok` with `syncRequested`, then OneNote showing the page.

**Sitting record (2026-11-26, against the 4.11 build; onenote-dev re-pointed and reconnected, old server stopped):**

- Harness, read-only on MCP Test: 844 comparisons, 0 mismatches, 39 large results reassembled, none over 100,000 chars.
- Integration tests with `ONENOTE_MCP_INTEGRATION=1`, filtered to SyncHierarchy, CloseNotebook and OpenNotebook: 5 passed, 1 skipped. The skip was `CloseNotebook_ThrowawayB_…` ("Open the throwaway notebook … first"): in the combined run the OpenNotebook class closes and reopens Throwaway b at the same time (xUnit runs classes in parallel). Run alone, the CloseNotebook filter passed 2 of 2. This is a test-ordering race between two classes on one notebook, not a behaviour change.
- "Sync MCP Test" through the 4.11 server: `syncRequested`.
- "Show me an MCP Test page": `navigate_to` on probe quickstyle 1110-131501 returned ok with no warnings, and `get_windows` then showed that page in OneNote's window.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` (no variable) -- all pass, counts as recorded.
- `dotnet build spikes/stress-harness -c Release`, then its `--contract-only` against a `contract-ok` fake -- exit 0.
- `pwsh -File scripts/pack-plugin.ps1`, then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Not in this sweep:** test-helper consolidation; `OpenNotebookUseCase.IsUrl`; the log wiring; `TestNotebookGuard.MapLocalhostAdminShare` (the integration guard keeps its own copy of `OpenNotebookUseCase`'s, so it doesn't depend on the code it guards); 4.7's note that a page title holding `=` can't be followed.

## Review Triage Log

| # | Lens | Finding | Verdict | Route / evidence |
|---|---|---|---|---|
| 1 | quick | The plan records no baseline test counts and no outcome of item 5's read, so the first acceptance criterion can't be checked from the work. | low | Rejected (its fix is to edit this build's plan); the counts and the read's outcome are recorded in Implementation Notes below anyway. The reviewer found the code identical in behaviour on every path traced, and no other finding. |

## Implementation Notes

- **Baseline (`ee89b5c`, main with 4.7, 4.9 and entry 10 merged):** unit 4201 passed, 3 skipped (4204); integration without the variable 415 passed, 137 skipped (552).
- **Branch:** unit 4214 passed, 3 skipped (4217): the 13 new cases are 10 in `AppStateChecksTests` and 3 rows in `CloseNotebookToolTests` (`HTTP://`, `http://`, a UNC path). Integration unchanged: 415 passed, 137 skipped (552). Build `-warnaserror`: 0 warnings. Harness `--contract-only`: `contract-ok` exits 0 ("contract ok: 35 tools"), `contract-no-readonly` exits 3. `pack-plugin.ps1` packed `onenote-0.10.0-rc.1-win-x64.zip`; `claude plugin validate --strict dist/stage/onenote` passed. All builds and tests ran with a separate `--artifacts-path`.
- **Item 5:** entry 10's Review Triage Log has three findings, all fixed in 4.10, so nothing joins this sweep; `deferred-work.md` gained nothing since this plan but 4.10's resolution entry.
- **String literals:** no message, userAction or warning string was added, removed or changed in `src/`; the `window` branches and helper moves are code motion only. `RequireHierarchyId` and `UrlPath.IsHttp` refuse a null argument; every caller passes a non-null string (`?? string.Empty` or a parsed path), so no input reaches that.
- **Not run here (left for Phill's sitting, per Q2):** the harness on MCP Test, the round-3 integration filters with `ONENOTE_MCP_INTEGRATION=1`, and the sync and navigate through Claude. Before it: stop any old `OneNoteMcp.exe` and re-point `onenote-dev` at the new build (the zip above was packed from this branch; repack after any later change).
