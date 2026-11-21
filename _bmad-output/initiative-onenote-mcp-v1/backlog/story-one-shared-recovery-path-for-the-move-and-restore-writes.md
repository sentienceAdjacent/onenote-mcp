---
id: 17
type: story
title: "One shared recovery path for the move, section-move and restore writes"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: false
risk: medium
estimate: ""
---

# One shared recovery path for the move, section-move and restore writes

## Description

The page move, the section move and the restore each carry their own copy of the partial-copy re-read. The copies have drifted apart: names are compared with `Ordinal` in two of them and `OrdinalIgnoreCase` in the third, and IDs in a protected target are hidden in two but not in the third. This story replaces the copies with one shared re-read and aligns them on the safer choice: protected targets give counts only, and one agreed name comparison. It also gives the `settled` warning one text, moves the constants the sibling writes borrow from the page move into one shared place, and moves step-7 recovery toward the write pipeline, which already owns ambiguous-failure handling. Behaviour is unchanged except where the copies disagreed.

## Acceptance Criteria

Verify:
- Unit tests pin the shared re-read's message for a page move, a section move and a restore: counts only in a protected target, and the same rule in all three.
- One `settled` text is asserted for the section move, the restore and the merge.
- The existing move, restore and merge tool tests pass. Any test that changes because the copies disagreed is listed, with the choice made.
- `dotnet build -warnaserror` gives 0 warnings.

## Boundaries

- Must not change: what each write sends to OneNote, its error codes, its journal entries, or its `outcomeUnknown` behaviour. The only intended changes are where the three copies disagreed.
- Out of scope: new recovery behaviour; the merge's own failure re-read, except where it can share the same helper unchanged.

## References

- retro — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural-retrospective.md, findings D1, D2, D3 and P1; action item 8
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-7 Moves and step 7

## Notes

- Decision (Phill, 2026-11-19, Inbox): filed from the epic 3 retrospective, action item 8.
- Where the copies are (retro D1): `MovePageWrite`, `MoveContainerWrite` and `RestoreFromRecycleBinWrite`, each in its `PartialCopyFindingsAsync`. The `SettledWarning`/`SettledUserAction` copies are in `MergeSectionsWrite`, `MoveContainerWrite` and `RestoreFromRecycleBinWrite`. The borrowed constants are `PartialCopyHResult`, `OperationName` and `ProtectedPositionUserAction`, read from `MovePageWrite`.
- Decision (Phill, 2026-11-21, Inbox): names compare by kind, one rule in all three: page names `Ordinal`, section and section-group names `OrdinalIgnoreCase`, as OneNote compares them (evidence in the plan's Decisions, Q1).
