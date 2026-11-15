---
title: 'Skill, README and harness for hierarchy writes'
type: 'feature'
ticket: '7'
created: '2026-11-15'
status: 'draft'
baseline_revision: '8652c1948dca9194835f4dd134f11be017cdec4d'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-skill-and-readme-for-page-writes-plan.md'
warnings: ['oversized']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 5's seven tools (5.3–5.6) have tool-map rows in the guide skill and README rows, but no recipes ("recipes are entry 7's", plans 5.3 l.48, 5.4 l.45, 5.5 l.52, 5.6 l.70). Several lines are now wrong: `references/page-writes.md` says this release can't rename or delete anything (l.140–142, l.116, l.124); the README's Grants section knows only two kinds of grant, its "What it does" table has no `permanently_delete_hierarchy_object` row, its Recovery journal says only page edits are journaled (l.122), backups appear only in the Safety model row (l.70), and Uninstall says purge "never looks inside subfolders" (l.250) though it removes `backups` (`UninstallCommand.cs` l.303–318). The stress harness's write-tool deny-list lacks all seven tools (`Calls.cs` l.70), so its startup contract check would fail on the 23-tool contract.

**Approach:** Documentation plus a three-line harness fix, describing the code at the baseline and the 2026-11-15 live sitting. The skill gets hierarchy recipes in a sibling reference file (Q1) and complete error, warning and `outcomeUnknown` text for the hierarchy tools. The README gets the permanent-delete grant, backups, the missing tool row and every stale line fixed (Q2). The harness lists the new write tools. The live step (Phill) verifies, including the one claim left unproven.

## Boundaries & Constraints

**Always:**
- Follow 2.12's conventions: `SKILL.md` keeps safety rules, tool map, errors and warnings, and a short overview that says which reference to read before which write; recipes point to tool descriptions instead of copying them; tools by full prefixed name; placeholders only ("notebook A", "MCP Test"); description at most 1,024 characters (923 now).
- Recipes (each: what to read first, the call, what to tell the user, how to undo):
  1. **Create** a section, section group or notebook: only on request; `alreadyExisted`; a group name with `.` is refused before COM (handled per Q4); `create_notebook`'s folder dialog, never picking for the user, OneDrive unsupported, `createdNotGranted`; a just-created node's date can move for a moment, so a follow-up write may get `StaleWrite` once (5.3 l.188): re-read, don't resend.
  2. **Rename**: basis from `get_hierarchy` scope `self`; a page by its title through `update_page_content`; a notebook's displayed name only; OneDrive group `NotSupported`; read-grant location clash; undo = rename back (`restore_from_journal` can't restore a hierarchy entry).
  3. **Reorder**: every child from scope `children`, once; sections before groups; a page keeps its level and moves with its subpages (5.4 L6); a reorder that moves only section groups may not be kept (`readBackDiffers`, 5.4 L3): tell the user, suggest OneNote, don't retry; undo = reorder back with the earlier `childIds` (keep them).
  4. **Soft delete**: page basis `{lastModifiedTime, pageHash}`, node basis `{lastModifiedTime}`; a page goes to Deleted Pages under `meta.recycleBinId`; a section keeps its ID; a group dissolves, its sections go to the bin one by one, an empty group leaves nothing in the bin (5.5 L5, L6); refusals; undo = OneNote's restore from the recycle bin (5.5 Q5a), or `restore_from_journal` for a page as a new copy.
  5. **Permanent delete**: only on an explicit request, confirmed in chat first; the dialog asks the user to type the object's name unless the notebook has the permanent-delete grant; `PermanentDeleteNotGranted` → relay `userAction`, never run it; give the user `meta.journalEntryId`, `meta.backupFile` and `meta.pageEntryIds`; undo = `restore_from_journal` per page entry (new pages), and for a section the backup file can be copied out of the journal and opened, without a promise; an empty section is `BackupFailed` (Publish refuses it, 5.6 P9): offer a soft delete; a protected section or notebook: relay `userAction` (soft delete then empty the bin; close and delete the folder).
- `SKILL.md` error rows: add `PermanentDeleteNotGranted`, `BackupFailed`, `FileExists`; widen `StaleWrite`, `JournalFailed`, `NotSupported`, `ObjectNotFound`, `Cancelled` (create_notebook's dialog) beyond pages; warnings `noBasis`, `readBackDiffers`, `readBackFailed` for hierarchy writes; the `outcomeUnknown` re-read paragraph (l.237, and `page-writes.md` l.136) names what to re-read after each hierarchy tool. When to use (l.14) and the title cover hierarchy writes.
- README: a `permanently_delete_hierarchy_object` row; `list_grants` row names permanent-delete grants; Grants gains the permanent-delete grant (CLI only, needs the write grant, revoking writes revokes it, the per-delete dialog stores nothing, `list_grants` kind `permanentDeleteNotebook`) and the Safety model row shrinks to point there; Recovery journal covers which writes journal what, what `restore_from_journal` can't restore, and backups (plain `.one` from Publish, `backups\<entry>.one`, pruned with their entry and counted in `maxSizeMB`, 30 days, outside OneDrive unless the journal is moved there, open only a copy, restore unproven, AD-8 l.270, l.279); Limits l.81 rewritten; Uninstall's purge list adds backups and drops "never looks inside subfolders"; Files it uses and How it works name backups and hierarchy writes.
- The group-rename read-grant claim (README l.41, `rename_hierarchy_object` description) is worded as unconfirmed in the docs this story writes, and checked in the live step.
- `PluginPackagingTests` stay green.

**Never:** spine edits; a `<Version>` bump or release; touching OneNote, settings or the grant CLI in the build; editing `tickets.toml` or `CLAUDE.md`; tool-description or other code changes beyond the harness, unless Q3 or Q4 says so.

## I/O & Edge-Case Matrix

What an agent does when it follows the skill. These rows are the live step's script.

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Create | "make a section Ideas in MCP Test" | `create_section`; reports the ID | `alreadyExisted`: says it was there |
| Dotted group | "make a section group v1.2" | per Q4 | no workaround beyond Q4 |
| Rename | "rename section Ideas to Ideas 2026" | scope `self` read, then rename with its basis | `StaleWrite`: re-read, ask |
| Reorder | "sort the sections A–Z" | scope `children` read, full `childIds` | `readBackDiffers`: tells the user |
| Soft delete | "delete page P" | basis from `get_page_content`; says it's in the recycle bin | protected page: relays `NotSupported` |
| Permanent delete | "permanently delete section S" (one page) | confirms in chat; dialog; reports backup and page entries | decline: `PermanentDeleteNotGranted`, stops |
| Empty section | permanent delete of an empty section | `BackupFailed`; offers a soft delete | no retry |
| Unsupported | "move page P", "delete notebook A" | says it can't; OneNote route | no workaround |

</frozen-after-approval>

## Open Questions

1. **Where the recipes go.** (a) A new `plugin/skills/guide/references/hierarchy-writes.md` beside `page-writes.md`; `SKILL.md` "Writing pages" becomes "Writing" and says which file to read before which write. (b) Append them to `page-writes.md`, renamed `writes.md`. Recommended: (a) — each file stays the size 2.12 chose, and a page edit doesn't load delete recipes.
2. **README layout.** (a) Add a "Permanent-delete grants" subsection under Grants and a "Backups" subsection under Recovery journal; the Safety model row keeps two sentences and links them; existing anchors stay. (b) A new top-level "Permanent delete and backups" section. Recommended: (a) — matches 2.12 Q4 and keeps grants in one place.
3. **Tool-description mismatches found in the audit.** `list_grants`' description omits kind `permanentDeleteNotebook` and the `--revoke --permanent-delete` command (`ListGrantsTool.cs` l.24–29); `permanently_delete_hierarchy_object`'s doesn't say a section in the recycle bin can be deleted (`PermanentDeleteUseCase.cs` l.14). (a) Docs only, with `deferred-work.md` entries (2.12's rule). (b) Fix both descriptions here, with the contract snapshot (`tool-contract.json`) regenerated. Recommended: (b) — epic 5 ships after this story, the client always shows descriptions, and the change is two sentences under the existing contract test.
4. **Section group names with a `.`.** Code refuses them before COM and tells the user to rename in OneNote, yet the sitting showed `rename_hierarchy_object` can set a dotted group name (5.3 l.185; `SectionFileNames.cs` l.118). (a) Keep today's advice: create without the `.`, the user renames in OneNote. (b) The recipe creates without the `.`, then, with the user's say-so, renames with `rename_hierarchy_object`; the `userAction` and `create_section_group` description change to match (code). Recommended: (a) for this story — (b) is a behaviour change better proven live first; log it as a deferred idea.
5. **Harness verification without OneNote.** The only OneNote-free harness run is the fake-server failing case (`--fake-variant contract-no-readonly`, exit 3), which passes with or without the fix. (a) Build the harness and run that case here; the real contract check runs in Phill's sitting. (b) Also add a `contract-ok` fake variant that lists the real 23 contract tools with today's hints and must pass the startup check, so the lane proves the fix. Recommended: (b) — about 15 lines in `FakeServer.cs`, and it keeps the harness honest when epic 3 adds tools.
6. **Live step scope.** (a) Two sessions on MCP Test: a plugin session that runs every matrix row with no tool names given, then a README-only session (server registered without the plugin) that does the ticket's permanent delete of a throwaway section holding one page, after Phill's CLI grant; plus the group-rename read-grant check and a harness run. (b) The ticket's minimum only: the README-only permanent delete and a harness run. Recommended: (a) — the recipes are the larger risk, and 2.12 Q7 (b) set this pattern.
7. **Plan size.** About 3,200 tokens (2,400 words), twice the 1,600 guide; 2.12's plan was oversized too and Phill kept it whole (2.12 Q8). (a) Keep it whole. (b) Split the harness fix into its own ticket. Recommended: (a) — the harness part is a few lines and the ticket bundles it.

## Code Map

- `plugin/skills/guide/SKILL.md` -- l.3 description (923 chars; add a sort and a permanent-delete trigger); l.6 title; l.14 When to use; l.16–29 Writing pages (l.18 points only to `page-writes.md`; l.27 permanent-delete "say-so" without the CLI flag); l.53–59 hierarchy rows (accurate; keep, move detail to recipes only if Q1 (a) needs room); l.213–235 error table; l.237 `outcomeUnknown` (page tools only); l.250–254 page-only warnings.
- `plugin/skills/guide/references/page-writes.md` -- l.116, l.124 "can't delete pages"; l.115 restorable entries; l.136 `outcomeUnknown`; l.140–142 "What this release can't do" (keep only move, notebook delete, images and ink).
- `README.md` -- l.22 `list_grants`; l.30–43 write table (add the permanent-delete row after l.43); l.41 group-rename grant claim; l.70 Safety row; l.81 Limits "Page writes only"; l.87–94 Grants ("two kinds"); l.120–139 Recovery journal (l.122); l.233, l.245–250 Uninstall; l.270–277 Files; l.287 How it works.
- `src/OneNoteMcp.Server/Tools/{CreateSection,CreateSectionGroup,CreateNotebook,RenameHierarchyObject,ReorderHierarchyChildren,DeleteHierarchyObject,PermanentlyDeleteHierarchyObject,ListGrants}Tool.cs` -- the contract; read only unless Q3 (b).
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs` l.18–41, l.91–110, l.292–301 -- `--permanent-delete` grant and revoke facts. `src/OneNoteMcp.Core/Access/PermanentDeleteDialog.cs` l.4–9 -- the dialog stores nothing.
- `src/OneNoteMcp.Core/Journal/{JournalFiles,JournalPruner}.cs` (l.57–58; l.32–36), `src/OneNoteMcp.Core/Writes/SectionBackup.cs`, `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` l.189–196 -- backup facts and the "open a copy" wording to reuse.
- `spikes/stress-harness/Calls.cs` l.70 -- add the seven tools to `WriteTools`; `Program.cs` l.88 -- count comment (twenty-three since 5.6); `README.md` l.23 -- names only `create_page`; `FakeServer.cs` l.46–48 -- variants (Q5).
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` l.266–318 -- doc guards already read `references/*.md`; no change expected.
- Prior plans for facts: 5.3 l.184–188, 5.4 l.210–220, 5.5 l.88, l.130, l.213, 5.6 l.225–241; spike part 2 findings l.15, l.85–96, l.100; spine AD-7 l.245–248, AD-8 l.270, l.279.

## Tasks & Acceptance

**Execution:**
- [ ] `plugin/skills/guide/references/hierarchy-writes.md` (per Q1) -- recipes 1–5, failure handling, what this release can't do.
- [ ] `plugin/skills/guide/SKILL.md` -- description, title, When to use, Writing overview, error and warning rows, `outcomeUnknown`.
- [ ] `plugin/skills/guide/references/page-writes.md` -- the stale lines in the Code Map.
- [ ] `README.md` -- per Q2 and Boundaries.
- [ ] `spikes/stress-harness/{Calls,Program,README}` (and `FakeServer.cs` under Q5 (b)) -- the seven tools, the count, the README line.
- [ ] Tool descriptions and `tool-contract.json` -- only under Q3 (b) or Q4 (b).
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- entries for what Q3 (a) or Q4 (a) leaves, and a 5.7 live-check entry.

**Acceptance Criteria:**
- Given the plugin packed from this branch, when `claude plugin validate --strict dist/stage/onenote` runs, then it passes with only the existing missing-icon notice, and the stage holds both reference files.
- Given `SKILL.md`, both references and `README.md`, when searched for "planned", "yet", "can't", "only page", then only moves, notebook deletes, image and ink deletes, OneDrive notebook creation and exports remain unsupported.
- Given Phill's CLI write and permanent-delete grants on MCP Test, when an agent given only the README is asked to permanently delete a throwaway section, then it reads first, passes the basis, reports the backup file and page entries, and never runs the grant command.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Why recipes say "confirm in chat" for permanent delete.** The server's dialog is the gate (AD-6, AD-7); the chat confirmation is courtesy, so a client without dialogs still gets a clear refusal rather than a surprise. No gate lives only in the skill (spine l.128).

**Unconfirmed claim.** After a group rename OneNote renamed the group's folder on disk lazily (5.4 l.210), and a read grant is keyed by the section's file path, so a protected section inside a renamed group may keep its grant until the folder moves. The docs say the grant ends "once OneNote renames the group's folder"; the live step checks it, and the wording is tightened (or a code fix logged) from what it shows.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, `PluginPackagingTests` and the contract test included.
- `dotnet build spikes/stress-harness -c Release`, then the fake-server run(s) per Q5 -- expected: `contract-no-readonly` exits 3; under Q5 (b) `contract-ok` passes the startup check.
- `pwsh -File scripts/pack-plugin.ps1`, then `claude plugin validate --strict dist/stage/onenote` -- expected: passes.
- Description length -- expected: at most 1,024 characters.
- Staged-diff grep for real notebook or employer names -- expected: empty.

**Manual checks (hitl, Phill's sitting; the build lane runs none of these):**
- Phill, in his own terminal: the CLI permanent-delete grant on MCP Test (the write grant is already there); `list_grants` shows kind `permanentDeleteNotebook`.
- Per Q6: a fresh plugin session (`claude --plugin-dir dist/onenote-<ver>-win-x64.zip`) runs the matrix rows on throwaway nodes in MCP Test; a README-only session permanently deletes a throwaway section holding one page, and the agent reports `meta.backupFile` and `meta.pageEntryIds`; Phill revokes the permanent-delete grant afterwards if he wants.
- Group-rename read grant: grant a read of a protected section inside a throwaway group in MCP Test, rename the group, then read the section: note whether the grant holds, and whether it ends once the folder is renamed on disk.
- The stress harness against the real server, with OneNote running (its startup contract check passes).
