---
id: 3
type: bug
title: "OneNote shows 'cleaning up' after a restart while a server is connected"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# OneNote shows 'cleaning up' after a restart while a server is connected

## Description

At the 5.7 sitting (2026-11-15), Phill closed OneNote and started it again; OneNote showed "We're sorry. OneNote is cleaning up from the last time it was open. Please wait." and stayed there until he ended ONENOTE.EXE in Task Manager. Suspected cause: a OneNoteMcp server (the onenote plugin in any open Claude session) still holds its COM reference, which `ComGateway` releases only after `timeouts.idleReleaseSeconds` (300 s by default), and that reference keeps ONENOTE.EXE running after its window closes.

## Acceptance Criteria

Given OneNote running and one OneNoteMcp server that made a call less than 5 minutes ago, when the user closes OneNote's window, then ONENOTE.EXE exits (or the server lets it go) and the next start shows no "cleaning up" dialog. Verify first at a sitting: close OneNote with no server connected, then with a connected idle server, and record whether ONENOTE.EXE stays alive and the dialog appears in each case.

## Boundaries

- Must not: kill or close OneNote from the server (CLAUDE.md), or start it.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — "5.7 sitting findings (2026-11-15)"
- `src/OneNoteMcp.Com/ComGateway.cs` (idle release), `src/OneNoteMcp.Com/StaThread.cs` (`FinalReleaseComObject`), `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs` (default 300 s)

## Notes

- Decision (Phill, 2026-11-15): file it and test at the next sitting.
- Unknown: whether OneNote's own background work (sync, indexing) causes the same dialog with no server connected; check OneMore's "COM Layer" notes on releasing the Application object before designing a fix.
