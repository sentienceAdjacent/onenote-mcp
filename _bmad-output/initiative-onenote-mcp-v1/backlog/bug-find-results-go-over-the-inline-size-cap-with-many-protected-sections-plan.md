---
title: 'find_pages and find_meta first text block over the inline cap'
type: 'bugfix'
ticket: '15'
created: '2026-11-19'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A stored `find_pages` or `find_meta` result returns a first index whose text block is over `results.inlineMaxChars`. On MCP Test there were 16 such blocks, the largest 102,870 characters; a client with a strict cap may truncate or refuse them.

**Measured cause (2026-11-19, `FindResultCapTests`, real `ResultDelivery` and `EnvelopeMeasure`, cap 100,000).** The first index carries the original `meta` and `warnings` whole. `ResultResponses` sizes only the index entries against the cap, so when the header alone is over the cap, the block is over the cap. The two lists in the header grow with the notebook:
- **`meta.filtered`**, one entry per page the search removed (every `noMatch` page, recycle-bin pages and so on), at about 118 characters per entry. 1,000 entries alone push the block to 125,192 characters, with 9 withheld sections.
- **One `readNotGranted` warning per withheld section**, at about 744 characters each (the advice text repeats). Going from 9 to 29 sections adds about 14,900 characters: 1,000 filtered gives 125,192 with 9 sections and 140,072 with 29. That explains why story 5.7's run (9 encrypted sections) stayed under and the 29-section run did not.
- **Without a large `meta.filtered`**, 29 warnings fit (29 with 0, and 29 with 200 filtered, are under the cap). The ticket's inferred cause, the warnings alone, is therefore only part of it: the filtered list is the main driver, and the warnings tip it over.

The spine's Deferred row already names the direction: "if it happens … amend AD-20 to store lists such as `meta.filtered` with the result".

**Approach:** When a stored result's first index wouldn't fit, the lists in its header that grow with the notebook leave the header and are kept with the stored result, readable through `read_result`. Nothing is dropped (AD-20 "No loss"). How they are exposed, and whether the per-section warnings change shape, are Phill's choices (Open Questions).

## Boundaries & Constraints

**Always:**
- Every response's first text block is at most `results.inlineMaxChars`, for every tool. The `ToolRunner` oversize log stays as a last resort.
- No loss (AD-20). Every `meta.filtered` entry, every withheld section ID and every warning stays readable by the model through the tools.
- Results whose header fits are unchanged: same meta, warnings and index, byte for byte.
- Withheld pages are still never listed in `meta.filtered` (AD-21).

**Never:**
- No truncating a list without a way to read the rest.
- No change to the search post-filter or to what is withheld.
- No change to `read_result`'s existing parts, runs and index pages.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected |
|---|---|---|
| Header fits | 29 withheld, 200 filtered | unchanged |
| Filtered too long | 29 withheld, 1,000 filtered | first block ≤ cap; the full `meta.filtered` readable (Q1) |
| Many warnings | 300 withheld, 0 filtered | first block ≤ cap; every section ID and its reason readable (Q2) |
| Both | 29 withheld, 2,000 filtered | first block ≤ cap; everything readable |
| Small body, long header | a body under the cap, a filtered list over it | first block ≤ cap (it is stored) |

</frozen-after-approval>

## Open Questions

- **Q1. How the model reads a `meta.filtered` list that doesn't fit.**
  - (a) Keep it as a second stored text result. The first index carries `meta.filteredCount` and `meta.filteredResultId`, and `read_result(filteredResultId)` returns the list as JSON text in fitted parts, like any stored text. This reuses `read_result` unchanged; the contract adds two optional `meta` fields.
  - (b) Keep it with the same stored result and add a pseudo-ref, `read_result(resultId, ref: "meta.filtered")`. This needs a new `read_result` path and a contract change to `read_result`.
  - (c) Put the first N entries in `meta.filtered`, with `meta.filteredCount` for the total, and serve the rest as in (a).

  (a) is the smallest change, and stays within the spine's Deferred wording. (c) gives the model the start of the list without a second call, but splits one list across two places.

  Recommended: (a).
- **Q2. The per-section `readNotGranted` warnings.** They repeat the same about 400-character advice for each section, and `meta.withheld` already lists every section ID.
  - (a) One `readNotGranted` warning for all withheld sections: the count and the reasons by kind (marked, by path). `meta.withheld` keeps every ID, and the IDs protected by path are listed. This is a contract change to the warnings of the search tools only. No content is lost, because the IDs stay in `meta.withheld`.
  - (b) Keep one warning per section. When they don't fit, move them as in Q1.
  - (c) Keep one warning per section, but share the `userAction` text, by putting it once in the first warning.

  (a) shrinks the header for good and reads better. (b) keeps today's shape but adds a second overflow path.

  Recommended: (a).
- **Q3. Which tools.** Only `find_pages` and `find_meta` carry these lists today. Should the overflow rule be written generally, for any stored result whose header doesn't fit, or only for the search tools?
  - (a) Generally, in `ResultDelivery`, with the search lists as its only users today.
  - (b) Search tools only.

  Recommended: (a). It closes the Deferred row for every tool.

## Code Map

- `src/OneNoteMcp.Core/LargeResults/ResultDelivery.cs` (`Deliver`, l.39–66): stores a result whose block is over the cap, and builds the first index from `entry.Original`.
- `src/OneNoteMcp.Core/LargeResults/ResultResponses.cs`: `FirstIndex` (l.28), `RootOverhead` (l.191, which counts meta and warnings as overhead but never caps them), `OriginalIndex` (l.197), and `Paginate` (l.167, whose first entry always goes in).
- `src/OneNoteMcp.Core/UseCases/SearchResults.cs:44-62` builds the search success: `Filtered`, `Withheld`, `broad.Warnings`.
- `src/OneNoteMcp.Core/Access/AccessDecisions.cs:51-58` (`BroadAccess.Warnings`: one warning per withheld section, with `ReadAccess.WithheldUserAction`).
- `src/OneNoteMcp.Server/ToolRunner.cs:117-124`: the oversize log, the last resort.
- `tests/OneNoteMcp.Tests/FindResultCapTests.cs`: the red measurement, skipped until Q1–Q3 are answered. `LargeResultTestKit.cs` provides the harness-style "no block over the threshold" check.
- Spine: AD-20 and its Deferred row "A header line over the inline threshold", l.909; AD-12/AD-21 for `meta.filtered` and `meta.withheld`.

## Tasks & Acceptance

**Execution** (after Q1–Q3, rewritten to the answers):
- [ ] `ResultDelivery.cs` / `ResultResponses.cs` -- size the header; move what doesn't fit, per Q1.
- [ ] `AccessDecisions.cs` -- the warning shape, per Q2.
- [ ] `ToolMeta.cs`, the contract snapshot, `read_result`'s description and the search tools' descriptions, if Q1 adds fields.
- [ ] `FindResultCapTests.cs` -- un-skip it. Every row of the matrix, plus reassembly: the full filtered list read back equals what was moved.
- [ ] The spine: AD-20 amended and its Deferred row resolved; a memlog entry.

**Acceptance Criteria:**
- Given a stored search result whose header is over the cap, when it returns, then its first text block is at most `results.inlineMaxChars`, and every filtered entry, withheld ID and warning is readable through the tools.
- Given the harness on MCP Test (a live step for the next sitting), when it runs, then it exits 0, with 0 blocks over the inline maximum.

## Implementation Notes

- 2026-11-19, the measurement only (no fix). `FindResultCapTests` was written first, against the real `ResultDelivery` and `EnvelopeMeasure`. It failed for 1,000 and 2,000 filtered entries and passed for 0 and 200 (figures in the Intent). It is committed skipped so CI stays green until the fix.

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, `FindResultCapTests` un-skipped and green.
- TDD: `FindResultCapTests` is the red test; each new case is written first.

**Live (the coordinator's next sitting):** the stress harness on MCP Test exits 0, with 0 blocks over the inline maximum.
