---
id: 22
type: story
title: "Spine sync: record what epics 2, 5 and round 2 built"
parent: none
covers: [R4, R5, R6, R8]
after: []
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# Spine sync: record what epics 2, 5 and round 2 built

## Description

The spine updates for epic 5 (stories 5.3 to 5.6) and parts of epics 2 and round 2 were left for "a separate chore" (round-2 spine-amendments plan, J7) that never ran. The spine now lags the code in 33 places, and in two it says the opposite: AD-7 says a reorder may list only some children, but every live child must be listed once (D134); and AD-7 says a declined file path offers sending the image inline, which the code stopped doing in 2.13 (D171). Several Deferred rows still say "not probed" or "open" for questions the 2026-11-13 to 11-15 sittings settled (D114, D116, D122, D139, D148).

This story amends the spine (and its memlog, one dated line per decision recorded) so that each of the 33 items in group A of the triage (and D160's leftover: the delete rules still say an empty-group delete never ran) is either written into the AD it belongs to, or its Deferred row is marked settled with the sitting that settled it. It writes down what the code does; it changes no code. A rule the builder finds the code and the spine disagree on in a way the triage didn't list goes back to Phill as a question, not into the spine. Phill's step: approve the amended spine after its Fable review.

## Acceptance Criteria

Verify: every group-A item in the triage is either reflected in the spine (citing the AD and line) or closed as a settled Deferred row, recorded in the story's plan as a table; the spine no longer contradicts the code on reorder lists or declined image paths; the Fable final review has run and its confirmed findings are resolved; Phill approves the spine.

## Boundaries

- No code, test or contract change.
- No new design decision: where the code and a recorded decision disagree, ask Phill.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D90, D94, D114, D116, D122, D124, D125, D126, D127, D133, D134, D135, D138, D139, D141, D142, D143, D144, D145, D146, D148, D149, D150, D151, D152, D153, D154, D155, D156, D157, D171, D175, D181)
- `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` and its `.memlog.md`
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md` — J7, where this chore was left

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).
- Planning doc: runs the Fable final review before it is marked final (global rule).
