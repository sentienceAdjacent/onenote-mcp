---
title: 'find_pages cost'
type: 'feature'
ticket: '19'
created: '2026-11-06'
status: 'in-review'
baseline_revision: '1c4af981f07b99a83366dc19600c7a14a892fbcd'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** find_pages reads every candidate page whose name doesn't already hold all the terms, one GetPageContent call (about 26 ms) per page on the single COM thread. On real notebooks of about 190 pages that is 2–5 s per search, against about 50 ms for the raw `FindPages` (spike 1.15), and it grows linearly with page count.

**Approach:** A per-process, in-memory page-text cache keyed by page ID and the page's `lastModifiedTime` from the search XML, so later searches read only pages that changed.

## Boundaries & Constraints

**Always:**
- For the same notebook state, the result set is identical to today's, page for page, and so are `meta.filtered` and `meta.possiblyIncomplete` (AD-12).
- One STA thread still owns every COM call (AD-3). Reads stay serialised.
- Page text never goes to disk or the log (AD-13).
- Decision (Phill, 2026-11-06): option (a), the page-text cache keyed by page ID + `lastModifiedTime`. The known gap (a page edited through COM while not open in OneNote keeps its old `lastModifiedTime`, so search can match its old text) is documented in the README and in find_pages' description. The setting `search.cacheMaxMB` bounds the cache, and `0` turns it off.
- The setting is a spine change: AD-10's settings shape gains `search{cacheMaxMB}` and AD-12 gains the cache rule, logged in the memlog, with a Fable review of the amendment before the build stops at built.

**Never:** changing find_meta, the AD-12 text rule, or the result shape (AD-14 contract stays 0.5).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| First search | cold server | same results as today | — |
| Repeat search | same or different query, pages unchanged | same results as today, faster | — |
| Page edited between searches | text changed | the edit is reflected | — |
| Page deleted | read gives ObjectNotFound | removed as `notFound`, as today; its cache entry dropped | — |
| Cache off | `search.cacheMaxMB` = 0 | every candidate read, as today | — |
| Cache full | cached text past `cacheMaxMB` | least recently used entries dropped; results unchanged | — |
| Setting out of range | `search.cacheMaxMB` = -1 | file is `SettingsInvalid`; defaults used | per AD-10 |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/UseCases/FindPagesUseCase.cs:21-63` -- the read loop: recycle-bin pages removed unread, name-only matches kept unread, everything else read with piBasic and matched by `PageText.ContainsAll(terms, name, PageText.Of(xml))`.
- `src/OneNoteMcp.Core/UseCases/SearchResults.cs` -- `Pages(xml)` gives each candidate's ID, name, recycle-bin flag and index; extend it with `lastModifiedTime` if a cache is chosen.
- `src/OneNoteMcp.Server/ServerSetup.cs:34-42` -- use cases are singletons, so a cache can live in one and be injected.
- `src/OneNoteMcp.Core/LargeResults/ResultStore.cs` -- precedent for an in-memory store with a `TimeProvider`, an LRU cap and logging of sizes only.
- `spikes/stress-harness` -- the search phase compares find_pages with its own prediction and reports round-trip p50 and max; that gives the before and after times.

## Tasks & Acceptance

**Execution:**
- [x] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` and its `.memlog.md` -- add `search{cacheMaxMB}` (0–1,024, default 64) to AD-10's shape and ranges, and an AD-12 bullet: candidate page text may be cached in memory per process, keyed by page ID + `lastModifiedTime`, never on disk, with the COM-edit gap stated; log the decision with `uv run _bmad/scripts/memlog.py append` -- spine first, so the code follows it.
- [x] `src/OneNoteMcp.Core/Settings/SettingsStore.cs`, `SettingsSnapshot.cs` -- `search{cacheMaxMB}` with the range, as for `results{}`.
- [x] `src/OneNoteMcp.Xml` / `SearchResults.Pages` -- expose each candidate's `lastModifiedTime` (missing or unparsable means "don't cache").
- [x] `src/OneNoteMcp.Core/UseCases/PageTextCache.cs` (new) and `FindPagesUseCase.cs` -- a thread-safe LRU of the extracted `PageText.Of` text (not the XML), keyed by ID + `lastModifiedTime`, sized as UTF-16 bytes against `cacheMaxMB` read once per call; a hit skips the read; `notFound` drops the entry; registered as a singleton in `ServerSetup.cs`. Log hits, misses and size only.
- [x] `src/OneNoteMcp.Server/Tools/FindPagesTool.cs`, `README.md`, `plugin/skills/guide/SKILL.md` -- one sentence on the cache and the COM-edit gap; README documents `search.cacheMaxMB`.
- [x] `tests/OneNoteMcp.Tests` -- every I/O matrix row with the fake gateway, counting `GetPageContent` calls (first search reads N, the repeat reads 0, an edited page is re-read, cache off reads N, eviction), plus settings range tests.
- [x] Stress harness -- run on MCP Test with Ballast before (main) and after, and record find_pages p50 and max for both in Implementation Notes.

**Acceptance Criteria:**
- Given the Ballast fixture, when the stress harness's search phase runs after this change, then it reports 0 search mismatches and a lower find_pages p50 than the run on main.
- Given two identical find_pages calls with no edits between them, when the second runs, then it makes no GetPageContent calls.

## Implementation Notes

- Spine: AD-10 shape gains `search{cacheMaxMB}` with its range bullet (0–1,024, default 64); AD-12 gains the page-text-cache and known-gap bullets; AD-13's in-memory note and both Binds lines extended. Memlog entry 136 (decision by Phill).
- `SearchResultPage` gains `LastModifiedTime` (`DateTimeOffset?`, parsed with `XmlConvert.ToDateTimeOffset`; missing, empty or unparsable is null, so the page is never cached).
- `PageTextCache` (Core/UseCases): `Dictionary` + `LinkedList` LRU under one lock; size = UTF-16 bytes of the extracted text; a text larger than the cap alone isn't cached; a lookup at another `lastModifiedTime` drops the stale entry; `Trim(0)` empties it. `FindPagesUseCase` reads `search{}` once per call, trims to the cap before any lookup, so `cacheMaxMB: 0` turns the cache off and frees it. The one-argument constructor (tests, integration) gets its own default cache.
- Logging (AD-13): the use case returns a non-wire `ToolSuccess.SearchCache` (`hits, misses, evicted, entries, bytes`) and `ToolRunner` logs one line of those counts, never IDs or text.
- Docs: one sentence in find_pages' description (descriptions are stripped from the contract snapshot, so AD-14 stays 0.5), README limits bullet, `search.cacheMaxMB` section and the settings-file row, and a guide-skill search caveat.
- Tests: `FindPagesCacheTests` (20) covers every matrix row with read counts and compares each cached result with a cold search of the same state (xml, `meta.filtered`, `possiblyIncomplete`, warnings); the known gap is pinned by a test. `SettingsStoreTests` adds range, type and default tests; `SearchResultFilterTests` the time parse; `ToolRunnerLoggingTests` the log line. A wiring test builds the container through `ServerSetup` and shows one shared cache and `search.cacheMaxMB: 0` in settings.json taking effect at the next call (Fable finding 7; it fails if `FindPagesUseCase` is registered with the one-argument constructor). 964 unit tests pass; integration with the flag: 86 passed, 7 skipped.
- Stress harness, MCP Test with Ballast, 20 find_pages queries (seed 1), each run on a freshly packed exe in one server process:

  | | main (1c4af98) | this branch |
  |---|---|---|
  | find_pages round trip p50 | 11,542 ms | 75 ms |
  | p95 | 13,979 ms | 103 ms |
  | max | 16,365 ms | 12,371 ms (the first, cold search) |
  | search mismatches / all mismatches | 0 / 0 | 0 / 0 |
  | whole run | 280 s, 617 comparisons | 47 s, 617 comparisons |

- Fable review of the AD-10/AD-12 amendment (2026-11-06): 10 findings (1 high, 6 medium, 3 low), relayed to the team lead for Phill. Only finding 7, a missing wiring test, was applied here; the spine findings await Phill's decision, so the spine isn't set final for this amendment yet.
- Behaviour to know: plain LRU over a candidate set larger than the cap thrashes in document order (each insert evicts the next page to be looked up), so repeat searches then read every page, as before the cache. Default 64 MB holds roughly 33 M characters of page text; Ballast (503 pages) and the 190-page notebooks are far under it.

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens): high 0, medium 4, low 2, false 0.
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | ParseDateTime lets ArgumentOutOfRangeException escape, failing find_pages and find_meta | medium | patch | The reviewer ran XmlConvert.ToDateTimeOffset on "0001-01-01T00:00:00+14:00" and it threw. Catch it, treat the time as unparsable, add tests. |
| 2 | Spine stays `final` while the amendment's Fable findings are open | medium | held for Phill | It's the same open item as the Fable review (10 findings). Phill decides, then the spine is final. |
| 3 | Memlog says the Fable review is pending, though it ran | low | patch | Append an event entry. |
| 4 | AD-10's schemaVersion bullet omits `search{}` | low | held for Phill | Same as Fable #4. |
| 5 | "Edits made in OneNote itself are always seen" overstates it (second-granular times) | medium | held for Phill | Same as Fable #2b. The README and spine wording follow his decision. |
| 6 | Cache cap counts only text, not keys and overhead | medium | patch | PageTextCache.SizeOf counts only text.Length*2. Add per-entry overhead; README says "estimated size". |

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`; `dotnet test tests/OneNoteMcp.Tests`; `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration`.
- Stress harness on MCP Test with the Ballast fixture, before and after -- expected: 0 search mismatches; find_pages p50 lower after.
