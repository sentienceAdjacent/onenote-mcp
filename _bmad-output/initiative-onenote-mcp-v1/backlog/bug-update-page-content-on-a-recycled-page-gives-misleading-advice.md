---
id: 6
type: bug
title: "update_page_content on a recycled page gives misleading advice"
parent: none
covers: [R4, R6]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# update_page_content on a recycled page gives misleading advice

## Description

`update_page_content` on a page in the recycle bin (in Deleted Pages, or in a recycled section) goes to COM, and `UpdatePageContent` returns `0x8004200C` hrPageReadOnly, local and cloud (spike 3.1, rows `r5-local-update-recycled-page` and `r5-cloud-update-recycled-page`; Implication 7). The triage row maps it to `Internal`, `retryable: true`, with the `userAction` "make it editable in OneNote, then retry", which sends the user looking for a read-only setting that isn't there. `create_page` already refuses a recycle-bin parent before COM; page writes don't.

Phill's rule (2026-11-16, 3.2 plan J6; spine AD-7 `PageWrite` validation "Recycled pages", AD-9): a page write to a recycled page is refused with `ObjectNotFound` before COM, with a `userAction` to restore the page first; and the `0x8004200C` triage `userAction` names the recycle bin as one cause.

## Acceptance Criteria

Expected: `update_page_content` and `delete_page_content` on a page with `isInRecycleBin` (or in a section that is) return `ObjectNotFound` with a restore-first `userAction`, and make no COM write. `0x8004200C` from any other cause keeps `Internal` and its retry flag, with a `userAction` that mentions the recycle bin.

Verify: unit tests on the FakeGateway show both page writes on a recycled page refused with `ObjectNotFound` before the COM call; `HResultMapTests` pins the new `0x8004200C` `userAction`.

## Boundaries

- Must not: touch OneNote in tests, or change `create_page`'s existing refusal.
- Small fix in Core's page-write validation and Com's triage row; no new error code.

## References

- parent — none (standalone backlog leaf)
- Spike 3.1 findings, Implication 7 and the HRESULTs table (`0x8004200C`), in `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/`
- Story 3.2 plan, Decision J6; spine AD-7 `PageWrite` validation and AD-9 Codes

## Notes

- Filed by story 3.2 (Phill's J6, 2026-11-16).
