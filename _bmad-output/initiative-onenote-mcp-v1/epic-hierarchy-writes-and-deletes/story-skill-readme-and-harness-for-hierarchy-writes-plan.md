---
title: 'Skill, README and harness for hierarchy writes'
type: 'feature'
ticket: '7'
created: '2026-11-15'
status: done
baseline_revision: '8652c1948dca9194835f4dd134f11be017cdec4d'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-skill-and-readme-for-page-writes-plan.md'
warnings: ['oversized']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 5's seven tools (5.3–5.6) have tool-map rows in the guide skill and README rows, but no recipes ("recipes are entry 7's", plans 5.3 l.48, 5.4 l.45, 5.5 l.52, 5.6 l.70). Several lines are now wrong: `references/page-writes.md` says this release can't rename or delete anything (l.140–142, l.116, l.124); the README's Grants section knows only two kinds of grant, its "What it does" table has no `permanently_delete_hierarchy_object` row, its Recovery journal says only page edits are journaled (l.122), backups appear only in the Safety model row (l.70), and Uninstall says purge "never looks inside subfolders" (l.250) though it removes `backups` (`UninstallCommand.cs` l.303–318). The stress harness's write-tool deny-list lacks all seven tools (`Calls.cs` l.70), so its startup contract check would fail on the 23-tool contract.

**Approach:** Documentation, two tool-description fixes, one changed `userAction`, and a small harness fix, describing the code at the baseline and the 2026-11-15 live sitting. The skill gets hierarchy recipes in a new `plugin/skills/guide/references/hierarchy-writes.md` and complete error, warning and `outcomeUnknown` text for the hierarchy tools. The README gets the permanent-delete grant, backups, the missing tool row and every stale line fixed. The harness lists the new write tools and gains a passing fake contract variant. The live step (Phill) verifies, including the claims left unproven.

**Decisions (Phill, 2026-11-15, plan approval):**
1. Q1 (a): recipes in a new `references/hierarchy-writes.md`; `SKILL.md` "Writing pages" becomes "Writing" and says which file to read before which write.
2. Q2 (a): README subsections "Permanent-delete grants" under Grants and "Backups" under Recovery journal; the Safety model row shrinks to a pointer; existing anchors stay.
3. Q3 (b): fix `list_grants`' description (kind `permanentDeleteNotebook`, its revoke command) and `permanently_delete_hierarchy_object`'s (a section in the recycle bin can be deleted) here, and regenerate `tool-contract.json`.
4. Q4 (b): for a section group name with `.`, the recipe has Claude create the group without the `.` and then, with the user's OK, rename it with `rename_hierarchy_object` to the dotted name. The pre-COM refusal stays; `create_section_group`'s refusal `userAction`, its tool description, and the `SKILL.md` and README wording change to match. A live-step row proves the create-then-rename path and that the dotted name sticks; a `deferred-work.md` note says it is unproven until that row passes.
5. Q5 (b): add a `contract-ok` fake-server variant that lists the real contract tools with today's hints and must pass the harness's startup check.
6. Q6 (a): the full live step (plugin session over every matrix row, README-only permanent delete, group-rename read-grant check, harness run, the Q4 row).
7. Q7 (a): keep the plan whole.
8. Q8: approved.

## Boundaries & Constraints

**Always:**
- Follow 2.12's conventions: `SKILL.md` keeps safety rules, tool map, errors and warnings, and a short overview that says which reference to read before which write; recipes point to tool descriptions instead of copying them; tools by full prefixed name; placeholders only ("notebook A", "MCP Test"); description at most 1,024 characters (923 now).
- Recipes (each: what to read first, the call, what to tell the user, how to undo):
  1. **Create** a section, section group or notebook: only on request; `alreadyExisted`; a group name with `.` is refused before COM, so create it without the `.` and, with the user's OK, rename it to the dotted name with `rename_hierarchy_object` (decision 4); `create_notebook`'s folder dialog, never picking for the user, OneDrive unsupported, `createdNotGranted`; a just-created node's date can move for a moment, so a follow-up write may get `StaleWrite` once (5.3 l.188): re-read, don't resend.
  2. **Rename**: basis from `get_hierarchy` scope `self`; a page by its title through `update_page_content`; a notebook's displayed name only; OneDrive group `NotSupported`; read-grant location clash; undo = rename back (`restore_from_journal` can't restore a hierarchy entry).
  3. **Reorder**: every child from scope `children`, once; sections before groups; a page keeps its level and moves with its subpages (5.4 L6); a reorder that moves only section groups may not be kept (`readBackDiffers`, 5.4 L3): tell the user, suggest OneNote, don't retry; undo = reorder back with the earlier `childIds` (keep them).
  4. **Soft delete**: page basis `{lastModifiedTime, pageHash}`, node basis `{lastModifiedTime}`; a page goes to Deleted Pages under `meta.recycleBinId`; a section keeps its ID; a group dissolves, its sections go to the bin one by one, an empty group leaves nothing in the bin (5.5 L5, L6); refusals; undo = OneNote's restore from the recycle bin (5.5 Q5a), or `restore_from_journal` for a page as a new copy.
  5. **Permanent delete**: only on an explicit request, confirmed in chat first; the dialog asks the user to type the object's name unless the notebook has the permanent-delete grant; `PermanentDeleteNotGranted` → relay `userAction`, never run it; give the user `meta.journalEntryId`, `meta.backupFile` and `meta.pageEntryIds`; undo = `restore_from_journal` per page entry (new pages), and for a section the backup file can be copied out of the journal and opened, without a promise; an empty section is `BackupFailed` (Publish refuses it, 5.6 P9): offer a soft delete; a protected section or notebook: relay `userAction` (soft delete then empty the bin; close and delete the folder).
- `SKILL.md` error rows: add `PermanentDeleteNotGranted`, `BackupFailed`, `FileExists`; widen `StaleWrite`, `JournalFailed`, `NotSupported`, `ObjectNotFound`, `Cancelled` (create_notebook's dialog) beyond pages; warnings `noBasis`, `readBackDiffers`, `readBackFailed` for hierarchy writes; the `outcomeUnknown` re-read paragraph (l.237, and `page-writes.md` l.136) names what to re-read after each hierarchy tool. When to use (l.14) and the title cover hierarchy writes.
- README: a `permanently_delete_hierarchy_object` row; `list_grants` row names permanent-delete grants; Grants gains the permanent-delete grant (CLI only, needs the write grant, revoking writes revokes it, the per-delete dialog stores nothing, `list_grants` kind `permanentDeleteNotebook`) and the Safety model row shrinks to point there; Recovery journal covers which writes journal what, what `restore_from_journal` can't restore, and backups (plain `.one` from Publish, `backups\<entry>.one`, pruned with their entry and counted in `maxSizeMB`, 30 days, outside OneDrive unless the journal is moved there, open only a copy, restore unproven, AD-8 l.270, l.279); Limits l.81 rewritten; Uninstall's purge list adds backups and drops "never looks inside subfolders"; Files it uses and How it works name backups and hierarchy writes.
- The group-rename read-grant claim (README l.41, `rename_hierarchy_object` description) is worded as unconfirmed in the docs this story writes, and checked in the live step.
- `PluginPackagingTests` stay green.

**Never:** spine edits; a `<Version>` bump or release; touching OneNote, settings or the grant CLI in the build; editing `tickets.toml` or `CLAUDE.md`; code changes beyond the harness, the two descriptions (decision 3), the dotted-group `userAction` and description (decision 4) and the tests and contract snapshot they move; removing the pre-COM dotted-group refusal.

## I/O & Edge-Case Matrix

What an agent does when it follows the skill. These rows are the live step's script.

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Create | "make a section Ideas in MCP Test" | `create_section`; reports the ID | `alreadyExisted`: says it was there |
| Dotted group | "make a section group v1.2" | creates "v12" (or similar, no `.`), asks the user, then renames it to "v1.2" with `rename_hierarchy_object`; a re-read shows "v1.2" | user declines: leaves the undotted name and says so |
| Rename | "rename section Ideas to Ideas 2026" | scope `self` read, then rename with its basis | `StaleWrite`: re-read, ask |
| Reorder | "sort the sections A–Z" | scope `children` read, full `childIds` | `readBackDiffers`: tells the user |
| Soft delete | "delete page P" | basis from `get_page_content`; says it's in the recycle bin | protected page: relays `NotSupported` |
| Permanent delete | "permanently delete section S" (one page) | confirms in chat; dialog; reports backup and page entries | decline: `PermanentDeleteNotGranted`, stops |
| Empty section | permanent delete of an empty section | `BackupFailed`; offers a soft delete | no retry |
| Unsupported | "move page P", "delete notebook A" | says it can't; OneNote route | no workaround |

</frozen-after-approval>

## Code Map

- `plugin/skills/guide/SKILL.md` -- l.3 description (923 chars; add a sort and a permanent-delete trigger); l.6 title; l.14 When to use; l.16–29 Writing pages (l.18 points only to `page-writes.md`; l.27 permanent-delete "say-so" without the CLI flag); l.53–59 hierarchy rows (accurate except l.54's dotted-group advice; keep); l.213–235 error table; l.237 `outcomeUnknown` (page tools only); l.250–254 page-only warnings.
- `plugin/skills/guide/references/page-writes.md` -- l.116, l.124 "can't delete pages"; l.115 restorable entries; l.136 `outcomeUnknown`; l.140–142 "What this release can't do" (keep only move, notebook delete, images and ink).
- `README.md` -- l.22 `list_grants`; l.30–43 write table (add the permanent-delete row after l.43); l.41 group-rename grant claim; l.70 Safety row; l.81 Limits "Page writes only"; l.87–94 Grants ("two kinds"); l.120–139 Recovery journal (l.122); l.233, l.245–250 Uninstall; l.270–277 Files; l.287 How it works.
- `src/OneNoteMcp.Server/Tools/{CreateSection,CreateSectionGroup,CreateNotebook,RenameHierarchyObject,ReorderHierarchyChildren,DeleteHierarchyObject,PermanentlyDeleteHierarchyObject,ListGrants}Tool.cs` -- the contract. Edit only `ListGrantsTool.cs` l.24–29 (kind `permanentDeleteNotebook` and its fields, the `--revoke --permanent-delete` command), `PermanentlyDeleteHierarchyObjectTool.cs` (a section in the recycle bin can be deleted; `PermanentDeleteUseCase.cs` l.12–16) and `CreateSectionGroupTool.cs` l.27 (decision 4).
- `src/OneNoteMcp.Core/SectionFileNames.cs` l.110–112 `GroupPeriodUserAction` -- new advice: create the group without the `.`, then, if the user agrees, rename it to the name they asked for with `rename_hierarchy_object`. `CreateHierarchyUseCase.cs` l.47, l.296–298 reuse it; `ComGateway.cs` l.314–319 too. Tests compare by the constant (`ComGatewayLifecycleTests.cs` l.688, `CreateHierarchyToolTests.cs` l.300–305; refresh l.300's comment). Keep the check itself.
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- replace with the snapshot `ToolContractTests.Tools_MatchTheContractSnapshot` prints on failure; no `<Version>` bump (5.3–5.6 changed the snapshot at 0.9 without one).
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs` l.18–41, l.91–110, l.292–301 -- `--permanent-delete` grant and revoke facts. `src/OneNoteMcp.Core/Access/PermanentDeleteDialog.cs` l.4–9 -- the dialog stores nothing.
- `src/OneNoteMcp.Core/Journal/{JournalFiles,JournalPruner}.cs` (l.57–58; l.32–36), `src/OneNoteMcp.Core/Writes/SectionBackup.cs`, `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` l.189–196 -- backup facts and the "open a copy" wording to reuse.
- `spikes/stress-harness/Calls.cs` l.70 -- add the seven tools to `WriteTools`; `Program.cs` l.88 -- count comment (twenty-three since 5.6); `README.md` l.23 -- names only `create_page`; `FakeServer.cs` l.46–48 -- variants: add `contract-ok`, listing exactly the embedded contract's tools with each one's `readOnlyHint` as `tool-contract.json` records it (11 true, 12 false), so the check passes only if `WriteTools` covers every non-read tool. `Options.cs` l.27, l.69, l.134–145 -- accept `contract-ok`, and add a `--contract-only` flag; `Program.cs` l.91–102 -- with `--contract-only`, return `Exit.Ok` right after the startup check passes, before `ProcessProbe` (l.102), so a fake run can never reach OneNote. The harness `README.md` l.76 documents both beside `contract-no-readonly`.
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` l.266–318 -- doc guards already read `references/*.md`; no change expected.
- Prior plans for facts: 5.3 l.184–188, 5.4 l.210–220, 5.5 l.88, l.130, l.213, 5.6 l.225–241; spike part 2 findings l.15, l.85–96, l.100; spine AD-7 l.245–248, AD-8 l.270, l.279.

## Tasks & Acceptance

**Execution:**
- [ ] `plugin/skills/guide/references/hierarchy-writes.md` -- new: before any hierarchy write, recipes 1–5, when a hierarchy write fails, what this release can't do.
- [ ] `plugin/skills/guide/SKILL.md` -- description, title, When to use, "Writing" overview naming both references, l.54 dotted-group advice, error and warning rows, `outcomeUnknown`.
- [ ] `plugin/skills/guide/references/page-writes.md` -- the stale lines in the Code Map.
- [ ] `README.md` -- per decision 2 and Boundaries; l.39 dotted-group advice.
- [ ] `src/OneNoteMcp.Core/SectionFileNames.cs`, the three tool descriptions, `CreateHierarchyToolTests.cs` comment, `tool-contract.json` -- decisions 3 and 4.
- [ ] `spikes/stress-harness/{Calls,Program,FakeServer,README}` -- the seven tools, the count, the `contract-ok` variant, the README lines.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- a 5.7 entry: the live checks pending, the create-then-rename dotted-group path unproven until its live row passes, the group-rename read-grant claim unconfirmed.

**Acceptance Criteria:**
- Given the plugin packed from this branch, when `claude plugin validate --strict dist/stage/onenote` runs, then it passes with only the existing missing-icon notice, and the stage holds both reference files.
- Given `SKILL.md`, both references and `README.md`, when searched for "planned", "yet", "can't", "only page", then only moves, notebook deletes, image and ink deletes, OneDrive notebook creation and exports remain unsupported.
- Given Phill's CLI write and permanent-delete grants on MCP Test, when an agent given only the README is asked to permanently delete a throwaway section, then it reads first, passes the basis, reports the backup file and page entries, and never runs the grant command.

## Implementation Notes

**Build constraints (unattended build lane, 2026-11-15):**
- Never touch OneNote: no integration tests, no running `OneNoteMcp.exe` against OneNote, no harness run without `--contract-only`, no MCP OneNote tool calls.
- Never read, list or write `%APPDATA%\OneNoteMcp` or `%LOCALAPPDATA%\OneNoteMcp`. A hook blocks any shell command whose text holds the CLI's name followed by "grant": write docs with the file tools, never through the shell.
- Don't edit settings, `CLAUDE.md`, `tickets.toml` or the spine. Don't commit; the build session commits.

**Coordinator addition (2026-11-15, during the build).** Story 2.13 (PR #51, unmerged) rewords the code's `noBasis` warning message to "checked against its current version (read internally; nothing of it is returned)". The widened `SKILL.md` `noBasis` row says it that way, not "checked against the page as the server read it just then", so it doesn't read as a way around the read grant. The warning's code text stays 2.13's. Done in `SKILL.md`'s `noBasis` row and `references/page-writes.md` (The basis).

**What the build found.** `tool-contract.json` needed no change: the snapshot strips descriptions (`ToolContractTests.StripDescriptions`), so decision 3's two description fixes don't move it, and `Tools_MatchTheContractSnapshot` passes unchanged. The harness's `ContractTools()` now reads each tool's `readOnlyHint` from the embedded contract for `contract-ok`, and the temporary-settings cleanup became `DeleteSettingsDir`, also run on the check's early exits.

**Matrix test audit.** The I/O matrix describes what an agent does when it follows the skill, so, as in 2.12, it is the live step's script (hitl, Phill's sitting); no unit test can cover its rows, and the build ran none. What the build can check ran: the doc guards in `PluginPackagingTests` (every contract tool named in `SKILL.md` or `references/*.md`, none other; README names every tool; purge phrases) and the harness's fake contract runs.

**Live check (2026-11-15 sitting, Phill).** All manual checks passed: the plugin session over every matrix row, the dotted group ("v12" renamed to "v1.2"; it survived a sync and a OneNote restart), the README-only permanent delete under the CLI grant (no dialog, backup and page entries reported, grant command never run), the `permanentDeleteNotebook` row in `list_grants`, and the stress harness on MCP Test (719 comparisons, 0 mismatches). The group rename moved the protected section's live path at once, so its read grant ends at the rename. Phill's follow-ups, applied here: recipe 5 asks in the chat and waits for a yes (the session skipped it); the basis section says to re-read a section just before deleting it after a page write (one `StaleWrite` in both sessions); recipe 1 says the `.` survives a rename; README and recipe 2 say a group rename ends the read grant. Filed in `backlog/`: the `z` story (2.13 probe) and the "cleaning up" bug. Details in `deferred-work.md`.

## Plan Change Log

## Review Triage Log

### 2026-11-15 — Review pass (quick)
- verdicts: 6 findings — high 0, medium 1, low 5, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `SKILL.md` "In short, for the hierarchy" told Claude to pass "a page's `meta.basis`" for a page delete, which holds `elementHashes` that both delete tools refuse (`DeleteHierarchyObjectTool` l.44, `PermanentlyDeleteHierarchyObjectTool` l.30) — now `{lastModifiedTime, pageHash}` from `meta.basis`, never `elementHashes`.
  - `[low]` `[patch]` `SKILL.md` `NotSupported` row said permanently deleting "something already in the recycle bin" is refused, and the `ObjectNotFound` row put everything in the bin under `ObjectNotFound`; for a permanent delete a recycled page and Deleted Pages are `NotSupported` (`PermanentDeleteUseCase` l.572–578, l.611–619), only the bin itself is `ObjectNotFound`, and a recycled section is allowed — both rows now say so.
  - `[low]` `[patch]` README How it works said creates follow the basis-and-journal steps, and `SKILL.md` said to pass "each write" a basis; creates take neither (README Recovery journal, recipe 1) — README now gives creates their own sentence; `SKILL.md` says "each rename, reorder or delete" and that creates take no basis.
  - `[low]` `[patch]` `SKILL.md` intro l.8 still said `list_grants` lists only read and write permissions — now read, write and permanent-delete.
  - `[low]` `[patch]` harness README claimed `contract-ok` "can never reach OneNote", but nothing enforces `--contract-only` with it (a `contract-ok` run given a notebook and `--out` proceeds as a normal OneNote-reading run, which the operator asked for) — the README now states it isn't enforced; no guard added (low, and a guard is a new branch rather than a direct correction).
  - `[low]` `[patch]` `--contract-only` refusal and README said it takes "only --exe and --exe-arg" though `Options` refuses only notebook, `--out`, diag and probe modes and ignores the rest — message and README now say exactly that.

## Design Notes

**Why recipes say "confirm in chat" for permanent delete.** The server's dialog is the gate (AD-6, AD-7); the chat confirmation is courtesy, so a client without dialogs still gets a clear refusal rather than a surprise. No gate lives only in the skill (spine l.128).

**Unconfirmed claim.** After a group rename OneNote renamed the group's folder on disk lazily (5.4 l.210), and a read grant is keyed by the section's file path, so a protected section inside a renamed group may keep its grant until the folder moves. The docs say the grant ends "once OneNote renames the group's folder"; the live step checks it, and the wording is tightened (or a code fix logged) from what it shows.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, `PluginPackagingTests` and the contract test included.
- `dotnet build spikes/stress-harness -c Release`, then the harness with `--contract-only --exe <StressHarness.exe> --exe-arg --fake-server --exe-arg 100 --exe-arg --fake-variant --exe-arg <variant>` -- expected: `contract-ok` exits 0; `contract-no-readonly` exits 3; `contract-ok` with one hierarchy tool removed from `WriteTools` exits 3 (checked once, then restored). Always with `--contract-only`.
- `pwsh -File scripts/pack-plugin.ps1`, then `claude plugin validate --strict dist/stage/onenote` -- expected: passes.
- Description length -- expected: at most 1,024 characters.
- Staged-diff grep for real notebook or employer names -- expected: empty.

**Manual checks (hitl, Phill's sitting; the build lane runs none of these):**
- Phill, in his own terminal: the CLI permanent-delete grant on MCP Test (the write grant is already there); `list_grants` shows kind `permanentDeleteNotebook`.
- Per decision 6: a fresh plugin session (`claude --plugin-dir dist/onenote-<ver>-win-x64.zip`) runs the matrix rows on throwaway nodes in MCP Test; a README-only session permanently deletes a throwaway section holding one page, and the agent reports `meta.backupFile` and `meta.pageEntryIds`; Phill revokes the permanent-delete grant afterwards if he wants.
- Dotted group (decision 4): ask for a section group named with a `.`; the agent creates it without the `.`, asks, renames it with `rename_hierarchy_object`; a re-read and OneNote's own view show the dotted name, and it survives a sync and a OneNote restart.
- Group-rename read grant: grant a read of a protected section inside a throwaway group in MCP Test, rename the group, then read the section: note whether the grant holds, and whether it ends once the folder is renamed on disk.
- The stress harness against the real server, with OneNote running (its startup contract check passes).
