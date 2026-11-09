---
title: 'Refactor sweep'
type: 'refactor'
ticket: '14'
created: '2026-11-09'
status: done
baseline_revision: '8c6d2c7cb55fdb587dd8d571e8e72346fff9d226'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 1's stories left deferred findings in `deferred-work.md`, in their plans' triage logs, and in the spine's Deferred table. Some are fixed but never closed, some are small test gaps, and some belong to epic 2 but aren't recorded there. The ticket's check is "every deferred finding from the epic is closed or moved to a new story".

**Approach:**
- Close two test gaps that guard the transport: stdout carries only JSON-RPC, and arguments bind through the real MCP SDK.
- Stop attaching raw `XmlException`s to `InvalidXml` errors.
- Make the bookkeeping true: resolved entries for what's fixed, entries for what moves to epic 2, and spine Deferred rows updated to what epic 1 found.
- Apply Phill's decisions below (2026-11-09).

**Decisions (Phill, 2026-11-09):**
- **Q1, won't-fix:** `ToolRunner.RunAsync`'s use case and call stay separate, and so does the `?? string.Empty` layering. Each tool has one use case, and the extra guard is harmless.
- **Q2, a new `Cancelled` error code:**
  - It's used only when the call's own token was cancelled (`cancellationToken.IsCancellationRequested`). Any other `OperationCanceledException` stays `Internal`.
  - It's retryable, because every epic 1 tool only reads.
  - It's a contract change: bump `contractVersion`, refresh the `ToolContractTests` snapshot, and amend AD-9's code list.
- **Q3:** `0x80080005` maps to `ComUnavailable` (retryable), because the integrity check already passed before COM was called (`ComGateway.cs:436`). Amend AD-9's row.
- **Q4:**
  - Close the branch-protection item, because AD-15 states it.
  - Add a `deferred-work.md` entry for the plugins repo: a check that the two validator pins match.
  - Phill adds a GitHub tag-protection rule for `v*` himself.
- **Matrix row 3, "Misnamed argument" (renegotiated by Phill, 2026-11-09):** it's met as "not Internal". The SDK rejects the call before the tool runs, with its own `isError` result. There's no `InvalidArgument` code. Rejecting unknown argument names, and naming the argument that failed, moves to epic 2 in `deferred-work.md`.
- **Q5:**
  - Add a `deferred-work.md` probe entry for epic 2 covering symbols in search queries, how reliable `FindMeta` is, and the guide skill's list and image `alt` claims.
  - Add one README line: the search cache gives no benefit when a search has more candidate pages than `search.cacheMaxMB` holds.

## Boundaries & Constraints

**Always:**
- Cleanup only. No tool's arguments or success output change. Error codes change only as Q2 and Q3 decide.
- `deferred-work.md` is append-only: add "resolved" or "moved" entries, never edit old ones.
- Spine edits go through the Fable final review before the PR. Memlog entries go in via `memlog.py append`.
- Tests use FakeGateway. Nothing in this story calls OneNote.
- Never quote real notebook names. MCP Test names are fine.

**Never:**
- Write tools, the AD-17 hash rules, or HRESULT triage. Those are epic 2.
- Changes in the `sentienceAdjacent/plugins` repo or in GitHub settings. Those are Phill's.
- Reworking `ToolRunner.RunAsync`'s signature, or the `?? string.Empty` layering (Q1).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error handling |
|---|---|---|---|
| Logging stays off stdout | The server host is built and logs at every level | stdout gets nothing; stderr gets one line per record | — |
| SDK argument binding | In-process client: `tools/list`, then `tools/call get_hierarchy {scope:"notebooks"}` | 12 tools listed; an `ok:true` envelope from FakeGateway | — |
| Misnamed argument | `tools/call get_page_content {page_id:"x"}` (wrong case) | Same envelope as a missing `pageId` | InvalidArgument, not Internal |
| Bad XML | The gateway returns malformed XML | `InvalidXml` with a cleaned message | No `XmlException` attached as the inner exception |
| Client cancels | The call's token is cancelled mid-call | `Cancelled`, retryable | — |
| Stray cancellation | An `OperationCanceledException` while the call's token is not cancelled | `Internal`, as today | — |
| CO_E_SERVER_EXEC_FAILURE | Activation returns `0x80080005` after the integrity check matched | `ComUnavailable`, retryable | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Server/Program.cs:59-70`: the logging setup (`ClearProviders`, `AddConsole` writing to stderr, `AddSimpleConsole(SingleLine)`, Serilog). It isn't reachable from tests.
- `src/OneNoteMcp.Server/ServerSetup.cs:29`: `AddOneNoteMcp`. Move the logging setup next to it as an extension the host calls.
- `tests/OneNoteMcp.Tests/ToolRunnerLoggingTests.cs`: uses its own `CapturingLogger`. Leave it as it is.
- ModelContextProtocol 2.3.1 (`Directory.Packages.props:6`): an in-process client/server pair over pipe streams. `LargeResultTestKit.cs:91-97` shows how tools are built for tests today.
- XML errors with `innerException: ex`:
  - `GetHierarchyUseCase.cs:86`
  - `GetPageContentUseCase.cs:80`
  - `FindPagesUseCase.cs:154`
  - `SearchResults.cs:74`
  - `ListGrantsUseCase.cs:92`
  Messages are already cleaned by `OneNoteXml.cs:25-28`.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md`. Already fixed but still open there:
  - StaThread Dispose (`StaThread.cs:84-98`)
  - the release tag check (`release.yml:42-52`)
  - LogInternal leaks (fixed by the cleaned messages)
  - XPSFile (story 1.21)
- Spine Deferred rows (`architecture-onenote-mcp.md`):
  - ~584 "header line over the inline threshold": 1.18's harness saw none, and `ToolRunner.cs:186` logs a warning if one happens.
  - ~587 "search XML carries `encrypted`": the 1.20 plan (line 152) found that it does.
  - ~588 "section `path` stability": never captured. Retarget it to epic 2's access story, beside the notebook `path` row (~579).
- Not recorded anywhere yet, so add to `deferred-work.md` for epic 2: modal dialogs block `UpdatePageContent` but not `CreateNewPage` (`LifecycleScenarioTests.cs:145`).

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Server/ServerSetup.cs`, `Program.cs`: move the logging setup into `ServerSetup.AddOneNoteMcpLogging(ILoggingBuilder, …)`. Program calls it, with no behaviour change.
- [x] `tests/OneNoteMcp.Tests/ServerLoggingTests.cs`: build the logging with the console streams redirected. Assert stdout stays empty and stderr gets single-line records.
- [x] `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs`: in-process client against the real server registration on FakeGateway. Cover the matrix's binding rows, and assert the listed tools match `ToolContractTests`' snapshot.
- [x] The five use cases above: drop `innerException: ex` from the `InvalidXml` throws, and keep the cleaned message. Update the tests that assert the inner exception, if any.
- [x] `deferred-work.md`: add "resolved" entries (with evidence) for the four fixed items, the xml-model port comment, and `FormatDate`. Add "moved to epic 2" entries for the `CreateNewPage` modal note and section `path` stability. Add one entry per Open Question answer.
- [x] Spine: update rows ~584, ~587 and ~588 as in the Code Map, set `updated:`, and add a memlog entry.
- [x] `src/OneNoteMcp.Core/ErrorCode.cs`, `src/OneNoteMcp.Core/Results/ToolErrors.cs:45` (retryable map), `ToolRunner.cs:68-71`: add `Cancelled` per Q2. Bump `<Version>` in `Directory.Build.props` per AD-14 (`contractVersion` in `tests/OneNoteMcp.Tests/Contract/tool-contract.json` is its major.minor), and refresh the snapshot. Add tests for both cancellation rows.
- [x] `src/OneNoteMcp.Com/HResultMap.cs:89`, `ComGatewayTests.cs:30-37`: map `0x80080005` to `ComUnavailable` with a userAction (restart OneNote, then retry) per Q3.
- [x] Spine AD-9: add `Cancelled` to the code list, and change the `0x80080005` row. Add a memlog entry for each.
- [x] `README.md`: one line on the search cache limit (Q5). Mention `Cancelled` wherever error codes are listed.
- [x] `plugin/skills/guide/SKILL.md`: add a row for `Cancelled` if the skill lists error codes.

**Acceptance Criteria:**
- Given the sweep is done, when `deferred-work.md` and the spine's Deferred table are read, then every epic 1 item is resolved, moved to a named epic or story, or closed with the owner's reason.
- Given the change, when CI and the integration suite run, then both stay green and `ToolContractTests`' snapshot is unchanged, unless Q2 or Q3 changes a code.

## Implementation Notes

- **Misnamed argument (matrix row 3):** there is no `InvalidArgument` code. With SDK 2.3.1 a missing `pageId` and a misnamed `page_id` both fail before the tool runs, as the SDK's own `isError` result ("An error occurred invoking 'get_page_content'.", no `structuredContent`). They match, and neither is our `Internal`. An unknown tool is a JSON-RPC `InvalidParams` error. `McpSdkBindingTests` pins both.
- **`Cancelled` retryability:** retryable for reads only, the same as `Timeout` (`ToolErrors.IsRetryable`), because Q2's reason is that every epic 1 tool reads. A cancelled write's `outcomeUnknown` is left to epic 2's pipeline (deferred-work).
- **Stray cancellation** keeps today's envelope exactly (`Internal`, "The call was cancelled.") through its own catch, not the generic "Unexpected …" one.
- **`0x80080005`'s userAction** comes from `HResultMap.ToException` ("Restart OneNote, then retry."), so the code's default `ComUnavailable` text stays for the other sources.
- **Version:** `0.7.0-rc.2` → `0.8.0-rc.1` (pre-1.0 contract change bumps the minor, AD-14). The snapshot only gains `Cancelled` in each tool's `error.code` enum.
- **Logging test** runs in a non-parallel xunit collection, since it swaps the process-wide `Console.Out`/`Console.Error`.

## Plan Change Log

- `InvalidXml` throws in `ReadAccess.cs:465` and `GrantCommand.cs:236` also dropped `innerException: ex`. They weren't in the Code Map's five, but the intent ("stop attaching raw `XmlException`s") covers them.
- AD-9's sentence on SDK errors before a tool runs said input-schema rejection is a JSON-RPC error. The binding test showed it isn't, so the sentence was corrected beside the planned AD-9 edits (memlog entry), for the same Fable review.
- `deferred-work.md` also closes or routes the entries the plan didn't list (the AD-14 bump check, the `NewerSchema` advice, the stderr-format test), so every epic 1 entry has an outcome.

## Review Triage Log

Pass 1 (2026-11-09):
- The quick lens raised 9 findings and the Fable final review of the spine raised 11, so 20 in all. I verified each against the source, merged them by cause into 15 entries, and rejected 2 more.
- Phill picked which to fix in `review/1-14-findings.md`. He chose all of them, and option (a) for matrix row 3.
- Every fix went to impl-1-14 as a patch.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| A | AD-7 step 7 and AD-9's outcomeUnknown sentence lack `Cancelled` (quick 1, Fable 1) | medium | patch | spine:156, :215 vs the new row :231 |
| B | The SDK sentence over-claims "misnamed" (Fable 2) | medium | patch | `GetHierarchyTool.cs:36`: an optional `startNodeId` is ignored when misnamed |
| M | 3 Deferred rows have no owner; the probe entry lacks the `encrypted` check (quick 5) | medium | patch | spine:577, :585, :588; DW entry 121 |
| L | The skill's `Cancelled` row contradicts its retryable rule (quick 3) | medium | patch | `SKILL.md:184` names only ComBusy and Timeout as exceptions |
| D | Header-line row: one figure for two runs, and a dangling possessive (Fable 4, 11) | low | patch | 1.20 plan lines 160 and 165 |
| E | Rationale in two spine rows (Fable 5) | low | patch | spine:228, :586; memlog 147 and 149 hold it |
| F | `0x80080005` on the silent-activation path gets the restart advice (Fable 6, quick 4) | low | patch (doc only) | `IProcessProbe.cs:27`: no OneNote counts as a match |
| G | The `Cancelled` row says "the client cancelled" (Fable 7) | low | patch | the code only sees the token |
| H | The access story is named two ways (Fable 8) | low | patch | spine:580 vs :581 |
| I | The skill says "already retried" (Fable 9, quick 2) | low | patch | `ComGateway.cs:296-297`: the reconnect trigger excludes `0x80080005` |
| J | The HResultMap comment claims an unobserved cause (Fable 10) | low | patch | the only observation is spike 1's elevation row |
| K | "optimisation" (Fable 11) | low | patch | spine:589 |
| N | The SDK test checks tool names only (quick 7) | low | patch | the task asked for the snapshot |
| O | The pinned-release item is closed before its rule exists (quick 8) | low | patch | DW entry 100 |
| R3 | Matrix row 3 is unmet as written (quick 6) | — | Phill: (a) | Accepted as met; epic 2 deferred entry added |
| x1 | Fable 3: the source never names `FindPages` | false | reject | `ProtectedSectionIntegrationTests.cs:80-82` reads the raw `FindPages` XML |
| x2 | Quick 9: the Fable gate is still open | false | reject | This pass closes it with Phill's choice |

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests`: all pass
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration`: passes or skips as before (read-only; Phill's OneNote must be running)

**Manual checks:**
- A name grep on the staged diff finds nothing.
- The Fable final review of the spine edits is done, and Phill chose which findings to fix.
