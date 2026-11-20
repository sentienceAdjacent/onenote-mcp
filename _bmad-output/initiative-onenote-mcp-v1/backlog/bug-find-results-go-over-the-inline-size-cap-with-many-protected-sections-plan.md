---
title: 'find_pages and find_meta first text block over the inline cap'
type: 'bugfix'
ticket: '15'
created: '2026-11-19'
status: 'done'
baseline_revision: '27dcff387355e461de0c6277dd455ae7d898ede0'
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

## Decisions (Phill, 2026-11-19, plan approved with every recommendation)

- **Q1, a.** A `meta.filtered` list that doesn't fit becomes a second stored result: JSON text of the list, read with `read_result` like any stored text. The first index, and every index of that stored result, carries `meta.filteredCount` and `meta.filteredResultId` in place of `meta.filtered`.
- **Q2, a.** One `readNotGranted` warning for all withheld sections, giving the count and the reasons by kind (password-protected, or protected by path). `meta.withheld` keeps every ID. This applies to every tool that withholds sections (`find_pages`, `find_meta`, and `get_hierarchy`'s broad reads).
- **Q3, a.** A general rule in `ResultDelivery`: when a stored result's first index wouldn't fit, `meta.filtered` moves out as in Q1. Today only the search tools carry it.
- **Version (AD-14).** No bump: `<Version>` is `0.9.0-rc.1`, not yet released (the latest tag is `v0.8.0-rc.2`), and before 1.0.0 one minor bump covers every contract change since the last release.
- **Version, revised (Phill, 2026-11-19, Inbox).** The premise above was wrong: `v0.9.0-rc.1` was tagged and released on 2026-11-19, before this change. This change bumps to `0.10.0-rc.1`, with contract snapshot `0.10`. A pre-release tag counts as a release (AD-14).
- **Every index (Phill, 2026-11-19).** The first index and every `read_result` index page carry `meta.filteredCount` and `meta.filteredResultId`, as built.
- **Fable optional findings 5–12 (Phill, 2026-11-19).** All eight are kept.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/LargeResults/ResultDelivery.cs` (`Deliver`, l.39–66): stores a result whose block is over the cap, and builds the first index from `entry.Original`.
- `src/OneNoteMcp.Core/LargeResults/ResultResponses.cs`: `FirstIndex` (l.28), `RootOverhead` (l.191, which counts meta and warnings as overhead but never caps them), `OriginalIndex` (l.197), and `Paginate` (l.167, whose first entry always goes in).
- `src/OneNoteMcp.Core/UseCases/SearchResults.cs:44-62` builds the search success: `Filtered`, `Withheld`, `broad.Warnings`.
- `src/OneNoteMcp.Core/Access/AccessDecisions.cs:51-58` (`BroadAccess.Warnings`: one warning per withheld section, with `ReadAccess.WithheldUserAction`).
- `src/OneNoteMcp.Server/ToolRunner.cs:117-124`: the oversize log, the last resort.
- `tests/OneNoteMcp.Tests/FindResultCapTests.cs`: the red measurement, skipped until Q1–Q3 are answered. `LargeResultTestKit.cs` provides the harness-style "no block over the threshold" check.
- Spine: AD-20 and its Deferred row "A header line over the inline threshold", l.909; AD-12/AD-21 for `meta.filtered` and `meta.withheld`.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- `FilteredCount` and `FilteredResultId`, both optional (Q1).
- [x] `src/OneNoteMcp.Core/LargeResults/ResultDelivery.cs` -- after building the stored result and its first index: if the first index is over the cap and `meta.filtered` isn't empty, store the list as a second text result. Then rebuild the entry with the meta replaced, so `read_result`'s root index pages match the first index (Q1, Q3).
- [x] `src/OneNoteMcp.Core/Access/AccessDecisions.cs` -- `BroadAccess.Warnings`: one `readNotGranted` warning for all withheld sections (Q2).
- [x] `tests/OneNoteMcp.Tests/FindResultCapTests.cs` -- un-skip it as the red test. Add reassembly (the second result reads back as the full list), the `read_result` root index matching the first index, and the single warning.
- [x] Existing tests that assert one warning per withheld section -- updated to the single warning.
- [x] `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- the two new optional meta fields.
- [x] The descriptions of `find_pages`, `find_meta` and `read_result`, and the plugin skill's search reference -- the new fields and the single warning.
- [x] The spine: AD-20 amended and its Deferred row resolved; the AD-12 and AD-21 lines on per-section warnings; a memlog entry.

**Acceptance Criteria:**
- Given a stored search result whose header is over the cap, when it returns, then its first text block is at most `results.inlineMaxChars`, and every filtered entry, withheld ID and warning is readable through the tools.
- Given the harness on MCP Test (a live step for the next sitting), when it runs, then it exits 0, with 0 blocks over the inline maximum.

## Implementation Notes

- 2026-11-19, the measurement only (no fix). `FindResultCapTests` was written first, against the real `ResultDelivery` and `EnvelopeMeasure`. It failed for 1,000 and 2,000 filtered entries and passed for 0 and 200 (figures in the Intent). It is committed skipped so CI stays green until the fix.
- **Build (2026-11-19).**
  - `ResultDelivery.Deliver` builds the stored entry, then measures its first index. If that is over the cap and `meta.filtered` isn't empty, it stores the list as JSON text: a `ResultKind.Text` entry under the parent's grants, in the envelope's camelCase. It then rebuilds the entry with `Filtered` null and `FilteredCount` and `FilteredResultId` set, and only after that adds it to the store and returns `FirstIndex`.
  - `BroadAccess.Warnings` now gives one `readNotGranted` warning: the count, split by marker and by path, with no IDs.
  - `ToolMeta` gains `FilteredCount` and `FilteredResultId`.
  - The descriptions of `find_pages`, `find_meta` and `get_hierarchy`, the plugin skill and the README are updated.
  - The contract snapshot was regenerated from the test's printed JSON: the two new optional fields, and nothing else.
- **TDD.** `FindResultCapTests` was un-skipped, with new cases added first: 300 withheld sections; reassembly through `read_result`; the root expand paged like the first index; the single warning; a header that fits staying unchanged. 8 of 11 failed, as expected. After the fix all 11 pass.
  - The reassembly test's first run failed on my own call shape: `read_result` needs a ref, here `/`, with `offset`. I fixed the test, not the code.
  - The root-index test first asserted fields that `expand` responses never carry. It was corrected to compare paging, which is what the shared header affects.
- **Existing tests updated to the single warning:** `ProtectedByPathTests.ANotebookFolderUnderARoot_…`, and in `ProtectedSectionToolTests` (and its `ProtectedByPathToolTests` subclass) the broad-hierarchy, locked-vault and find-pages warning lists. One test was renamed `…WithOneWarningForAll`.
- **Version.** No bump (Decisions).
- **Verification (2026-11-19).** Build: 0 warnings. Unit tests: 3,794 passed, 3 skipped.

## Plan Change Log

## Review Triage Log

- 2026-11-19, quick lens: 8 findings. Six were patched and two deferred.
  - **medium, patch.** The live tests broke on the new shapes:
    - `ProtectedSectionIntegrationTests` looked for the vault ID in the warning;
    - `SearchToolsIntegrationTests` read `meta.filtered` unconditionally;
    - the stress harness counted filtered entries from `meta.filtered` only.

    Fixes: the integration tests now accept the single warning and the moved list. The harness reads a moved list back through `read_result`, the way it reassembles bodies.
  - **low, patch.** The `read_result` description didn't mention `filteredResultId` or `meta.binary`. Both are now there, and the find tools' success shapes name `filteredCount` and `filteredResultId`.
  - **low, patch.** The "small body, long header" row had no test. Added `ASmallBody_WithAFilteredListOverTheCap_IsStored_AndTheListMoves`.
  - **low, patch.** The spine's claim that "nothing else grows" was wrong, because `meta.withheld` does. The spine was reworded and a deferred entry added.
  - **medium, defer.** The moved list can be evicted by the main result's own store. Rare; recorded in deferred-work.
  - **low, defer.** The list's store event isn't logged. Recorded in deferred-work.
- 2026-11-19, Fable check of the spine edit, at low effort: 4 must-fix and 8 optional findings, each checked against the source. All held.
  - **Must 1 and 2, version: open for Phill.** `v0.9.0-rc.1` was tagged and released on 2026-11-19 at 4abcaa1, after the plan recorded "no bump" on the premise that it was unreleased. AD-14 (before 1.0.0, any contract change bumps the minor version) points to 0.10.0. The memlog now records the question instead of the unsupported reading. The frozen Version decision is left as written, for Phill to revisit.
  - **Must 3, fixed.** As in the quick lens: `meta.withheld` is named as the remaining growing list, and a Deferred row was added.
  - **Must 4, fixed in code.** Q1 says every index carries the moved list's fields. They were on the first index only, so `ExpandResponse` and `RunIndexResponse` now add them through `WithMovedList`. Red test first: `ReadResultsIndexes_CarryTheListsCountAndId_AndArePagedLikeTheFirstIndex`.
  - **Optional 5–12, applied:**
    - where the list's text arrives (`meta.binary`; the AD-9 sentence widened);
    - the expiry, eviction and grant conditions of the second result;
    - the Deferred row now says the fix is built and the live check is pending, and that the cause was measured in a unit test;
    - the lost per-section reason is noted;
    - AD-21 prose and the `get_hierarchy` sibling bullet;
    - the AD-4 and AD-12 lines and AD-20's Contract line;
    - the memlog `updated` date.
- Verification after both: build 0 warnings; unit tests 3,795 passed, 3 skipped; the stress harness builds.
- 2026-11-19, Phill's answers recorded in Decisions: version `0.10.0-rc.1` (Directory.Build.props, `contractVersion` 0.10, and AD-14 states that a pre-release tag counts), every index, and all optionals kept.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, `FindResultCapTests` un-skipped and green.
- TDD: `FindResultCapTests` is the red test; each new case is written first.

**Live (the coordinator's next sitting):** the stress harness on MCP Test exits 0, with 0 blocks over the inline maximum.
