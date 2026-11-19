---
id: 15
type: bug
title: "find_pages and find_meta return a first text block over the inline size cap when many sections are protected"
parent: none
covers: [R2]
after: []
assignee: ""
refined: false
hitl: false
risk: low
severity: P3
estimate: ""
---

# find_pages and find_meta return a first text block over the inline size cap when many sections are protected

## Description

When a search result is stored (AD-20), the first text block of its envelope should stay within `results.inlineMaxChars` (100,000 by default). With many password-protected sections, the block from `find_pages` and `find_meta` goes over it, so a client with a strict cap may truncate or refuse it. Apart from its size, the content is correct.

## Reproduction

1. MCP Test, with 29 encrypted sections (2026-11-19).
2. Run the large-results harness on MCP Test.
3. Actual: 0 mismatches, but 16 text blocks over 100,000 characters (largest 102,870): 11 from `find_pages` and 5 from `find_meta`.
4. Expected: no block over the inline maximum. The story 5.7 run, with 9 encrypted sections, had none over.

## Cause Hypothesis

Inferred, not measured: the per-section `readNotGranted` warnings in the stored result's envelope aren't counted against the cap when the inline part is sized. They grow with the number of protected sections, which would explain why 29 sections push the block past the cap when 9 did not.

## Acceptance Criteria

Verify:
- The harness on MCP Test exits 0, with 0 blocks over inline-max.
- Unit tests build stored `find_pages` and `find_meta` results with enough protected-section warnings to cross the cap, and show that the first text block stays within `results.inlineMaxChars`.

1. **The expected behavior holds**
   **Given** a search over many protected sections whose result is stored
   **When** `find_pages` or `find_meta` returns
   **Then** the envelope's first text block is at most `results.inlineMaxChars`, and nothing is lost: every warning and result stays reachable, inline or through `read_result`
2. **Tests cover the condition found and fixed**
   **Given** the unit test suite
   **When** it runs
   **Then** a test that fails on the defect and passes on the fix covers each of the two tools, with warnings that cross the cap
3. **Or: no change is needed, with proof**
   **Given** the reproduction
   **When** it is run on the current code
   **Then** no block is over the cap, or the harness was counting something else, with the evidence recorded in Notes; this supersedes 1 and 2

## References

- parent — none
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-20 (large-result delivery)

## Notes

- Open question: the cause is inferred. Confirm which part of the envelope pushes it over before choosing a fix.
- Phill's standing rule: a fix never loses functionality to meet the cap. Warnings may move into the stored part, but are never dropped.
