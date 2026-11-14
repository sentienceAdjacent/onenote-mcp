---
title: 'Skill and README for page writes'
type: 'feature'
ticket: '12'
created: '2026-11-14'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The four page-write tools are built (2.4, 2.9–2.11), but the guide skill still reads as a read-only release: its description says "this release can only add empty pages", two lines say writes don't exist so `meta.basis` can be ignored, it has tool rows but no write recipes, its error table lacks `StaleWrite` and `JournalFailed`, its `WriteNotGranted` and `outcomeUnknown` text names only `create_page`, and its warning table has none of the write warnings. The README still marks writing, stale-edit checks and the journal "(planned)", files its write-grant bullets under "Password-protected sections", and has no section on the journal, DPAPI or the limits of writes.

**Approach:** Documentation only, describing what this branch (71ae343) does, checked against the spine (AD-6, AD-7, AD-8, AD-9, AD-17, AD-18, AD-21). The skill gets page-write recipes, complete error and warning rows, and a description and intro that cover writes, in the structure Q2 picks. The README gets sections on grants (read and write), safety limits and the recovery journal, in the layout Q4 picks. Every deferred entry routed to 2.12 is closed (Design Notes). Page writes only, per Q1.

## Boundaries & Constraints

**Always:**
- Describe what the code does. Where code and spine differ, follow the code and add a spine-sync line to `deferred-work.md` (PR #43 is the open spine sync). Where a tool description is wrong, don't change it here: add a deferred entry.
- Safety rules stay in the body of `SKILL.md`, whatever Q2 picks: write only when the user asks; never run the grant CLI or edit `settings.json`; on `outcomeUnknown` re-read before anything else and never retry a write blindly; never work around a missing tool (no `.one` edits, no UI scripting).
- Skill recipes: (1) change text on a page (read, send each changed Outline whole with every OE and its `objectID`, `removeObjectIds` for deletions, `basis` = the read's `meta.basis` unchanged, then chain the returned `meta.basis`; `StaleWrite` → re-read, redo, ask the user if their change conflicts); (2) add content (an Outline or OE with no `objectID`); (3) remove a block (`delete_page_content`) versus a paragraph (`removeObjectIds`), and that images and ink can't be deleted; (4) add an image or attachment (Q5); (5) write in a password-protected section (write grant alone; without a read grant only new content, no basis, `noBasis` + `readBackWithheld`; `delete_page_content` needs the read grant, so read first); (6) undo with `restore_from_journal` (`meta.journalEntryId` or the ID a failure message names; a new page, the original stays; where it lands; `binaryMissing`; 30 days; no listing tool, so an ID lost with the session can't be looked up); (7) a new page, then its content (`create_page`, then `update_page_content` with no basis, `noBasis` expected).
- Skill text also covers: the HTML rule by pointing to the tool description rather than copying its allowlist; `one:HTMLBlock` refused; no DOCTYPE; write retries differ from reads (`ComBusy` before the call is retryable; a timeout at the call is `outcomeUnknown`); writes from several sessions queue on the machine-wide lock (AD-18); a caveat that a write to a page the user is editing in OneNote can disturb it (spike 2.2 "Not probed", OneMore lessons row 12: equations under a live selection); OneNote leaves a `%TEMP%\{GUID}.bin` copy per read of a page with an attachment.
- README: Safety model rows "Writing" and "Stale edits" become built (the stale check is best effort: on a page that isn't displayed only the content hashes catch a change, AD-17); "Permanent delete" stays planned (epic 5). The journal: what is saved and when, `journal.path`, `journal.retentionDays` (30), `journal.maxSizeMB` (2048), oldest pruned first, DPAPI for protected sections and its limit (any process running as the user can read an entry for its 30 days, AD-8), restore's behaviour, and that attachments come back only while OneNote's temp copy exists. Fix every stale line: status, `get_page_content` and `list_grants` rows, the "(planned) Writes" line (only rename, move, section and page deletes, and exports stay planned), the grants limit "(read grants now, write grants later)", the planned journal line, Uninstall's "Once writes exist", Files it uses, and How it works (write lock).
- `PluginPackagingTests` stay green: the skill names only contract tools by full prefixed name, the README names every contract tool in backticks, and the purge guards' phrases stay.
- The skill description stays at or under 1,024 characters (the Agent Skills limit; `claude plugin validate --strict` doesn't check it, measured on a 1,134-character copy).
- Placeholders only in examples ("notebook A", "MCP Test"); no real notebook, employer or person names.

**Never:** code or tool-description changes; epic 5's tools (Q1); spine edits; a `<Version>` bump or release (inception decision 5); the open spine decisions on `noBasis` wording and `create_page` returning `lastModifiedTime` (the skill describes today's behaviour); touching OneNote, settings or the grant CLI in the build; editing `tickets.toml`.

## I/O & Edge-Case Matrix

What an agent does when it follows the skill. These rows are the live check's script (Verification).

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Edit | "add the line X to page P", notebook granted | `get_page_content`, then `update_page_content` with `basis`; reports success and that it can be undone | `StaleWrite`: re-read, redo, tell the user |
| Undo | "undo that" | `restore_from_journal` with the edit's `meta.journalEntryId`; says it is a new page and the edited one stays | `ObjectNotFound`: relay `userAction` |
| Remove a block | "delete the second block" | `delete_page_content` with the Outline's `objectID` | an OE: `removeObjectIds` instead |
| Ungranted | any write | relays the dialog outcome or `WriteNotGranted` with its command | never runs the command |
| Unsupported | "rename this section", "move this page" | says this release can't; suggests doing it in OneNote | no workaround |
| Image | "add C:\pics\a.png to page P" | per Q5 | decline: `WriteNotGranted`, stop |
| Ambiguous failure | `outcomeUnknown: true` | re-reads the page first | no blind retry |
| Protected, no read grant | "add a note to page P" | a new Outline only, no basis; mentions `noBasis`, `readBackWithheld` | an edit of existing content: `ReadNotGranted`, ask about reading |

</frozen-after-approval>

## Open Questions

1. **Epic 5's tools.** Epic 5's create, rename, reorder, soft-delete and permanent-delete tools (PRs #44–#47, plus #48 on top) are unmerged drafts, stacked apart from this branch. They already add tool rows and intro text to the same skill and README lines. Epic 5 has its own docs ticket, entry 7 ("Skill, README and harness for hierarchy writes", hitl). On this branch, `GuideSkill_NamesOnlyContractTools` fails if the skill names a tool not in this branch's contract.
   - (a) Page writes only. 2.12 merges first in epic 2's lane, the epic 5 branches rebase onto it (their rows and sentences go into 2.12's structure), and 5.7 writes their recipes.
   - (b) 2.12 waits until epic 5 merges, then covers both.
   - (c) 2.12 also writes epic 5's recipes now, on a branch stacked on 5.6.
   Recommended: (a). It matches the ticket and both epics' boundaries, keeps the contract test honest, and avoids documenting behaviour epic 5's live sittings may still change.
2. **Skill structure.** `SKILL.md` is about 5,350 words, and all of it loads on every OneNote request, reads included. The write recipes, grants, journal and new table rows add an estimated 1,500–2,000 more.
   - (a) One file: add a "Writing pages" section to `SKILL.md`.
   - (b) `SKILL.md` keeps the rules, tool map, error and warning rows and a short write overview; the recipes and details go to `plugin/skills/guide/references/page-writes.md`, which the skill says to read before any write. `PluginPackagingTests`' named-tool checks widen to `references/`.
   - (c) A second skill just for writes, with its own description.
   Recommended: (b). Reads stop paying for write detail, the safety rules stay in the file that always loads, and 5.7 can add a `hierarchy-writes.md` the same way. (c) would repeat the grant and error text in two skills.
3. **The skill's description.** Today it tells Claude that edits aren't possible ("this release can only add empty pages"), so a request like "fix the typo on my page" meets a skill that says no.
   - (a) Rewrite: add write triggers ("add a line to that page", "fix the text on my page", "delete that block", "undo the change you made", "bring back the earlier version"), and say move, rename and section or page deletes aren't supported yet. At most 1,024 characters.
   - (b) Only drop the stale clause.
   Recommended: (a). The description is what makes Claude load the skill for a write request at all.
4. **README layout.** The write-grant bullets sit under the "Password-protected sections" paragraph, and nothing covers the journal. The epic asks for sections on grants, safety limits and the journal.
   - (a) Restructure: tool tables stay in "What it does"; new top-level sections "Grants" (read and write, CLI, revoke, `list_grants`, where the exe is, limits) and "Recovery journal"; "Safety model" and "Limits" updated. Existing anchors (`#uninstall`) keep working.
   - (b) Add only: keep today's text, and add a "Writing pages" section after "Password-protected sections".
   Recommended: (a). It is what the epic's done-when names, and a reader looking for write grants won't find them under protected sections.
5. **How the skill tells Claude to add an image.** The tool description says images go as inline `one:Data` (base64), and a new file path raises an accept/decline dialog. A typical photo is hundreds of KB, which is hundreds of thousands of base64 characters the model would have to write out. An image by `one:Image/one:File@path` hasn't been tried live (the sitting's dialog check used an `InsertedFile`).
   - (a) A file on disk goes by its full local path (the user confirms it in the dialog); inline `one:Data` only for small images already in hand as base64, such as one copied with `get_binary_page_content`, and on clients without dialogs.
   - (b) Always inline `one:Data`, as the tool description says.
   - (c) Always a file path.
   Recommended: (a). It is the only way a normal-sized image fits in a tool call, and the dialog keeps the user in charge. The live check adds an image by path to confirm OneNote takes it.
6. **The `z` re-stack item** (joint sitting C3, unassigned). Resending an Outline's `one:Position z` re-stacks the Outlines, so another Outline's hash changes and an older basis for it gets `StaleWrite`.
   - (a) The skill says to always use the `meta.basis` from the latest read or write, and the root fix (drop `z` from sent Positions, or from the hash) goes to the refactor sweep, 2.13.
   - (b) The skill also says to leave `one:Position` out of a resent Outline. Not tested live.
   - (c) Leave it all to a code fix.
   Recommended: (a). Chaining the newest basis is always right and needs no untested claim; the code fix belongs with code.
7. **The human part (hitl).** The ticket's check is "after Phill grants MCP Test through the CLI, an agent following the README makes an edit and restores it". Claude Code users get the skill, not the README.
   - (a) One Claude Code session with the plugin packed from this branch: Phill runs `grant --write` for MCP Test in his own terminal, then asks for the matrix rows on a throwaway page in MCP Test.
   - (b) As (a), plus a short session with the server registered without the plugin (`--mcp-config`), where the agent is given only the README and asked for an edit and an undo.
   - (c) The README-only session alone, as the ticket words it.
   Recommended: (b). It tests what plugin users get and what the ticket names; the README-only run takes a few minutes.
8. **Plan size.** This plan is about 4,500 tokens, and about 3,000 once the Open Questions are answered and removed: over the 1,600 guide either way, mostly the Boundaries' recipe list and the Code Map's line references.
   - (a) Keep it whole: one deliverable, and the skill and README must agree.
   - (b) Split the README into its own story.
   Recommended: (a). The excess is reference detail the build needs, not a second goal, and a split would make two stories describe the same tools twice.
9. **Approve the plan** with these answers, so the build can start: Approve / Change.

## Code Map

- `plugin/skills/guide/SKILL.md` -- l.3 description; l.6 title ("reading notebooks"); l.8 intro; l.14 "any OneNote read"; l.16–18 write guidance; l.38–41 write tool rows (keep; move detail to recipes); l.74 and l.119 "writes don't exist… ignore it"; l.115 attachments "hasn't been tested" (2.9's sitting B tested them); l.190 retry rules (reads only); l.193–213 errors (`WriteNotGranted` l.209, `InvalidXml` l.211 "of a read", no `StaleWrite` or `JournalFailed`); l.215 `outcomeUnknown` (create_page only); l.217–225 warnings (add `noBasis`, `partialBasis`, `readBackDiffers`, `imageLost`, `readBackFailed`, `readBackWithheld`, `binaryMissing`, `restoredElsewhere`, plus the missing read-side `resultNotIndexed` and `settingsInvalid`).
- `src/OneNoteMcp.Core/ErrorCode.cs`, `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- the codes, read only. `FileExists`, `PermanentDeleteNotGranted`, `ExportPathNotAllowed`, `BackupFailed` are epics 3 and 5.
- `src/OneNoteMcp.Server/Tools/{CreatePage,UpdatePageContent,DeletePageContent,RestoreFromJournal}Tool.cs` -- the descriptions are the contract; recipes point to them, not copy them.
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs` l.29–36 -- `journal` `{path, retentionDays: 30, maxSizeMB: 2048}`.
- `README.md` -- l.5 status; l.15 `get_page_content` ("basis for safe edits later"); l.22 `list_grants` (protected sections only); l.41–53 grants (write bullets l.46–48); l.59 "(planned) Writes"; l.68–78 Safety model; l.80–86 Limits (l.85, l.86); l.180 Uninstall; l.217–224 Files it uses; l.226–236 How it works.
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` l.244–316 -- the doc guards; widen the named-tool regex to `references/` only under Q2 (b).
- `origin/story/5.3`–`5.6`, `origin/fix/dialog-name-hygiene` -- already edit the skill's description, intro and rows and the README's status and tables: the second to merge rebases.

## Tasks & Acceptance

**Execution:**
- [ ] `plugin/skills/guide/SKILL.md` (and `references/page-writes.md` under Q2 (b)) -- description (Q3), intro, When to use, recipes, grants, journal and restore, errors and warnings, stale lines.
- [ ] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- only under Q2 (b): the named-tool checks read `references/*.md` too.
- [ ] `README.md` -- per Q4 and Boundaries.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- mark the 2.12 entries resolved; add any description or spine mismatch found.

**Acceptance Criteria:**
- Given the plugin packed from this branch, when `claude plugin validate --strict dist/stage/onenote` runs, then it passes, with only the existing missing-icon notice.
- Given Phill's CLI write grant on MCP Test, when a fresh Claude Code session with that plugin is asked to add a line to a throwaway page and then undo it, then the agent reads first, passes `meta.basis`, and restores with `restore_from_journal`, without being told any tool name.
- Given `SKILL.md` and `README.md`, when searched for "planned", "read-only", "no writes" and "later", then only permanent delete, hierarchy writes, moves and exports remain planned.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

**Deferred entries routed here** (all in `deferred-work.md` unless named):
- l.310 (2.9): description still read-only; `WriteNotGranted` row names only `create_page`.
- l.322 (2.9): images as inline `one:Data`, the file-path dialog, the date-only rule without a read grant (`ReadNotGranted`, `readBackWithheld`).
- l.343 (2.10): `WriteNotGranted` and `outcomeUnknown` text name only `create_page`.
- l.382 (joint sitting): OneNote's `%TEMP%\{GUID}.bin` copy per read, "worth a line in the guide skill"; this plan also puts it in README Limits, since restore depends on those copies (2.11 Q2).
- l.379 (joint sitting): the `z` re-stack (Q6).
- Spike 2.2 findings, "Not probed": the equation-selection caveat "the skill gets".
- Plans 2.4, 2.8, 2.9, 2.10, 2.11 (Never lists): recipes and the README journal sections are 2.12's.
- Epic done-when 5 and ticket 12: recipes; README grants, limits, journal with DPAPI.

Not for 2.12 (spine or code): the `noBasis` wording without a read grant (l.376); `create_page` returning `lastModifiedTime` (l.373); the journal listing tool (l.412).

**Why point to tool descriptions.** The HTML allowlist and path rules already sit in `update_page_content`'s description, which the client always shows. Copying them into the skill makes two texts to keep in step; the skill says when they matter and what to do on refusal.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, `PluginPackagingTests` included.
- `pwsh -File scripts/pack-plugin.ps1`, then `claude plugin validate --strict dist/stage/onenote` -- expected: passes.
- The description's length -- expected: at most 1,024 characters.

**Manual checks (hitl, Phill):**
- Phill runs the `grant --write` command for MCP Test in his own terminal (the agent never does).
- A fresh Claude Code session with `claude --plugin-dir dist/onenote-<ver>-win-x64.zip`, on a throwaway page in MCP Test: the I/O matrix rows (edit, undo, remove a block, an image by path with accept and decline, an unsupported rename, the protected-section row in MCP Test's protected section). The agent should choose the tools from the skill alone.
- Under Q7 (b): a session with the server registered without the plugin, given only the README: an edit and an undo.
- Phill reads the README's new sections.
