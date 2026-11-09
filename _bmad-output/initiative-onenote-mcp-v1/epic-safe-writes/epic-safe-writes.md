---
type: epic
title: "Safe page writes"
parent: initiative-onenote-mcp-v1
covers: [R4, R5, R6]
after: []
assignee: ""
risk: high
---

# Safe page writes

## Description

This epic builds the AD-7 write pipeline and delivers v1's page writes through it:
- write grants, with the elicitation dialog, the `grant` CLI, `grant --revoke` and `list_grants`;
- the machine-wide write lock;
- basis checks;
- the recovery journal, with `restore_from_journal`;
- `CreateNewPage`, `UpdatePageContent` and `DeletePageContent`.

It ships as a release with the skill and README updated.

The requirements are shared with other epics:
- **R4:** this epic delivers the pipeline, the write lock, write grants, the `PageWrite` class, and the page part of the `Create` class. Epic 5 delivers the hierarchy classes and the permanent-delete grant.
- **R5:** this epic delivers the journal and `restore_from_journal`. Epic 5 delivers the permanent-delete backup and the `HierarchyWrite` and `SoftDelete` entries, and epic 3 delivers `restore_from_recycle_bin`.
- **R6:** this epic delivers the page writes. Epic 5 delivers `OpenHierarchy` create, `UpdateHierarchy` and `DeleteHierarchy`.

## Outcome

Claude changes pages in Phill's notebooks only where he has granted access, never silently overwrites newer content, and can always recover the previous state of a page it changed.

## Done when

1. `create_page`, `update_page_content` and `delete_page_content` succeed on a granted notebook. On an ungranted one, each returns `WriteNotGranted` and raises the grant dialog, where the client supports one.
2. A page write whose basis is stale returns `StaleWrite`, including on a page that isn't displayed.
3. Every `PageWrite` leaves a journal entry. `restore_from_journal` recreates the earlier content as a new page.
4. Two server processes writing at the same time never interleave, as the integration suite shows.
5. It ships as a GitHub release through the epic 1 pipeline. The skill holds the page-write recipes, and the README's sections on grants, safety limits and the journal match the code.

## Boundaries

This epic owns:
- **in Core:** the pipeline, write access, the journal and settings writes;
- the `grant` CLI's write grants;
- the page-write tools and `restore_from_journal`;
- the AD-18 mutex;
- the skill's page-write recipes, and the README sections on grants, limits and the journal.

Out of scope: hierarchy creation, rename, reorder and every delete of a page, section, section group or notebook through `DeleteHierarchy` (epic 5); move, merge, exports and recycle-bin restore (epic 3); and app-state tools (epic 4).

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R4, R5, R6
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-3, AD-6, AD-7, AD-8, AD-9, AD-10, AD-16, AD-17, AD-18, AD-21
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/epic-hierarchy-writes-and-deletes.md, which extends this epic's pipeline
- deferred work — _bmad-output/initiative-onenote-mcp-v1/deferred-work.md, every entry naming epic 2
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, write and failure-mode rows
- product — README.md, section Safety model

## Notes

- Waits on epic 1 because it builds on the gateway, the envelope, the settings type, the XML model with `ElementHash`, the test harnesses, and the release pipeline and plugin.
- Decision (2026-11-03, Phill): permanent delete is in v1, here, with the backup. Superseded 2026-11-09: it moves to epic 5, still in v1.
- Moved to epic 5 (2026-11-09): `Publish` to OneNote format (`pfOneNote`/`pfOneNotePackage`) as the backup, and `DeleteHierarchy` on a whole notebook or section group.
- Unknown: how elicitation behaves in Claude Code (interactive only, and hooks can auto-answer it). Tested here.
- Unknown (OneMore lessons, `../epic-read-only-release/research-onemore-lessons.md` rows 1, 2, 4, 5, 6, 12, 13): settle these at inception.
  - Updates merge by objectID and never remove omitted content, so a replace needs explicit `DeletePageContent`.
  - Some edits are silently ignored, so AD-7 needs a read-back check after each write.
  - Notebook close/reopen regenerates all IDs. Settled in the spine on 2026-11-03: grants key on `NotebookKey` (AD-6), and restore uses `notebookPath`/`parentNamePath` (AD-8). The open part is verifying that `path` is stable (Deferred).
  - Merged cells are flattened through the 2013 schema. Spike: does a 2013 round-trip unmerge cells, and does `UpdatePageContent` accept `xs2026`? Decide AD-4's schema before write stories.
  - Side effects on untouched content (child `lastModifiedTime`, dropped local styles, equation corruption across a live selection).
  - HRESULTs to add to AD-9, `0x80042022` content loss first.
- Unknown: whether `UpdatePageContent` drops an image with no local binary when its Outline is replaced (the basis for the AD-7 refusal). It's verified here with a cloud-page fixture.
- Decision (2026-11-09, Phill, retro C2; AD-17 amended): a notebook's, section group's or section's basis is the `lastModifiedTime` attribute on its element in the hierarchy XML. Hierarchy reads add no `meta.basis`; the caller passes `basis: {lastModifiedTime}`. See `../change-epic-1-retro-wording/` and the spine memlog.
- Decision (2026-11-09, Phill, inception decision 2): the tracer bullet is entry 4, `create_page` through the write pipeline with a grant made through the CLI. The initiative's tracer path (`update_page_content` with a journal snapshot and a restore) is reached at entries 9–11.
- Decision (2026-11-09, Phill, inception decision 3): a write in a password-protected section is allowed with both the notebook's write grant and the section's read grant. Entry 3 amends AD-6, AD-7, AD-8, AD-9, AD-10, AD-17 and AD-21 to match, and settles the order of the dialogs, whether a `once` grant covers a write, whether `create_page` needs the read grant, and restore into a locked or missing section.
- Decision (2026-11-09, Phill, inception decision 4): the write probes also run on a second throwaway notebook on OneDrive, "MCP Test Cloud". Phill creates it as entry 2's first step, and the integration guard is widened to allow it.
- Decision (2026-11-09, Phill, inception decision 5): no release ticket. Phill tags the release after the refactor sweep, as with v0.8.0-rc.1 and rc.2.
- Decision (2026-11-09, Phill, inception decision 3b): the journal encrypts an entry from a password-protected section with Windows DPAPI for the current user, and keeps it 30 days like other entries. Restore puts it back only into the same protected section, never into `Recovered`. Entry 3 writes this into AD-8, AD-10 and AD-21; entries 8 and 11 build it.
- Decision (2026-11-09, Phill, inception): the breakdown is entries 1–13 in `tickets.toml`. Entries 1 and 2 open together (code and probes), and 3 runs beside 4. After 5, entries 6 and 7 run side by side. Everything else is one lane, because each write story touches the tool runner, the error list, the gateway or the pipeline runner. Entries 1 and 2 wait on 1.14, epic 1's closing sweep. The draft, the independent check and the Fable findings are in `review/`.
