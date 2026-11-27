---
id: 24
type: spike
title: "Three unverified behaviours: a forced reconnect, dropped files and media hashes"
parent: none
covers: [R3, R4]
after: [22]
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# Three unverified behaviours: a forced reconnect, dropped files and media hashes

## Description

Three questions no sitting has answered, each needing real OneNote with Phill present:
- D82: when the server is forced to reconnect (or OneNote crashes and restarts), does activation ever start a hidden `-Embedding` OneNote? The crash detection is built, but no record shows this live check ran.
- D183: does a file dropped straight onto a page (not inside an Outline) have an `objectID`, and so an element hash?
- D184: is a page-level `one:MediaFile` unhashed because its `pathCache` is not excluded from ElementHash, as inferred?

Who waits: the spine's Deferred row for D184, AD-17's note on dropped files (D183), AD-3 on reconnects (D82, which has no Deferred row yet: the findings add one if the answer is not clean), and any follow-up story the answers call for. Spine edits merge after ticket 22. The answers go in a findings doc beside this ticket and a dated memlog line each. Phill's step: be present for the sitting (about 15 minutes), on throwaway notebooks only.

## Acceptance Criteria

Verify: a findings doc gives one observed result per question with the steps that produced it, says what each answer means for the spine's Deferred row, and names any follow-up ticket it recommends; each answer has a dated memlog line.

## Boundaries

- No product code change.
- Throwaway notebooks only; never kill or close OneNote: a crash is simulated only in a way Phill agrees to at the sitting.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D82, D183, D184)
- AD-3 (activation, reconnect) and AD-17 (element hashes) in the spine
- `_bmad-output/initiative-onenote-mcp-v1/backlog/story-hash-page-level-images-ink-and-files.md` — backlog 5, related hashing work

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).
- Open question: how to simulate a crash safely is agreed with Phill before the sitting.
