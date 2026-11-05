---
type: initiative
title: "OneNote MCP v1"
parent: none
covers: [R1, R2, R3, R4, R5, R6, R7, R8, R9]
after: []
assignee: ""
risk: high
---

# OneNote MCP v1

## Description

This initiative delivers a local MCP server that lets Claude Code, and other MCP clients, read and manage the notebooks in the user's OneNote desktop app over COM. It ships as a Claude Code plugin: a skill plus a pre-built exe. The goal is the full COM surface, delivered in rounds:
- v1 (epics 1–2) is reading, searching and safe core writes, permanent delete included.
- Round 2 adds exports, move and merge.
- Round 3 adds app-state and UI calls.

How it is built is fixed by the architecture spine (AD-1…AD-19). The README describes what users get.

## Outcome

Phill, and anyone who installs the plugin, can have Claude read, search and change OneNote notebooks safely. The signal: daily use with no lost or silently overwritten note content, because every change is granted, checked and journaled.

## Requirements

- **R1 — Read.** `GetHierarchy` at every scope, `GetHierarchyParent`, `GetPageContent` (binary only when asked), `GetBinaryPageContent`, `GetHyperlinkToObject`, `GetWebHyperlinkToObject`, `GetSpecialLocation`. Returned XML is unchanged, and degradations are reported in `meta`. Reads return the basis (`lastModifiedTime`, `pageHash`, `elementHashes`). (AD-4, AD-5, AD-17 read side)
- **R2 — Search.** `FindPages` and `FindMeta` include unindexed pages, drop recycle-bin pages, require `startNodeId`, pass `fDisplay=false`, and mark results `possiblyIncomplete`. (AD-11 search part, AD-12)
- **R3 — Robust operation.**
  - The COM binding and the vtable smoke test (AD-1).
  - The single-STA gateway: lifecycle, timeouts, hung-call recovery, reconnect, and the not-running and integrity checks (AD-2, AD-3).
  - The result envelope and error map (AD-9).
  - The settings schema, read side (AD-10).
  - Logging (AD-13).
  - `correlationId` (AD-18, its id part).
  - The test seam and the vendored XSD (AD-16).
- **R4 — Write safety.**
  - Write grants and permanent-delete grants per notebook, created by elicitation or the `grant`/`revoke` CLI, with `list_grants` (AD-6).
  - The pipeline for each operation class (AD-7).
  - Write-side basis checks (AD-17).
  - The machine-wide write lock (AD-18).
  - Settings writes under the settings mutex (AD-10).
  - The XSD hard gate, once proven (AD-16).
- **R5 — Recovery.**
  - The journal: snapshots per AD-7, retention, size cap, `restore_from_journal`.
  - The OneNote-format `Publish` backup taken before a permanent delete.
  - `restore_from_recycle_bin`. (AD-8)
- **R6 — Core writes.** `CreateNewPage`, `UpdatePageContent`, `DeletePageContent`, `OpenHierarchy` creating a section, section group or notebook at an empty path, `UpdateHierarchy` add, rename and reorder, `DeleteHierarchy` soft and permanent. (AD-7 rows `PageWrite`, `Create`, `HierarchyWrite` (not move), `SoftDelete`, `PermanentDelete`)
- **R7 — Distribution.**
  - A plugin marketplace with the `onenote` skill and the server config.
  - A self-contained release exe built by CI, plus `claude plugin validate`.
  - A README with install steps for users and agents.
  - `uninstall --purge`.
  - Plugin and exe versions released in lockstep.
  - Tool contract versioning. (AD-14, AD-15, AD-19)
- **R8 — Round 2: external and structural.** `Publish`, `OpenPackage` and `MergeFiles` into `exportRoots`, with the output path reported. Move via `UpdateHierarchy`, `MergeSections`, and `restore_from_recycle_bin`, which depends on move. (AD-7 `ExternalWrite`, `HierarchyWrite` move; AD-11 exports)
- **R9 — Round 3: app state and UI.** `OpenHierarchy` on an existing notebook, `CloseNotebook`, `SyncHierarchy`, `SetFilingLocation`, `NavigateTo`, `NavigateToUrl`, `QuickFiling`, `Windows`; events if feasible. (AD-7 `AppState`, AD-11)

## Done when

1. Following `README.md` alone, an agent installs the plugin on a Windows machine with OneNote running, and lists the user's notebooks with `get_hierarchy`.
2. A write to an ungranted notebook is refused. Once the user grants it, the write lands, and `restore_from_journal` brings back the earlier content as a new page.
3. A permanent delete with no permanent-delete grant is refused. With the grant, it runs only after a OneNote-format backup has succeeded.
4. Every OneNote Application method has a released tool, or a dated Decision recording why it isn't exposed.
5. The CI unit suite and the opt-in integration suite against real OneNote both pass on the latest release.

## Boundaries

The boundary is the capability: one repo, one owner (Phill, with agent lanes). The epics split by outcome and by round:
- **Epic 1:** usable read-only, released.
- **Epic 2:** safe writes.
- **Epic 3:** round 2.
- **Epic 4:** round 3.

Out of scope: Microsoft Graph and OneNote on the web, OneNote for Windows 10 (UWP), macOS, and non-Windows clients.

Tracer path: `get_hierarchy` through `ComGateway`, installed from a GitHub release (epic 1). Then `update_page_content` on a granted notebook, with a journal snapshot and a restore (epic 2).

Touch points:
- **OneNote desktop (`ONENOTE.EXE`):** consumed over COM. Owner: epic-read-only-release (gateway).
- **Windows Search index:** consumed indirectly by `FindPages`. Owner: epic-read-only-release.
- **Claude Code plugin system and marketplace:** configured. Owner: epic-read-only-release. Later epics update the skill.
- **GitHub Releases and Actions:** configured. Owner: epic-read-only-release.

## References

- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-1…AD-19 and Deferred
- rationale — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md
- constraint — CLAUDE.md, section OneNote COM constraints
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md
- product — README.md

## Notes

- Decision (2026-11-03, Phill): four epics, with distribution folded into epic 1 so every epic ships a release. Order: read-only release, safe writes, round 2, round 3.
- Decision (2026-11-03, Phill): permanent delete is in v1 (epic 2), behind its second grant and the OneNote-format backup.
- Decision (2026-11-03, Phill): spike 1 (done) moves under epic 1. Its files moved with it, and the references to them were updated.
- Decision (2026-11-03): no new cross-epic decision is needed. Every shared contract lives in the spine (AD-7, AD-9, AD-10, AD-14, AD-17).
- Unknown: `Publish` to OneNote format as the permanent-delete backup, and `DeleteHierarchy` on a whole notebook, were never probed. Epic 2 owns both. If the backup route fails, permanent delete is blocked and the spine is amended.
- Unknown: managed-code events (Microsoft says they aren't supported). Owned by epic 4.
