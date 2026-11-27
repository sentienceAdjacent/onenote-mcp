---
id: 30
type: spike
title: "One shared server for all sessions"
parent: none
covers: [R3, R7]
after: []
assignee: ""
refined: false
hitl: true
risk: high
estimate: ""
---

# One shared server for all sessions

## Description

Every Claude session starts its own `OneNoteMcp.exe` over stdio, so several sessions mean several COM clients, each with its own write lock. The idea: one shared server (for example on localhost HTTP) that every session connects to. The question: is it worth it, and if so what changes: transport, how clients authenticate on localhost, one write lock and journal across sessions, grant dialogs shown to the right session, and plugin install.

Who waits: Phill's decision on whether to plan it as a future epic. The answer goes in a findings doc beside this ticket (a short design comparison, no code). Phill's step: read the findings and decide.

## Acceptance Criteria

Verify: a findings doc compares per-session stdio with one shared server on safety (write lock, grants, dialogs), security (localhost access), install and effort, with a recommendation; Phill's decision is recorded as a dated memlog line.

## Boundaries

- No code, no transport prototype in the product.
- A research spike: it runs no OneNote probe unless Phill asks for one.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D63)
- AD-1 (process model), AD-3, AD-6 and AD-18 (write lock) in the spine

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).
- Larger than a backlog story if adopted: a yes would become its own epic or initiative.
