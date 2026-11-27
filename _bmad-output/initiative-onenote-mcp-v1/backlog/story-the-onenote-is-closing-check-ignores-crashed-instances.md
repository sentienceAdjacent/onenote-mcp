---
id: 29
type: story
title: "The "OneNote is closing" check ignores crashed instances"
parent: none
covers: [R3]
after: [22]
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# The "OneNote is closing" check ignores crashed instances

## Description

Before connecting, the server checks for a OneNote window across every ONENOTE.EXE process in the session, not filtered for Windows Error Reporting. A second windowed OneNote, or a crashed one's window, lets a call connect while an older instance is still closing. It fails open by decision, and which process COM binds to was never measured. This story measures that with Phill, then, only if the measurement shows it is safe, filters crashed (WER) instances out of the check, and records the rule in the spine (AD-3).

## Acceptance Criteria

Verify: with Phill present, the sitting records which process COM binds when a crashed instance's window is present; if the filter is added, unit tests cover a crashed instance's window being ignored and a live one still counting, and the spine records the rule; if not, the spine records why.

## Boundaries

- Never kills or closes OneNote; a crash is only observed or simulated as Phill agrees.
- Keeps failing open: a doubtful case never blocks the user.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D178)
- AD-3 (activation) in the spine
- `_bmad-output/initiative-onenote-mcp-v1/backlog/bug-onenote-shows-cleaning-up-after-a-restart-while-a-server-is-connected.md` — the related restart bug

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).
- Pairs well with spike 24's reconnect probe (D82) in one sitting.
