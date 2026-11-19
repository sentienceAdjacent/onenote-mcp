---
id: 16
type: bug
title: "restore_from_recycle_bin offers a journal entry that an earlier restore already used"
parent: none
covers: [R5, R8]
after: []
assignee: ""
refined: false
hitl: false
risk: low
severity: P3
estimate: ""
---

# restore_from_recycle_bin offers a journal entry that an earlier restore already used

## Description

When `restore_from_recycle_bin` matches a recycled page by name and lists candidates, it can include a soft-delete journal entry whose page has already been restored. The model then asks the user to choose between entries when one of them has already been used. Whether a used entry should be left out is still open.

## Reproduction

1. MCP Test, during the 3.10 sitting (2026-11-19). Two pages named "S310 Dup" were soft-deleted, giving the journal entries 846d72ff and eea8e8ac.
2. In part A, one of them was restored from one of those entries.
3. The notebook was closed and reopened, which changes the recycle-bin IDs, and the other page was then restored by name.
4. Actual: both entries are listed as candidates.
5. Expected, if used entries are excluded: only the unused entry matches, so the page is restored without a choice, and the result carries `restoredByName`.

## Cause Hypothesis

Nothing records that a restore has used a soft-delete entry, so the name match counts every soft-delete entry with that title. The `recycleBinId` rule, which leaves out entries owned by another page still in the bin, doesn't help after a reopen, because those IDs have changed.

## Acceptance Criteria

Verify:
- Unit tests show that, after a page has been restored from a soft-delete entry, a later by-name restore of a page with the same name doesn't list that entry as a candidate. If Phill decides used entries stay listed, the candidate text marks that entry as already used instead.
- Optionally, live: the reproduction above gives one candidate or none.

1. **The expected behavior holds**
   **Given** a soft-delete entry whose page an earlier restore brought back
   **When** a recycled page with the same name is restored by name
   **Then** that entry isn't offered as a candidate (or is marked as used, per the decision below)
2. **Tests cover the condition found and fixed**
   **Given** the unit test suite
   **When** it runs
   **Then** a test that fails on the defect and passes on the fix covers a used entry and an unused one with the same title
3. **Or: no change is needed, with proof**
   **Given** the reproduction
   **When** it is run on the current code
   **Then** the used entry is already excluded or marked, with the evidence recorded in Notes; this supersedes 1 and 2

## References

- parent — none
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-8 `restore_from_recycle_bin` (Which entry)
- plan — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-restore-from-recycle-bin-plan.md

## Notes

- Unknown: should a used entry be excluded from the candidates, marked as used, or left as it is? Settle it with Phill before the build. The restore's own journal entry names the target, but not the soft-delete entry it used, so recording that link may be part of the fix.
- Severity is low: the result is a question to the user, never a wrong restore.
