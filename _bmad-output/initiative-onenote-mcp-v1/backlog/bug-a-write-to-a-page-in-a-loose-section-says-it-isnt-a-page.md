---
id: 12
type: bug
title: "A write to a page in a loose section says it isn't a page"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# A write to a page in a loose section says it isn't a page

## Description

A `.one` file opened by hand in OneNote sits under `one:OpenSections`, outside any notebook (entry 15's probe P3). Entry 15's sitting S1 (2026-11-17) soft-deleted a page of such a copy and got `ObjectNotFound` "… isn't a page" instead of a refusal that says why. Nothing was written.

The cause predates entry 15. Every page write's step 1 (`WriteAccess.CheckAsync`, called from `PageWrite.AuthorizeAsync`, `src/OneNoteMcp.Core/Writes/PageWrite.cs:104`) needs the page's owning notebook for the write grant. A loose section has no notebook, so the walk finds none, and the fallback error is `NotAPage("isn't a page")`. Entry 15's protected check runs later, in step 3, so it is never reached. Entry 15 didn't change `WriteAccess`, `HierarchyWalk` or that fallback (`git diff 4be192b..647b298` touches neither).

Writes to a loose section can't be granted (a write grant is per notebook), so refusing is right. Only the message is wrong. It should say the page is in a section opened outside any notebook, which the server can't write to, and that a protected one (marked, or under an export root or the backups folder) needs a read grant to be read.

## Acceptance Criteria

Verify: a page write (`update_page_content`, `delete_page_content`, `delete_hierarchy_object`, `permanently_delete_hierarchy_object`, `set_page_level`, a move) on a page in a loose section under `one:OpenSections` is refused with a code and message that name the loose section, not "isn't a page", with nothing written; unit tests on a fixture shaped as P3 saw it.

## Boundaries

- Must not change: writes stay refused for loose sections (no write grant exists for them); entry 15's protection rule.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-protected-by-path-export-roots-and-the-backups-folder-plan.md` — entry 15, its P3 finding and sitting S1
- AD-6 (write grants per notebook), AD-7 step 1

## Notes

- Found 2026-11-17 in entry 15's sitting S1; filed as predating entry 15.
