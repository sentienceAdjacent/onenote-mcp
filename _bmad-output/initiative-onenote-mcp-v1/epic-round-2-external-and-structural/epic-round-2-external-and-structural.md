---
type: epic
title: "Round 2: exports, move and merge"
parent: initiative-onenote-mcp-v1
covers: [R5, R8]
after: []
assignee: ""
risk: medium
---

# Round 2: exports, move and merge

## Description

This epic adds `ExternalWrite` and the rest of `HierarchyWrite`:
- `Publish`, `OpenPackage` and `MergeFiles`, writing only under `exportRoots` and reporting the output path;
- move through `UpdateHierarchy`;
- `MergeSections`;
- `restore_from_recycle_bin`, which depends on move.

R5 is shared: this epic delivers only `restore_from_recycle_bin`.

## Outcome

Claude can export, move and merge notebook content, and bring back deleted items, without the user leaving the conversation. Exports land only in folders the user controls, and the skill says where each file went.

## Done when

1. Exports land under `Documents\OneNote Exports` or a configured root. Any other path returns `ExportPathNotAllowed`. The result carries `meta.outputPath`, and the skill reports it.
2. A page or section moves between sections or notebooks only when every notebook involved is granted. The hierarchy snapshot is in the journal.
3. `restore_from_recycle_bin` restores a soft-deleted page or section. When the name is ambiguous, it asks the user to choose.
4. It ships as a GitHub release through the epic-1 pipeline, with the skill and README updated.

## Boundaries

The `ExternalWrite` class and the move/merge path of `HierarchyWrite`, the export-root handling in settings, these tools, and the skill and README sections that cover them. Out of scope: app-state and UI tools (epic 4).

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R5, R8
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-6, AD-7, AD-8, AD-10, AD-11, AD-17, Deferred

## Notes

- Waits on epic 2 because it uses the pipeline, grants, write lock and journal.
- Waits on epic 5 because move builds on its `UpdateHierarchy`, and `restore_from_recycle_bin` needs its soft delete.
- Unknown: move semantics through `UpdateHierarchy`, and how `MergeSections` and `MergeFiles` behave. None was probed; this epic settles them, starting with a spike if needed.
