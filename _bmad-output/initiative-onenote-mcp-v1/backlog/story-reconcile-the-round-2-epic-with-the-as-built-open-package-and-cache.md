---
id: 20
type: story
title: "The round-2 epic describes the as-built open_package destination and the page-only cache drop"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: true
risk: low
estimate: ""
---

# The round-2 epic describes the as-built open_package destination and the page-only cache drop

## Description

The accepted round-2 epic still describes two things the way they were planned, not the way they were built.
- **E3 and Done when 1** say `open_package` unpacks "into a new folder under an export root". As built (story 3.8), it unpacks into the opened-copies folder, and a full path inside an export root is refused. The spine already says so.
- **E5** says a move "drops moved pages from the search cache". A section move drops none, and needs none, because the cache is keyed by page ID and date.

This story edits the epic file to match, as a doc-only change. Phill approves the wording at build, since the epic is accepted (hitl).

## Acceptance Criteria

Verify:
- The epic file's E3 and Done when 1 describe the opened-copies destination, matching the spine's AD-7 and AD-11 `open_package` text.
- E5's cache sentence covers page moves only.
- A dated Decision in the epic's Notes cites the retrospective.
- Phill approves the diff before it merges. No code or test changes.

## Boundaries

- Must not change: anything in the epic but those two passages and the Notes line; the spine, which is already right; any code.

## References

- retro — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural-retrospective.md, findings R1 and R7; action items 4 and 5
- epic — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural.md, E3, E5, Done when 1
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-7 and AD-11 (`open_package`)
- plan — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-open-package-plan.md (the Q3 rework)

## Notes

- Decision (Phill, 2026-11-19, Inbox): filed from the epic 3 retrospective, action items 4 and 5. A doc edit to an accepted epic, so it needs Phill's approval at build.
- hitl: Phill approves the wording.
