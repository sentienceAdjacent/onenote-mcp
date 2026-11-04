---
title: 'Result envelope, error codes, operation classes and tool contract'
type: 'feature'
ticket: '3'
created: '2026-11-04'
status: 'built'
baseline_revision: 'b5d430e814825fb0ad376be1608690da1252e25e'
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

**Problem:** `get_hierarchy` returns bare text and `code: message` errors, every COM failure maps to `Internal`, tools carry no operation class, and nothing guards the public tool contract (AD-5, AD-9, AD-14).

**Approach:** Add the AD-9 envelope (structured content, `isError`, `correlationId`), the per-code retryable/userAction table, the `OperationClass` enum and one Core use case per tool, the HRESULT map in Com with names copied from `OneNoteProbe.psm1`, and a tool-contract snapshot test with the contract version in `serverInfo`. `get_hierarchy` adopts all of it.

**Decisions (Phill, 2026-11-04):**
- Plan size: keep as one plan.
- HRESULT breadth: by family. The does-not-exist object codes map to `ObjectNotFound` and the XML codes to `InvalidXml` (see Design Notes). AD-9's Source column is amended to match; no new codes. The spine edit gets a Fable review before the PR.
- `correlationId`: both envelopes carry `meta.correlationId`; the error envelope is `{ok:false, error, meta:{correlationId}}`.
- Contract version: one number. `<Version>` in `Directory.Build.props` (start `0.1.0`) is the release tag (AD-19) and `serverInfo.version`; AD-14's rules apply to its major.minor; the snapshot stores `"0.1"`.

## Boundaries & Constraints

**Always:**
- Spine dependencies only; Core never references Com or Server (AD-2). The HRESULT map lives only in Com (AD-9).
- HRESULT names exactly as `spikes/com-capabilities/OneNoteProbe.psm1` lists them, never from memory.
- Tools never throw; gateways throw only `OneNoteException`. Messages never contain page content.
- `ErrorCode` stays the closed AD-9 list; no new codes.
- `outcomeUnknown: true` always forces `retryable: false` and userAction "re-read, then retry if the change is absent".

**Never:**
- Retries, backoff, timeouts, reconnect (story 5); settings or file logs (story 4); new tools or scopes (story 7); basis in `meta` (stories 7–8).
- Changing returned XML (AD-4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| HAPPY_PATH | gateway returns notebooks XML | `isError` false; structured `{ok:true, xml, meta:{correlationId}, warnings:[]}`; `xml` byte-identical | none |
| NOT_RUNNING | gateway throws `OneNoteNotRunning` | `isError` true; `{ok:false, error:{code:"OneNoteNotRunning", retryable:true, userAction, message}}` | no `hresult` |
| MAPPED_HRESULT | COM throws `0x80042014` | `ObjectNotFound`, `hresult:"0x80042014"`, message names `hrObjectDoesNotExist` | retryable false |
| UNMAPPED_HRESULT | COM throws `0x80004005` | `Internal`, hresult kept, message names `E_FAIL` | retryable false |
| UNKNOWN_HRESULT | HRESULT in no table | `Internal`, hresult kept, message shows hex only | |
| READ_VS_WRITE | `ComUnavailable`/`Timeout` | retryable true for `Read`, false for any other class | |
| OUTCOME_UNKNOWN | exception flagged outcomeUnknown | `outcomeUnknown:true`, `retryable:false`, the fixed userAction | |
| UNEXPECTED | gateway throws non-OneNoteException / cancel | `Internal` envelope, tool does not throw | |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/ErrorCode.cs`, `OneNoteException.cs` -- exist (story 2). Add `bool OutcomeUnknown` to the exception (ctor optional arg); story 5 sets it.
- `src/OneNoteMcp.Com/ComGateway.cs` -- `RunAsync` and `EnsureApplication` wrap every exception as `Internal`; swap in the HRESULT map. Keep the disconnect release (`0x800706BA`/`0x80010108`) and the probe check.
- `src/OneNoteMcp.Server/Tools/GetHierarchyTool.cs` -- current try/catch + `Text()`; becomes a thin call through the runner. Keep the `[McpServerTool]` name and annotations, and the `startNodeId` parameter.
- `src/OneNoteMcp.Server/Program.cs` -- DI wiring; move into a shared registration so the contract test builds the same server.
- `spikes/com-capabilities/OneNoteProbe.psm1:36-100` -- `$script:HResultNames`: 49 OneNote codes (no `0x80042021`) plus 13 generic COM/RPC codes. Source of truth for names.
- Spike findings: `GetHierarchy`/`FindPages` with a bogus ID return `0x80042014` (U5, row 31).
- `tests/OneNoteMcp.Tests/GetHierarchyToolTests.cs`, `FakeGateway.cs` -- rewrite assertions for the envelope; keep `ThrowingGateway`.
- `tests/OneNoteMcp.Integration/ComGatewayIntegrationTests.cs` -- `GetHierarchy_UnknownStartId_IsInternalWithHResult` becomes `ObjectNotFound`.
- Workspace: a Claude session is running `src/OneNoteMcp.Server/bin/Debug/.../OneNoteMcp.Server.exe`, so the default output is locked. Build and test with `--artifacts-path %TEMP%\onenote-mcp-artifacts` (e.g. `dotnet build OneNoteMcp.slnx -warnaserror --artifacts-path ...`). Do not commit or push; the orchestrator does.
- SDK 2.3.1: `CallToolResult.StructuredContent` (JSON object), `McpServerToolAttribute.OutputSchemaType`, `McpServerOptions.ServerInfo` (`Implementation.Version`), registered `McpServerTool.ProtocolTool` gives name and schemas.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/OperationClass.cs` -- enum of the eight AD-7 classes -- AD-5.
- [x] `src/OneNoteMcp.Core/Results/*.cs` -- `ToolSuccess` (xml?, meta, warnings), `ToolWarning` (code, message, userAction?), `ToolError` (code, hresult?, retryable, outcomeUnknown?, userAction, message) and `ToolErrors.From(OneNoteException, OperationClass)` holding the per-code retryable and userAction table (Design Notes) -- one envelope (AD-9).
- [x] `src/OneNoteMcp.Core/UseCases/IUseCase.cs`, `GetHierarchyUseCase.cs` -- `OperationClass Class`; use case takes a `ToolCallContext` (correlationId) and returns `ToolSuccess` -- AD-5; journal needs the id later.
- [x] `src/OneNoteMcp.Com/HResultMap.cs` -- internal: `Name(int)` and `ToErrorCode(int)`; ComGateway messages read `"{method} failed: {name} (0x{hr:X8})."` -- AD-9.
- [x] `src/OneNoteMcp.Com/ComGateway.cs` -- use the map for call and activation failures.
- [x] `src/OneNoteMcp.Server/ToolRunner.cs` -- creates the correlationId, runs the use case, catches everything, builds `CallToolResult` (structuredContent + one text block holding the same JSON, `isError`), logs one stderr line (correlationId, tool, code, hresult, ms; no XML).
- [x] `Directory.Build.props` -- `<Version>0.1.0</Version>`.
- [x] `src/OneNoteMcp.Server/ServerSetup.cs` + `Program.cs` -- one `AddOneNoteMcp()` registration; `ServerInfo` name `OneNoteMcp`, version from the assembly's `<Version>`.
- [x] `src/OneNoteMcp.Server/Tools/GetHierarchyTool.cs` -- thin: use case through the runner; `OutputSchemaType` = envelope DTO.
- [x] `tests/OneNoteMcp.Tests/HResultMapTests.cs` -- parse the psm1 table and assert the C# names match it exactly; one expected code for every entry (Design Notes).
- [x] `tests/OneNoteMcp.Tests/ToolErrorsTests.cs`, `GetHierarchyToolTests.cs` -- the I/O matrix.
- [x] `tests/OneNoteMcp.Tests/ToolContractTests.cs` + `Contract/tool-contract.json` -- build the server via `AddOneNoteMcp()`, snapshot `{contractVersion, tools:[{name, inputSchema, outputSchema, annotations}]}` with `description` keys stripped; on mismatch print the actual JSON. Assert `serverInfo.version`'s major.minor equals `contractVersion`.
- [x] `tests/OneNoteMcp.Integration/ComGatewayIntegrationTests.cs` -- unknown ID → `ObjectNotFound`, hresult `0x80042014`.
- [x] Spine AD-9 (orchestrator, not the implementer) -- Source column for `ObjectNotFound`/`InvalidXml` lists the HRESULT families; error shape gains `meta:{correlationId}`; AD-14 notes the single version; memlog entries; Fable review before the PR.

**Acceptance Criteria:**
- Given a renamed tool or a changed parameter, when unit tests run, then the contract test fails and prints the new snapshot.
- Given every HRESULT in the psm1 table, when mapped, then its name equals the psm1 name and its code equals the plan's mapping.
- Given Claude Code connected to the rebuilt server, when `get_hierarchy` is called with a bogus ID, then the envelope comes back with `isError: true` and `ObjectNotFound`.

## Implementation Notes

- Implemented by a subagent from the plan; builds and tests used `--artifacts-path %TEMP%\onenote-mcp-artifacts` because the default exe is locked by a running Claude session.
- The SDK's schema generator marked positional record parameters required; `hresult`/`outcomeUnknown` became optional parameters and a contract test checks both envelope shapes carry every required property.
- The psm1 table has 49 OneNote codes plus 12 generic codes (the Code Map said 13); the test reads the file, so the count is not hard-coded.
- `ObjectNotFound` userAction (orchestrator, after the Fable spine review): "Re-read the hierarchy or page and use a current ID. If the notebook itself is missing, open it in OneNote."
- Spine AD-9/AD-14 amended (orchestrator), Fable-reviewed: 9 findings, all applied with Phill's approval; memlog entries 104-107.

## Plan Change Log

## Review Triage Log

**Pass 1 (quick lens):** high 0, medium 1, low 3, false 2, maybe-false 0.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | Review patch asserts the one-sentence ObjectNotFound userAction; test would fail | false | reject | The working tree uses `Assert.StartsWith` and passes (437/437); the patch was staged one edit early. Regenerated at the end. |
| 2 | Default console formatter writes each tool-call log on two lines (plan: one stderr line) | medium | patch | `AddConsole` uses the simple formatter without `SingleLine`; patched to single-line, stderr threshold kept. |
| 3 | `StripDescriptions` removes a `description` key under schema `properties`, hiding a future parameter of that name from the contract test | low | patch | Direct correction: skip keys directly under `properties`. |
| 4 | `ToolRunner.RunAsync` takes the use case (for its class) and the delegate separately, so a future tool could pair the wrong class | low | reject | Correct for the only tool; fix needs new public surface (generic execute on `IUseCase`) and the mismatch would need a coding mistake in a thin tool. Revisit in the refactor sweep (entry 14) if more tools copy the pattern. |
| 5 | `Internal` userAction says "with its HRESULT" for cancelled/unexpected errors that have none | low | patch | Reworded to fit both. |
| 6 | AC3 and the integration run have no recorded evidence | false | reject | Not a defect in the diff; both need OneNote and an unlocked exe, and are run with Phill at presentation (step 5). |

## Design Notes

**Retryable per code** (AD-9 column read as "can a retry succeed, possibly after a user action"): true for `OneNoteNotRunning`, `ComBusy`, `ModalDialogOpen`, `SectionLocked`, `WriteNotGranted`, `PermanentDeleteNotGranted`, `SettingsInvalid`; `ComUnavailable` and `Timeout` true only for `Read`; false for the rest. Each code has one short userAction (e.g. `ModalDialogOpen`: "Close the open dialog in OneNote, then retry."; `Internal`: "Report this error with its HRESULT.").

**HRESULT → code:** `0x80080005` IntegrityMismatch; `0x800706BA`, `0x80010108` ComUnavailable; `0x80010001`, `0x8001010A` ComBusy; `0x80042030` ModalDialogOpen; `0x8004201B` SectionLocked; `0x80042010` StaleWrite; ObjectNotFound for `0x80042004` hrSectionDoesNotExist, `0x80042005` hrPageDoesNotExist, `0x8004200E` hrPageObjectDoesNotExist, `0x80042011` hrGroupDoesNotExist, `0x80042012` hrPageDoesNotExistInGroup, `0x80042014` hrObjectDoesNotExist, `0x80042015` hrNotebookDoesNotExist; InvalidXml for `0x80042000` hrMalformedXML, `0x80042001` hrInvalidXML, `0x80042020` hrInvalidXMLSchema; `0x80042019` InvalidQuery; `0x8004201A` FileExists; everything else Internal. Stay Internal on purpose: `0x80042006` hrFileDoesNotExist and `0x80042018` hrFolderDoesNotExist (paths, not objects), `0x8004200F` hrBinaryObjectDoesNotExist (story 8 turns it into the `BinaryUnavailable` warning), `0x800706BE` (can mean bad XML, OneMore).

**Wire shape:** camelCase, nulls omitted, `hresult` as `"0x80042014"`. The text block duplicates the JSON (MCP spec SHOULD for older clients); the manual check confirms what Claude Code shows the model, and whether the XML then appears twice.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` (OneNote running) -- expected: all pass.

**Manual checks (if no CLI):**
- After `/mcp` reconnect: `get_hierarchy` with `startNodeId` `{00000000-0000-0000-0000-000000000000}{1}{B0}` returns the error envelope with `isError: true`; a plain call returns `ok: true` with `meta.correlationId`. Read-only.
