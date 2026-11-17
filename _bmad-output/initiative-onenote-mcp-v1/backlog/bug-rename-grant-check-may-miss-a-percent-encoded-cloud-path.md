---
id: 8
type: bug
title: "Rename's read-grant check may miss a percent-encoded cloud path"
parent: none
covers: [R4, R6]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# Rename's read-grant check may miss a percent-encoded cloud path

## Description

Before a rename, story 5.4 checks that it won't move a section onto a location that holds a read grant, so our own write never hands one section's grant to another (AD-21). `HierarchyWrite.GrantLocationRefusal` in `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` does this with `ReadAccess.HoldsReadGrantAt`, which matches the stored grant key exactly (ignoring case). A cloud path written with percent-encoding (`%20` for a space) on one side and decoded on the other wouldn't match, so the check could let the rename through and the moved section would pick up the grant.

PR #72 (story 3.12, not merged yet) adds `GrantKeys.SameLocation` (trimmed, URL-decoded on both sides, ignoring case) and `ReadAccess.HoldsReadGrantAtLocation` for the same check on a move. Its plan names this rename check as the one-line follow-up.

Not confirmed: we don't know yet whether OneNote ever gives the same cloud section's path in two encodings. Low severity.

## Acceptance Criteria

Expected: the rename check refuses a rename onto a location whose grant key differs from the new path only by percent-encoding.

Fix: in `GrantLocationRefusal`, use `HoldsReadGrantAtLocation` in place of `HoldsReadGrantAt`. Consider `GrantKeys.SameLocation` for the "section stays at its own key" comparison beside it too, so a case-only or encoding-only rename still doesn't count as a move.

Verify: a unit test renames a cloud section (and a section group holding one) onto a location whose stored grant key is percent-encoded where the new path isn't, and gets `InvalidArgument` with nothing renamed; the existing rename tests still pass.

## Boundaries

- Must not: touch OneNote in tests; change how grants are stored or how reads match them.
- Depends on PR #72 (3.12) landing; build after it merges.

## References

- parent — none (standalone backlog leaf)
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs`, `RenameHierarchyWrite.GrantLocationRefusal`
- `src/OneNoteMcp.Core/Access/ReadAccess.cs`, `HoldsReadGrantAt`; PR #72, `GrantKeys.SameLocation` and `ReadAccess.HoldsReadGrantAtLocation`
- Spine AD-21

## Notes

- Filed from the 3.12 build's "Not changed here" note; Phill approved filing it (2026-11-17).
