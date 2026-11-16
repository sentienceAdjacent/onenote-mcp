---
id: 4
type: bug
title: "IntegrityMismatch reported right after OneNote crashes"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# IntegrityMismatch reported right after OneNote crashes

## Description

Right after ONENOTE.EXE died (a crash during spike 3.1 sitting A, 2026-11-16), the server's next call returned `IntegrityMismatch` with the message "server=unreadable onenote=[]" instead of `OneNoteNotRunning`. This looks like a race: the integrity check reads no OneNote process and treats that as a mismatch, rather than the not-running check winning.

## Acceptance Criteria

Expected: when no ONENOTE.EXE process exists (an empty list), or the integrity read fails because the process is gone, the server reports `OneNoteNotRunning`.

Verify: a unit test with a fake process probe that returns no processes and an unreadable server integrity gives `OneNoteNotRunning`, not `IntegrityMismatch`.

## Boundaries

- Must not: start, kill or close OneNote (CLAUDE.md). Small fix; don't change the real mismatch case (OneNote running at a different integrity level).

## References

- parent — none (standalone backlog leaf)
- Spike 3.1 sitting A findings (2026-11-16), in `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/`
- AD-3 and AD-9 in the architecture spine (`OneNoteNotRunning`, `IntegrityMismatch`)

## Notes

- Filed only, not fixed (Phill, 2026-11-16).
