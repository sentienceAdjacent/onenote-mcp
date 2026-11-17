---
id: 7
type: bug
title: "reorder_hierarchy_children errors show a protected section's page count"
parent: none
covers: [R4, R6]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# reorder_hierarchy_children errors show a protected section's page count

## Description

`reorder_hierarchy_children` (story 5.4) on a password-protected section needs only a write grant. With no read grant at the call's start, `ReorderHierarchyWrite` already hides page IDs (`namesIds` is false, set in `ReadMoreAsync` from `ReadAccess.HoldsReadGrant`), but its messages still carry counts, in `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs`:

- `ValidateChange`: "{foreign.Count} ID(s) aren't live children of …" and "{missing.Count} child(ren) of … are missing from childIds". The caller knows how many IDs it sent, so these two counts give the section's page count. A caller can learn it by sending one made-up ID.
- `CheckPageBlocks`: "{moved.Count} page(s) would no longer follow their own parent page", which says something about where the subpages sit.
- `ReadBack`: "{read.Count} live child(ren) read back", and the `pageLevel` count after it. These only come after a write that went through, which needed every page ID, so they leak little; they are listed for completeness.

Story 3.4 treated the same thing as a leak (PR #67, security patch): `MovePageWrite`'s position errors no longer carry a target's page count, and a position in a protected target needs a read grant first.

This conflicts with a recorded decision. The spine's AD-7 `HierarchyWrite` read-back bullet allows counts: "In a protected section with no read grant, the rename and reorder tools' warnings and refusals carry counts only" (Phill, 2026-11-12, 5.2 Fable N6, which accepted the page-reorder ID oracle with counts-only warnings). So the fix needs Phill to decide again, in light of 3.4, and an AD-7 amendment.

Not confirmed live. Low severity: the count is all that leaks, and only to a caller that already holds the notebook's write grant.

## Acceptance Criteria

Expected: in a protected section with no read grant at the call's start, no `reorder_hierarchy_children` refusal or warning carries a count or ID of the section's pages.

One way to get there, to weigh in the plan: refuse with `ReadNotGranted` (never prompting) before any list check, as 3.4 did for a position. A full `childIds` list can't be built without reading the page IDs anyway, which needs a read grant. Reordering sections and section groups (a notebook or section group target) is unaffected either way.

Verify: unit tests on the FakeGateway: a reorder of a protected section without a read grant, with a made-up ID, a missing ID and a broken subpage block, returns messages with no count and no ID of its pages; with a read grant the current messages are unchanged.

## Boundaries

- Must not: touch OneNote in tests; change what a read grant allows; prompt for a read grant.
- Needs Phill's decision and an AD-7 amendment (the counts-only line) before the build.

## References

- parent — none (standalone backlog leaf)
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs`, `ReorderHierarchyWrite` (`ReadMoreAsync`, `ValidateChange`, `CheckPageBlocks`, `ReadBack`)
- Spine AD-7, `HierarchyWrite` read-back bullet; `epic-hierarchy-writes-and-deletes/story-spine-amendments-from-the-hierarchy-probes-plan.md`, N6
- PR #67 (story 3.4), the position-count security patch in `MovePageWrite`

## Notes

- Filed after the 3.4 security review; Phill approved filing it (2026-11-17).
