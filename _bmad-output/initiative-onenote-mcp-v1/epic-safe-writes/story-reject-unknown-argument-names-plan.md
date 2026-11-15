---
title: 'Reject unknown argument names'
type: 'feature'
ticket: '5'
created: '2026-11-11'
status: done
baseline_revision: 'c427af09c7645615e4f2b9d9ccd2b64537c40ef7'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The MCP SDK (2.3.1) silently ignores an argument name a tool doesn't declare, so a misnamed optional argument (`scop`, `Style`) runs the tool with its default. For a write tool that means a page created with a layout the caller didn't ask for, and no sign anything was wrong (deferred-work "Misnamed argument"; AD-9).

**Approach:** Every tool refuses any argument name its input schema doesn't declare, with the AD-9 code `InvalidArgument` (`retryable: false`) inside the envelope, naming the argument, before the use case runs. The check runs in `ToolRunner` after the SDK has bound the call, so the SDK's own argument errors still come first (AD-9). The new code changes the output schema, so the contract snapshot is regenerated; the contract stays `0.9` (2.4 plan Q3: 2.5 rides on 2.4's bump, and no release has shipped since).

## Boundaries & Constraints

**Always:**
- Declared names are the keys of the matched tool's `inputSchema.properties`, compared ordinally (case-sensitive, as the SDK binds). No declared properties (`list_grants`) means every name is unknown.
- The check runs inside `ToolRunner.RunAsync`'s `try`, before `run` is called, so the refusal carries a `correlationId`, is logged as one call line with `result=InvalidArgument`, and nothing else happens: no gateway call, no grant dialog, no write lock, no settings read beyond what the runner already does.
- `message` names every unknown argument, sorted ordinally, each JSON-quoted and cut to 64 characters (with `…`), at most 10 named then "and N more"; it also lists the tool's declared names. `userAction` (one per-code text in `ToolErrors`) says to call again using only the declared names, spelled exactly. Argument names and values are never logged (AD-13).
- `InvalidArgument` is added to `ErrorCode` after `InvalidXml` (AD-9 table order), not retryable for any class, with its own distinct `userAction`.
- Errors the SDK raises first stay as they are, outside the envelope: unknown tool (JSON-RPC), a missing or misnamed required argument, and an unbindable value (AD-9). `MisnamedArgument_FailsLikeAMissingOne_InTheSdk_BeforeTheToolRuns` keeps passing unchanged.
- Input schemas do not change (`tool-contract.json` differs only by the new enum value in each `outputSchema`). `<Version>` stays `0.9.0-rc.1`, `contractVersion` stays `0.9`.
- Repo is public: no real notebook names or IDs in tests or docs (MCP Test and made-up IDs only).

**Never:** `additionalProperties: false` or any other input-schema change; a custom message for the SDK's missing-required error; any spine edit; touching OneNote, the integration suite, the stress harness, the `grant` CLI or the server exe; any other new error code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Declared names only | `get_hierarchy {scope: "sections"}` | runs as before | — |
| Misnamed optional, read tool | `get_hierarchy {scop: "sections"}` | envelope `ok:false`, `InvalidArgument`, message names `"scop"` and lists `startNodeId, scope`; gateway not called | `retryable: false`, `correlationId` set |
| Misnamed optional, write tool | `create_page {sectionId: "x", Style: "blankPageNoTitle"}` | `InvalidArgument` naming `"Style"`; no create, no grant check, no lock hold | not retryable, no `outcomeUnknown` |
| Several unknown names | `find_pages {query: "a", zeta: 1, alpha: 2}` | one error naming `"alpha"` then `"zeta"` | — |
| Tool with no arguments | `list_grants {x: 1}` | `InvalidArgument` naming `"x"` | — |
| Every tool | required args valid + `bogusArgument` | `InvalidArgument` for each of the 13 tools | proves every tool passes the request to the runner |
| Unknown name plus an SDK binding failure | `get_hierarchy {scope: "bogus", extra: 1}` | the SDK's generic `isError` result, no `structuredContent` | SDK error first (AD-9) |
| Misnamed required | `get_page_content {page_id: "x"}` | unchanged SDK generic error | as today |
| Over-long or many names | 12 unknown names, one 200 chars | 10 named (the long one cut to 64 + `…`), then "and 2 more" | — |
| Direct call without a request | tool method called in a unit test with no request context | runs, no check (only the SDK path supplies the request) | — |

</intent-contract>

## Code Map

- `src/OneNoteMcp.Core/ErrorCode.cs` -- add `InvalidArgument` after `InvalidXml`, with a summary citing AD-9 (unknown argument names, story 2.5; AD-7 refusals later).
- `src/OneNoteMcp.Core/Results/ToolErrors.cs` -- `UserAction` gets an `InvalidArgument` line; `IsRetryable` default (`false`) already fits. `ToolErrorsTests` iterates `Enum.GetValues<ErrorCode>()`, so the new code is covered once its action is distinct.
- `src/OneNoteMcp.Server/ToolRunner.cs` -- `RunAsync` gains a trailing optional `RequestContext<CallToolRequestParams>? request = null`. First statement inside the existing `try`: if `request` is not null, compute unknown names and throw `OneNoteException(ErrorCode.InvalidArgument, message)`. Put the name logic in a small static helper in Server (e.g. `Tools/ArgumentNames.cs`: `Unknown(JsonElement inputSchema, IReadOnlyDictionary<string, JsonElement>? arguments)` and the message builder) so it is unit-testable without the SDK. Declared names come from `request.MatchedPrimitive` (an `McpServerTool`) → `ProtocolTool.InputSchema`; if `MatchedPrimitive` isn't an `McpServerTool`, look the tool up by `request.Params.Name` in `request.Server.ServerOptions.ToolCollection`; if neither yields a schema, that's a bug → throw `Internal` (fail closed; the SDK tests prove it never happens).
- `src/OneNoteMcp.Server/Tools/*Tool.cs` (13 tools: CreatePage, FindMeta, FindPages, GetBinaryPageContent, GetHierarchy, GetHierarchyParent, GetHyperlinkToObject, GetPageContent, GetSpecialLocation, GetWebHyperlinkToObject, ListGrants, NavigateTo, ReadResult) -- each tool method gains a trailing `RequestContext<CallToolRequestParams>? request = null` (after `CancellationToken`/`McpServer? server`) and passes it to `runner.RunAsync`. The SDK binds it from the request and leaves it out of the input schema (it is one of `RequestServiceProvider`'s augmented types, like `McpServer`). If the SDK 2.3.1 does not bind it (the snapshot test shows a new `request` property, or the binding tests see `null`), fall back to an `IServiceProvider`-free alternative: a call-tool filter (`WithRequestFilters(f => f.AddCallToolFilter(...))` in `ServerSetup.AddOneNoteMcp`) that puts `context` into `context.Items` / an `AsyncLocal` the runner reads, and record the switch in Implementation Notes. Existing direct-call unit tests keep compiling (optional parameter).
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- regenerate from the `ToolContractTests.Tools_MatchTheContractSnapshot` failure output; the only diff must be `"InvalidArgument"` in each `outputSchema` code enum.
- `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs` -- the SDK-path tests for the matrix (uses `FakeGateway`; `gateway.Calls` and the fake's create record show nothing ran). Add a `CreatePage` fake check that no create was recorded. The all-tools theory reads names and required arguments from `tool-contract.json` and fills required ones (`string` → `"x"`, `get_special_location.location` → `"backUpFolder"`); keep `Assert.Equal(13, …)`.
- `tests/OneNoteMcp.Tests/` -- a unit test file for the helper (sorting, quoting, 64-char cut, 10-name cap, empty schema, null arguments, case-sensitivity).
- `plugin/skills/guide/SKILL.md` (error table at ~l.190–210) -- one `InvalidArgument` row: the message names the argument; fix the name to one the tool declares and call again; don't drop the argument to get past the error.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` (~l.127) -- append a "Resolved (story 2.5)" entry for the misnamed-argument row: optional names now `InvalidArgument`; a misnamed required argument stays the SDK's generic error by AD-9.
- Don't touch: the spine, `Directory.Build.props`, `spikes/`, `tests/OneNoteMcp.Integration/`.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/ErrorCode.cs`, `src/OneNoteMcp.Core/Results/ToolErrors.cs` -- add the code and its userAction -- AD-9 closed list, amended in 2.3.
- [x] `src/OneNoteMcp.Server/Tools/ArgumentNames.cs` (new) + unit tests -- unknown-name computation and message -- testable without the SDK.
- [x] `src/OneNoteMcp.Server/ToolRunner.cs` -- request parameter and the check before `run` -- one place for every tool.
- [x] the 13 `src/OneNoteMcp.Server/Tools/*Tool.cs` -- pass the request context -- the runner needs the raw arguments.
- [x] `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- regenerate -- AD-14 snapshot.
- [x] `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs` -- matrix rows through the SDK -- the ticket's verify line.
- [x] `plugin/skills/guide/SKILL.md`, `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- skill row; resolved entry.

**Acceptance Criteria:**
- Given a running server over the SDK, when `get_hierarchy` or `create_page` is called with a misnamed optional argument, then the result is the AD-9 failure envelope with `InvalidArgument`, `retryable: false`, a `correlationId`, and a message naming the argument, and no gateway call or write happened.
- Given every tool in the snapshot, when called with its required arguments plus an undeclared one, then each returns `InvalidArgument`.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, when run, then both pass, and `tool-contract.json` differs from the baseline only by the added enum value.

## Implementation Notes

- Assumption (unattended, 2026-11-11): the check sits in `ToolRunner` after SDK binding rather than in a call-tool filter before it, because AD-9 says the SDK's argument errors come first and `InvalidArgument` applies "once the SDK has accepted the call", inside the envelope.
- Assumption (unattended, 2026-11-11): the message also lists the tool's declared names, so the model can correct itself in one step; names are schema text, never content.
- Assumption (unattended, 2026-11-11): no `additionalProperties: false` in input schemas. AD-14 makes schemas a contract and the spine doesn't ask for it; flagged to Phill.
- Assumption (unattended, 2026-11-11): the caller's unknown names are echoed (cut and capped) in `message` but never logged; AD-9's "messages never contain page content" is met because the names are the caller's own input, not note content.
- Assumption (unattended, 2026-11-11): no contract or `<Version>` bump; 2.4 plan Q3 says 2.5 rides on `0.9.0-rc.1`, and no 0.9 tag exists.
- Build (2026-11-11): SDK 2.3.1 binds a `RequestContext<CallToolRequestParams>` tool parameter and leaves it out of the input schema (the regenerated snapshot has no `request` property), so the call-tool-filter fallback wasn't needed.
- Build: `ArgumentNames.Unknown` takes `(IReadOnlyList<string> declared, IEnumerable<string>? argumentNames)` instead of the Code Map's `(JsonElement inputSchema, IReadOnlyDictionary<…>?)`; `Declared(JsonElement)` reads the schema separately. The runner reads the schema once and passes the declared list to both `Unknown` and `Message`, and `Params.Arguments` is an `IDictionary`, so its `Keys` fit either way.
- Build: an unknown name is cut to 64 characters, then `…`, before it is JSON-quoted (`"aaa…"`); the cut never splits a surrogate pair. Quoting uses the relaxed encoder, so non-ASCII names stay readable, while quotes and control characters are escaped. Duplicates can't happen (JSON object keys), but `Unknown` de-duplicates anyway. Declared names are listed unquoted in schema order; with none, the message says "It takes no arguments."
- Build: the schema lookup that fails closed throws `Internal` with "No input schema was found for <tool>." (no argument names), so `LogInternal` can't log a caller's names.
- Build: `InvalidArgument`'s `userAction` is "Call again using only the argument names the tool declares, spelled exactly as listed." It fits only the unknown-name case; the later AD-7 refusals (story 2.9's Outline check, `delete_page_content`'s target) should set their own `userAction` on the exception, which `ToolErrors.From` already prefers.
- Build: the "direct call without a request" matrix row is covered by the existing direct-call tool tests (`CreatePageToolTests`, `GetHierarchyToolTests`, …), which pass no request and still run.
- Build: `McpSdkBindingTests` gained a capturing logger provider, so the read-tool test checks the single `result=InvalidArgument` call line and that neither the unknown name nor the message reaches any log. Mutation check: passing `request: null` from `NavigateToTool` made the all-tools theory fail for `navigate_to` only (reverted).
- Files touched: `src/OneNoteMcp.Core/ErrorCode.cs`, `src/OneNoteMcp.Core/Results/ToolErrors.cs`, `src/OneNoteMcp.Server/ToolRunner.cs`, `src/OneNoteMcp.Server/Tools/ArgumentNames.cs` (new), the 13 `src/OneNoteMcp.Server/Tools/*Tool.cs`, `tests/OneNoteMcp.Tests/ArgumentNamesTests.cs` (new), `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs`, `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (13 added `"InvalidArgument"` lines only), `plugin/skills/guide/SKILL.md`, `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md`.
- Verification: `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 1443 passed, 0 failed. Integration tests, the stress harness, the grant CLI and the server exe were not run (session limits).

## Plan Change Log

## Review Triage Log

### 2026-11-11 — Review pass
- verdicts: 0 findings received — high 0, medium 0, low 0, false 0, maybe-false 0
- findings:
  - The quick lens (`review-2-5-quick`, context-free, diff at the session scratchpad) was launched, but its findings hadn't arrived when the orchestrator had to hand back. The orchestrating session reads its report and triages it. The orchestrator's own read of the diff (`ArgumentNames.cs`, `ToolRunner.RunAsync`, `ErrorCode`, `ToolErrors`, the 13 tools, `SKILL.md`, `deferred-work.md`) found nothing to patch. `tool-contract.json` differs from the baseline only by 13 added `"InvalidArgument"` lines.


Pass 2 (orchestrator, 2026-11-11): the build's reviewer `review-2-5-quick` and a second reviewer `review-2-5-lead` reported after commit acbfad9. No high or medium findings; all acceptance criteria met.

| # | Finding | Verdict | Route | Action |
|---|---|---|---|---|
| Q1 | deferred-work marks the misnamed-argument entry resolved, but "say which argument failed to bind" isn't done | low | patch | Reworded as partly resolved; the rest is Phill's question 2 |
| Q2 | `EveryTool_…_RefusesAnUndeclaredOne`'s "nothing ran" asserts cover only the 3 gateway methods `FakeGateway` records | low | rejected | The `InvalidArgument` assertion already proves the check ran first; making every fake method record adds complexity for no new protection |
| L1 | `InvalidArgument`'s default `userAction` fits only unknown names; AD-7's refusals reuse the code | low | defer | deferred-work entry for 2.9/2.10: set their own `UserAction` |
| L2 | Plan dates read 2026-11-11 while local time was 2026-11-10 | low | rejected | UTC date; harmless |

## Design Notes

The SDK binds arguments, then invokes the tool method; only then does `ToolRunner` see the request. So an unbindable value or a missing required argument fails in the SDK first, and every call that reaches a tool is checked in one place. Message shape:

```
get_hierarchy doesn't declare the argument "scop". Its arguments are: startNodeId, scope.
```

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `git diff -- tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- expected: only `"InvalidArgument"` lines added.

## Auto Run Result

- **Change:** every tool refuses an argument name its input schema doesn't declare. The call returns `InvalidArgument` (`retryable: false`) in the AD-9 envelope, naming the argument(s) and listing the tool's own, before the use case runs. The check runs after SDK binding, so the SDK's own argument errors still come first.
- **Files:**
  - `src/OneNoteMcp.Core/ErrorCode.cs`: the new code.
  - `src/OneNoteMcp.Core/Results/ToolErrors.cs`: its userAction.
  - `src/OneNoteMcp.Server/Tools/ArgumentNames.cs` (new): declared and unknown names, and the message.
  - `src/OneNoteMcp.Server/ToolRunner.cs`: the check, reading the matched tool's schema.
  - the 13 `src/OneNoteMcp.Server/Tools/*Tool.cs`: each passes `RequestContext<CallToolRequestParams>` to the runner (SDK 2.3.1 binds it and keeps it out of the schema).
  - `tests/OneNoteMcp.Tests/Contract/tool-contract.json`: the enum value.
  - `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs` and `ArgumentNamesTests.cs` (new): the matrix rows.
  - `plugin/skills/guide/SKILL.md`: an `InvalidArgument` row.
  - `deferred-work.md`: the resolved entry.
- **Review:** quick lens launched; its findings weren't received before hand-back (see triage log). 0 patched, 0 deferred, 0 rejected.
- **Follow-up review recommended:** false. Nothing was patched; the quick lens's pending report is the only open item.
- **Verification:** `dotnet build OneNoteMcp.slnx -warnaserror`: 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 1,443 passed, 0 failed, 0 skipped. The contract diff is only `"InvalidArgument"` (13 lines). `<Version>` stays `0.9.0-rc.1` and the contract stays `0.9`.
- **Residual risks:** a caller's unknown names are echoed (cut and capped) in `message`. A client that relied on extra arguments being ignored now gets an error. Neither the stress harness nor the skill sends undeclared names (checked).

