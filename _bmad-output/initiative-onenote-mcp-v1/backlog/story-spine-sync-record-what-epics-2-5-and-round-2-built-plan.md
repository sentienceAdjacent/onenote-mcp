---
title: 'Spine sync: record what epics 2, 5 and round 2 built'
type: 'chore'
ticket: '22'
created: '2026-11-27'
status: 'built'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
baseline_revision: 'ca2aa5917f837015a83d9557bb4c7f9d0758df59'
context: []
---

<frozen-after-approval reason="human-owned intent, do not modify unless human renegotiates">

## Intent

**Problem:** The spine lags the code in the 33 group-A items of `deferred-work-triage-2026-11-27.md` plus D160's leftover, and contradicts it on reorder lists (D134) and declined image paths (D171). Several Deferred rows still call questions open that the 2026-11-13 to 11-15 sittings settled.

**Approach:** Amend the spine and its `.memlog.md` (one dated line per decision recorded) so each item is written into its AD or its Deferred row is marked settled with the sitting that settled it, after checking every claim against `src/` and the sitting and plan evidence. No code, test or contract change; the spine's status stays as it is. A disagreement between the code and a recorded decision that the triage didn't list goes to Phill as a question, not into the spine.

</frozen-after-approval>

## Tasks & Acceptance

**Execution:**
- [x] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- write each item into its AD, or settle its Deferred row -- the spine is the binding design
- [x] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md` -- one dated line per item (D142, D143 and D146 share one) -- the memlog keeps the why

**Acceptance Criteria:**
- Given the triage's group A, when the spine is read, then every item appears in the table below with its AD and line, or its settled Deferred row.
- Given AD-7, when it is read, then it no longer says a reorder may list only some children, or that a declined path offers sending the image inline.

## Item table

Line numbers are in the amended spine. "Deferred row settled" names a row whose second cell now starts "Settled (<sitting>, <date>, <steps>)".

| Item | Where it went (spine) | Evidence |
|---|---|---|
| D90 | AD-7 PageWrite read-back l.248; AD-7 PageWrite l.229; AD-17 l.733 | src/OneNoteMcp.Xml/PageWriteCheck.cs:142-146,454-484; src/OneNoteMcp.Core/Writes/PageWrite.cs:267-270,299-333,433-434; 2.9 plan l.116,119 |
| D94 | AD-7 file paths l.238 | src/OneNoteMcp.Core/Writes/FilePathGate.cs:58,120-140 (no GetLongPathName in src) |
| D114 | AD-17 l.702; Deferred row settled l.1028 | epic-safe-writes/story-delete-page-content-plan.md:149; deferred-work.md:389-391 |
| D116 | AD-8 l.439; Deferred row settled l.1049 | src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs:342-355,394-396; epic-safe-writes/story-restore-from-journal-plan.md:148 |
| D122 | AD-8 l.436; AD-8 l.435; AD-7 Deferred-linked text l.1034; Deferred row settled l.1050 | story-restore-from-journal-plan.md:148-149; RestoreFromJournalUseCase.cs:374-385 |
| D124 | AD-7 Create l.263; AD-7 Create l.264; AD-7 Create l.266; AD-9 InvalidArgument l.539 | src/OneNoteMcp.Core/SectionFileNames.cs:25,79-133,159-203; UseCases/CreateHierarchyUseCase.cs:287,296-299,324-339; ComGateway.Writes.cs:137; 5.3 plan l.75,77,84,86,184-187 |
| D125 | AD-7 Create l.267; AD-7 class table l.400; AD-9 FileExists l.541 | CreateHierarchyUseCase.cs:120-260,393-415; NotebookFolders.cs:50-192; ElicitationNotebookLocationPrompt.cs:26-50; 5.3 plan l.180 |
| D126 | AD-6 l.176; AD-7 Create step 7 l.219; AD-7 class table l.400 | CreateHierarchyUseCase.cs:386,439-482; 5.3 plan l.182,200 |
| D127 | AD-9 meta l.485; AD-9 Cancelled l.532; AD-9 NotSupported l.548 | CreateHierarchyUseCase.cs:109,135-173,448-478; 5.3 plan l.84,89,90,171 |
| D133 | AD-7 HierarchyWrite Order l.273; AD-16 l.690 | HierarchyWrite.cs:176-177,690-694; HierarchyWriteSteps.cs:19-27; MoveContainerWrite.cs:487; tests HierarchyUpdateXmlTests.cs:258 |
| D134 | AD-7 HierarchyWrite l.272; AD-7 class table l.401; AD-9 InvalidArgument l.539 | HierarchyWrite.cs:612-631,666-742; HierarchyUpdate.cs:39-43; 5.4 plan l.210 (L6) |
| D135 | AD-17 l.721; AD-9 l.482; AD-7 read-back l.275 | HierarchyBasis.cs:19-90; HierarchyWrite.cs:95-101,169-237,533,565-571,744-768; UpdateHierarchyUseCase.cs:42-45 |
| D138 | AD-7 HierarchyWrite l.279; AD-21 l.872; AD-9 l.548 | HierarchyWrite.cs:381-395,425-482,556-560; RenamedLocations.cs:20-58; ReadAccess.cs:252-265 |
| D139 | AD-7 Order l.273; Deferred row settled l.1021 | epic-hierarchy-writes-and-deletes/story-rename-and-reorder-update-hierarchy-plan.md:210 (L3); UpdateHierarchyIntegrationTests.cs:132-140 |
| D141 | AD-7 SoftDelete l.321 | src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs:16-17,42-43,104-123 |
| D142 | AD-7 SoftDelete l.314; AD-7 class table l.402 | SoftDeleteUseCase.cs:49-51,221-230; 5.5 plan l.85,213 (L8) |
| D143 | AD-7 SoftDelete l.313 | SoftDeleteUseCase.cs:155-161,217-218,261-301; RecycleBin.cs:45-58; JournalStore.cs:226,239; 5.5 plan l.213 (L9) |
| D144 | AD-8 l.425 | RestoreFromJournalUseCase.cs:200-212; 5.5 plan l.87 |
| D145 | AD-9 l.482 | SoftDeleteUseCase.cs:133-143; ToolMeta.cs:56 |
| D146 | AD-7 SoftDelete l.319; AD-7 SoftDelete l.315; AD-9 SectionLocked l.534 | SoftDeleteUseCase.cs:238-245,377-396; 5.5 plan l.213 (L7) |
| D148 | AD-7 SoftDelete l.317; AD-7 SoftDelete l.320; AD-17 l.707; AD-21 l.875; Deferred row settled (protected page) l.1003; Deferred row settled (displayed page date) l.1022; Deferred row settled (empty/nested group) l.1023 | epic-hierarchy-writes-and-deletes/story-soft-delete-plan.md:213 (L3-L6); SoftDeleteUseCase.cs:194-206 |
| D149 | AD-7 PermanentDelete l.323; AD-17 l.741; AD-21 l.874 | src/OneNoteMcp.Core/UseCases/PermanentDeleteUseCase.cs:43-50,211-218,639-650 |
| D150 | AD-6 l.171; AD-6 l.177; AD-7 class table l.403 | SettingsWriter.cs:157-185; GrantCommand.cs:301-355; PermanentDeleteUseCase.cs:264-294; PermanentDeleteDialog.cs:73-86; DialogText.cs:58-59; ElicitationGrantPrompt.cs:284-286 |
| D151 | AD-6 l.175; AD-10 l.564 | SettingsWriter.cs:85-89,140-149,192-213,327-330; GrantCommand.cs:364-365 |
| D152 | AD-10 l.574; AD-10 l.575; AD-8 l.416; AD-7 PermanentDelete l.329 | PermanentDeleteUseCase.cs:446-461; SectionBackup.cs:113; JournalEntry.cs:29-32 |
| D153 | AD-7 PermanentDelete l.325; AD-7 class table l.403; AD-9 NotSupported l.548 | PermanentDeleteUseCase.cs:554-581 (RefuseUndeletable), 614-623 |
| D154 | AD-7 PermanentDelete l.326 | PermanentDeleteUseCase.cs:208-262 (AuthorizeAsync order, read this session), 673-682 |
| D155 | AD-7 PermanentDelete l.330; AD-7 ExternalWrite l.341; AD-18 l.752; AD-9 BackupFailed l.547 | PublishedFile.cs:23,30,46-52,101-106; SectionBackup.cs:85-123; ComGateway.cs:279; WritePipeline.cs:219-223 |
| D156 | AD-9 l.483 | PermanentDeleteUseCase.cs:54-61; ToolMeta.cs:18-21,55-58 |
| D157 | AD-6 l.178; AD-6 l.179; AD-7 PermanentDelete l.335; AD-7 PermanentDelete l.332 | PermanentDeleteUseCase.cs:220-279,476-520; DialogText.cs:12,25-55; ElicitationGrantPrompt.cs:288-327; ListGrantsUseCase.cs:31,87-98 |
| D160 | AD-7 PermanentDelete l.334; AD-7 PermanentDelete l.331; Deferred row (group backup) l.1001; Deferred row settled (stale date) l.1015 | epic-hierarchy-writes-and-deletes/story-permanent-delete-plan.md:241 (P1, P2, P9); PermanentDeleteUseCase.cs:602-611 |
| D171 | AD-7 file paths l.240; AD-7 file paths l.241 | src/OneNoteMcp.Core/Access/FilePathDialog.cs:17-24,57-60; plugin/skills/guide/references/page-writes.md:78-87; 2.12 plan l.60,66 |
| D175 | AD-4 l.138; AD-7 PageWrite l.246 | src/OneNoteMcp.Core/Writes/PageWrite.cs:568-582,679,687; src/OneNoteMcp.Xml/SentPositions.cs:16; backlog 2 plan |
| D181 | AD-7 ExternalWrite row l.404 | src/OneNoteMcp.Core/UseCases/PublishUseCase.cs:73-74,422-437; .memlog.md:321 |

Totals: 34 items (33 group-A items and D160's leftover). 28 written into ADs only; 6 also have a Deferred row settled (D114, D116, D122, D139, D148, D160). None skipped.

## Implementation Notes

Oneshot: a documentation-only change (about 80 spine lines), verified item by item. Five read-only subagents checked the items against `src/` and the plans in groups (page writes and restore; creates; rename and reorder; soft delete; permanent delete); the main session spot-checked the permanent-delete check order (`PermanentDeleteUseCase.cs:208-262, 540-650`), the name cap, the confirmation field, `FileShare.None`, the 250 ms poll, `WithoutZ` and the sitting lines (2.10 l.149, 5.4 l.210, 5.5 l.213, 5.6 l.241, 2.11 l.148) before writing.

Assumptions (run unattended):
- Memlog lines use the `(decision)` tag with the original decider and date quoted, since they record decisions already made rather than new ones; each ends "Wording only; no design change."
- The 2026-11-14 sitting is named as "the 2026-11-14 sitting, story 2.10 steps D4c and D4d", since the plan doesn't give it a story name.
- D139's row says what L3 showed and adds, marked inferred, that the full reorder put the groups back in their first order, so a kept change to the groups' own order hasn't been seen; no new probe is scheduled.
- The empty/nested group Deferred row is settled for both halves (5.5 L5, L6 and 5.6 P2) in one rewrite.
- The permanent-delete check order includes the loose-section refusal (D182, `RefuseLooseAsync`), which the code runs for a page before the read-grant check.
- Spine frontmatter (`status: final`, `updated: 2026-11-27`) left as it was.

Question for Phill (not written into the spine): AD-7 `Create` (spine "At an open notebook's path OneNote returns that notebook's ID, which is `AppState`") and l.223's class rule say a notebook create at an open notebook's path is `AppState` with no grant (memlog 2026-11-03), but `CreateHierarchyUseCase.cs:393-396` refuses an open notebook's own folder with `FileExists` before COM.

## Review Triage Log

Quick review, pass 1 (8 findings: 0 high, 3 medium, 2 low patched, 1 deferred to Phill, 2 rejected):
- medium, patched: AD-7 `PermanentDelete` check order said step 4 repeats every check; `ReadAsync` (PermanentDeleteUseCase.cs:299-339) repeats all but the non-page `pageHash` and loose-section checks. Wording now says so.
- medium, patched: AD-9 `Cancelled` row said "before any lock or COM call", but the default folder is read over COM (`GetSpecialLocationAsync`, CreateHierarchyUseCase.cs:153-157) before the dialog; now "before the lock and before any write", and the retry column names the create_notebook exception.
- medium, patched: AD-4 "the one change the server makes to XML it sends" ignored AD-8 restore's rewrites; now "to a caller's XML".
- low, patched: D116/D122 rows and the D116 memlog line cited a "Live verification" section the 2.11 plan lacks; now "Plan Change Log" (l.148-149).
- low, patched: "`delete` for an untitled page" narrowed the code's rule (any empty shown name, PermanentDeleteUseCase.cs:288).
- maybe-false, not patched: the AD-7 `Create` notebook bullet still ends "At an open notebook's path ... `AppState`" while CreateHierarchyUseCase.cs:393-396 refuses an open notebook's own folder with `FileExists`. An unlisted code/decision disagreement, so it is the question for Phill above, not a spine edit.
- low, patched: the BMAD template's frozen-block tag carried an em dash; replaced with a comma.
- false: the corrupted hunk was in the reviewer's temp diff file only (git's stderr mixed into it); the spine is intact.

## Verification

**Manual checks (if no CLI):**
- `git diff` touches only the spine, its memlog and this plan; no em dash added; staged diff grep for the banned names finds nothing.
