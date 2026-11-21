---
title: 'Round-2 epic: as-built open_package destination and page-only cache drop'
type: 'chore'
ticket: '20'
created: '2026-11-21'
status: 'built'
route: 'oneshot'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The accepted epic `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural.md` still says `open_package` unpacks "into a new folder under an export root" (E3, Done when 1) and that a move "drops moved pages from the search cache" (E5). As built, `open_package` unpacks into the opened-copies folder (3.8 Q3; spine AD-7 `open_package` bullet and the `ExternalWrite` row), and only a page move drops a cache entry (`MovePageWrite.cs:148`; `MoveContainerWrite.cs` drops none; retro R1, R7).

**Approach:** Replace exactly these passages, wording below, which Phill approves. Doc-only: no code, tests or spine.

**E3, whole bullet, becomes:**
> - **E3 (R8) — `open_package`.** Unpacks a `.onepkg` with `OpenPackage` into a new folder in the opened-copies folder (`%LOCALAPPDATA%\OneNoteMcp\opened`, outside OneDrive), and reports the folder and the notebook OneNote opened from it. A named destination is a folder name, which lands there, or a full path directly inside it; anything else, an export root included, is `ExportPathNotAllowed`, and a destination in a OneDrive-synced folder is refused before anything is written (Decision, 3.8 Q3). The package may come from any path, and the opened notebook gets no automatic write grant (Decision, Q6). Every section in it counts as protected under AD-21 and needs a read grant, as does any notebook under an export root, the backup folder or the opened-copies folder (Decision, Q11; 3.8 Q3). The user closes it in OneNote until epic 4 (Decision, Q13). (AD-7 `ExternalWrite`, AD-11, AD-21)

**Done when 1 becomes:**
> 1. `publish` writes only under an export root, and `open_package` only into a new folder directly inside the opened-copies folder (`merge_files` didn't ship). Any other path returns `ExportPathNotAllowed`, and an existing file or folder `FileExists`. The result carries `meta.outputPath`, and the skill reports it.

**E5, fourth sub-bullet, becomes:**
> - it reads back and reports the moved item's ID after the move; a page move drops the page's old ID from the search cache (AD-12), and a section or section-group move drops nothing, since the cache is keyed by page ID and `lastModifiedTime` (inferred, not tested; retrospective R7);

**Notes, appended after the last Decision:**
> - Decision (Phill, 2026-11-21, Inbox, backlog 20): E3, Done when 1 and E5's cache sentence now describe what was built: `open_package`'s opened-copies destination (3.8 Q3) and the page-only cache drop. From the retrospective (`epic-round-2-external-and-structural-retrospective.md`, findings R1 and R7; action items 4 and 5). The spine already said so; no code changed.

**Also (Decision, Phill, 2026-11-21, Inbox: Open Question 1, option a):** the Description's first bullet becomes "`Publish`, writing only under `exportRoots`, and `OpenPackage`, unpacking only into the opened-copies folder, each reporting the output path (`MergeFiles` was dropped);" and E1's "Its target sits under an `exportRoots` entry (AD-10, AD-11)" becomes "A `publish` target sits under an `exportRoots` entry (AD-10, AD-11); `open_package`'s destination follows E3". The Notes Decision names these two as well.

**Never:** touch the spine, code, tests, or any other epic passage.

</frozen-after-approval>

## Implementation Notes

Oneshot: about 6 lines changed in one markdown file.

- Applied the approved wording verbatim to E3, Done when 1, E5's fourth sub-bullet, the Description's first bullet and E1's second sentence; appended the Decision after the epic's last Decision. No build or tests (doc-only).

## Verification

**Manual checks (if no CLI):**
- `git diff --stat` shows only the epic file (and this plan); `rg -n "under an export root" <epic>` no longer matches E3 or Done when 1; `rg -n "drops moved pages" <epic>` finds nothing.
