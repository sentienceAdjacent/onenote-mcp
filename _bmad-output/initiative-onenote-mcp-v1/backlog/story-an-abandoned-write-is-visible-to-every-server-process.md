---
id: 31
type: story
title: "An abandoned write is visible to every server process"
parent: none
covers: [R3, R4]
after: []
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# An abandoned write is visible to every server process

## Description

Today only the process that started a write knows it was abandoned (timed out, still running in OneNote), so another session's server can take the write lock and write while that call may still be changing OneNote. This opens two ways: after the first process's pipeline deadline runs out (120 s, or 600 s for the classes that publish), and at once when the first process ends mid-write, because an abandoned mutex counts as acquired (AD-18). This story records an outstanding abandoned write where every server process's `ComBusy` check after taking the lock can see it, so a second process answers `ComBusy` too. The record outlives the process that wrote it, and its only escape is an age limit, so a call that never returns can't block writes forever. A sitting with Phill is needed to see it work across two real sessions.

## Acceptance Criteria

Verify: unit tests show that, while another process's abandoned write is recorded and younger than the age limit, a non-read pipeline in a second process returns `ComBusy` after taking the lock, both after the first process's deadline and after the first process has ended; that once the age limit passes, writes go through; and that a process's own abandoned-write handling is unchanged. At a sitting, Phill sees a second session refused with `ComBusy` while the first session's write is abandoned, and the spine (AD-3, AD-18) records the rule and the age limit.

## Boundaries

- Never auto-retries a write (AD-7, AD-17), and never kills or closes OneNote.
- The age limit is the record's only escape: it is not cleared when its process exits, since a process ending is one of the two ways in.
- Reads stay lock-free and are not refused by the record (AD-18).

## References

- parent: none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/backlog/spike-one-shared-server-for-all-sessions-findings.md`: Safety gap 1 and its narrow fix (spike 30, final)
- AD-3 (abandoned threads) and AD-18 (write lock) in the spine

## Notes

- Filed 2026-11-27 from spike 30; Phill chose option A (keep one stdio server per session) and this follow-up (Inbox, 2026-11-27).
- Open question: the age limit's value, and whether it is a setting (AD-10). It must keep AD-18's property that a call which never returns can't block writes forever.
- Open question: how the sitting makes a write hang long enough to be abandoned without disturbing OneNote; settle it with Phill before the sitting.
- Unknown: whether OneNote finishes an out-of-process call whose caller has exited (spike 30, Not checked); the sitting can observe it.
