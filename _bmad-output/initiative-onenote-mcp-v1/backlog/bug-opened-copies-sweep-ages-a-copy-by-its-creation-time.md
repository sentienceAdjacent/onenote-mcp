---
id: 21
type: bug
title: "The opened-copies sweep deletes a copy edited in the last 7 days, because it ages copies by creation time"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: false
risk: medium
severity: P2
estimate: ""
---

# The opened-copies sweep deletes a copy edited in the last 7 days, because it ages copies by creation time

## Description

At startup the server sweeps the opened-copies folder and deletes, whole, any copy older than 7 days that isn't open in OneNote. Age is measured from the copy folder's creation time. So suppose a user opens a package, edits pages in the copy for a week, then closes it. At the next start the copy is deleted, edits included, with no prompt and no backup. A copy changed within the last 7 days should never be deleted. Age should run from the last change to the folder or any file in it.

## Reproduction

1. Unit or temp-folder setup: an opened copy folder created 8 days ago, with a section file inside last written 1 day ago, and the copy not open in OneNote.
2. Run the startup sweep.
3. Actual: the copy folder is deleted, because its creation time is more than 7 days old.
4. Expected: the copy is kept, because its newest change is 1 day old.

## Cause Hypothesis

The sweep's age check reads the folder's creation time alone, and ignores later changes to the folder and its files.

## Acceptance Criteria

Verify: unit tests on a temporary folder show the sweep keeps a copy whose folder or any file inside changed within 7 days, even if the folder was created earlier. A copy whose newest change is over 7 days old, and that isn't open, is still deleted. The existing sweep tests pass.

1. **The expected behavior holds**
   **Given** an opened copy created more than 7 days ago, with a file changed within 7 days, not open in OneNote
   **When** the startup sweep runs
   **Then** the copy is kept
2. **Old, untouched copies still go**
   **Given** an opened copy whose newest change, to the folder or any file in it, is more than 7 days old, not open in OneNote
   **When** the startup sweep runs
   **Then** it is deleted, as before
3. **Tests cover the condition found and fixed**
   **Given** the unit test suite
   **When** it runs
   **Then** a test that fails on the defect and passes on the fix covers a recently edited copy, and one covers a nested file being the newest change
4. **Or: no change is needed, with proof**
   **Given** the reproduction
   **When** it is run on the current code
   **Then** the copy is already kept, with the evidence recorded in Notes; this supersedes 1 to 3

## References

- retro — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural-retrospective.md, finding V2; action item 12
- plan — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-open-package-plan.md (Phill's cleanup answer)

## Notes

- Decision (Phill, 2026-11-19, Inbox): age is measured from the last change in the folder or its files, not from the folder's creation time. Never delete a copy changed within 7 days.
- Where: `OpenedCopies.IsOld` (around l.161, `Directory.GetCreationTimeUtc`; `MaxAge` 7 days), called by the startup sweep service.
