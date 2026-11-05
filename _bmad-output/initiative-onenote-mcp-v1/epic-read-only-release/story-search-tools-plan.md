---
title: 'Search tools'
type: 'feature'
ticket: '9'
created: '2026-11-05'
status: 'built'
baseline_revision: '09abe0593bcc3bc6ae635b95268282bba5894a6f'
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

**Problem:** No search tool exists. R2 needs `find_pages` and `find_meta` under AD-12.
- Unindexed pages must be included, because indexed-only search lags or misses pages (spike row 28).
- Recycle-bin pages must be removed and listed.
- A search must not start from the root by accident.
- Results are always marked possibly incomplete.

Including unindexed pages makes OneNote return every page under the start node. Story 1.6's fixtures show it: `FindPages("Fixture")` and `FindMeta("onenote-mcp-fixture")` both return the same 17 pages, including the Seed pages that match neither.

**Approach:** Two tools, each with its own Read use case, following 1.7's pattern. Both call the gateway with `includeUnindexed: true` (the gateway already passes `fDisplay=false`). The Xml project removes whole `one:Page` elements from the returned XML without touching any other byte, and each removal is listed in `meta.filtered` with its reason. The contract goes to 0.4.

## Boundaries & Constraints

**Always:**
- **Signatures:** `find_pages(query, startNodeId = "", fromRoot = false)` and `find_meta(name, startNodeId = "", fromRoot = false)`. Exactly one of a non-empty `startNodeId` or `fromRoot: true` must be given. Neither or both returns `ok: false` with `InvalidQuery` before COM is called, and its `userAction` explains the rule. With `fromRoot`, the gateway gets `""`.
- **Removal order, metadata first (AD-12):**
  1. A recycle-bin page is removed with reason `recycleBin`. That means the page, or any ancestor `one:Section` or `one:SectionGroup`, has `isInRecycleBin="true"` or `isRecycleBin="true"`.
  2. For `find_meta`, a page with no child `one:Meta` whose `name` equals `name` (ordinal) is removed with reason `noMatch`. This uses the result's own metadata, with no page reads.
  3. Decision (2026-11-05, Phill): `find_pages` checks the text of what remains.
     - It reads each remaining candidate with piBasic, in document order. Decision (2026-11-05, Phill): a candidate whose `name` already contains every term is kept without a read.
     - A candidate is removed with reason `noMatch` unless every whitespace-separated term of `query` appears, case-insensitively, in its `name` or in its `one:T` text with the markup stripped.
     - A candidate whose read fails with `ObjectNotFound` (deleted since the search) is removed with reason `notFound`. Any other read error fails the call.
- `meta.filtered` is `[{pageId, reason}]`, in document order. It is always present, `[]` when nothing was removed.
- `meta.possiblyIncomplete` is always `true`.
- Apart from removed `one:Page` elements, the XML is byte for byte what OneNote returned (AD-4). When nothing is removed, it is the exact string.
- Messages, warnings and logs carry IDs, counts and reasons only. They never carry the query, page names or content (AD-9, AD-13).
- Decision (2026-11-05, Phill, from story 1.7): bump `<Version>` to 0.4.0, the snapshot to `0.4`, and the marketplace URL to v0.4.0.

**Never:**
- No `includeUnindexed` parameter: it is always true.
- No change to `IOneNoteGateway`, `ComGateway` or `HResultMap`.
- No removal of non-Page elements, such as sections, section groups or empty recycle-bin sections.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Recycle bin | `find_pages("Fixture")` from the MCP Test notebook (fixture) | the 10 `isInRecycleBin` pages removed, each listed as `recycleBin`; every other byte unchanged; `possiblyIncomplete: true` | none |
| Text check | `find_pages("Fixture")`, same fixture, with page reads served | recycle-bin pages are removed first; of the live pages, the two Seed pages are removed as `noMatch`, because their title and text lack "Fixture"; the Fixtures pages stay | none |
| Candidate gone | a candidate's piBasic read throws `0x80042014` | that page is removed as `notFound`; the call is still ok | none |
| Meta filter | `find_meta("onenote-mcp-fixture")`, same fixture | recycle-bin pages removed first (`recycleBin`), then live pages with no matching Meta (`noMatch`); only Fixture Nested remains | none |
| Ancestor-only flag | hand-built XML with a page under an `isInRecycleBin` section but no flag on the page | the page is removed as `recycleBin` | none |
| Nothing to remove | XML with no recycle-bin pages and all matching | the exact string returned; `filtered: []` | none |
| No start | neither `startNodeId` nor `fromRoot` | `ok: false`, `InvalidQuery`; gateway not called | via `ToolRunner` |
| Both | `startNodeId` and `fromRoot: true` | `ok: false`, `InvalidQuery`; gateway not called | via `ToolRunner` |
| From root | `fromRoot: true` | gateway gets `""` | none |
| Bad start / empty query | gateway throws `0x80042014` or `0x80042019` | `ObjectNotFound` or `InvalidQuery` | via `ToolRunner` |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` L23/L26 -- `FindPagesAsync(start, query, includeUnindexed, ct)` and `FindMetaAsync(start, name, includeUnindexed, ct)`. `ComGateway` L159–176 already passes `fDisplay: false` and `xs2013`.
- `tests/OneNoteMcp.Tests/Fixtures/mcp-test/search/{findpages,findmeta}-fixture.xml` -- byte-identical. They hold 17 pages: 7 live (Seed ×2, Fixtures ×5, one with `one:Meta name="onenote-mcp-fixture"`) and 10 under `OneNote_RecycleBin`, each flagged `isInRecycleBin` on the page and the section. Manifest keys are `FindPages`/`FindMeta` with args `(notebookId, query|name, "true")`.
- `src/OneNoteMcp.Xml/` -- add `SearchResultFilter`: given the raw XML and a predicate, it returns the XML with whole `one:Page` elements cut out and the list of removed IDs. Cut by text span, for example with `XmlReader` and `IXmlLineInfo`, or an equivalent that leaves every other byte alone. **Don't** round-trip through `XDocument.ToString()`, which re-serialises the rest. Malformed XML raises `OneNoteXmlException`, which maps to `InvalidXml`.
- Page reads for the text check: the manifest has piBasic fixtures for the five Fixtures pages, but not for the two Seed "Untitled" pages. The fake answers those with `ObjectNotFound`, which would show up as `notFound`. So the Text check row wraps `FakeGateway` and serves the Seed pages from a minimal hand-built page XML that has no "Fixture" in it.
- `HResultMap` -- `0x80042019` maps to `InvalidQuery` and `0x80042014` to `ObjectNotFound`, as they are now.
- 1.7's pattern, mirrored: `src/OneNoteMcp.Server/Tools/GetHierarchyTool.cs`, `src/OneNoteMcp.Core/UseCases/GetHierarchyUseCase.cs`, `Core/Results/ToolMeta.cs` (add `Filtered` and `PossiblyIncomplete`), the `ServerSetup.cs` registrations, and `tests/OneNoteMcp.Tests/HierarchyReadToolTests.cs` (for the new `SearchToolTests.cs`).
- `tests/OneNoteMcp.Tests/ToolContractTests.cs` + `Contract/tool-contract.json`; `Directory.Build.props`; `.claude-plugin/marketplace.json`.
- `tests/OneNoteMcp.Integration/PageContentToolsIntegrationTests.cs` -- the guarded write pattern (`TestNotebook.WriteAsync` + partial `UpdatePageContent`), and the tool-level calls over the real gateway.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/SearchResultFilter.cs` -- the byte-preserving page removal.
- [x] `src/OneNoteMcp.Core/Results/{ToolMeta,FilteredPage}.cs` -- `meta.filtered`, `meta.possiblyIncomplete`, and the reason constants.
- [x] `src/OneNoteMcp.Core/UseCases/{FindPages,FindMeta}UseCase.cs` -- the start rule, the removal order, and the meta.
- [x] `src/OneNoteMcp.Server/Tools/{FindPages,FindMeta}Tool.cs` + `ServerSetup.cs` -- descriptions say "Read-only.", that results are possibly incomplete, and that removed pages are listed in `meta.filtered`.
- [x] `tests/OneNoteMcp.Tests/{SearchToolTests,SearchResultFilterTests}.cs` -- every matrix row. Byte preservation: the output equals the input with exactly the removed `<one:Page …>…</one:Page>` (or self-closed) spans cut out.
- [x] Contract files: nine tools, `0.4`.
- [x] `tests/OneNoteMcp.Integration/SearchToolsIntegrationTests.cs` -- through the guarded write, put a fresh GUID token and a `one:Meta name="onenote-mcp-search" content="<token>"` on MCP Test's seed page. Then assert that `find_pages(token)` and `find_meta("onenote-mcp-search")` from the MCP Test notebook both return that page, `possiblyIncomplete` is true, and no recycle-bin page is in the result.
- [x] `epic-read-only-release.md` Notes -- a dated Finding on the ticket's unknown: with unindexed pages included, `FindMeta` and `FindPages` return every page under the start node (the 1.6 fixtures are byte-identical), so `find_meta` does its own Meta check. Add the integration result.

**Acceptance Criteria:**
- Given the repo, when the Release build and the unit tests run, then both pass with 0 warnings.
- Given OneNote running and `ONENOTE_MCP_INTEGRATION=1`, when the new integration class runs, then it passes and its only write is to MCP Test.

## Implementation Notes

- Build and test with `-c Release --artifacts-path <temp dir>`, because the running `onenote` MCP session locks the Debug output. Don't run the integration suite: it writes to MCP Test, so the lead runs it with Phill. Just make sure it compiles.

- Integration run with Phill (2026-11-05): `SearchToolsIntegrationTests` passed twice (0.9 s, then 0.4 s). The only writes were the token and Meta on MCP Test's seed page.
- Title shortcut (Phill's decision after review): a page whose name already holds every term is kept without a read.
- `OneNoteException` gained an optional `userAction`, used by `ToolErrors.From`, so the start-rule error can explain itself.

## Plan Change Log

## Review Triage Log

- 2026-11-05, quick lens, pass 1: 2 findings. 1 low (patched by the lead), 1 false.
  - low, patch: the epic Finding said symbols in queries need no handling, but no evidence backed that, since the raw query reaches OneNote's `FindPages`. It was reworded to say the question is still open. The lead made this one-sentence correction directly.
  - false: "the integration result is missing." The run with Phill is the next step, and it's recorded when it happens, as in stories 1.7 and 1.8. That is not a defect in the change.
- Raised by the implementer, not a lens finding: `find_pages` reads every live page, even when the title already matches. It's left for Phill to decide.

## Design Notes

- **Why an exactly-one start rule.** AD-12 requires a start node, and `fromRoot` makes searching everything explicit. Accepting both would make it unclear which one wins. `InvalidQuery` is the closest closed code (AD-9), and the check runs before COM, so it costs nothing.
- **Why ancestors are checked too.** OneMore records the bin flags on the section group and its sections (research row 8). Our fixtures also flag the pages. Checking both covers either shape.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror --artifacts-path <tmp>` -- expected: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests -c Release --artifacts-path <tmp>` -- expected: all pass
- `ONENOTE_MCP_INTEGRATION=1 <Integration exe> -class OneNoteMcp.Integration.SearchToolsIntegrationTests` -- run with Phill; expected: pass
