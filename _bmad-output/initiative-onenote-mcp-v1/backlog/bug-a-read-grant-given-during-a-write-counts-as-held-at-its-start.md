---
id: 9
type: bug
title: "A read grant given during a write counts as held at its start"
parent: none
covers: [R4, R6]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# A read grant given during a write counts as held at its start

## Description

Several writes need a protected section's read grant "held at the call's start" (AD-7 step 1.2, AD-21): a page write whose basis carries a hash or whose XML carries an `objectID` or `removeObjectIds`, `delete_page_content`, a page permanent delete whose basis carries a hash, `move_hierarchy_object` with a `position` in a protected section (3.4), `publish` from a protected section, and `reorder_hierarchy_children` on a protected section's pages (backlog 7). Each asks `ReadAccess.HoldsReadGrant` / `HoldsReadGrantAt` with the call's start time.

Those checks only ask whether a grant is unexpired at that instant (`src/OneNoteMcp.Core/Access/ReadAccess.cs`, `HoldsReadGrantAt`; `OnceWindows.IsOpen` tracks expiry only). A stored grant made, or a `once` window opened, after the call started but before its check runs still counts. Measured in the backlog 7 build (2026-11-17): a FakeGateway test whose step-3 read moved the clock 1 s and opened a `once` window for the section saw the reorder go through.

Low severity: only the user's own consent, in the grant dialog or the grant command, makes a grant, so this lets nothing through that the user didn't allow. It makes "held at the call's start" true only for expiry.

## Acceptance Criteria

Expected: a read grant counts for a write only if it was given at or before the call's start (stored grant `grantedAt <= at`, `once` window opened at or before `at`), and is unexpired then.

Phill approved filing it (Inbox, 2026-11-17), covering every "held at the call's start" check at once.

Verify: unit tests on the FakeGateway: for each of the checks above, a grant stored, or a `once` window opened, after the call's start (during its step-3 read) is refused with `ReadNotGranted`; a grant held before the call's start still passes; reads and `list_grants` are unchanged.

## Boundaries

- Must not: touch OneNote in tests; prompt for a grant from a write; change how reads decide access, or how grants are stored on disk beyond what an opened-at time needs.

## References

- parent — none (standalone backlog leaf)
- `src/OneNoteMcp.Core/Access/ReadAccess.cs` (`HoldsReadGrant`, `HoldsReadGrantAt`, `HoldsReadGrantAtLocation`), `OnceWindows`
- Callers to check: `PageWrite` (page writes and `delete_page_content`), `PermanentDeleteUseCase`, `PublishUseCase`, `MovePageWrite` (position), `ReorderHierarchyWrite`; `MoveContainerWrite` and the rename use `HoldsReadGrantAtLocation` for a different question (where a section lands) and may stay as they are
- Spine AD-7 step 1.2, AD-21 Writes (l.239's "no grant that would let a read of that section pass at the call's start")
- Backlog 7 plan, Implementation Notes (the measurement)

## Notes

- Filed from the backlog 7 build; Phill approved filing it (2026-11-17).
