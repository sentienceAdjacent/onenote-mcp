---
title: 'Hierarchy read tools'
type: 'feature'
ticket: '7'
created: '2026-11-05'
status: 'built'
baseline_revision: 'aaf06fce78afe6af4f1c3ef0730aabd475be3cda'
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

**Problem:** Only the `get_hierarchy` tracer exists, and it is fixed to the Notebooks scope. R1 needs every hierarchy-side Read method as a tool: `GetHierarchy` at every scope, `GetHierarchyParent`, `GetSpecialLocation`, `GetHyperlinkToObject` and `GetWebHyperlinkToObject`.

**Approach:** Give `get_hierarchy` a `scope` parameter. Add four tools, each in its own file and each backed by its own `Read` use case over the existing port (AD-5). Report locked sections as a warning on `get_hierarchy`. Return scalar results in `meta` and leave XML unchanged (AD-4). Update the contract snapshot.

## Boundaries & Constraints

**Always:**
- Names follow AD-14: `get_hierarchy`, `get_hierarchy_parent`, `get_special_location`, `get_hyperlink_to_object`, `get_web_hyperlink_to_object`. Each tool carries the same annotations as the tracer (read-only, idempotent, not destructive, closed world) and declares `OperationClass.Read`.
- `get_hierarchy.scope` is an enum (`self`, `children`, `notebooks`, `sections`, `pages`). It defaults to `notebooks`, so a call without it behaves as today.
- `get_hierarchy` XML is byte for byte what the gateway returned. Every `one:Section` with `locked="true"` in it adds one warning, `sectionLocked`, whose message carries the section's ID. Nothing from page content goes in a message.
- IDs pass through opaque: never parsed, trimmed or built from parts.
- Tools never throw, and errors come through `ToolRunner` as they do now.
- Decision (2026-11-05, Phill): bump `<Version>` to 0.2.0 and the snapshot's `contractVersion` to `0.2` here, following AD-14 literally. Stories 1.8 and 1.9 bump again.
- Decision (2026-11-05, Phill): scalar results go in optional `meta` fields: `parentId`, `path` and `link`, following AD-11's `meta.outputPath`. No AD amendment.

**Never:**
- No call that is visible to the user (AD-11), and no write.
- No change to `IOneNoteGateway`, `ComGateway` or `HResultMap`.
- No walking the parent chain in the server. `get_hierarchy_parent` is one call, and the chain is the caller's.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Any scope | notebook ID × each of the 5 scopes | fixture XML unchanged; `warnings: []` | none |
| Locked section | hierarchy XML with 2 sections `locked="true"`, 1 not | XML unchanged; 2 `sectionLocked` warnings with those IDs | none |
| Parent of page/section | page or section ID | `meta.parentId` = the section or notebook ID | none |
| Parent of notebook | notebook ID | `ok: true`, `meta.parentId: ""` (the empty string is emitted, not omitted) | none |
| Special location | each of the 3 values | `meta.path` = the fixture text | none |
| Hyperlink | (section, omitted) and (page, OE ID) | `meta.link` = the `onenote:` URL | none |
| Web link, local notebook | page ID | `ok: true`, `meta.link: ""`, warning `webLinkUnavailable` | none |
| Unknown ID | any tool | `ok: false`, `ObjectNotFound` | via `ToolRunner` |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Server/Tools/GetHierarchyTool.cs` -- the pattern for each new tool: an `[McpServerToolType]` class with a `Name` constant, the `[McpServerTool(...)]` annotations, a description that starts with "Read-only." and states both envelope shapes, and a call to `runner.RunAsync(Name, useCase, …)`. Add `scope` here.
- `src/OneNoteMcp.Core/UseCases/GetHierarchyUseCase.cs` -- the pattern for each use case (`IUseCase`, `Class => Read`, `ExecuteAsync(ToolCallContext, …)`). It currently hardcodes `HierarchyScope.Notebooks`.
- `src/OneNoteMcp.Server/ServerSetup.cs` (about L34–38) -- add `AddSingleton<…UseCase>()` and `.WithTools<…Tool>(EnvelopeJson.Options)` for each tool.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- currently `ToolMeta(string CorrelationId)`. Add nullable `ParentId`, `Path` and `Link`. `EnvelopeJson.Options` omits nulls, so the error shape is unchanged.
- `src/OneNoteMcp.Core/Results/ToolSuccess.cs`, `ToolWarning.cs` -- `ToolWarning(Code, Message, UserAction?)`. No warning codes exist yet. Add `ToolWarnings` constants (`sectionLocked`, `webLinkUnavailable`) beside it.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `HierarchyScope.cs`, `SpecialLocation.cs` -- consumed as they are.
- `src/OneNoteMcp.Xml/` -- add `LockedSections.Find(string hierarchyXml) → IReadOnlyList<string>` (section IDs), using the `One` namespace. Malformed XML raises `OneNoteXmlException`, which maps to `InvalidXml`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- fixture-backed. Manifest keys use the enum `ToString()`. `HierarchyXml` overrides every `GetHierarchy`, and is how the locked-section XML is served. No fixture has `locked`.
- `tests/OneNoteMcp.Tests/GetHierarchyToolTests.cs` -- keep it, and add scope cases there.
- `tests/OneNoteMcp.Tests/ToolContractTests.cs` + `Contract/tool-contract.json` -- the snapshot to regenerate (the failure message prints it). `BothEnvelopeShapes_…` (about L54) calls `.Single()` on the tools, so loop over every tool instead.
- `tests/OneNoteMcp.Integration/LifecycleScenarioTests.cs` (`ReadMethods_Smoke_OverMcpTest`, L68–110) -- the pattern: `RequireRunningOneNote()`, `Gateway(...)`, `TestNotebook.EnsureAsync`. The gate is `VtableSmokeTests.OneNoteFixture.Enabled`.
- `README.md` L106 already says "call `get_hierarchy` with scope `notebooks`", which this story makes true.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/LockedSections.cs` -- the locked-section finder.
- [x] `src/OneNoteMcp.Core/Results/{ToolMeta,ToolWarnings}.cs` -- the meta fields and the warning codes.
- [x] `src/OneNoteMcp.Core/UseCases/{GetHierarchy,GetHierarchyParent,GetSpecialLocation,GetHyperlinkToObject,GetWebHyperlinkToObject}UseCase.cs` -- one per tool. The locked and web-link warnings are built here.
- [x] `src/OneNoteMcp.Server/Tools/*Tool.cs` + `ServerSetup.cs` -- the four new tools, `scope` on `get_hierarchy`, and the registrations.
- [x] `tests/OneNoteMcp.Tests/{HierarchyReadToolTests,LockedSectionsTests}.cs` -- every matrix row through tool → use case → `FakeGateway`, with XML and scalars asserted byte for byte against the fixtures.
- [x] `tests/OneNoteMcp.Tests/ToolContractTests.cs`, `Contract/tool-contract.json`, `Directory.Build.props` -- loop over all tools; regenerate the snapshot at `0.2`; `<Version>` 0.2.0.
- [x] `tests/OneNoteMcp.Integration/HierarchyToolsIntegrationTests.cs` -- through the real `ComGateway`, call the tools over MCP Test: the notebook at all 5 scopes and the root at Notebooks, each XSD-valid; the parent chain page → section → notebook → `""`; the 3 special locations; the hyperlink; the empty web link.

**Acceptance Criteria:**
- Given the repo, when the Release build and the unit tests run, then both pass with 0 warnings.
- Given a client listing tools, when it reads `tools/list`, then all five tools appear with their schemas, and the snapshot test passes at the contract version.
- Given OneNote running and `ONENOTE_MCP_INTEGRATION=1`, when the new integration class runs, then it passes and nothing outside MCP Test changes.

## Implementation Notes

- Build and test with `-c Release --artifacts-path <temp dir>`: the running `onenote` MCP session locks the Debug output. Don't run the integration suite (it touches Phill's OneNote; the lead runs it with him); make sure it compiles.

- Contract choices made in the build:
  - `location` names: `backUpFolder`, `unfiledNotesSection`, `defaultNotebookFolder`.
  - Parameters: `objectId`, `hierarchyId`, and `pageContentObjectId` (optional, default `""`).
  - The wire enums reject integers (`StrictStringEnumConverter`).
  - `locked="1"` also counts, because it is a valid `xs:boolean`.
- `marketplace.json`'s release URL moved to v0.2.0, because `PluginPackagingTests` ties it to `<Version>`. Its sha256 is still the zero placeholder (story 1.11).
- The integration project now references Server, so it can call the tools over the real gateway. The new class hasn't been run yet; it needs a run with Phill.
- Integration run with Phill (2026-11-05): `HierarchyToolsIntegrationTests` passed against real OneNote (1/1, 0.8 s). MCP Test already existed, so there were no writes.

## Plan Change Log

## Review Triage Log

- 2026-11-05, quick lens, pass 1: 3 findings. 1 medium, 1 low, 1 false.
  - medium, patch: wire enums accept integers (`1`, `"3"`), and an out-of-range `99` reaches `ToCore()` and is reported as `Internal`. The reviewer reproduced this against the Release build: `JsonStringEnumConverter<T>` defaults to `allowIntegerValues: true`. Fix: reject integers, with a test.
  - low, patch: the `sectionLocked` text "OneNote left its pages out" is wrong at the self, children and sections scopes, and nothing confirms it (no locked fixture exists). Fix: reword it so it doesn't depend on scope, in the use case, the tool description and the doc comment.
  - false: the scope tests are in `HierarchyReadToolTests.cs` rather than `GetHierarchyToolTests.cs`. Every scope case is covered and passes, and no caller or rule depends on which file holds them.

## Design Notes

- **Ticket unknown resolved.** `GetSpecialLocation` and `GetWebHyperlinkToObject` have both been called since the ticket was written: story 1.5's smoke test and story 1.6's fixtures. All three special locations return paths, and the web link is an empty string for a local notebook. So the web link becomes a warning, not an error.
- **Locked is a warning, not an error, on reads.** AD-4 puts degraded reads in `warnings`. `SectionLocked` as an error is for writes (AD-9).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror` -- expected: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests -c Release` -- expected: all pass
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration -c Release` -- run with Phill; expected: the new class passes
