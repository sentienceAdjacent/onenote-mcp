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

Claude can export, move and merge notebook content, and bring back deleted items, without the user leaving the conversation. Exports land only in folders the user controls, the skill says where each file went, and no export, move, merge or restore puts a password-protected section's content where the model couldn't read it before.

## Requirements

- **E1 (R8) — Export roots.** Every `ExternalWrite` call runs the AD-7 pipeline: the write lock and the 600 s deadline for classes that publish. Its target sits under an `exportRoots` entry (AD-10, AD-11); the default root is `Documents\OneNote Exports`, created on first use. Before COM, the server:
  - makes the target canonical (`GetFullPath`, junctions resolved), then checks it: outside every root returns `ExportPathNotAllowed`, and an existing file returns `FileExists`;
  - creates the target's folder, because `Publish` into a missing folder raises a modal and blocks (5.1 constraint 6).
  
  The call counts as done only once the file exists, because a package can land after `Publish` returns (5.1 constraint 18). Every export result carries `meta.outputPath`, and the skill tells the user where the file went. The user manages `exportRoots` by editing `settings.json` (Decision, Q8). (AD-7 `ExternalWrite`, AD-11)
- **E2 (R8) — `publish`.** Exports a page, section or notebook with `Publish`, in each format the spike proves for that scope. A scope or format that can't work is refused before COM, with the reason. A notebook can't publish as `.one`; of the OneNote formats only `.onepkg` works (5.1 constraint 18), and entry 1 probes the rest. `Publish` can't export a section group at all (5.1 constraint 6), so `publish` refuses one before COM; exporting a group section by section is a skill recipe (Decision, Q7). A locked section is refused as `SectionLocked` before COM, because `Publish` would return a misleading `0x80042014` (5.1 constraint 16). (AD-7 `ExternalWrite`, AD-11)
- **E3 (R8) — `open_package`.** Unpacks a `.onepkg` with `OpenPackage` into a new folder under an export root, and reports the folder and the notebook OneNote opened from it. The package may come from any path, and the opened notebook gets no automatic write grant (Decision, Q6). Every section in it counts as protected under AD-21 and needs a read grant, as does any notebook under an export root or the backup folder (Decision, Q11). The user closes it in OneNote until epic 4 (Decision, Q13). (AD-7 `ExternalWrite`, AD-21)
- **E4 (R8) — `merge_files`.** `MergeFiles` into a target under an export root, shipped only if entry 1 shows a result the user would want; otherwise a dated Decision records why it isn't exposed (Decision, Q5). (AD-7 `ExternalWrite`)
- **E5 (R8) — Move.** Moves a page, section or section group to another section, section group or notebook through `UpdateHierarchy`, as an explicit move by a separate move tool that takes the item, its new parent and a position and builds the XML in Core (Decision, Q2):
  - it needs the write grant of every notebook it touches, resolved through the gateway (AD-6);
  - it checks the basis (AD-17, as entry 2 sets it for a move) and takes the snapshot entry 2 sets for a move: a hierarchy snapshot of both parents, plus a page snapshot if entry 1 shows a page move can lose the page (AD-7, AD-8);
  - it refuses a move into or out of the recycle bin (restoring is E7);
  - it reads back and reports the moved item's ID after the move, and drops moved pages from the search cache (AD-12);
  - a section move updates what depends on the section's path, its `SectionKey`, as entry 2 sets.

  No other hierarchy write moves anything: `update_hierarchy` keeps refusing an ID that isn't already under its parent (5.1 constraint 4). A write's notebook is re-resolved under the write lock, so a section moved between notebooks can't take another write's grant (deferred-work, story 2.4). (AD-7 `HierarchyWrite` move)
- **E6 (R8) — `merge_sections`.** Merges one section into another with `MergeSections`, under the same grants and basis check as a move, and reports what became of the source section. If entry 1 shows it removes the source or gives its pages new IDs, it takes a page snapshot of every source page, DPAPI-encrypted where the source is protected (Decision, Q12). (AD-7 `HierarchyWrite`)
- **E7 (R5, R8) — `restore_from_recycle_bin`.** Brings a soft-deleted page or section back out of the recycle bin. A page is matched to its soft-delete journal entry through the recycle-bin ID that story 5.5 records (Decision, Q10). That ID goes stale when the notebook reopens or the section is protected (AD-8; 5.1 constraint 15), so when it is missing or gone, the match falls back to the page's name. The tool takes the recycled item's ID; a lookup by name that matches several returns the candidates for the model to ask the user (Decision, Q3). The item goes back under its original parent from its journal entry, or under a target the caller names, and is otherwise refused with a `userAction` asking for a target. An item from a protected section goes back only into its own section, found by `SectionKey`, with no override and no `parentNamePath` fallback (Decision, Q4; AD-8). A soft-deleted section group can't come back as a unit: its sections land in the recycle bin one by one (5.1 constraint 8). (AD-7 `HierarchyWrite`, AD-8)
- **E8 (R8) — Protected sections.** No export, move, merge or restore puts a password-protected section's content in a file, section or notebook that AD-21 wouldn't let the model read. Moves and merges out of a protected section are refused, as story 5.2 records (Decision, Q9); entry 2 may relax that with entry 1's evidence. That covers moving a protected section whole (Decision, Q14). Restore isn't in that rule; E7's `SectionKey` limit keeps protected content in its own section (Decision, Q4). An export of protected content needs the section's read grant and warns that the file isn't encrypted; a notebook package needs the read grant of every protected section in it (Decision, Q1). Unpacked packages follow E3 (Decision, Q11). (AD-6, AD-21)
- **E9 (R8) — Coverage.** `Publish`, `OpenPackage`, `MergeFiles`, `MergeSections` and the `UpdateHierarchy` move each have a released tool, or a dated Decision recording why not (the initiative's Done when 4).

## Done when

1. `publish`, and `open_package` and `merge_files` where they ship, write only under an export root. Any other path returns `ExportPathNotAllowed`, and an existing file `FileExists`. The result carries `meta.outputPath`, and the skill reports it.
2. A page, section or section group moves between sections, section groups or notebooks, and `merge_sections` merges a section, only when every notebook involved is granted. The snapshot entry 2 sets is in the journal, and the result gives the moved item's current ID.
3. `restore_from_recycle_bin` restores a soft-deleted page or section. When a name matches several recycled items, it returns the candidates so the user chooses.
4. An export, move, merge or restore of content in a password-protected section is refused unless E8's rule allows it, and the integration suite shows it.
5. Every round-2 COM method has a released tool or a dated Decision, and the release ships through the epic 1 pipeline with the skill and README updated.

## Boundaries

This epic owns:
- the `ExternalWrite` class and its pipeline, and the export-root handling in settings;
- the move, merge and recycle-bin restore paths of `HierarchyWrite`;
- `Publish` formats beyond the backup's, `OpenPackage`, `MergeFiles` and `MergeSections` on the port and both gateways;
- these tools, and the skill and README sections that cover them.

Out of scope: app-state and UI tools (epic 4), including `CloseNotebook` for a notebook that `open_package` opened. Epic 5 owns `UpdateHierarchy` add, rename and reorder, soft and permanent delete, and the `Publish` backup.

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R5, R8; Done when 4
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-3, AD-5, AD-6, AD-7, AD-8, AD-9, AD-10, AD-11, AD-12, AD-14, AD-16, AD-17, AD-18, AD-21, Deferred
- rationale — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md, constraints 4–8, 11, 15–18, and "Open before final, and epic impact"
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, rows 19–24 and the short answer's point 6
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/epic-hierarchy-writes-and-deletes.md, the `HierarchyWrite` pipeline, `UpdateHierarchy`, soft delete and the `Publish` backup this epic extends
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/epic-safe-writes.md, the pipeline, grants, write lock and journal
- prior art — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md, rows 17–20, and CLAUDE.md, section "Prior art: OneMore"
- deferred work — _bmad-output/initiative-onenote-mcp-v1/deferred-work.md, the create-page race on a section moved between notebooks (story 2.4 section)

## Notes

- Decision (Phill 2026-11-12, Q1, option a): exporting content in a password-protected section needs that section's read grant, and the result warns that the file isn't encrypted; a notebook package needs the read grant of every protected section in it. Exports are plaintext: PDF, Word and MHTML by nature (spike 1 row 23), `.one` because `Publish` writes a protected section in the clear (5.1 constraint 17). Entry 2 writes this rule in AD-21 beside 5.2's rule for the permanent-delete backup, so backups and exports have one rule in one place. Rejected: refusing every such export (b); the write grant alone (c), which contradicts AD-21.
- Decision (Phill 2026-11-12, Q2, option a): moves go through a separate move tool that takes the item, its new parent and a position, and builds the `UpdateHierarchy` XML in Core. Entry 2 records it in AD-5 as a promoted task beside `update_hierarchy`, and its name in AD-14, as for `create_page`. Rejected: a move argument on `update_hierarchy` (b), which would change its contract after epic 5 ships it and have the model write XML spanning two notebooks.
- Decision (Phill 2026-11-12, Q3, option a): `restore_from_recycle_bin` takes the recycled item's ID. A lookup by name that matches several returns the candidates (ID, deletion time, where each came from), and the model asks the user. Entry 2 rewords AD-8's "asks the user to choose" to match. Rejected: an elicitation dialog (b).
- Decision (Phill 2026-11-12, Q4, options a and e): a restored item goes back under its original parent, found from its soft-delete journal entry (`parentId`, else `parentNamePath`), or under a target the caller names; with neither, it is refused, with a `userAction` asking for a target. An item from a protected section goes back only into its own section, found by `SectionKey`, with no override and no `parentNamePath` fallback (AD-8). 5.2's refusal of moves and merges out of protected sections isn't extended to restore (e): the `SectionKey` limit already keeps protected content in its own section.
- Decision (Phill 2026-11-12, Q5, option a): `merge_files` ships only if entry 1 shows a result the user would want; otherwise entry 2 records a dated Decision not to expose it, which closes E4, and entry 9 is dropped.
- Decision (Phill 2026-11-12, Q6, options a and c): `open_package` takes a package from any path, and the notebook it opens gets no automatic write grant.
- Decision (Phill 2026-11-12, Q7, option a): `publish` refuses a section group before COM; exporting each section of a group into a folder is a skill recipe (entry 10).
- Decision (Phill 2026-11-12, Q8, option a): the user manages `exportRoots` by editing `settings.json` by hand, as the README will say (entry 10). No new CLI.
- Decision (Phill 2026-11-12, Q11, option a): every section of a notebook that `open_package` opened, and of any notebook under an export root or the backup folder, counts as protected under AD-21 and needs a read grant, because an opened package or backup carries no `encrypted` marker (5.1 constraints 17, 18). This also covers a backup opened by hand.
- Decision (Phill 2026-11-12, Q12, option a; conditional on entry 1): if entry 1 shows `MergeSections` removes the source section or gives its pages new IDs, a merge takes a page snapshot of every source page, DPAPI-encrypted where the source is protected. If it doesn't, the hierarchy snapshot stands. Entry 2 writes whichever entry 1's result calls for.
- Decision (Phill 2026-11-12, Q13, option a): the user closes a notebook that `open_package` opened, in OneNote, until epic 4's `CloseNotebook`; the README will say so (entry 10). Entry 8's integration tests close the `-unpacked` notebooks they open through the test harness's own COM seam and delete their folders.
- Decision (Phill 2026-11-12, Q14, option a): moving a protected section whole counts as a move out of a protected section, so 5.2's refusal covers it until entry 2 relaxes the rule on entry 1's evidence.
- Decision (2026-11-12, Phill, relayed by the team lead): plan rounds 2 and 3 now, and build them only after epics 2 and 5 land. This file's `after` gates every entry on both epics being done. That gate is the one cross-epic dependency model here: entries name epic 5's stories only as what they build on, never in `after`.
- Decision (2026-11-12, Phill, Q9): keep the whole-epic gate; entry 1 runs after epics 2 and 5. Story 5.2 sets a cautious rule: moves and merges out of a protected section are refused. Entry 2 may relax it with entry 1's evidence. 3.1 is not added to 5.2's `after`. Epic 5's `tickets.toml` (5.2) records it.
- Decision (2026-11-12, Phill, Q10, option a): story 5.5 reads back a soft-deleted page's recycle-bin ID and stores it in its journal entry, and 5.2 adds that field to AD-8 and AD-10; entry 5 uses it to match a recycled page to its original place. Epic 5's `tickets.toml` (5.2, 5.5) records it.
- Waits on epic 2 because it uses the pipeline, grants, write lock and journal.
- Waits on epic 5 because move builds on its `UpdateHierarchy` (5.4), `restore_from_recycle_bin` on its soft delete and journal field (5.5), and `publish` on its `Publish` backup on the gateway (5.6).
- Answered before this epic, so the spike doesn't repeat them:
  - `Publish` of a page to PDF, `.docx` and MHTML works, in 95 ms, 2.9 s and 20 ms. Over an existing file it returns `0x8004201a` (spike 1, rows 23 and 24).
  - `Publish` in OneNote format works for a page and a section in about 0.1 s. A section group fails in every form tried. A notebook fails as `.one` and works as `.onepkg` (other formats weren't tried), and the package file may land after the call returns. A missing target folder raises a modal and the call blocks. A locked protected section returns `0x80042014`. Under File > Options or the password dialog, `Publish` returned `0x80042006`, not `0x80042030` (5.1 constraints 6, 11, 16, 18).
  - `Publish` of an unlocked protected section writes its titles and body text in the clear, straight from `Publish`, local and cloud (5.1 constraint 17, run 5).
  - `OpenPackage` unpacks a notebook package in about 0.4–0.6 s. It opens as an ordinary notebook named `<package>-unpacked`, with no `encrypted` marker, and a pages-scope read straight after listed no pages (5.1 constraints 7, 17, 18).
  - `UpdateHierarchy` moves a section listed under another section group of the same notebook, with no error. The section keeps its ID and its own date, and the move synced on OneDrive (5.1 constraint 4).
  - Where soft-deleted items go: a page to Deleted Pages under a new ID; a section to `OneNote_RecycleBin` with its ID; a section group dissolves. A protected section keeps `encrypted` in the recycle bin and is locked straight after the delete (spike 1 rows 19 and 22; 5.1 constraints 8 and 16).
  - Protecting a section gives it and its pages new IDs (5.1 constraint 15), so nothing in this epic may rely on a remembered ID across that.
- From OneMore and Microsoft's docs (checked 2026-11-12; rows 17–20 of `../epic-read-only-release/research-onemore-lessons.md`):
  - Microsoft documents no scope per `Publish` format, nothing on `OpenPackage`, and no way to restore from the recycle bin. OneMore has no restore feature either.
  - `MergeFiles` is a three-way merge of versions of one `.one` file (same OneNote ID), "intended for mobile scenarios", failing `0x8004201F` hrMergeFailed. OneMore never calls it.
  - OneMore moves pages, within a notebook and across notebooks, by removing the `Page` from the source section's hierarchy XML, adding it to the target's, and calling `UpdateHierarchy` on each. Only the moved page's ID changes. Its comments also say that leaving a sibling out of an `UpdateHierarchy` call deletes it, which 5.1 didn't see for sections (constraint 4).
  - OneMore imports a `.one` file by opening it with `OpenHierarchy` and merging it into a section with `MergeSections`, because an opened section's pages aren't loaded (`areAllPagesAvailable=false`). That may be why 5.1 saw no pages in an opened backup (constraint 7).
- Unknown, settled by entry 1 (the spike), which probes each on MCP Test and MCP Test Cloud, with a Sync Now check after each cloud step:
  - `Publish` in the other formats (PDF, XPS, Word, MHTML, HTML, EMF, OneNote 2007) for a page, section and notebook: timings, when the file lands, and which formats write more than one file. Also a notebook package holding a locked or unlocked protected section: is that section encrypted in the package?
  - Export edge cases: a `.onepkg` that never lands; a partial file left by a timed-out call, which would then block a retry with `FileExists`; and whether `0x80042006` under a modal comes from the dialog.
  - `OpenPackage` into an existing or non-empty folder, its focus, and a package holding a protected section.
  - What `MergeFiles` does with three `.one` files.
  - `MergeSections` within and across notebooks: does the source survive, do the pages get new IDs, a protected source or destination. Also whether merging an opened backup of an unprotected section shows its pages, as OneMore's import does; never a protected section's backup, because opening one writes plaintext.
  - Moving a page to another section and another notebook: does sending the source section without the page move it or delete it? Moving a section to another notebook, and a section group. IDs, dates and paths of the item and both parents.
  - Protected moves: a page moved out of and into an unlocked and a locked protected section; a protected section moved to another section group and another notebook (does `encrypted` survive, and what happens to its path and so its `SectionKey`).
  - Restoring a recycled page and section through `UpdateHierarchy`, a recycled protected section (locked straight after the delete), and what triggers `0x8004202E`.
  - The modal-dialog result and HRESULTs per method, including `0x80042031` hrPublishFormatUnsupportedForLabels if it can be triggered.
- Assumption (unconfirmed): no release ticket. Phill tags the release after the refactor sweep, as in epics 2 and 5.
- Assumption (unconfirmed): no opening refactor, because epic 5's refactor sweep (5.8) closes just before this epic starts.
- Assumption (unconfirmed): section-group moves, and moves on OneDrive notebooks, follow what 5.2 decides for a section-group rename on OneDrive, which didn't sync (5.1 constraint 5).
- Assumption (unconfirmed): exports default to `Documents\OneNote Exports`, which on many machines is synced to OneDrive. The README will say so (entry 10); the epic doesn't change the default.
- Assumption (unconfirmed): restoring a dissolved section group as a unit is out of scope; its sections come back one by one.
- Assumption (unconfirmed): COM can't unlock a protected section, so no test unlocks MCP Test's protected section without Phill. Integration tests run the refusals and `SectionLocked` against that section locked. Unit tests on `FakeGateway` cover the unlocked paths. Entry 10 runs them once live, with Phill unlocking the section. Entry 5 is hitl because a recycled protected section needs Phill to unlock a section before its soft delete.
- Assumption (unconfirmed): test fixtures. Entry 4 owns the second notebook for cross-notebook moves and merges: it reuses a throwaway notebook 5.1 left open (`MCP Test Throwaway 1112-034716 a`, which the integration guard already allows) and grants it in the tests' temporary settings; entries 12 and 6 reuse it. Entry 7 also exports that notebook, which holds no protected section, so entry 8 has a package to open whatever Q1 decides.
- Decision (Phill 2026-11-12, accepting the plan by answering its questions; drafted in an autonomous inception and revised after its Fable review the same day): the breakdown is entries 1–13 in `tickets.toml`, in one lane, because every story after the spine amendment touches the tool registration and contract snapshot, the error list or the pipeline runner. The tracer bullet is entry 3, `publish` of a page to PDF through the new `ExternalWrite` pipeline. The moves (entries 4 and 12) follow it as the slices least known. If entry 5 (restore) is dropped, entry 6 goes after entry 12. If entry 9 (`merge_files`) is dropped, entry 10 goes after entry 8.
