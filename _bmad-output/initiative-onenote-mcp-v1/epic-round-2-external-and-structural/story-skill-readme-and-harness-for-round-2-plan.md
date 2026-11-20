---
title: 'Skill, README and harness for round 2'
type: 'feature'
ticket: '10'
created: '2026-11-19'
baseline_revision: 'e97afd76365f24a9d451ac216f9d55cf2610eb72'
status: 'done'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/story-skill-readme-and-harness-for-hierarchy-writes-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Round 2 shipped six tools (`publish`, `open_package`, `move_hierarchy_object`, `merge_sections`, `restore_from_recycle_bin`, `set_page_level`) and `create_page`'s `parentPageId`, but the guide skill and README name none of them (`PluginPackagingTests.DocumentedByRound2Entry10` exempts them, l.256). Several lines are now wrong: the skill says moves and exports can't be done (`SKILL.md` l.3, l.32; `references/hierarchy-writes.md` l.113–115; `references/page-writes.md` l.140–142), and so does the README (l.5, l.54, l.82, l.308 "*(planned)* `Documents\OneNote Exports\`"). The harness's `WriteTools` (`spikes/stress-harness/Calls.cs` l.70) lacks all six, so its startup contract check fails on today's 29-tool contract. E8's allowed paths in an unlocked protected section haven't run live through the tools.

**Approach:** Documentation and a small harness fix, describing the code at the baseline and the round-2 sittings, in 5.7's form (its plan is the template). The skill gets recipes for every round-2 tool, tool-map rows, and error and warning rows; the README gets the rows, an export-roots section and every stale line fixed; the harness lists the new write tools; the exemption set is emptied. A live step with Phill (hitl) runs the matrix below and E8's unverified paths.

**Decisions (Phill, 2026-11-19, "Proceed" on the coordinator's recommendations):**
1. Q1 (a): recipes 1–2 go in a new `plugin/skills/guide/references/exports-and-packages.md`; recipes 3–6 are added to `references/hierarchy-writes.md`. `SKILL.md`'s Writing overview names which file to read before which write.
2. Q2 (a): the full live step: a plugin session over every matrix row, the README-only export and restore, the harness on MCP Test, and E8's five paths through the tools with Phill unlocking MCP Test's protected section: `publish` of it with a read grant (`exportNotEncrypted`), a merge into it, a page moved into it (with and without `position`), `set_page_level` and a subpage `create_page` in it with a read grant, and a restore by the name fallback.
3. Q3 (a): the story stays whole.
4. Q4: approved.

## Boundaries & Constraints

**Always:**
- 2.12's conventions: `SKILL.md` keeps safety rules, tool map, errors and warnings and a "Writing" overview that says which reference to read before which write; recipes point to tool descriptions instead of copying them; tools by full prefixed name; placeholders only ("notebook A", "MCP Test"); the frontmatter description at most 1,024 characters (1,022 now, so it is rewritten, not appended to).
- Recipes (each: what to read first, the call, what to tell the user, how to undo):
  1. **Export** (`publish`): formats per scope (a notebook only as `.onepkg`); tell the user `meta.outputPath`; never overwrite (`FileExists`: pick a new name); a protected section's content needs its read grant and gives `exportNotEncrypted`, which the user must hear; a notebook package needs none; a section group section by section into a folder (epic Q7); a timeout is `outcomeUnknown`: look for the file before retrying.
  2. **Open a package** (`open_package`): copies go to `%LOCALAPPDATA%\OneNoteMcp\opened`, a name or a direct child only; OneDrive refused, and why; the copy is protected and has no write grant; tell the user `meta.outputPath` and that they close it in OneNote; `pagesNotListed`; a closed copy older than 7 days is deleted at a later server start (while OneNote runs); undo = close it.
  3. **Move** (`move_hierarchy_object`): basis from the item's parent's `children` read; both notebooks' write grants; a page gets a new ID (`meta.objectId`), subpages stay behind (`subpagesLeftBehind`); refusals (protected source, recycle bin, cloud section into another notebook's group: move to the root first); undo = move back with a fresh basis.
  4. **Merge** (`merge_sections`): the source stays; merging twice duplicates (the basis can't tell), so confirm and never retry blindly; `meta.pageIds`; undo = delete the copies.
  5. **Restore from the recycle bin** (`restore_from_recycle_bin`): find the item with `get_hierarchy` scope `pages`; a page comes back with a new ID; a group comes back section by section; candidates → ask the user, retry with `entryId`; `restoredNotMoved`; a protected section only to its own place, still locked.
  6. **Subpages** (`set_page_level`, `create_page` with `parentPageId`): level rules and fixing a refused level by changing a neighbour first; `subpageNotPlaced`; undo = set the level back.
- `references/hierarchy-writes.md` l.85 (undo of a soft delete) points to recipe 5; l.113–115 and `page-writes.md` l.140–142 keep only what's still unsupported (deleting a notebook, images or ink, a notebook on OneDrive); `page-writes.md` l.124 says `create_page` with `parentPageId` journals the section.
- `SKILL.md` error rows: add `ExportPathNotAllowed`; widen `FileExists` (exports, opened copies), `NotSupported` (refused before COM, not only "not in this release"), `SectionLocked`, `ObjectNotFound` (recycle bin); warning rows `exportNotEncrypted`, `pagesNotListed`, `subpagesLeftBehind`, `settled`, `readGrantAtLocation`, `subpageNotPlaced`, `restoredByName`, `restoredNotMoved`; `outcomeUnknown` re-reads per round-2 tool.
- README: a row for each tool and `parentPageId`; l.5, l.54, l.82 rewritten; an "Export folders" section (editing `exportRoots` by hand, epic Q8; a `Documents` folder synced to OneDrive, its web addresses also protected (backlog 13); exports of protected content aren't encrypted; nothing is ever overwritten); "Opened packages" (where, OneDrive refused, closing, 7-day cleanup, purge); the Files table's export and opened-copies rows; the journal list covers move, merge, restore, `set_page_level` and subpage `create_page`, and what `restore_from_journal` can't restore.
- `PluginPackagingTests.DocumentedByRound2Entry10` emptied (and its two guard uses kept), so every contract tool must be named; `PluginPackagingTests` stay green.

**Never:** spine edits; a `<Version>` bump or release; touching OneNote, settings or the grant CLI in the build; editing `tickets.toml` or `CLAUDE.md`; code changes beyond the harness, the test's exemption set and any tool-description fix an Open Question decides.

## I/O & Edge-Case Matrix

What an agent does when it follows the skill: the live step's script (MCP Test; the throwaway notebook for cross-notebook rows).

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Export a page | "save page P as PDF" | `publish`; tells the user the path | `FileExists`: asks for another name |
| Export a group | "export section group G" | each section into a folder, paths listed | a locked section: says so, goes on |
| Open a package | "open this .onepkg" | copy in AppData; tells path and how to close | a OneDrive destination: relays refusal |
| Move a page | "move P to section S2" | basis read; reports the new ID | protected source: relays `NotSupported` |
| Merge | "merge S1 into S2" | confirms; reports copies; S1 stays | asked again: says it would duplicate |
| Restore | "bring back deleted page P" | finds it in the bin; restores; new ID | two candidates: asks which |
| Subpage | "make P a subpage of Q" | `set_page_level` (or reorder first) | refused level: fixes a neighbour, asks |

</frozen-after-approval>

## Code Map

- `plugin/skills/guide/SKILL.md` -- l.3 description (1,022 chars; drop "can't do yet", add export/move/merge/restore/subpage triggers); l.16–32 Writing (l.32 "never work around a missing tool" lists moves); l.34–62 tool map (add six rows after l.62; widen `create_page` l.52); l.143 Recipes; l.216–241 errors; l.247–267 warnings; `outcomeUnknown` paragraph.
- `plugin/skills/guide/references/hierarchy-writes.md` -- l.85, l.105–115; `references/page-writes.md` -- l.124, l.128–132 (`create_page` recipe), l.140–142.
- Tool descriptions, the facts source (cite, don't copy): `src/OneNoteMcp.Server/Tools/{Publish,OpenPackage,MoveHierarchyObject,MergeSections,RestoreFromRecycleBin,SetPageLevel,CreatePage}Tool.cs`; warning codes in `src/OneNoteMcp.Core/Results/ToolWarnings.cs`.
- Facts from the sittings: publish plans (tracer l.125–127; notebooks l.136–140; other formats l.132, EMF refusal); move plans (page l.129; section l.131, `settled`); merge l.204–214 (`0x8004201F` 1 in 3); restore l.297–302 (cloud section to a local group: `restoredNotMoved`); subpage l.201–212; open-package l.200–end (AppData, OneDrive crash, cleanup); backlog 13 plan (web addresses).
- `README.md` -- l.5, l.13–44 tables, l.54, l.82, l.123, l.133–160 journal, l.260–285 Uninstall (opened copies already there), l.301–308 Files.
- `spikes/stress-harness/Calls.cs` l.70 -- add the six tools; `Program.cs` l.96–98 count comment (29 tools); `README.md` l.23 write-tool list and l.79's "11 read-only, 12 not". `contract-ok` (`FakeServer.cs` l.54) then passes with `--contract-only`.
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` l.256–267 -- empty the set; keep `ToolsAwaitingRound2Docs_AreAllInTheContract` trivially true or delete it with the set.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` l.615–616 -- close the entry.

## Tasks & Acceptance

**Execution:**
- [ ] `plugin/skills/guide/references/exports-and-packages.md` (new) and `references/hierarchy-writes.md` (decision 1) -- recipes 1–6, "when a write fails" (re-reads), "what this release can't do".
- [ ] `plugin/skills/guide/SKILL.md` -- description, Writing overview, tool map, error and warning rows, `outcomeUnknown`.
- [ ] `plugin/skills/guide/references/{hierarchy,page}-writes.md` -- the stale lines.
- [ ] `README.md` -- per Boundaries.
- [ ] `spikes/stress-harness/{Calls,Program,README}` -- the six tools, the count.
- [ ] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- empty the exemption.
- [ ] `deferred-work.md` -- close l.615–616; record the live checks pending.

**Acceptance Criteria:**
- Given the branch, when `dotnet test tests/OneNoteMcp.Tests` runs, then every contract tool is named in `SKILL.md` or a reference and in the README, with no exemption.
- Given the harness built from the branch, when it runs `--contract-only` against itself as a `contract-ok` fake server (harness `README.md` l.79), then it exits 0.
- Given the plugin packed from the branch, when `claude plugin validate --strict dist/stage/onenote` runs, then it passes with only the existing missing-icon notice.
- Given `SKILL.md`, the references and `README.md`, when searched for "planned", "not built", "can't", "yet", then only notebook deletes, image and ink deletes and OneDrive notebook creation remain unsupported.

## Implementation Notes

- 2026-11-19, built directly (no implementation subagent, per the coordinator's standing rule), test-first where code changed:
  - `PluginPackagingTests`: the exemption set and its test removed; `GuideSkill_MentionsEveryContractTool` and `Readme_NamesEveryContractTool` went red (2 failing) on the six round-2 tools, then green once the docs named them.
  - Harness: it didn't build on main (`ProbeSectionSync.cs` used `HierarchyNode.Encrypted`, renamed `EncryptedMarker` by entry 15); fixed. Its `--contract-only` check against a `contract-ok` fake then failed (exit 3, 6 tools not read-only), and passes (exit 0, "contract ok: 29 tools") with the six tools in `WriteTools`.
  - Skill: new `references/exports-and-packages.md` (recipes 1 export, 2 open a package); `references/hierarchy-writes.md` recipes 6 move, 7 merge, 8 restore from the recycle bin, 9 subpages, recipe 4's undo, the `outcomeUnknown` re-reads and "can't do"; `page-writes.md` l.124, the new-page recipe and "can't do"; `SKILL.md` description (1,015 chars), intro, When to use, Writing, six tool-map rows and `create_page`'s, `FileExists`, `NotSupported` and a new `ExportPathNotAllowed` row, eight warning rows.
  - README: status line, six rows and `create_page`'s, the "(planned)" line removed, Limits, the journal list, a new "Exports and opened packages" section ("Export folders", "Opened packages"), the Files rows. "edit `settings.json` yourself", not "by hand", which `Readme_DocumentsPurge_AsBuilt` forbids.
- Matrix test audit: the matrix is what an agent does following the skill, the live step's script (as in 5.7); no unit test covers its rows. What the build could check ran: the doc guards, the harness's fake contract run, and `claude plugin validate --strict dist/stage/onenote` (passed; the stage holds all three references).

- 2026-11-19, sitting part A, three doc fixes (docs only): the README now says Claude asks for another name on `FileExists`, as the skill does; recipe 8 and the README say the server lists candidates only for a restore matched by name (a stale recycle-bin ID), and that the agent asks first when several items in the bin match; recipe 1 exports a nested section group with `scope: sections` (as `publish`'s refusal suggests), mirroring the tree in subfolders.

## Plan Change Log

## Review Triage Log

- 2026-11-19, quick lens, 13 findings, each checked against the tool descriptions, `ToolWarnings.cs` and the docs; 12 patched, 1 noted:
  - medium, patched: README `create_page` row said "never a read grant", but `parentPageId` in a protected section needs one (`CreatePageTool.cs`).
  - medium, patched: `SKILL.md`'s grant rule said every write but `create_notebook` needs the write grant; `publish` and `open_package` need none.
  - medium, patched: `SKILL.md`'s `outcomeUnknown` paragraph lacked the round-2 re-reads (Boundaries); added per tool.
  - low, patched: `SectionLocked` (publish's export-the-others route) and `ObjectNotFound` (its meaning for `restore_from_recycle_bin`) rows widened.
  - low, patched: the harness README's exit-3 text still said twenty-three tools.
  - low, patched: README Files row said "later export settings".
  - low, patched: README "What it can't restore" didn't name the round-2 layout entries.
  - low, patched: README `restore_from_recycle_bin` row implied only `delete_hierarchy_object` deletes can be restored.
  - low, patched: `SKILL.md`'s `restore_from_recycle_bin` row put `parentId` as a fallback; it wins when given.
  - low, patched: recipe 6 and the `settled` row implied a page move waits for a cloud settle; only a section or group move does (`ToolWarnings.Settled`).
  - low, patched: recipe 1 left `onepkg` out of a page's formats (only a notebook is limited to it).
  - low, patched: README `move_hierarchy_object` row said "OneDrive notebook" for a rule that covers any cloud notebook.
  - noted: the AC's search expects only three unsupported items, but moving a notebook is unsupported too (`move_hierarchy_object` takes pages, sections and groups). The docs say so consistently now; the AC's list is narrower than the fact, and the frozen plan isn't edited.
- Verification after the patches: build 0 warnings; unit 3774 passed, 3 skipped; integration 220 passed, 126 skipped; harness `--contract-only` against a `contract-ok` fake: "contract ok: 29 tools", exit 0; `claude plugin validate --strict dist/stage/onenote`: passed.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, the exemption gone.
- The harness's `--contract-only --exe <StressHarness.exe> --exe-arg --fake-server --exe-arg 100 --exe-arg --fake-variant --exe-arg contract-ok` (harness `README.md` l.79) -- exit 0.
- `pwsh -File scripts/pack-plugin.ps1`, then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks (the live step, Phill):** per decision 2.

**Sitting script (main session with Phill; MCP Test, its protected section Ballast Vault, and the throwaway notebook; build c9ef3ee).** Load the new skill with the packed plugin (`claude --plugin-dir dist/onenote-0.9.0-rc.1-win-x64.zip`, or onenote-dev plus the stage's skill). Give the terminal heads-up before every step that can raise a grant dialog.

A. The docs, through the plugin (the agent works only from the skill and README):
1. "Save page P of MCP Test as a PDF": `publish`, tells the path. Ask again with the same name: `FileExists`, asks for another name.
2. "Export section group G": one file per section in a folder named after the group, listed; a locked section is reported and skipped.
3. "Open this .onepkg" (a package of the throwaway notebook): the copy in `%LOCALAPPDATA%\OneNoteMcp\opened\…`, the path and how to close it; Phill closes it.
4. "Move page P to section S2": basis read, reports the new ID. "Move a page out of Ballast Vault": relays `NotSupported`.
5. "Merge S1 into S2" (throwaway sections): confirms first, reports the copies, S1 kept. "Do it again": warns it would duplicate.
6. Soft-delete a throwaway page, then "bring back the page I deleted": restored, new ID. Two same-named deleted pages: lists the candidates and asks.
7. "Make P a subpage of Q": `set_page_level` (or a reorder first), tells the level.
8. README only: a fresh agent given only the README exports an MCP Test page and restores a soft-deleted one.

B. The harness on MCP Test (normal run, not `--contract-only`): its startup check passes on 29 tools, and 0 mismatches.

C. E8 live, with Phill unlocking Ballast Vault:
1. `publish` a Vault page with a read grant (Phill allows once): ok with `exportNotEncrypted`, the agent says the file is plain; then delete the file.
2. `merge_sections` a one-page throwaway section into the Vault: copies land encrypted, `meta.pageIds` absent. Phill removes the copy in OneNote.
3. `move_hierarchy_object` a throwaway page into the Vault without `position` (write grant only), then another with `position` (needs the read grant held at the start): both ok. Phill moves them out or deletes them in OneNote (a move out is refused by design).
4. In the Vault with the read grant: `set_page_level` on a page, and `create_page` with `parentPageId`; both ok. With no grant (after the once window ends): `ReadNotGranted`.
5. Restore by name: soft-delete a throwaway page, Phill closes and reopens its notebook (the recycle-bin ID goes stale), then `restore_from_recycle_bin`: restored with `restoredByName`.
6. Phill locks Ballast Vault again.

D. Phill deletes what the sitting made that he doesn't want.

### Sitting results (2026-11-19)

Build: the 3.10 head with backlog 12 merged in (scratch commit c4aa562), version 0.9.0-rc.1.

- **A. A fresh agent working from the skill and README only:** 7 checks passed and 2 partly passed. The two partial passes led to three doc fixes in 90d35c7:
  - `FileExists`: the README now says to ask the user for another name.
  - A same-named restore: the skill and README now say when the server lists candidates.
  - Nested section groups in an export: now covered.
  
  One code finding went to backlog 14.
- **B. The harness on MCP Test:** 843 comparisons, 0 mismatches, and the contract held for 29 tools. The harness exited 1 because 16 text blocks were over inline-max (the largest 102,870 characters). That issue predates this story and is backlog 15.
- **C. E8 live, with Ballast Vault unlocked and a `once` grant:**
  1. `publish` of a Vault page: `ok`, with `exportNotEncrypted`. The file was then deleted.
  2. A merge into the Vault: `ok`, with no `pageIds`.
  3. A move into the Vault with `position`, and one without `position` after the grant expired: both `ok`.
  4. `set_page_level` and `create_page` with `parentPageId`: `ok` with the grant. Without it, both gave `ReadNotGranted`, with no dialog.
  5. After the notebook was closed and reopened, a restore by its stale ID matched by name. Two journal entries matched, so it gave `InvalidArgument` listing the candidates. A retry with `entryId` restored the page into the reopened section. One entry that had already been used was still listed: backlog 16.
  6. The Vault was locked again.
- **D:** the exports were deleted. The Vault leftovers were kept, as Phill chose.
- **Backlog 12, live:** a `create_page` into a loose section gave `NotSupported`.

