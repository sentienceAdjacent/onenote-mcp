---
type: epic
title: "Round 2: exports, move and merge"
parent: initiative-onenote-mcp-v1
covers: [R5, R8]
after: [epic-safe-writes, epic-hierarchy-writes-and-deletes]
assignee: ""
risk: high
---

# Round 2: exports, move and merge

## Description

This epic adds the `ExternalWrite` class and the rest of `HierarchyWrite`, through the AD-7 pipeline that epics 2 and 5 build:
- `Publish`, `OpenPackage` and `MergeFiles`, writing only under `exportRoots` and reporting the output path;
- moving a page, section or section group through `UpdateHierarchy`;
- `MergeSections`;
- `restore_from_recycle_bin`, which builds on move.

It opens with a probe spike for the COM behaviours no earlier spike settled, then amends the spine, and ships as a release with the skill and README updated.

R5 is shared: this epic delivers only `restore_from_recycle_bin`. Epic 2 delivers the journal and `restore_from_journal`, and epic 5 the permanent-delete backup.

## Outcome

Claude can export, move and merge notebook content, and bring back deleted items, without the user leaving the conversation. Exports land only in folders the user controls, the skill says where each file went, and no export, move or merge puts a password-protected section's content where the model couldn't read it before.

## Requirements

- **E1 (R8) — Export roots.** Every `ExternalWrite` target sits under an `exportRoots` entry (AD-10, AD-11). The default root is `Documents\OneNote Exports`, created on first use. A target is made canonical first (`GetFullPath`, junctions resolved) and then checked: outside every root returns `ExportPathNotAllowed`, and an existing file returns `FileExists`, both before COM. Every export result carries `meta.outputPath`, and the skill tells the user where the file went. (AD-7 `ExternalWrite`, AD-11)
- **E2 (R8) — `publish`.** Exports a page, section or notebook with `Publish` in each format the spike proves for that scope. A scope or format that can't work is refused before COM, with the reason. A notebook exports only as a package (`.onepkg`, spike 5.1 constraint 18), and a section group can't be exported by `Publish` at all (5.1 constraint 6). (AD-7 `ExternalWrite`, AD-11)
- **E3 (R8) — `open_package`.** Unpacks a `.onepkg` with `OpenPackage` into a new folder under an export root, and reports the folder and the notebook OneNote opened from it. (AD-7 `ExternalWrite`)
- **E4 (R8) — `merge_files`.** `MergeFiles` into a target under an export root, or a dated Decision recording why it isn't exposed. (AD-7 `ExternalWrite`)
- **E5 (R8) — Move.** Moves a page, section or section group to another section, section group or notebook through `UpdateHierarchy`, as an explicit move. It needs the write grant of every notebook it touches (AD-6), checks the basis (AD-17), takes a hierarchy snapshot of the affected nodes (AD-7, AD-8), and reports the moved item's ID after the move. No other hierarchy write moves anything (5.1 constraint 4). (AD-7 `HierarchyWrite` move)
- **E6 (R8) — `merge_sections`.** Merges one section into another with `MergeSections`, under the same grants, basis check and snapshot as a move, and reports what became of the source section. (AD-7 `HierarchyWrite`)
- **E7 (R5, R8) — `restore_from_recycle_bin`.** Brings a soft-deleted page or section back out of the recycle bin. When the name is ambiguous, the user chooses, because soft-deleted pages get new IDs (AD-8). A soft-deleted section group can't come back as a unit: its sections land in the recycle bin one by one (5.1 constraint 8). (AD-7 `HierarchyWrite`, AD-8)
- **E8 (R8) — Protected sections.** No export, move, merge or restore puts a password-protected section's content in a file or section that AD-21 wouldn't let the model read. Moves and merges follow the rule epic 5's spine amendments (5.2) set for the Deferred row on moving or merging content out of a protected section; exports follow the rule this epic's spine amendment (entry 2) sets. (AD-6, AD-21)
- **E9 (R8) — Coverage.** `Publish`, `OpenPackage`, `MergeFiles`, `MergeSections` and the `UpdateHierarchy` move each have a released tool, or a dated Decision recording why not (the initiative's Done when 4).

## Done when

1. `publish`, and `open_package` and `merge_files` where they ship, write only under an export root. Any other path returns `ExportPathNotAllowed`, and an existing file `FileExists`. The result carries `meta.outputPath`, and the skill reports it.
2. A page or section moves between sections or notebooks, and `merge_sections` merges a section, only when every notebook involved is granted. The hierarchy snapshot is in the journal, and the result gives the moved item's current ID.
3. `restore_from_recycle_bin` restores a soft-deleted page or section. When the name is ambiguous, it asks the user to choose.
4. An export, move or merge of content in a password-protected section without the grant E8's rule asks for is refused, and the integration suite shows it.
5. Every round-2 COM method has a released tool or a dated Decision, and the release ships through the epic 1 pipeline with the skill and README updated.

## Boundaries

This epic owns:
- the `ExternalWrite` class and its pipeline, and the export-root handling in settings;
- the move, merge and recycle-bin restore paths of `HierarchyWrite`;
- `Publish` formats beyond the backup's, `OpenPackage`, `MergeFiles` and `MergeSections` on the port and both gateways;
- these tools, and the skill and README sections that cover them.

Out of scope: app-state and UI tools (epic 4), including closing a notebook that `open_package` opened (`CloseNotebook`). Epic 5 owns `UpdateHierarchy` add, rename and reorder, soft and permanent delete, and the `Publish` backup.

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R5, R8; Done when 4
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-3, AD-5, AD-6, AD-7, AD-8, AD-9, AD-10, AD-11, AD-12, AD-14, AD-17, AD-18, AD-21, Deferred
- rationale — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md, constraints 4, 6, 7, 8, 11, 15, 17, 18 and "Open before final, and epic impact"
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, rows 19–24 and the short answer's point 6
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/epic-hierarchy-writes-and-deletes.md, the `HierarchyWrite` pipeline, `UpdateHierarchy`, soft delete and the `Publish` backup this epic extends
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/epic-safe-writes.md, the pipeline, grants, write lock and journal
- prior art — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md, and CLAUDE.md, section "Prior art: OneMore"
- deferred work — _bmad-output/initiative-onenote-mcp-v1/deferred-work.md, the create-page race on a section moved between notebooks (story 2.4 section)

## Notes

- Waits on epic 2 because it uses the pipeline, grants, write lock and journal.
- Waits on epic 5 because move builds on its `UpdateHierarchy`, `restore_from_recycle_bin` needs its soft delete, and `publish` builds on its `Publish` backup on the gateway.
- Decision (2026-11-12, Phill, relayed by the team lead): plan rounds 2 and 3 now, and build them only after epics 2 and 5 land. This file's `after` gates every entry on both epics being done.
- Answered before this epic (so the spike doesn't repeat them):
  - `Publish` of a page to PDF, `.docx` and MHTML works, in 95 ms, 2.9 s and 20 ms; over an existing file it returns `0x8004201a` (spike 1, rows 23 and 24).
  - `Publish` in OneNote format works for a page and a section in about 0.1 s. A section group fails in every form tried. A notebook publishes only as `.onepkg`, and the file may land after the call returns. A missing target folder raises a modal and the call blocks. A locked protected section returns `0x80042014` (5.1 constraints 6, 16, 18).
  - Assumption (unconfirmed in the findings doc): `Publish` of an unlocked protected section to `.one` writes its content in the clear. 5.1 constraint 17 proves it only for a backup that OneNote has opened, and lists the deciding probe as pending; the team lead reports that probe ran locally on 2026-11-12 and showed plaintext, but the result isn't recorded yet.
  - `OpenPackage` unpacks a notebook package in about 0.4–0.6 s and opens it as an ordinary notebook named `<package>-unpacked`, with no `encrypted` marker; a pages-scope read straight after listed no pages (5.1 constraints 7, 17, 18).
  - `UpdateHierarchy` moves a section listed under another section group of the same notebook, with no error; the section keeps its ID and its own date, and the move synced on OneDrive (5.1 constraint 4).
  - Where soft-deleted items go: a page to Deleted Pages under a new ID, a section to `OneNote_RecycleBin` with its ID, a section group dissolves; a protected section keeps `encrypted` in the recycle bin and is locked straight after the delete (spike 1 rows 19 and 22; 5.1 constraints 8 and 16).
  - Protecting a section gives it and its pages new IDs (5.1 constraint 15), so nothing in this epic may rely on a remembered ID across that.
- From OneMore and Microsoft's docs (checked 2026-11-12; rows 17–20 of `../epic-read-only-release/research-onemore-lessons.md`):
  - Microsoft documents no scope per `Publish` format, nothing on `OpenPackage`, and no way to restore from the recycle bin. OneMore has no restore feature either.
  - `MergeFiles` is a three-way merge of versions of one `.one` file (same OneNote ID), "intended for mobile scenarios", failing `0x8004201F` hrMergeFailed. OneMore never calls it.
  - OneMore moves pages, within a notebook and across notebooks, by removing the `Page` from the source section's hierarchy XML, adding it to the target's, and calling `UpdateHierarchy` on each. Only the moved page's ID changes. Its comments also say that leaving a sibling out of an `UpdateHierarchy` call deletes it, which 5.1 didn't see for sections (constraint 4); entry 1 checks it for pages before entry 4 relies on either.
  - OneMore imports a `.one` file by opening it with `OpenHierarchy` and merging it into a section with `MergeSections`, because an opened section's pages aren't loaded (`areAllPagesAvailable=false`). That may be why 5.1 saw no pages in an opened backup (constraint 7); entry 1 tries it.
- Unknown, settled by entry 1 (the spike): `Publish` in the other formats and scopes; `OpenPackage` into an existing or non-empty folder, its focus, and protected sections inside a package; what `MergeFiles` does at all; `MergeSections` (does the source survive, page IDs, across notebooks, protected sections); moving a page, moving across notebooks, moving a section group, IDs and dates after a move; restoring from the recycle bin through `UpdateHierarchy`, and where `0x8004202E` comes from; the modal-dialog result per method. Entries 3 to 9 wait on its answers through entry 2.
- Source conflict: AD-7 `ExternalWrite` row — "Grant: none" vs AD-21: an export to PDF, Word or MHTML is plaintext by nature (spike 1 row 23), likely a `.one` export too (5.1 constraint 17), and the model can read an export file with its own file tools. So an export with no grant would read a protected section without AD-21's read grant. Entry 2 settles it (open question Q1).
- Open questions for Phill, each settled in entry 2 unless it says otherwise:
  - Q1, protected exports: (a) exporting content in a protected section needs that section's read grant, and the result warns that the file isn't encrypted; (b) refuse every export that holds protected content; (c) the notebook's write grant is enough. A notebook package needs the read grant of every protected section in it under (a). Recommended: (a).
  - Q2, the move tool: (a) a separate tool that takes the item, its new parent and a position and builds the `UpdateHierarchy` XML in Core; (b) `update_hierarchy` accepts a move only with an explicit move argument. Recommended: (a), because a cross-notebook move through hand-written hierarchy XML is easy to get wrong and hard to check. Under (a), AD-14 needs an exception: the tool wraps `UpdateHierarchy`, whose snake_case name epic 5's `update_hierarchy` already has.
  - Q3, choosing among recycled items with the same name: (a) the tool takes the recycled item's ID, and a lookup by name that matches several returns the candidates (ID, deletion time, where each came from) for the model to ask the user; (b) the server raises an elicitation dialog listing the candidates, falling back to (a) without elicitation. Recommended: (a); AD-8's "asks the user" is reworded to match.
  - Q4, where a restored item goes: (a) back under its original parent, found from its soft-delete journal entry (`parentId`, else `parentNamePath`), else refused with a `userAction` asking for a target; (b) always where the caller says; (c) the original parent when found, else a `Recovered` section. Recommended: (a), with an optional target that overrides it.
  - Q5, `merge_files`: (a) ship it only if the spike shows a result the user would want, else a dated Decision not to expose it; (b) ship it as a thin tool whatever the spike shows. Recommended: (a).
  - Q6, `open_package`: may the package come from any path (a), or only from under an export root (b)? And does the opened notebook get an automatic write grant: no (c), or yes, as a created notebook does (d)? Recommended: (a) and (c).
  - Q7, section groups: `Publish` can't export one. (a) `publish` refuses a section group; (b) `publish` exports each section in it into a new folder (a promoted task, AD-5). Recommended: (a), with (b) as a skill recipe.
  - Q8, managing export roots: (a) the user edits `exportRoots` in `settings.json` by hand, as the README says; (b) a new `OneNoteMcp export-root` CLI. Recommended: (a).
  - Q9, when the spike runs: (a) keep the whole-epic gate, so the spike runs after epic 5 lands; (b) let entry 1 run as a probe-only sitting once 5.1 is final, before 5.2, so 5.2's rule for moves and merges out of protected sections rests on move and merge evidence. Choosing (b) means replacing this file's gate on epic 5 with per-entry prerequisites (entry 2 after 5.8, say) and adding 3.1 to 5.2's `after`; with the gate in place that would be a cycle. Under (a), 5.2 sets that rule with no move evidence, and entry 2 may revise it. Recommended: (b). This is for Phill, not entry 2.
  - Q10, matching a recycled page to its journal entry: a soft-deleted page gets a new ID (spike 1 row 19), and nothing in AD-8 or 5.5 records it, so restoring it to its original parent (Q4 (a)) can only match by name and time. (a) 5.5, still unbuilt, reads back the page's recycle-bin ID after the delete and stores it in the journal entry; (b) entry 5 matches by name and deletion time and falls back to Q3's choice. Recommended: (a). This is for Phill and epic 5.
- Assumption (unconfirmed): no release ticket. Phill tags the release after the refactor sweep, as in epics 2 and 5.
- Assumption (unconfirmed): no opening refactor; epic 5's refactor sweep (5.8) closes just before this epic starts.
- Assumption (unconfirmed): moves of section groups, and moves on OneDrive notebooks, follow whatever 5.2 decides for a section-group rename on OneDrive, which didn't sync (5.1 constraint 5).
- Assumption (unconfirmed): exports default to `Documents\OneNote Exports`, which on many machines is synced to OneDrive. The README says so; the epic doesn't change the default.
- Assumption (unconfirmed): restoring a dissolved section group as a unit is out of scope; its sections come back one by one.
- Decision proposed, not yet approved (autonomous inception, 2026-11-12): the breakdown is entries 1–11 in `tickets.toml`, in one lane, because every story after the spine amendment touches the tool registration and contract snapshot, the error list or the pipeline runner. The tracer bullet is entry 3, `publish` of a page or section through the new `ExternalWrite` pipeline. Entry 4, the move, follows it as the slice least known. Entry 1 waits on 5.1, entry 2 on 5.2, entry 3 on 5.6 (`Publish` on the gateway), entry 4 on 5.4 (`UpdateHierarchy`), and entry 5 on 5.5 (soft delete).
