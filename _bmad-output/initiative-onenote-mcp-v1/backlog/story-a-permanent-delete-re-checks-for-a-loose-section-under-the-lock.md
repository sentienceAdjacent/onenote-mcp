---
id: 33
type: story
title: "A permanent delete re-checks for a loose section under the lock"
parent: none
covers: [R4, R6]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# A permanent delete re-checks for a loose section under the lock

## Description

A permanent delete of a page refuses a page in a section open outside any notebook (a loose section, `NotSupported`) only in step 1, before the lock. Step 4 repeats the other refusals under the lock but not this one, so a section that becomes loose between step 1 and the lock isn't refused there. This story adds the loose-section check to the permanent delete's step 4, under the lock, in the same place in the check order as step 1, with a unit test. The spine's `PermanentDelete` check-order bullet then drops its note that step 4 skips it.

## Acceptance Criteria

Verify: a unit test shows that a page permanent delete whose section passes step 1's loose-section check but is loose by step 4 is refused with `NotSupported` (the loose-section message and `userAction`) under the lock, with no journal entry, no backup and no `DeleteHierarchy` call; the existing permanent-delete tests still pass; and the spine's AD-7 `PermanentDelete` check order no longer says step 4 skips the loose-section check.

## Boundaries

- Must not change: the order of the other refusals, step 1's behaviour, or any other write's checks.
- Never auto-retries a write (AD-7, AD-17).

## References

- parent: none (standalone backlog leaf)
- Spine AD-7 `PermanentDelete`, the check-order bullet (backlog 22)
- `_bmad-output/initiative-onenote-mcp-v1/backlog/story-spine-sync-record-what-epics-2-5-and-round-2-built-plan.md`: question 2

## Notes

- Filed 2026-11-27 from backlog 22; Phill's choice (Inbox, 2026-11-27, backlog 22 Q2).
- The `pageHash`-on-a-non-page check also runs only in step 1; it depends only on the target's kind, which a step-4 read can't change into a page, so it stays out of scope.
