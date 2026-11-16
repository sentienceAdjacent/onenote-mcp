---
id: 5
type: story
title: "Hash page-level images, ink and files"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# Hash page-level images, ink and files

## Description

An `one:Image` (also ink and `one:InsertedFile`) placed directly on the page, not inside an Outline, gets no `elementHash`. A stale write to it is therefore caught only by the page-wide checks. Found by backlog 2's sitting check, 2026-11-16. Low priority.

## Acceptance Criteria

Verify: hashes appear for page-level images, ink and inserted files, and a stale edit to one of them is refused with the element-level error (not only the page-wide one).

## Boundaries

- Must not change: the hash and read-back rules for Outlines (AD-17).

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/backlog/story-stop-sending-z-in-the-outline-positions-we-write.md` — backlog 2, whose sitting check found this
- AD-7 and AD-17 in the architecture spine

## Notes

- Priority: low (Phill, 2026-11-16).
