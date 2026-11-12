---
title: 'Name the argument in every argument error'
type: 'feature'
ticket: '14'
created: '2026-11-11'
status: 'built'
baseline_revision: '2a717a863c6842fac43ed2521c0466a0e645c8eb'
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

**Problem:** A missing or misnamed required argument, or a value the SDK can't bind (`get_page_content {page_id}`, `get_hierarchy {scope: "bogus"}`), returns the SDK 2.3.1's bare `An error occurred invoking '<tool>'.` with no code, no `structuredContent` and no hint which argument was wrong (deferred-work "say which argument failed to bind"; Phill, 2.5 Q2). Input schemas also don't tell clients up front that undeclared names are refused (Phill, 2.5 Q1).

**Approach:** A call-tool request filter checks the raw arguments against the matched tool's input schema before the SDK binds them. Any undeclared name, missing required argument, or value of the wrong JSON type or outside its enum fails with `InvalidArgument` (`retryable: false`) in the AD-9 envelope, with one message naming every bad argument and what it expects. Every tool's input schema gains `additionalProperties: false`. 2.5's after-binding check in `ToolRunner` moves into this filter. AD-9's wording changes to match, recorded in the spine memlog.

## Boundaries & Constraints

**Always:**
- The check reads the schema from `request.MatchedPrimitive` (an `McpServerTool`; the SDK matches before filters run). No matched tool (unknown tool) → call `next` unchanged, so it stays the SDK's JSON-RPC error.
- Strict schema conformance: `string` = JSON string; `boolean` = true/false; `integer` = a JSON number that `TryGetInt32` accepts (our integer parameters are `int?`); `null` only where `type` lists `"null"`; `array` checks each item against `items`; `enum` compares ordinally (case-sensitive, exact). Required = present (a present `null` is then a type check). Unknown names: always refused (2.5 rule), whatever `additionalProperties` says.
- The message names arguments and what each expects, never a caller's values (they may be page content, AD-9/AD-13). Unknown names stay JSON-quoted, cut to 64 and capped at 10 as in 2.5. A call with only unknown names keeps 2.5's exact message (`get_hierarchy doesn't declare the argument "scop". Its arguments are: startNodeId, scope.`).
- The refusal goes through `ToolRunner`: a fresh `correlationId`, one `tool call … result=InvalidArgument hresult=-` log line, no names or values logged, no gateway call, no grant dialog, no write lock.
- `additionalProperties: false` is added to the tool objects themselves (so `tools/list`, `ToolCollection` and `GetServices<McpServerTool>()` all agree); `tool-contract.json` differs from baseline only by that key in the 13 input schemas.
- `<Version>` stays `0.9.0-rc.1`, `contractVersion` stays `0.9` (no 0.9 tag shipped; 2.5 rode on the same bump).
- Public repo: no real notebook names or IDs.

**Never:** echoing argument values in a message or log; new error codes; changing output schemas or tool names; a post-binding rewrite of the SDK's generic error; touching OneNote, the integration suite, the stress harness, the `grant` CLI or `spikes/`; changing the spine's `status` or `updated`.

## I/O & Edge-Case Matrix

| Scenario | Input | Expected | Notes |
|---|---|---|---|
| Valid calls | `get_hierarchy {scope:"sections"}`, `get_page_content {pageId:"x"}`, `read_result {resultId:"x", page:null}` | as before (never `InvalidArgument`) | nullable `null` accepted |
| Misnamed required | `get_page_content {page_id:"x"}` | `InvalidArgument`: `get_page_content is missing the required argument "pageId" (a string). get_page_content doesn't declare the argument "page_id". Its arguments are: pageId, includeBinary.` | was SDK generic |
| Missing required | `get_page_content {}` / no arguments | names `"pageId"` missing | |
| Write tool, misnamed required | `create_page {section_id:"x"}` | names `sectionId` and `section_id`; no create | |
| Enum value | `get_hierarchy {scope:"bogus"}` | `get_hierarchy's argument "scope" must be one of: self, children, notebooks, sections, pages (it got a string that isn't one of them).` + trailer | was SDK generic; value not echoed |
| Enum case | `get_hierarchy {scope:"Sections"}` | `InvalidArgument` | strict (Phill, 2026-11-12) |
| Wrong kind | `pageId: 5`, `includeBinary:"true"`, `page: 1.5`, `page:"5"`, `run:[1]`, `pageId:null` | `InvalidArgument` naming the argument, expected type and the JSON kind it got | |
| Several problems | `get_hierarchy {scope:"bogus", extra:1}` | one message: scope's sentence, then `doesn't declare the argument "extra"`, then trailer | replaces 2.5's "SDK error first" |
| Every tool, every property | wrong-kind value per property | `InvalidArgument`, never the SDK's generic error | coverage proof |
| Unknown tool | `no_such_tool` | unchanged JSON-RPC `Unknown tool` | |
| Schema | `tools/list` | every `inputSchema` has `additionalProperties: false` | |

</intent-contract>

## Code Map

- `src/OneNoteMcp.Server/Tools/ArgumentNames.cs` -- 2.5's `Declared`, `Unknown`, `Message`, `Quote` (64-char cut, relaxed encoder). Reuse: grow it (or add `Tools/ArgumentCheck.cs`) with `Problems(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)` returning missing / invalid (name + expected text + got-kind) / unknown, and a message builder: sentences in order missing, invalid (schema order), unknown, then `Its arguments are: …` / `It takes no arguments.` Each sentence starts with the tool name. Expected text from the property schema: `a string`, `true or false`, `an integer`, `an array of strings or nulls`, `one of: a, b, c`, joined with ` or ` for unions (`an integer or null`).
- `src/OneNoteMcp.Server/ToolRunner.cs` -- remove `RejectUnknownArguments`, `InputSchema` and the `request` parameter; add a public method (e.g. `Refuse(string tool, OneNoteException error)`) that builds the failure envelope with a new correlationId, logs the one call line via `LogCall`, and returns `ToResult`. The exception carries `retryable: false` (check `OneNoteException`'s ctor for the parameter; `ToolErrors.From` prefers it) so the operation class doesn't matter.
- `src/OneNoteMcp.Server/ServerSetup.cs` -- in `AddOneNoteMcp`: `.WithRequestFilters(f => f.AddCallToolFilter(next => async (request, ct) => …))` running the check, resolving `ToolRunner` from `request.Services`; and after the `WithTools` chain, wrap every non-keyed `McpServerTool` service descriptor (factory or instance) so the created tool's `ProtocolTool.InputSchema` gets `"additionalProperties": false` (parse to `JsonObject`, set, re-serialise; `Tool.InputSchema` is settable). If the SDK doesn't allow that, record the alternative used in Implementation Notes.
- the 13 `src/OneNoteMcp.Server/Tools/*Tool.cs` -- drop the `RequestContext<CallToolRequestParams>? request = null` parameter and `request: request` added by 2.5 (nothing reads it now). Keep `McpServer? server`.
- `src/OneNoteMcp.Core/Results/ToolErrors.cs` (l.74) -- `InvalidArgument`'s userAction becomes one that fits every argument error, e.g. "Call again with the arguments the message names corrected: only names the tool declares, spelled exactly as listed, each with a value of the type it expects." Must stay distinct (`ToolErrorsTests`).
- `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs` -- rewrite `MisnamedArgument_FailsLikeAMissingOne_…`, `UnknownTool_…_AnUnbindableArgument_…` (keep the unknown-tool half) and `AnUnknownName_WithAnUnbindableValue_IsTheSdksError_First` to the new behaviour; add the matrix rows; an all-tools/all-properties wrong-kind theory driven by `tool-contract.json`; a `create_page` misnamed-required case asserting `gateway.CreateCalls` empty; the log check (call line only, no names/values). Update the userAction string asserted there. Keep `EveryTool_WithItsRequiredArguments_RefusesAnUndeclaredOne` (now proves the filter covers every tool).
- `tests/OneNoteMcp.Tests/ArgumentNamesTests.cs` (or a new `ArgumentCheckTests.cs`) -- unit tests for the checker and message: each type, union with null, array items, int32 range, enum exactness, null/absent arguments dictionary, combined message order, no values in messages.
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- regenerate from `ToolContractTests.Tools_MatchTheContractSnapshot`'s failure output.
- `plugin/skills/guide/SKILL.md` (~l.209) -- widen the `InvalidArgument` row to missing names, wrong types and enum values.
- `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- AD-9 bullet at ~l.233 and the `InvalidArgument` row at ~l.269 (wording in Design Notes). Don't change `status` or `updated`.
- `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md` -- append a `(decision by Phill)` line for 2.14's AD-9 change and an `(event)` line (Fable final review and Phill's approval pending); bump its `updated`.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` (~l.131) -- append a "Resolved (story 2.14)" entry for the "say which argument failed to bind" half.
- Don't touch: `Directory.Build.props`, `spikes/`, `tests/OneNoteMcp.Integration/`, `tickets.toml`.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Server/Tools/ArgumentNames.cs` (+ unit tests) -- schema check and message -- testable without the SDK.
- [x] `src/OneNoteMcp.Server/ToolRunner.cs`, the 13 `Tools/*Tool.cs` -- move the check out; add the refusal method -- one check, before binding.
- [x] `src/OneNoteMcp.Server/ServerSetup.cs` -- call-tool filter and `additionalProperties: false` -- the ticket's two contract changes.
- [x] `src/OneNoteMcp.Core/Results/ToolErrors.cs` -- userAction -- fits every argument error.
- [x] `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs`, `Contract/tool-contract.json` -- matrix through the SDK; regenerated snapshot -- verify line.
- [x] `plugin/skills/guide/SKILL.md`, spine AD-9, spine `.memlog.md`, `deferred-work.md` -- docs.

**Acceptance Criteria:**
- Given a server over the SDK, when `get_page_content {page_id:"x"}` or `create_page {section_id:"x"}` is called, then the result is the AD-9 failure envelope with `InvalidArgument`, `retryable: false`, a `correlationId`, and a message naming both the missing and the undeclared name, and nothing ran.
- Given `get_hierarchy {scope:"bogus"}`, when called, then `InvalidArgument` names `scope` and lists its allowed values, without echoing `bogus`.
- Given every tool and every property in the snapshot, when called with a wrong-kind value, then the result is `InvalidArgument`, never the SDK's generic error.
- Given the server, when a client calls `tools/list`, then every input schema declares `additionalProperties: false`, and the snapshot differs from baseline only by that.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, when run, then both pass.

## Implementation Notes

- Confirmed by Phill (2026-11-12, Inbox thread "2.14 argument errors"): strict schema conformance, and 2.5's after-binding check plus its `request` parameter removed. Phill also took all eight Fable review fixes and approved the AD-9 wording.

- Assumption (unattended, 2026-11-11): strict schema conformance. Values the SDK used to coerce (`"5"` for an integer, a case-variant enum such as `"Sections"`) are now `InvalidArgument`. The ticket says to check against the input schema, and the project already rejects integer enum values (`StrictStringEnumConverter`). Open question for Phill.
- Assumption (unattended, 2026-11-11): 2.5's after-binding check moves into the filter and the per-tool `request` plumbing is removed, since the filter sees every call first and nothing would read it.
- Assumption (unattended, 2026-11-11): no post-binding safety net that rewrites the SDK's generic error; the all-tools/all-properties theory proves the check covers every binding failure our schemas allow.
- Build: the checker is a new `Tools/ArgumentCheck.cs`; `ArgumentNames` keeps 2.5's API and exposes its sentence pieces (`UnknownSentence`, `Trailer`, `Quote`) internally, so the only-unknown message is 2.5's exactly. The 13 tool files were restored to their pre-2.5 text (2.5's plumbing was the only change to them since).
- Build: `additionalProperties: false` is set by rewriting each non-keyed `McpServerTool` descriptor in `AddOneNoteMcp` (`CloseInputSchemas`); `Tool.InputSchema`'s setter accepted it, no alternative needed. The SDK's `CallToolFilters` doc text says they wrap the handler for tools "not found in the collection", but in 2.3.1 they wrap every call and `MatchedPrimitive` is set; the binding tests prove it.
- Build: wording choices the plan left open. A union whose part already says "or" joins with ", or " (`an array of strings or nulls, or null`, `true or false, or null`). A number refused by an integer property says `a number that isn't a 32-bit integer` (1.5, 1.0, 2^32). A boolean got-kind is `a boolean`. A bad array item says `an array with an item that is <kind>`. One sentence per missing or invalid argument; unknown names stay one sentence.
- Build: `Refuse` logs `ms=0` (nothing ran) and uses `OperationClass.Read` for `ToolErrors.From`; the exception's own `retryable: false` decides.

## Plan Change Log

## Review Triage Log

### 2026-11-11 — Review pass
- verdicts: 2 findings — high 0, medium 1, low 1, false 0, maybe-false 0
- findings:
  - `medium` `patch` Strict checking also refuses `null` for the 8 optional plain-`string` arguments (`startNodeId` ×3, `pageContentObjectId` ×3, `ref`, `expand`) that the tools accepted before (`?? string.Empty`); untested, and missing from the open question's list of newly refused inputs (memlog, deferred-work) — verified at `ArgumentCheck.cs:180` and the tools' `?? string.Empty`. Kept strict (plan's rule); added `NullForAnOptionalStringArgument_IsInvalidArgument` (`get_hierarchy {startNodeId:null}`) and named the case in the memlog decision line and the deferred-work entry.
  - `low` `patch` `deferred-work.md:253` (2.9/2.10 entry) quotes the old `InvalidArgument` userAction and says it "fits only unknown names", which 2.14 changed — appended to its evidence that 2.14 widened the default userAction, and that AD-7's refusals still need their own.

## Design Notes

Spine AD-9, replacing the bullet "Errors the MCP SDK raises before a tool runs are outside the envelope. …":

> Errors the MCP SDK raises before a tool runs are outside the envelope: an unknown tool is a JSON-RPC error. Every argument error is ours (story 2.14, Phill 2026-11-11): before the SDK binds a call, the server checks its arguments against the tool's input schema, and an undeclared name, a missing required argument, or a value of the wrong type or outside its enum fails with `InvalidArgument` inside the envelope, naming each argument and what it expects, never its value. Every input schema declares `additionalProperties: false`. The SDK's generic `isError` result (a text block, no `structuredContent`) would mean the check missed a case, which is a bug. History: until story 2.5 the SDK ignored a misnamed optional argument (SDK 2.3.1, story 1.14); 2.5 refused it after binding; 2.14 moved the check before binding.

`InvalidArgument` row: "an argument that doesn't fit the tool's input schema: an undeclared name, a missing required argument, or a value of the wrong type or outside its enum (stories 2.5, 2.14); an argument the server refuses: …(unchanged)".

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `git diff -- tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- expected: only `"additionalProperties": false` added (13 input schemas).

## Auto Run Result

- **Change:** before the SDK binds a call, a call-tool filter checks its arguments against the tool's input schema. An undeclared name, a missing required argument, or a value of the wrong JSON type or outside its enum returns `InvalidArgument` (`retryable: false`) in the AD-9 envelope. One message names each argument and what it expects, never its value. Every input schema declares `additionalProperties: false`. 2.5's after-binding check moved into the filter. Only an unknown tool stays outside the envelope.
- **Files:**
  - `src/OneNoteMcp.Server/Tools/ArgumentCheck.cs` (new): the schema check and message.
  - `src/OneNoteMcp.Server/Tools/ArgumentNames.cs`: 2.5's sentence pieces exposed internally.
  - `src/OneNoteMcp.Server/ServerSetup.cs`: the filter and `CloseInputSchemas`.
  - `src/OneNoteMcp.Server/ToolRunner.cs`: 2.5's check and `request` parameter removed; `Refuse` added.
  - the 13 `src/OneNoteMcp.Server/Tools/*Tool.cs`: 2.5's `request` plumbing removed (back to their pre-2.5 text).
  - `src/OneNoteMcp.Core/Results/ToolErrors.cs`: a wider `InvalidArgument` userAction.
  - `tests/OneNoteMcp.Tests/ArgumentCheckTests.cs` (new) and `McpSdkBindingTests.cs`: the matrix, a wrong-kind value for every property of every tool, and null for an optional string.
  - `tests/OneNoteMcp.Tests/Contract/tool-contract.json`: `additionalProperties: false` in 13 input schemas.
  - `plugin/skills/guide/SKILL.md`: the `InvalidArgument` row.
  - the spine (AD-9 bullet and `InvalidArgument` row) and its `.memlog.md`.
  - `deferred-work.md`: a resolved entry, and a note on the 2.9/2.10 entry.
- **Review:** quick lens, 2 findings (1 medium, 1 low), both patched; 0 deferred, 0 rejected.
- **Follow-up review recommended:** false. A first pass patched no high finding and only one medium.
- **Verification:**
  - `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors.
  - `dotnet test tests/OneNoteMcp.Tests`: 1,538 passed, 0 failed, 0 skipped.
  - The contract diff is only `"additionalProperties": false`, 13 times.
  - Integration tests, the stress harness and the server exe were not run, because a live probe sitting was using OneNote.
- **Residual risks:**
  - Strict conformance refuses inputs the SDK used to coerce or accept: `"5"` for an integer, a case-variant enum value, and `null` for an optional string. This is open for Phill.
  - The SDK 2.3.1 doc text says call-tool filters wrap only unmatched tools, but in practice they wrap every call (the tests prove it). An SDK upgrade could change that, and the binding tests would catch it.
  - The AD-9 spine change awaits the Fable final review and Phill's approval.
