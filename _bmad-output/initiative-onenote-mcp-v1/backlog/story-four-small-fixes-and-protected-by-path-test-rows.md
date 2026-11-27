---
id: 23
type: story
title: "Four small fixes and the missing protected-by-path test rows"
parent: none
covers: [R3, R4]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# Four small fixes and the missing protected-by-path test rows

## Description

Five low-severity items the triage found still open:
- D182: writes that check the section before the write grant (set_page_level, moves, create_page under a parent page, permanent delete) refuse a page in a protected or subpaged loose section (one opened outside any notebook) with ReadNotGranted or the subpage error, because their section check runs before the write-grant check; a plain loose section already gets backlog 12's loose-section NotSupported refusal. Protected and subpaged loose sections should get that same refusal.
- D186: the opened-copies sweep deletes a copy's folder in one call with no retry; if Windows briefly refuses, it can leave an empty folder without its marker, which later sweeps then ignore.
- D188: the filtered list's own stored-result event (id, size, evictions) is dropped instead of logged, recorded as Deferred in AD-20.
- D189: `meta.withheld` stays in the result header, so thousands of withheld sections can push the header past `results.inlineMaxChars`; today this is only logged (Deferred in AD-20).
- D180: protected-by-path rows (export roots and the backups folder) have no use-case tests for update_page_content, create_page with a parent page, set_page_level, restore_from_journal, or a loose section read through get_hierarchy and get_page_content.

## Acceptance Criteria

Verify: the unit suite passes with new tests that fail on main for D182 (each listed write on a protected or subpaged loose section gives the loose-section refusal), D186 (a refused delete is retried and no marker-less empty folder is left), D188 (the event is logged) and D189 (a header with many withheld sections stays within the inline cap), plus the D180 rows; the contract snapshot is unchanged unless D189's fix changes the result shape, in which case the spine and contract change together.

## Boundaries

- Must not change: which writes are refused, only the code and message for D182.
- D189 keeps every withheld id reachable (large results must work).

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work-triage-2026-11-27.md` — the triage that filed this (items D182, D186, D188, D189, D180)
- `_bmad-output/initiative-onenote-mcp-v1/backlog/bug-a-write-to-a-page-in-a-loose-section-says-it-isnt-a-page.md` — backlog 12, the refusal D182 extends
- AD-20 (large results; its two Deferred notes this story closes) and AD-21 in the spine

## Notes

- Filed 2026-11-27 from the triage of `deferred-work.md`; Phill chose it as a ticket (Inbox, 2026-11-27).

