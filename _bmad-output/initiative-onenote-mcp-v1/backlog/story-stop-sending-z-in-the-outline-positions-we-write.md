---
id: 2
type: story
title: "Stop sending z in the Outline positions we write"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# Stop sending z in the Outline positions we write

## Description

`update_page_content` strips the `z` attribute from every `one:Position` it sends to `UpdatePageContent`, so resending an Outline no longer makes OneNote re-stack the page's Outlines and change the hash of Outlines the caller didn't send.

## Acceptance Criteria

Verify: a unit test on the XML sent to the gateway shows no `z` on any `one:Position` (an Outline sent with `z` and one sent without), with `x` and `y` kept; the existing update tests still pass.

## Boundaries

- Must not change: the hash and read-back rules (AD-17), or any other attribute of a sent Position.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — Story 2.13 entry "Probe done (2026-11-15 sitting…)" and the original "Resending an Outline's own `one:Position z`" entry (sitting C3)
- `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-refactor-sweep-plan.md` — Answers Q1

## Notes

- Decision (Phill, 2026-11-15, 2.13 Q1): if the live probe shows no re-stack without `z`, a small follow-up stops sending `z`. The 5.7 sitting probe showed exactly that: with `z`, the Outlines re-stacked and the unsent one's hash changed; without `z`, nothing moved.
- Assumption: dropping `z` on a newly added Outline is harmless (OneNote stacks a new Outline on top); the build confirms with the existing create and update tests.
