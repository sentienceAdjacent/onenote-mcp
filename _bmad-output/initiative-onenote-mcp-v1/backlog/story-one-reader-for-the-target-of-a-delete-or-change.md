---
id: 1
type: story
title: "One reader for the target of a delete or change"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# One reader for the target of a delete or change

## Description

The soft delete, the permanent delete and the rename/reorder writes read their target through one Xml reader with one rule for the recycle bin and one `pageLevel` parse, in place of today's `DeleteTarget` (5.5), `PermanentDeleteTarget` (5.6) and `HierarchyTarget` (5.4), and their subpage counts.

## Acceptance Criteria

Verify: unit tests for the odd XML inputs (a malformed, signed, padded or below-1 `pageLevel`; `isRecycleBin` on an element that isn't a section group; `isDeletedPages` beside `isInRecycleBin`) pass, and the three call sites (`SoftDeleteUseCase`, `PermanentDeleteUseCase`, `HierarchyWrite`) use the one reader.

## Boundaries

- Must not change: anything a real OneNote read produces. Every refusal, error code, message and `userAction` of the three tools stays as it is for well-formed hierarchy XML; only the readers' answers on XML OneNote never writes may move, and the new tests pin them.

## References

- parent — none (standalone backlog leaf; epic 5 closed with story 5.8)
- `_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/story-refactor-sweep-plan.md` — Answers (Q1) and Intent's out-of-scope list
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — Story 5.6 entry "Share points left after the rebase onto the built 5.5"
- `src/OneNoteMcp.Xml/HierarchyElements.cs` — the shared element checks 5.8 left for this merge

## Notes

- Decision (Phill, 2026-11-15, 5.8 Q1): merge the three target readers, as this small follow-up, with tests for the odd inputs; not in 5.8.
- Assumption (5.8's recommendation, for the build to confirm): take `HierarchyTarget`'s rules (`isRecycleBin` on any element, or a section group named `OneNote_RecycleBin`; `pageLevel` as `NumberStyles.Integer`, values below 1 read as 1), and keep `IsInRecycleBin` and `IsDeletedPages` apart with a computed `InRecycleBin`, since a permanent delete allows a recycled section and refuses Deleted Pages.
- Decision (Phill, 2026-11-15, 5.8 Q2): the refusals the two deletes share stay separate; this story merges readers only.
