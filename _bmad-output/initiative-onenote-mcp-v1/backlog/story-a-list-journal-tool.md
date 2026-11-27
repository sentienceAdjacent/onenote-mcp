---
id: 27
type: story
title: "A list_journal tool"
parent: none
covers: [R5]
after: [22]
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# A list_journal tool

## Description

A caller that lost a write's `meta.journalEntryId` can't find the journal entry it needs for `restore_from_journal`: no tool lists the journal. This story adds a read-only `list_journal` tool that lists entries by page or object ID and time range, newest first, without returning page content, and adds it to the spine (AD-8 journal, AD-10 entry shape, AD-14's tool list) and the skill. Phill's step: approve the spine amendment after its Fable review.

## Acceptance Criteria

Verify: list_journal returns the entries for a given page or object ID and time range, newest first, with each entry's id, operation, target and time but no content; entries for protected pages follow AD-21; the contract snapshot and coverage table are updated; the skill tells agents to use it when a journal entry id is lost.

## Boundaries

- Read-only: it never changes, prunes or restores an entry.
- No page content in its result (AD-13, AD-21).

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D121)
- AD-8 (journal), AD-10 (entry shape), AD-14 (tool contract) and AD-21 in the spine

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).
- A new tool moves the tool contract: the release that ships it bumps the minor version (AD-14).
