---
title: 'Tracer: sync_hierarchy through the AppState pipeline'
type: 'feature'
ticket: '3'
created: '2026-11-21'
status: 'done'
baseline_revision: '8df0c015e9d967e14a0f5d07ef33d1a2bff81336'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Round 3's app-state tools have no pipeline: `navigate_to` (story 1.21) calls the gateway directly, with no deadline and no `outcomeUnknown`, and nothing exposes `SyncHierarchy` (IApplication slot 28).

**Approach:** Build the spine's AD-7 `AppState` pipeline beside `WritePipeline` (step 1 checks, the abandoned-write `ComBusy` check at the start, steps 3–4, step 7, the 120 s deadline, `outcomeUnknown` on an ambiguous step-7 failure, no grant, no basis, no snapshot), add `SyncHierarchy` to the port and both gateways, ship a thin `sync_hierarchy` tool on it, and move `navigate_to` onto it.

## Boundaries & Constraints

**Always:**
- `sync_hierarchy(hierarchyId)`: any notebook, section group, section or page ID; no pre-read. `ok` means OneNote accepted the request, not that a sync ran or finished, and the result says so (spine AD-7 `AppState`; spike 4.1 S1). `0x80042014` stays `ObjectNotFound` (4.1 S2).
- Neither tool takes the write lock (OQ1 a). Both return `ComBusy` before COM while an abandoned write is outstanding, checked at the pipeline's start.
- A step-7 `Timeout`, `ComUnavailable`, `Cancelled` or `0x800706BE`, the deadline, or a client cancel during step 7 is `outcomeUnknown`, never retried. An abandoned `SyncHierarchy` or `NavigateTo` is not an abandoned write (`ComGateway` tracks only `CallKind.Write`): send `SyncHierarchy` as `CallKind.AppState`, no disconnect retry.
- `ModalDialogOpen` (`0x80042030`) passes through, retryable, not `outcomeUnknown`.
- `sync_hierarchy` is not user-visible (4.1 S1): its description doesn't start "User-visible", but says to call it only when the user asks, never on its own after a write (AD-11). Annotations: not read-only, not destructive, idempotent.
- `navigate_to`: checks and arguments unchanged; its description gains the deadline and `outcomeUnknown`; its `0x80042014` `userAction` drops "wait two seconds and try once more" and keeps "re-read the hierarchy and use a current ID" (AD-9; spike 4.12 R1).
- Contract: one new tool, `<Version>` stays `0.10.0-rc.1` (no tag since it was set; AD-14). Logs carry IDs only (AD-13).

**Never:** the write lock or step 8 for these tools (stories 4.4, 4.5 add the lock path); a `window` argument (4.6); `close_notebook`, `open_notebook`, `navigate_to_url`, `get_windows`; any spine change; a sync call from any other use case; the integration env var set by the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Sync ok | open notebook, section or page ID | `ok`, warning `syncRequested` | — |
| Unknown or stale ID | `0x80042014` | — | `ObjectNotFound`, re-read userAction |
| Empty ID | `""` | no COM call | `InvalidArgument` |
| Abandoned write outstanding | FakeGateway `AbandonedWrites` pending | no COM call | `ComBusy`, retryable |
| Dialog open | `0x80042030` | — | `ModalDialogOpen`, retryable |
| Call timeout / disconnect / `0x800706BE` | step 7 | — | same code, `outcomeUnknown: true`, not retryable |
| Deadline or client cancel in step 7 | — | — | `Timeout` / `Cancelled`, `outcomeUnknown` |
| Deadline before step 7 | — | no COM call | `Timeout`, retryable |
| (all rows also for `navigate_to`) | | | |

## Decisions (Phill, 2026-11-22, Inbox plan approval)

- **Q1 (a):** every `ok` from `sync_hierarchy` carries the warning `syncRequested` (this story names it, AD-9): OneNote accepted the request; it doesn't say when or whether the sync finishes.
- **Q2 (a):** `NavigateToToolTests.cs:64` is updated to the new `0x80042014` userAction ("re-read the hierarchy and use a current ID"; no "wait two seconds").
- **Q3:** the plan is kept whole (about 2,000 tokens).

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/WritePipeline.cs:98-320` -- the model: deadline source and linked token (103-108), `AbandonedWrites()` check (205), step-7 ambiguity mapping (246-266, `IsAmbiguous` 316), `DeadlineExpired` (~366). Reuse `IsAmbiguous` and the deadline helper; don't change this class.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs:12` -- shape for a smaller `AppStateOperation<T>` (`Validate`, `ReadAsync`, `CallAsync`, `ChangesState`).
- `src/OneNoteMcp.Core/OperationClass.cs` -- `AppState` exists.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs:47,178` -- `NavigateToAsync`, `WaitForAbandonedWritesAsync`; add `SyncHierarchyAsync(id, ct)`.
- `src/OneNoteMcp.Com/ComGateway.cs:234-246` -- copy `NavigateToAsync`'s shape; `CallKind` 1245, `ThrowIfWriteOutstanding` 1093, retry rule 754. `Interop/IApplication.cs:150-151` already declares `SyncHierarchy`.
- `src/OneNoteMcp.Core/UseCases/NavigateToUseCase.cs` -- calls the gateway directly (27); userAction 18-19.
- `src/OneNoteMcp.Server/Tools/NavigateToTool.cs` -- description; new `SyncHierarchyTool.cs` beside it. `ServerSetup.cs:102,115,244-272` registration.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- warning codes (`Settled` 143); `ToolErrors.cs:29-58` rewrites an `outcomeUnknown` userAction.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` (`Failure` 73, `NavigateCalls` 145, `AbandonedWrites` 321), `FakeApplication.cs`, `NavigateToToolTests.cs` (64 repeat userAction; 99-130 user-visible convention), `ComGatewayLifecycleTests.cs:778,792,1458-1552`.
- `tests/OneNoteMcp.Tests/ToolContractTests.cs:81` (tool list), `Contract/tool-contract.json`, `McpSdkBindingTests.cs:92` (count 29), `PluginPackagingTests` (skill and README name every tool).
- `tests/OneNoteMcp.Integration/TestNotebook.cs:598-660` (guarded `SyncHierarchyAsync`, `EnsureAsync` 203 in `CreatePageIntegrationTests`).
- `plugin/skills/guide/SKILL.md` (count 8, table ~52, user-visible rule 289), `README.md` tool table, `spikes/stress-harness/Calls.cs:67-79,323`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/AppState/AppStatePipeline.cs`, `AppStateOperation.cs` -- the pipeline: start `ComBusy` check, step 1, steps 3–4, step 7 with `outcomeUnknown` when `ChangesState`, 120 s deadline -- AD-7 `AppState`.
- [ ] Port, `ComGateway`, `FakeGateway` (`SyncCalls`, per-method failure), `FakeApplication` -- `SyncHierarchyAsync` as `CallKind.AppState`, no retry.
- [ ] `UseCases/SyncHierarchyUseCase.cs`, `Tools/SyncHierarchyTool.cs`, `ServerSetup.cs` -- the tool and its result note.
- [ ] `NavigateToUseCase.cs`, `NavigateToTool.cs` -- run through the pipeline; new userAction and description.
- [ ] Unit tests: `AppStatePipelineTests` and `SyncHierarchyToolTests` for every matrix row, both tools; `NavigateToToolTests` updated (userAction text; `sync_hierarchy` not "User-visible"); `ComGatewayLifecycleTests` for `SyncHierarchy`'s kind (refused while a write is outstanding, not retried, not tracked as abandoned).
- [ ] Contract snapshot, `ToolContractTests` list, `McpSdkBindingTests` count 30.
- [ ] `tests/OneNoteMcp.Integration/SyncHierarchyIntegrationTests.cs` -- through the use case and `ComGateway`: sync MCP Test, a section and a page in it (`ok`), and a made-up ID (`ObjectNotFound`).
- [ ] `plugin/skills/guide/SKILL.md`, `README.md`, `spikes/stress-harness/Calls.cs` -- a `sync_hierarchy` row (only when the user asks; ok means requested); the harness refuses it like `navigate_to`.

**Acceptance Criteria:**
- Given the built server, when the tool list is read, then `sync_hierarchy` is listed and the snapshot test passes with only that addition.
- Given an `AppState` call that times out at step 7, when a write follows, then the write is not refused as `ComBusy` on its account.

## Design Notes

- A separate `AppStatePipeline`, not flags on `WritePipeline`: that one is built around grants, the cache and the lock, none of which these tools use, and the spine says "beside". 4.4 and 4.5 add the lock path to it; building it now would ship untested code.
- The result's "requested" note is the warning `syncRequested` (Decision Q1), as `settled` is a warning that informs.

## Live sitting (Phill, about 10 minutes; hitl)

Before: OneNote open in a window with MCP Test and MCP Test Cloud, online. The build session stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build.

1. Phill runs `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter SyncHierarchy`. He sees it pass; nothing changes on screen.
2. In OneNote: File > Info > View Sync Status. He notes each notebook's status (expect "Up to date").
3. In Claude Code he asks "sync my MCP Test Cloud notebook". He sees `ok` and the note that the sync was requested, not confirmed. OneNote shows nothing new.
4. In OneNote he right-clicks MCP Test Cloud > Sync This Notebook Now, then opens View Sync Status again. Expect no sync error on MCP Test Cloud and no other notebook changed. He reports `0xE0000644` or "Incomplete sync" if either appears.
5. He asks Claude to show an MCP Test page. OneNote shows it, as before.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Implementation Notes

- Implemented by a context-free subagent from this plan (2026-11-22); verified by the build session: `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 3870 passed, 3 skipped (the existing `ProtectedByPathToolTests` skips), 0 failed. The integration tests and the pack/validate check weren't run by the build session (the live sitting runs the integration filter `SyncHierarchy`).
- `navigate_to` refuses an empty `hierarchyId` before COM, as the I/O matrix's empty-ID row applies to both tools.

## Plan Change Log

## Review Triage Log

- Decision (Phill, 2026-11-22, Inbox): a call that times out while still queued in the gateway is reported outcomeUnknown, as in WritePipeline; navigate_to now does the same. Kept as built.

- 2026-11-22, quick lens (one reviewer, same model), pass 1: 2 findings; verdicts 0 high, 0 medium, 2 low, 0 false, 0 maybe-false. Both rejected; no patch, no loopback.
  - low, rejected -- `NavigateToUseCase.Navigate.Validate`: `navigate_to` now refuses an empty `hierarchyId` with `InvalidArgument` before COM, where it used to send it. The frozen I/O matrix applies every row, the empty-ID row included, to `navigate_to`, so the code follows the approved intent; an empty ID could never show anything, and `InvalidArgument` is AD-9's generic refusal. Unlikely to be met in use.
  - low, rejected -- `AppStatePipeline` step 7: a call that times out or is cancelled while still queued in `ComGateway` (`ComGateway.cs:978`, `1000-1014`) never reached OneNote but is reported `outcomeUnknown`. `WritePipeline` does the same, and the spine's step-7 rule names `Timeout` and `Cancelled` without that distinction; the error is conservative (it asks the caller to check rather than claiming nothing happened). Telling queued from started apart would add a gateway signal and branches for a rare case.
- Live sitting (Phill, 2026-11-23): passed all 5 steps. Integration filter SyncHierarchy 2 passed; sync_hierarchy on MCP Test Cloud returned ok with syncRequested in 290 ms; Sync This Notebook Now afterwards left every notebook up to date (no 0xE0000644, no Incomplete sync); navigate_to to an MCP Test page returned ok in 372 ms and OneNote showed it.
