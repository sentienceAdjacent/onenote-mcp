---
id: 18
type: story
title: "Tests show page writes and restore re-check the notebook under the lock"
parent: none
covers: [R6, R8]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# Tests show page writes and restore re-check the notebook under the lock

## Description

Story 3.12 made the write pipeline re-check, under the lock, that every notebook step 1 granted is still the one the target sits in. Create, the moves, merge, `set_page_level` and the hierarchy writes have tests for a target that moves to another notebook mid-call. The page writes and `restore_from_recycle_bin` don't. Those page writes are `update_page_content`, `delete_page_content`, soft delete and permanent delete. Without such tests, a regression could let a page be written under the first notebook's grant with no test failing. This story adds them; no behaviour changes.

## Acceptance Criteria

Verify:
- For each of `update_page_content`, `delete_page_content`, `delete_hierarchy_object` (page), `permanently_delete_hierarchy_object` (page) and `restore_from_recycle_bin`, a unit test moves the target's section to another, also granted, notebook between step 1 and the lock. Each test asserts `WriteNotGranted`, retryable, nothing sent, and no journal entry.
- Each new test fails when the pipeline's re-check is disabled (a mutation check recorded in the plan), and passes with it in place.

## Boundaries

- Must not change: the pipeline or any write's behaviour. This story adds tests only. If a test shows a real gap, that gap becomes a bug of its own.

## References

- retro — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural-retrospective.md, finding V1; action item 6
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-7 "A write's notebook is re-resolved under the lock"

## Notes

- Decision (Phill, 2026-11-19, Inbox): filed from the epic 3 retrospective, action item 6.
- The re-check is the ledger re-resolve in `WritePipeline`. Existing examples to copy are `CreatePageToolTests.ASectionMovedToAnotherNotebookAfterStep1_…` and `SetPageLevelToolTests.ANotebookChangedUnderTheLock_…`.
