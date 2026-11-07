---
title: 'Large-result delivery'
type: 'feature'
ticket: '18'
created: '2026-11-06'
status: 'built'
baseline_revision: 'ad71c2a15057a8878e4a26ced6e1aed08c6c6586'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude Code shows the model only a 2 KB preview (over 50,000 chars) or a bare notice (past the token cap) of a large tool result. Spike 1.15 measured this on 11–34% of real page reads and every whole-notebook listing, so content is silently lost.

**Approach:** Implement spine AD-20 and AD-9's new text block exactly as written. Every read tool declares `_meta["anthropic/maxResultSizeChars"]: 500000`. The text block becomes a header line plus the raw body. A result over `results.inlineMaxChars` is kept in an in-memory result store and returned as an index, and a new server-only `read_result` tool serves exact-substring parts.

## Boundaries & Constraints

**Always:**
- The spine (AD-4, AD-9, AD-10, AD-13, AD-14, AD-17, AD-20) is the specification. Where this plan and the spine differ, the spine wins.
- No loss: every character of every stored result is reachable through `read_result`, and no text block exceeds `results.inlineMaxChars`.
- Parts are exact substrings of the stored body; the XML is never re-serialised (AD-4).
- Nothing the store holds is written to disk or logged; only `resultId` and sizes are logged (AD-13).
- Contract change: bump the minor version (0.4 → 0.5) and the contract snapshot (AD-14).

**Never:**
- AD-21 grant checks or `ReadNotGranted`: story 1.20 adds those. The store entry carries an empty grants field that 1.20 fills.
- Changing `ElementHash` or `PageBasis` hashing, tool names or existing parameters.
- Writing to any real notebook. Integration runs are read-only except the existing guarded MCP Test fixture.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Small result | text block ≤ inlineMaxChars | header line + `\n` + body unchanged; structuredContent full envelope | — |
| Oversized page | Ballast page, ~2.6M chars | `meta.result` + first index page; every part fetchable; concatenating offset reads of `/` reproduces the body exactly | — |
| Oversized hierarchy | pages-scope listing >inlineMaxChars | index labelled by node `name`, refs are hierarchy `ID`s | — |
| Oversized single element | one `one:T` or base64 bigger than bodyMax | `ref` returns its (empty) index; offset/length reads cover it | offset ≥ span length → `InvalidQuery` |
| Expired or unknown result | `resultId` past TTL, evicted, or never issued | — | `ResultExpired`, userAction "repeat the original call" |
| Bad ref | `ref` not in the stored result | — | `UnknownObjectId` |
| Settings out of range | `results.inlineMaxChars` = 10 | file is `SettingsInvalid`; reads use defaults | per AD-10 |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Server/ToolRunner.cs:71-82` -- `ToResult`: the only place the text block is built. Implement AD-9's header-line + body here (body = `xml`, or `meta.binary` for `get_binary_page_content`); the over-threshold decision and store hand-off belong in the runner or a Core service it calls, not in each tool.
- `src/OneNoteMcp.Server/Tools/*Tool.cs` -- nine tools, `[McpServerTool(..., UseStructuredContent=true, OutputSchemaType=typeof(ToolEnvelope))]`. Add `[McpMeta("anthropic/maxResultSizeChars", 500000d)]` (SDK 2.3.1 has no int overload; test that `tools/list` serialises `500000`). Precedent: `spikes/stress-harness/FakeServer.cs:91-93`.
- `src/OneNoteMcp.Server/ServerSetup.cs:34-54` -- register the `read_result` use case and tool.
- `src/OneNoteMcp.Server/ToolEnvelope.cs:11-48` -- add `Index`; keep `EnvelopeJson.Options` as is.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs`, `ToolSuccess.cs`, `PageBasisResult.cs` -- add `meta.result`, the index, and make `ElementHashes` optional (the over-threshold index omits it).
- `src/OneNoteMcp.Core/ErrorCode.cs`, `Results/ToolErrors.cs:31-67` -- add `ResultExpired` (not retryable; userAction: repeat the original call). `InvalidQuery` already exists.
- `src/OneNoteMcp.Core/UseCases/GetPageContentUseCase.cs:21-50` -- piBasic is already read first; keep its XML so the store can hash parts from it under `includeBinary`. `IUseCase.Class` declares `Read` for `read_result`, which takes no gateway.
- `src/OneNoteMcp.Core/Settings/SettingsStore.cs:142-203`, `SettingsSnapshot.cs:30-92` -- add `results{inlineMaxChars 20,000–380,000 default 100,000; ttlMinutes 1–1,440 default 30; maxStoreMB 16–4,096 default 256}` following the `journal{}` pattern, with a min/max int helper; out of range throws `InvalidSettingException`.
- `src/OneNoteMcp.Xml/SearchResultFilter.cs:74-213` -- private line/column→offset machinery (`LineStarts`, `Expect`, `StartTagEnd`). Generalise it into a reusable span scanner for the index builder; don't copy it. `PageText.Of` gives the AD-12 text rule for whole pages; add a per-element variant for labels. `ElementHash.Compute` for part hashes.
- `src/OneNoteMcp.Server/ToolRunner.cs:87-91` -- `[LoggerMessage]` pattern for store events (resultId, totalChars, store bytes).
- `Directory.Build.props` (`0.4.0-rc.4`), `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (`contractVersion 0.4`), `ToolContractTests.cs:24-121` -- bump, add `read_result`, decide `_meta` into `BuildSnapshot`.
- Tests asserting text == structured JSON, to update: `GetHierarchyToolTests.cs:39-48`, `PageContentToolTests.cs:310`, `SearchToolTests.cs:388,233`, `HierarchyReadToolTests.cs:131,295`. Fakes: `FakeGateway.HierarchyXml` takes synthetic large XML.
- `spikes/stress-harness/Calls.cs:80-95`, `Program.cs:75-83`, `Recorder.cs:242-249` -- envelope fallback parses text as JSON (breaks on header+body), tool-count check expects nine. Must reassemble over-threshold results through `read_result` to compare against raw COM.
- `README.md` -- "What it does" (:7), "Other MCP clients" (:82), "Uninstall" (:109), "Files it uses" (:123), "How it works" (:132).

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Settings/*` -- `results{}` settings with ranges and defaults -- AD-10/AD-20.
- [x] `src/OneNoteMcp.Core` (result store, `ResultExpired`, meta/index records) -- in-memory store: `resultId` (GUID), body, optional piBasic XML, basis, original meta and warnings, empty grants field; TTL after last read; LRU eviction past `maxStoreMB` counted as UTF-16 bytes; a single over-cap result stored alone; injectable clock for tests -- AD-20 store.
- [x] `src/OneNoteMcp.Xml` (span scanner + index builder) -- spans for every element with exact offsets; index entries (`ref`, `element`, `chars`, `children`, `contains`, `label`), positional refs, run grouping with `firstLabel`/`lastLabel`, paging sized on serialized JSON -- AD-20 index.
- [x] `src/OneNoteMcp.Core/UseCases/ReadResultUseCase.cs` + `src/OneNoteMcp.Server/Tools/ReadResultTool.cs` -- `ref`, `run`, `expand`+`page`, `ref`+`offset`+`length`; exactly one mode; redirects oversized ref/run to its index; per-part `meta.basis` from piBasic; `meta.result` on every response; expand responses carry the element's own hash -- AD-20 read_result.
- [x] `src/OneNoteMcp.Server/ToolRunner.cs`, `ToolEnvelope.cs`, `Tools/*Tool.cs`, `ServerSetup.cs` -- AD-9 text block; over-threshold → store + first index page; `_meta` on all ten tools; log store events -- AD-9/AD-20.
- [x] `tests/OneNoteMcp.Tests` -- unit tests for every I/O matrix row, header+body text block, settings ranges, TTL/LRU with a fake clock, index paging/runs/positional refs, exact-substring reassembly property test (concatenated offset reads == body), `_meta` in `tools/list`, updated contract snapshot -- covers the matrix.
- [x] `Directory.Build.props`, `tool-contract.json` -- version 0.5.0-rc.1, contract 0.5 -- AD-14.
- [x] `spikes/stress-harness/*` -- parse header+body text, accept ten tools, reassemble over-threshold results through `read_result` before comparing, report the max text block size -- ticket verify.
- [x] `README.md` -- large results, `read_result`, `results{}` settings, lower `inlineMaxChars` advice for other clients, Claude Code's own `tool-results` files are not purged -- ticket description.

**Acceptance Criteria:**
- Given the Ballast fixture in MCP Test, when the stress harness runs, then it reports 0 mismatches and no text block over `results.inlineMaxChars`.
- Given a headless Claude Code session loading the built server, when it reads the Ballast oversized page, then it reaches the page's last `one:T` text through `read_result` with no persisted-output preview.
- Given an entry unread for `ttlMinutes`, or evicted past `maxStoreMB`, when `read_result` asks for it, then it returns `ResultExpired`.

## Implementation Notes

Choices the spine leaves open, and where the code differs from this plan's Code Map:

- **Where the index lives.** `OneNoteMcp.Xml/ElementSpans.cs` is the span scanner (exact offsets, refs, labels, `contains`), built on `XmlPositions`, which is `SearchResultFilter`'s line/column machinery moved out and shared (`SearchResultFilter` now uses it). Grouping into runs and paging are in Core (`LargeResults/ResultResponses.cs`), because both are sized on the serialized envelope, and only Server knows the serializer. Core asks through a port, `ITextBlockMeasure`, which Server implements with `EnvelopeJson.Options` (`EnvelopeMeasure`).
- **The over-threshold decision** is `ResultDelivery.Deliver`, called once by `ToolRunner` for every tool. Use cases mark what may be stored with `ToolSuccess.Source` (kind, plus piBasic for a page read), which never goes on the wire. A `read_result` response has no `Source`, so it is never stored again.
- **`index` shape:** `{ref, run?, element?, chars, page, entries[]}`. `chars` is the span length of the element (or run) indexed, so offset reads know where it ends; `run` is set when a run too big to return comes back as its members' index.
- **`page` also goes with `run`,** for that members' index. The spine names `page` only with `expand`.
- **Refs.** An element whose `objectID`/`ID` already names an earlier element gets a positional ref, so every ref is unique. The root's span is the whole body (XML declaration included), so offset reads of `/` reproduce it exactly.
- **`meta.result.namespaces`** is keyed by attribute name (`{"xmlns:one": "..."}`), and is `{}` for a non-XML body. `expiresAt` is UTC `yyyy-MM-ddTHH:mm:ss.fffZ`. `pages` is on every index response, the first included.
- **Hashes.** The store keeps the piBasic XML (counted once when it is the body) and the original `meta.basis`, which the use case already computed from piBasic; part and expand hashes are looked up in it by `objectID`.
- **`bodyMax`** counts the `\n` between header line and body, so header + `\n` + body ≤ `inlineMaxChars`. Runs are sized with a header allowance measured on a template part plus each member subtree's serialized `elementHashes` size, and a 64-char margin. A run or ref whose actual part still exceeds the threshold (for example after `inlineMaxChars` was lowered) returns an index, never a cut part. Offset reads never end inside a surrogate pair.
- **LRU** orders by a read counter, not the clock, so two reads in one clock tick still order. Expired entries are swept on every store call and by a one-minute timer.
- **Deferred "header over the threshold":** `ToolRunner` logs a warning (`text block over the inline threshold`, IDs and sizes only) if any text block exceeds `inlineMaxChars`; the stress harness counts them too.
- **Also changed:** the guide skill (`plugin/skills/guide/SKILL.md`) for `read_result`, the text block and the new error codes, since `PluginPackagingTests` requires the skill to name every contract tool; the integration tests read a stored result back through `read_result` (`tests/OneNoteMcp.Integration/LargeResults.cs`), since MCP Test with Ballast is over the threshold at pages scope; and the stress harness's `--diag-page` reads the new text block.

- Verification evidence (2026-11-06): stress harness rerun on MCP Test + Ballast, exit 0: 617 comparisons, 0 mismatches, 2 unstableRead (InsertedFile pathCache quirk), maxTextChars 100,000, 0 over the threshold, 31 stored results reassembled from 256 read_result parts. Headless Claude Code 2.1.319 against dist/publish: get_page_content plus 5 read_result calls reached the 2,672,889-char page's last one:T ("Grizlop hulvonz rymbost jeltrum vrindom wumbrek."), with no persisted-output preview.

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens): high 0, medium 5, low 1, false 1. Phill chose to fix all six real ones (2026-11-06).
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | Existing read tools' descriptions still state the old success shape | medium | patch | Descriptions unchanged except `[McpMeta]`; oversized calls now return `index` + `meta.result`. |
| 2 | Unindexable oversized XML becomes InvalidXml, losing the content | medium | patch | ResultDelivery.cs:75-79 throws; fall back to a plain-text result. |
| 3 | maxStoreMB counts only body + piBasic, not the span tree | medium | patch | StoredResult.cs:49; per-node and label overhead uncounted. |
| 4 | Run index `chars` vs `ref` mismatch misleads offset reads | medium | patch | ResultResponses.cs:222 uses the parent ref with the run length; the guide says to offset-read `ref` up to `chars`. |
| 5 | Offsets described as "text" but count the raw span | low | patch | ReadResultUseCase.Offset uses node.Start + offset. |
| 6 | settings.json re-read several times per call | medium | patch | ResultStore.Settings => settings() per access; sizing and checks can disagree. |
| 7 | AC1/AC2 have no evidence | false | rejected | Harness rerun: 617 comparisons, 0 mismatches, maxTextChars 100,000. Headless `claude -p` (2.1.319) reached the last one:T in 6 tool calls with no persisted output. Recorded under Implementation Notes. |

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: success.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` -- expected: all pass (OneNote running, read-only).
- Stress harness against MCP Test with the Ballast fixture (see `spikes/stress-harness/README.md`) -- expected: 0 mismatches, max text block ≤ 100,000.

**Manual checks:**
- Grep the diff for real notebook names, the employer name and personal URLs: none.
