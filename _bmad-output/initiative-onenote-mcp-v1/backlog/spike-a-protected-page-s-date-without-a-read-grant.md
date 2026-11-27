---
id: 28
type: spike
title: "A protected page's date without a read grant"
parent: none
covers: [R4]
after: [22]
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# A protected page's date without a read grant

## Description

Without a read grant, nothing returns a password-protected page's `lastModifiedTime`, so a write there can only run with no basis and can't detect a conflict. The AD-17/AD-21 Deferred row is still open. The question: can the server give a protected page's date (from the hierarchy, or a create's result) without exposing anything AD-21 protects, and should it?

Who waits: the AD-17/AD-21 Deferred row and any story it leads to. The answer goes in a findings doc beside this ticket, and the decision in the spine's memlog. Phill's step: decide, from the findings, whether the date may be shown, and be present for any probe on a protected throwaway section.

## Acceptance Criteria

Verify: a findings doc says which reads expose a protected page's date without a grant, whether doing so leaks anything AD-21 protects, and recommends yes or no; Phill's decision is recorded as a dated memlog line and the Deferred row is updated.

## Boundaries

- No product code change.
- Never reads a protected section's content without a grant.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D109)
- AD-17 and AD-21, and the Deferred row on protected-page dates, in the spine

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).

