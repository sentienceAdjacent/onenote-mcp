---
title: 'Page content read tools'
type: 'feature'
ticket: '8'
created: '2026-11-05'
status: done
baseline_revision: '5ef349226351b61ff1e1dfd289ba48456e115a17'
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

**Problem:** No tool reads page content yet. R1 needs `get_page_content`, which returns the page XML with the AD-17 basis that epic 2's writes will check, and `get_binary_page_content` for images and attachments. Both must degrade, never fail, when a cloud page's binary isn't on this PC (AD-4, AD-9).

**Approach:** Add two tools, each with its own Read use case, following story 1.7's pattern. `get_page_content` always computes `meta.basis` from a piBasic read. With binary on, it also reads piBinaryData, returns that XML, and reports images missing from it. `get_binary_page_content` returns the base64 data in `meta`, and turns `0x8004200F` into a warning. The contract goes to 0.3.

## Boundaries & Constraints

**Always:**
- `get_page_content(pageId, includeBinary = false)`.
  - With `includeBinary` false, it makes one piBasic read and returns that XML.
  - With `includeBinary` true, it reads piBasic **first**, then piBinaryData, and returns the piBinaryData XML.
  - In both cases `meta.basis` comes from the piBasic read through `PageBasis.From`, so the basis never depends on `includeBinary`.
- `meta.basis` is `{lastModifiedTime, pageHash, elementHashes}`, exactly as `PageBasis` gives it. `lastModifiedTime` is the raw root string, omitted when absent. The caller passes it back unchanged (AD-17).
- When `includeBinary` is true and `ImageCount.Missing(basic, binary)` is greater than 0, `meta.binaryUnavailable` holds that count, and one `binaryUnavailable` warning is added. Otherwise the field is omitted.
- `get_binary_page_content(pageId, callbackId)` returns OneNote's base64 string unchanged in `meta.binary`.
  - `0x8004200F` (read from `OneNoteException.ComHResult`) gives `ok: true` with no `meta.binary` and a `binaryUnavailable` warning. The warning says the binary either isn't stored on this PC (a cloud image) or the callbackID is wrong, because OneNote returns that code for both.
  - Every other error goes through `ToolRunner` unchanged.
- XML and base64 come back byte for byte. IDs are opaque. Messages, warnings and logs carry IDs and counts only, never content (AD-9, AD-13).
- Unreadable page XML gives `InvalidXml`.
- Decision (2026-11-05, Phill, from story 1.7): bump `<Version>` to 0.3.0, the snapshot to `0.3`, and the marketplace URL to v0.3.0.

**Never:**
- No change to `IOneNoteGateway`, `ComGateway`, `HResultMap` or the Xml hash rules.
- No selection or file-type `PageInfo` variants. The tool exposes only `includeBinary`.
- No basis from a piBinaryData read.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Local page, default | nested page | piBasic fixture XML unchanged; `meta.basis` equals `PageBasis.From(that XML)`; no `binaryUnavailable` | none |
| Local page, binary | image page, `includeBinary: true` | BinaryData fixture XML unchanged; basis from Basic; no `binaryUnavailable` (counts are equal) | none |
| Cloud page, binary | cloud page, `includeBinary: true` | BinaryData XML unchanged; `meta.binaryUnavailable: 2`; one `binaryUnavailable` warning; basis from Basic | none |
| Cloud page, default | cloud page | Basic XML; basis; no warning | none |
| Binary present | image page + its callbackID | `meta.binary` = fixture base64 unchanged | none |
| Binary missing | cloud page + `{107}{B2}` callback, or local page + zero-GUID callback | `ok: true`, no `meta.binary`, `binaryUnavailable` warning | none |
| Unknown page | any ID not in fixtures | `ok: false`, `ObjectNotFound` | via `ToolRunner` |
| Malformed page XML | gateway returns non-XML | `ok: false`, `InvalidXml`, and the message doesn't quote the XML | via `ToolRunner` |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Xml/PageBasis.cs` -- `PageBasis.From(string pageXml) → PageBasis(LastModifiedTime?, PageHash, ElementHashes)`. Throws `OneNoteXmlException`. Its comment says the basis comes from piBasic.
- `src/OneNoteMcp.Xml/ImageCount.cs` -- `Missing(basicXml, binaryXml)` is `max(0, Count(basic) - Count(binary))`.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` L17/L20 -- `GetPageContentAsync(pageId, PageInfo, ct)` and `GetBinaryPageContentAsync(pageId, callbackId, ct)`. Use `PageInfo.Basic` and `PageInfo.BinaryData`.
- `src/OneNoteMcp.Core/OneNoteException.cs` L24 -- `int? ComHResult`. In `HResultMap`, `0x8004200F` maps to `Internal`. Catch it in the use case, so `ToolRunner` never logs it as an Internal warning (ToolRunner.cs L90).
- `src/OneNoteMcp.Core/Results/ToolMeta.cs`, `ToolWarnings.cs` -- add `Basis`, `BinaryUnavailable` (int?) and `Binary` (string?) to meta, and the `binaryUnavailable` code. The basis needs a serialisable record in Core.Results, so the output schema shows `lastModifiedTime`, `pageHash` and `elementHashes` (an object of string to string).
- 1.7's pattern, mirrored: `src/OneNoteMcp.Server/Tools/GetWebHyperlinkToObjectTool.cs`, `src/OneNoteMcp.Core/UseCases/GetWebHyperlinkToObjectUseCase.cs`, `ServerSetup.cs` registrations, and `tests/OneNoteMcp.Tests/HierarchyReadToolTests.cs`, whose structure the new `PageContentToolTests.cs` follows.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` + `Fixtures/manifest.json` -- the page fixtures are under `mcp-test/page/` and `cloud/`. The binary is `mcp-test/binary/image.b64.txt`. The two `0x8004200F` error entries exist. For the malformed case, use a test-local gateway, the same way 1.7's tests do.
- `tests/OneNoteMcp.Tests/ToolContractTests.cs` + `Contract/tool-contract.json`; `Directory.Build.props`; `.claude-plugin/marketplace.json` (`PluginPackagingTests` ties the URL to `<Version>`).
- `src/OneNoteMcp.Server/Logging/FileLog.cs` -- `FileLog.Start(new FileLogOptions(dir))` returns `.FilePath`. `FileLoggingTests.ToolCall_LogsTheCallLineWithoutXml` (L93) is the in-process pattern.
- `tests/OneNoteMcp.Integration/HierarchyToolsIntegrationTests.cs` -- the tool-level pattern.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- the guarded write is `WriteAsync(gateway, pageId, app => { app.UpdatePageContent(xml, DateTime.MinValue, XMLSchema.xs2013, force: false); return true; }, label, ct)`. The partial page XML shape is at `FixtureCapture.cs` L91 and L385–404.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Results/{ToolMeta,ToolWarnings,PageBasisResult}.cs` -- the meta fields, the warning code and the basis record.
- [x] `src/OneNoteMcp.Core/UseCases/{GetPageContent,GetBinaryPageContent}UseCase.cs` -- the read order, the basis, the missing-image count, and the `0x8004200F` degrade.
- [x] `src/OneNoteMcp.Server/Tools/{GetPageContent,GetBinaryPageContent}Tool.cs` + `ServerSetup.cs` -- the two tools, registered. Descriptions say "Read-only.", explain `meta.basis` (pass it back unchanged on writes) and name the warning.
- [x] `tests/OneNoteMcp.Tests/PageContentToolTests.cs` -- every matrix row against the fixtures; the read order (Basic before BinaryData) through a recording gateway; both use cases are Read class.
- [x] `tests/OneNoteMcp.Tests/ToolContractTests.cs`, `Contract/tool-contract.json`, `Directory.Build.props`, `.claude-plugin/marketplace.json` -- seven tools, contract `0.3`.
- [x] `tests/OneNoteMcp.Integration/PageContentToolsIntegrationTests.cs` -- write a fresh GUID sentinel into a page in MCP Test through the guarded `WriteAsync`. Read it through the tool with a temporary file log, with `includeBinary` both false and true. Assert that the XML contains the sentinel, `meta.basis.pageHash` is set, and the log file has the call lines and no sentinel.
- [x] `epic-read-only-release.md` Notes -- a dated Finding on the ticket's unknown: on local fixture pages, Basic and BinaryData image counts are equal, and only the cloud page differs (2 versus 0). It also notes that the unknown-callbackID ambiguity is surfaced in the warning.

**Acceptance Criteria:**
- Given the repo, when the Release build and the unit tests run, then both pass with 0 warnings.
- Given OneNote running and `ONENOTE_MCP_INTEGRATION=1`, when the new integration class runs, then it passes, its only write is to MCP Test, and the sentinel is in the XML but not in the log.

## Implementation Notes

- Build and test with `-c Release --artifacts-path <temp dir>`, because the running `onenote` MCP session locks the Debug output. Don't run the integration suite: it writes to MCP Test, so the lead runs it with Phill. Just make sure it compiles.

- Integration run with Phill (2026-11-05): `PageContentToolsIntegrationTests` passed twice in a row against real OneNote (1.0 s, then 0.4 s). The first run wrote the sentinel to MCP Test's seed page, and the second rewrote it. The sentinel was in the XML and never in the log.

## Plan Change Log

## Review Triage Log

- 2026-11-05, quick lens, pass 1: 3 findings. 1 medium, 1 low (rejected), 1 false.
  - medium, patch: the `get_binary_page_content` and `get_page_content` descriptions send the caller to the returned XML for a callbackID. With `includeBinary: true`, that XML has inline `one:Data` and no `one:CallbackID` (`image-BinaryData.xml` has 0, `image-Basic.xml` has 1). Fix: say that callbackIDs appear only in the default read.
  - low, rejected: a well-formed piBinaryData response whose root isn't `one:Page` would pass, because no root check is made. OneNote has never been shown returning one for a page ID, and the fix adds a guard for a state nobody has seen.
  - false: the task checkboxes are unticked. Step 5 ticks them when it marks the plan built. That is bookkeeping, not a defect in the change.

## Design Notes

- **Why Basic is read first.** If the page changes between the two reads, the basis is older than the XML returned. A write made with that basis then fails safe as `StaleWrite`. The reverse order would pair a newer basis with older XML, so an edit based on stale content could pass the check.
- **Why the basis is always from piBasic.** For a cloud page, piBinaryData drops the missing images and changes the hashes (`PageBasis` doc comment). One source keeps the basis the same whatever `includeBinary` is, and epic 2's step-3 read can use piBasic too.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror --artifacts-path <tmp>` -- expected: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests -c Release --artifacts-path <tmp>` -- expected: all pass
- `ONENOTE_MCP_INTEGRATION=1 <Integration exe> -class OneNoteMcp.Integration.PageContentToolsIntegrationTests` -- run with Phill; expected: pass
