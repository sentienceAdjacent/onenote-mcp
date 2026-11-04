---
type: epic
title: "Safe writes"
parent: initiative-onenote-mcp-v1
covers: [R4, R5, R6]
after: []
assignee: ""
risk: high
---

# Safe writes

## Description

This epic delivers every core write (R6) through the AD-7 pipeline:
- grants, with elicitation, the `grant`/`revoke` CLI and `list_grants`;
- basis checks;
- the machine-wide write lock;
- the recovery journal with `restore_from_journal`;
- permanent delete behind its second grant and a OneNote-format backup.

It ships as a release with the skill and README updated. R5 is shared: this epic delivers the journal, `restore_from_journal` and the permanent-delete backup. Epic 3 delivers `restore_from_recycle_bin`.

## Outcome

Claude changes Phill's notebooks only where he has granted access, never silently overwrites newer content, and can always recover the previous state of a page. This epic completes v1.

## Done when

1. Every R6 write succeeds on a granted notebook. On an ungranted one it returns `WriteNotGranted` and raises the grant dialog, where the client supports one.
2. A write whose basis is stale returns `StaleWrite`, including a delete on a page that isn't displayed.
3. Every `PageWrite` and `SoftDelete` leaves a journal entry. `restore_from_journal` recreates the earlier content as a new page.
4. A permanent delete needs both grants, and runs only after a OneNote-format backup succeeds. If the backup fails, the delete is refused.
5. Two server processes writing at the same time never interleave, as the integration suite shows.
6. It ships as a GitHub release through the epic-1 pipeline. The skill holds the write recipes, and the README's grant, safety-limits and journal sections match the code.

## Boundaries

Core: pipelines, access, journal, settings writes. The `grant` CLI, the write tools, the AD-18 mutex. The skill's write recipes and the README sections on grants, limits and the journal. It extends `uninstall --purge` to the journal folder.

Out of scope: move, merge, exports and recycle-bin restore (epic 3), and app-state tools (epic 4).

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R4, R5, R6
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-6, AD-7, AD-8, AD-10, AD-16, AD-17, AD-18
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, write and failure-mode rows
- product — README.md, section Safety model

## Notes

- Waits on epic 1 because it builds on the gateway, the envelope, the settings type, the XML model with `ElementHash`, the test harnesses, and the release pipeline and plugin.
- Decision (2026-11-03, Phill): permanent delete is in v1, here, with the backup.
- Unknown: `Publish` to OneNote format (`pfOneNote`/`pfOneNotePackage`) as the backup, and `DeleteHierarchy` on a whole notebook or section group. Neither was probed; this epic settles both.
- Unknown: how elicitation behaves in Claude Code (interactive only, and hooks can auto-answer it). Tested here.
- Unknown: whether `UpdatePageContent` drops an image with no local binary when its Outline is replaced (the basis for the AD-7 refusal). It's verified here with a cloud-page fixture.
