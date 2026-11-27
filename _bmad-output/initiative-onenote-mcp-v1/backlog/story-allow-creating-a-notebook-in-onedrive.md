---
id: 26
type: story
title: "Allow creating a notebook in OneDrive"
parent: none
covers: [R6]
after: [22]
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# Allow creating a notebook in OneDrive

## Description

`create_notebook` refuses a cloud (OneDrive) location: cloud notebook creation is switched off in code. The 2026-11-15 sitting's cloud probe passed (the read-back key matched the URL), but switching it on and how the OneDrive base URL is found were never decided. This story decides both with Phill, records them in the spine (AD-6 and AD-7 Create), switches the create on, and tests it. Phill's step: a sitting that creates a notebook in OneDrive and checks its grant and read-back.

## Acceptance Criteria

Verify: with Phill present, create_notebook makes a notebook in OneDrive, its write grant is created as for a local create, and the read-back matches; unit tests cover the cloud path's refusals; the spine records the decision.

## Boundaries

- Must not change: local create rules, the auto-grant rule (key match, not open before, empty notebook).

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D128)
- `_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/story-create-section-section-group-and-notebook-plan.md` — the 2026-11-15 cloud probe
- AD-6 and AD-7 Create in the spine

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).
- Open question: how the OneDrive base URL is found (settings, the account, or the user) is decided with Phill at planning.
