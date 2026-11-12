---
type: epic
title: "Hierarchy writes and deletes"
parent: initiative-onenote-mcp-v1
covers: [R4, R5, R6]
after: []
assignee: ""
risk: high
---

# Hierarchy writes and deletes

## Description

This epic delivers the rest of v1's core writes through the AD-7 pipeline that epic 2 builds:
- creating sections, section groups and notebooks (`OpenHierarchy` create), with the automatic grant for a new notebook;
- add, rename and reorder through `UpdateHierarchy` (not move, which is round 2);
- soft delete, and permanent delete behind its second grant and a OneNote-format backup.

It ships as a release with the skill and README updated.

The requirements are shared with other epics:
- **R4:** this epic delivers the hierarchy part of the `Create` class, and the `HierarchyWrite`, `SoftDelete` and `PermanentDelete` classes, and the permanent-delete grant. Epic 2 delivers the pipeline itself, the write lock and write grants.
- **R5:** this epic delivers the permanent-delete backup, and the `HierarchyWrite` and `SoftDelete` journal entries. Epic 2 delivers the journal and `restore_from_journal`. Epic 3 delivers `restore_from_recycle_bin`.
- **R6:** this epic delivers `OpenHierarchy` create, `UpdateHierarchy` add, rename and reorder, and `DeleteHierarchy` soft and permanent. Epic 2 delivers `CreateNewPage`, `UpdatePageContent` and `DeletePageContent`.

## Outcome

Claude can create, rename, reorder and delete notebook structure, only where Phill has granted access. A permanent delete never happens without a backup that can bring the item back. This epic completes v1.

## Done when

1. Creating a section, section group or notebook, adding, renaming and reordering through `UpdateHierarchy`, and soft-deleting all succeed on a granted notebook. On an ungranted one, each returns `WriteNotGranted` and raises the grant dialog, where the client supports one. A new notebook is granted automatically.
2. A hierarchy write or soft delete whose basis is stale returns `StaleWrite`, including a delete of a page that isn't displayed.
3. Every `HierarchyWrite` and `SoftDelete` leaves a journal entry. `restore_from_journal` brings back a soft-deleted page as a new page.
4. A permanent delete needs both grants. A section, section group or notebook is deleted only after a OneNote-format backup succeeds, and a failed backup refuses the delete.
5. It ships as a GitHub release through the epic 1 pipeline. The skill holds the recipes for these writes, and the README's sections on permanent delete and backups match the code.

## Boundaries

This epic owns:
- the `Create` (hierarchy), `HierarchyWrite` (add, rename, reorder), `SoftDelete` and `PermanentDelete` classes;
- the permanent-delete grant;
- `OpenHierarchy` create, `UpdateHierarchy` and `DeleteHierarchy` on the gateway;
- the `Publish` backup;
- the skill's recipes and the README sections for these writes.

Out of scope: move, merge, exports and `restore_from_recycle_bin` (epic 3), and app-state tools, including `OpenHierarchy` on an existing path (epic 4).

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R4, R5, R6
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-6, AD-7, AD-8, AD-9, AD-10, AD-16, AD-17, AD-18, AD-21, Deferred
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/epic-safe-writes.md, the pipeline, grants, journal and write gateway this epic extends
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, write and failure-mode rows

## Notes

- Decision (2026-11-09, Phill, epic 2 inception): split out of epic 2 so page writes ship first, and so permanent delete, the riskiest work in v1, comes after a retrospective on page writes. It builds after epic 2 and before round 2. Its id is 5, because ids are never reused; it builds third.
- Waits on epic 2 because it needs the write pipeline, the write lock, write grants and their dialog, the journal, `restore_from_journal`, and the section creation that epic 2's restore adds to the gateway.
- Decision (2026-11-09, Phill, epic 2 inception, decision 3): a write in a password-protected section is allowed with both the notebook's write grant and the section's read grant. Epic 2's entry 3 amends the spine, including how the journal holds that content. This epic's soft and permanent deletes follow the same rule.
- Decision (2026-11-09, Phill): no release ticket. Phill tags the release after this epic's refactor sweep.
- Unknown, settled by this epic's opening spike:
  - `Publish` to OneNote format as the backup route;
  - `DeleteHierarchy` on a whole notebook or section group;
  - a soft delete in a protected section, and a protected section deleted whole (does `encrypted` survive in the recycle bin?);
  - which `lastModifiedTime` `DeleteHierarchy` accepts;
  - `ModalDialogOpen` and HRESULTs per hierarchy method.

  If the backup route fails, permanent delete of sections, section groups and notebooks is blocked, and the spine is amended.
- Decision (2026-11-09, Phill, epic 2 inception): the breakdown is entries 1–8 in `tickets.toml`, all one lane after the spike. The tracer bullet is entry 3, the first hierarchy write through the pipeline. Entry 1 waits on epic 2's probes (2.2), entry 2 on epic 2's spine amendments (2.3), and entry 3 on epic 2's restore (2.11), which adds section creation to the gateway.
- Decision (2026-11-12, Phill, epic 3 inception Q9): entry 2 sets a cautious rule that refuses moves and merges out of a protected section; epic 3's spine amendments (3.2) may relax it with its probes' evidence. Epic 3 stays gated on this epic, so its spike doesn't feed entry 2.
- Decision (2026-11-12, Phill, epic 3 inception Q10): entry 5 reads back a soft-deleted page's recycle-bin ID and stores it in the journal entry, and entry 2 adds that field to AD-8 and AD-10, for epic 3's `restore_from_recycle_bin`.
