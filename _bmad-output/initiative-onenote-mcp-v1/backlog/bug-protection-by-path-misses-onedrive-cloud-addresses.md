---
id: 13
type: bug
title: "Protection by path misses OneDrive cloud addresses"
parent: none
covers: [R8]
after: []
assignee: ""
refined: true
hitl: true
risk: high
estimate: ""
---

# Protection by path misses OneDrive cloud addresses

## Description

AD-21's path rule (entry 15, on main since #89) treats a section under an export root or the journal's backups folder as password-protected. It compares local paths only: `PathUnder.Full` returns null for an `https://` path, so a URL is never under a root, and the rule fails open.

3.8's sitting S2 (2026-11-18) found the live case: after OneNote restarted, notebooks it had opened from folders under the default export root, which is OneDrive-redirected (`…\OneDrive\Documents\OneNote Exports`), came back with `https://d.docs.live.net/<cid>/Documents/OneNote Exports/…` paths (new notebook IDs, the cid's hex case varying between elements, trailing slashes on notebooks and section groups). `get_hierarchy` at pages scope then listed every page of every section with no grant and no warning. The same holds for any `.one` export or notebook under a OneDrive-redirected export root that OneNote reports by URL. Every place AD-21's rule applies is affected: reads, gated writes, journal snapshots, `publish`, the grant listing.

Phill (2026-11-18): a hotfix off main, merged before 3.8.

## Acceptance Criteria

Verify: the two skipped tests in `OneDriveUrlProtectionTests` (moved from 3.8's branch) run and pass (a copy notebook reported by URL withholds every section; a section reported by URL is `ReadNotGranted` at pages scope), the guard test (a URL elsewhere in the same OneDrive stays unprotected) still passes, and the rule's chosen form (Phill's pick) covers personal OneDrive and, as far as it can be checked, business OneDrive; a sitting reopens such a URL notebook and shows its pages withheld.

## Boundaries

- Must not change: the local-path rule (entry 15), `ExportTarget` (exports and `open_package` write local paths only), grant keys.
- Fails closed: a URL that can't be evaluated never counts as unprotected where its path would put it under a root.
- No real cid or account name in any committed file.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-protected-by-path-export-roots-and-the-backups-folder-plan.md` — entry 15
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-open-package-plan.md` — 3.8, sitting S2
- AD-21 "What's protected" in the architecture spine

## Notes

- Filed 2026-11-18 from 3.8's sitting S2; Phill: hotfix first (Q2).
