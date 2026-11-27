---
title: Triage of deferred-work.md
date: 2026-11-27
---

# Triage of deferred-work.md (2026-11-27)

All 192 items were checked against main at 7420d6e by four read-only agents. The main session spot-checked a sample and corrected one verdict (D159).

| Verdict | Count |
|---|---|
| fixed | 120 |
| decided | 23 |
| open | 46 |
| obsolete | 1 |
| unclear | 2 |

## Proposed groups (open and unclear items)

### A. Spine sync: record what epics 2, 5 and round 2 built (chore (spine; Fable review), 33 items)

| Id | Severity | Kind | What is still wrong | Evidence |
|---|---|---|---|---|
| D90 | low | docs | Read-back implementation choices (numeric/boolean comparison, WrongElement, non-date basis InvalidArgument, null elementHashes as partialBasis) are still not recorded in the spine. | src/OneNoteMcp.Xml/PageWriteCheck.cs:461 (SameValue), :145 (WrongElement); no spine match for these rules |
| D94 | low | design-question | File-path canonicalisation does not expand 8.3 short names and has a time-of-check gap between dialog and OneNote read; accepted for v1 but unrecorded in the spine. | src/OneNoteMcp.Core/Writes/FilePathGate.cs:58,128 (ResolveLinks; no GetLongPathName in src); spine:236 omits it |
| D114 | low | docs | The cloud date-basis deletes passed, but the spine Deferred row "DeletePageContent and dates" still says the MCP Test Cloud half is open. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:1014 |
| D116 | medium | docs | Phill kept the one empty-page retry after the probe (2026-11-13), but the spine still says it is not a rule and lists it as an open Deferred row. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:428,1035; epic-safe-writes/story-restore-from-journal-plan.md:148 |
| D122 | low | docs | RestoreAttachmentProbe ran (kept; temp path long form) on 2026-11-13, but the spine Deferred row still says unprobed; template deletion on a templated section is not recorded as checked. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:1036; epic-safe-writes/story-restore-from-journal-plan.md:148 |
| D124 | medium | docs | 5.3's shared name rule (all three kinds, .one added, recycle-bin names, kind clash) never reached the spine, which still says groups/notebooks are "story 5.3's call" and the tool may refuse a name without .one. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:260-263; code: src/OneNoteMcp.Core/SectionFileNames.cs:79; epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md l.120 (J7: other epic 5 spine-sync entries left for a separate chore, never done) |
| D125 | low | docs | create_notebook's location dialog and folder rules (exists, empty, not inside a notebook/OneNoteMcp folders, no UNC/relative, URL NotSupported) are absent from AD-7/AD-6. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:264 (only "checks folder empty"); src/OneNoteMcp.Core/UseCases/CreateHierarchyUseCase.cs:141-260; epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md l.120 (J7: other epic 5 spine-sync entries left for a separate chore, never done) |
| D126 | low | docs | The tightened auto-grant (key match, not open before, empty notebook) is not in AD-6/AD-7, which still say only "keyed by the path read back". | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:176,217,389; 5.3 plan security fixes 2026-11-13 |
| D127 | low | docs | meta.objectId/parentId/path for creates, NotSupported without elicitation and Cancelled on a declined dialog are not in the spine, and the build assumptions are not recorded as confirmed. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:217,263 (warnings only); epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md l.120 (J7: other epic 5 spine-sync entries left for a separate chore, never done) |
| D133 | medium | docs | AD-16 still says "Story 5.4 settles it… whether or not the XSD becomes a hard gate", and AD-7 says a group-before-section order is InvalidXml, whereas the code refuses it earlier with InvalidArgument and the XSD is a hard gate. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:270,677; HierarchyWrite.Validate |
| D134 | medium | docs | AD-7 still says reorder partial lists are allowed with no whole-list check, contradicting the built rule that every live child must be listed exactly once. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:269 vs src/OneNoteMcp.Core/Writes/HierarchyWrite.cs:576-682 |
| D135 | low | docs | Rename/reorder basis specifics (hash in basis is InvalidArgument, unchanged warning with no COM call, meta.objectId/journalEntryId, readBackDiffers scope) are not stated for these tools in AD-17/AD-9. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:704-708 (generic hierarchy basis only); epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md l.120 (J7: other epic 5 spine-sync entries left for a separate chore, never done) |
| D138 | low | docs | The rename half (refuse a rename that lands a section on a granted SectionKey; NotSupported when unpredictable) is built but AD-7 rename validation and AD-21 only allude to it ("as for a rename"). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:290,858; src/OneNoteMcp.Core/Writes/HierarchyWrite.cs:391,445,557 |
| D139 | medium | docs | The 5.4 sitting ran (2026-11-15), but the spine Deferred row still says groups-only reorder is unprobed, though L3 found OneNote doesn't keep a groups-only order (readBackDiffers). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:1007; epic-hierarchy-writes-and-deletes/story-rename-and-reorder-update-hierarchy-plan.md:210 |
| D141 | low | docs | AD-7 says a notebook delete is refused before COM but not that it happens in step 1 before any grant dialog from a pre-pipeline self read. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:315,326; src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs:17,111 |
| D142 | low | docs | The refusal of soft-deleting a page that has subpages (InvalidArgument with count) is built but absent from AD-7 SoftDelete. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:308-315; src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs:24,50 |
| D143 | medium | docs | AD-7 still says telling a deleted page apart from same-named pages "is story 5.5's to settle"; the built rule (ID diff narrowed by name, recycleBinId kept on rewrite failure) is not in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:309 |
| D144 | low | docs | AD-8 gives no soft-delete-specific userAction for NotSupported hierarchy-entry restores (where the section/group went). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:414 |
| D145 | low | docs | AD-9 still says journalEntryId is for page writes and does not list meta.recycleBinId for a delete's success meta. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:471 |
| D146 | low | docs | The SectionLocked refusal of a group with a locked section beneath, and the Internal refusal of a page without a usable date, are built but not in AD-7 SoftDelete. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:311-313; src/OneNoteMcp.Core/UseCases/SoftDeleteUseCase.cs:30 |
| D148 | medium | docs | The 5.5 sitting ran (L1–L12 passed, 2026-11-15) but the spine still marks its questions open: protected-page soft delete "Still open" (L4 showed the page vanishes, so the refusal stays), displayed page with hierarchy date "Not probed" (L3 passed), empty/nested group soft delete "Not run" (L5/L6 passed). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:311,314,989,1008,1009; epic-hierarchy-writes-and-deletes/story-soft-delete-plan.md:213 |
| D149 | low | docs | Spine AD-7 PermanentDelete does not yet state the basis shapes ({lastModifiedTime,pageHash} page / {lastModifiedTime} section), the up-front InvalidArgument for elementHashes or a pageHash on a non-page, or the subpage refusal with count; only the tool name is synced. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:316-325 (no basis/subpage rules), tool name at l.629, l.884; code PermanentDeleteUseCase.cs |
| D150 | medium | docs | Spine AD-6 "How a grant is created" still describes only the dialog/CLI/created routes for grants and says nothing of the permanent-delete grant being CLI-only or of the per-call typed-name confirmation that stores nothing; a reader of the spine could design a dialog that stores it. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:171, 173-178; code src/OneNoteMcp.Core/Access/PermanentDeleteDialog.cs:56, src/OneNoteMcp.Server/Cli/GrantCommand.cs:397 |
| D151 | low | docs | Spine AD-6/AD-10 do not record that revoking the write grant removes the whole entry (permanent-delete grant included), the separate --revoke --permanent-delete, or SaveWriteGrant never reviving a stale permanentDelete flag. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:175 ("grant --revoke removes a grant" only), l.547; code SettingsWriter.cs:192 |
| D152 | low | docs | Spine AD-10 still gives the entry shape as `xml / backupFile`, although a section permanent delete writes both plus per-page entries (meta.pageEntryIds) before Publish. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:561; code PermanentDeleteUseCase.cs:189, 454 |
| D153 | low | docs | Spine AD-7 PermanentDelete allows a recycled section but does not name the Deleted Pages refusal (NotSupported with empty-the-bin userAction) or a page in a recycled section. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:316-320 (recycle bin and recycled page only) |
| D154 | low | docs | Spine gives the check order (l.319) but not that notebook/protected refusals run in step 1 before any grant dialog, nor the step-1 ReadNotGranted for a hashed basis on an ungranted protected page before any prompt. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:319, 860 |
| D155 | low | docs | Spine AD-7 PermanentDelete backup rule (l.322) lacks the two-poll 250 ms stable-size check, the publishSeconds timeout, BackupFailed naming no path, and keeping the lock after a timed-out Publish (AD-18); l.330 already points back to it as if it said so. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:322 vs l.330 |
| D156 | low | docs | The 600 s deadline is synced, but AD-9 does not list meta.backupFile and meta.pageEntryIds on permanent-delete success. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:218 (deadline done); no meta.backupFile/pageEntryIds in spine; code ToolMeta.cs:21,57 |
| D157 | low | docs | Build-review rules (step-1 refusals of bin/Deleted Pages/non-empty group, no confirmation for an ungranted protected page, sanitized confirmation text, StaleWrite naming the onenoteBackup entry after a changed section, grant re-read before step 7, list_grants grantedVia cli) are not in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:316-325, 171-178 (none of these present) |
| D171 | medium | docs | Spine AD-7 still says a declined path offers sending the image inline and that images otherwise go only as inline one:Data, contradicting the code and skill since 2.13. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:238-239; src/OneNoteMcp.Core/Access/FilePathDialog.cs |
| D175 | low | docs | Spine AD-7 PageWrite and AD-4 still say nothing about update_page_content cutting z from sent one:Position elements, AD-4's one write-side exception. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md (no mention of z); src/OneNoteMcp.Core/Writes/PageWrite.cs:401, 573 |
| D181 | low | docs | Spine AD-7 ExternalWrite row still says "a notebook holding a locked or not fully downloaded section" instead of the unsynced placeholder page. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:393 |

### B. Four small bugs and one test gap (story, 5 items)

| Id | Severity | Kind | What is still wrong | Evidence |
|---|---|---|---|---|
| D182 | low | bug | Writes that check the section before WriteAccess (e.g. set_page_level) refuse a protected or subpaged loose section with ReadNotGranted or the subpage error instead of the loose-section NotSupported. | src/OneNoteMcp.Core/Writes/SetPageLevelWrite.cs:128-133 (read-grant check before WriteAccess.CheckAsync); same pattern in MovePageWrite, CreatePageUseCase, PermanentDeleteUseCase |
| D186 | low | bug | The opened-copies sweep deletes a copy in one Directory.Delete with no retry; if Windows refuses briefly, an empty marker-less folder can be left that later sweeps ignore. | src/OneNoteMcp.Core/Writes/OpenedCopies.cs:140-153 |
| D188 | low | bug | The filtered list's own store event (id, size, evictions) is discarded and not logged, against AD-13; recorded as Deferred in the spine. | src/OneNoteMcp.Core/LargeResults/ResultDelivery.cs:94; _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:774 |
| D189 | low | bug | meta.withheld still stays in the header, so thousands of withheld sections can push a header over results.inlineMaxChars; only logged, moving it is Deferred. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:774 |
| D180 | low | test-gap | Protected-by-path (export root/backups) rows still have no use-case tests for update_page_content, create_page with parentPageId, set_page_level, restore_from_journal, or a loose OpenSections section through get_hierarchy/get_page_content. | tests/OneNoteMcp.Tests: no exportRoot/ByPath cases in UpdatePageContentToolTests.cs, SetPageLevelToolTests.cs, CreatePageToolTests.cs; RestoreFromJournalToolTests.cs only l.375 (backups userAction) |

### C. Live probes for three unverified behaviours (spike (sitting with Phill), 3 items)

| Id | Severity | Kind | What is still wrong | Evidence |
|---|---|---|---|---|
| D82 | medium | test-gap | Crash detection is built, but no record shows the live check that a forced reconnect starts no hidden -Embedding instance; the accepted risk (an unreadable WER process blocks connecting) also stands. | story-write-gateway-timeouts-and-error-map-plan.md:168 asks for it, :170 records only PageWriteGatewayIntegrationTests; needs a live forced-reconnect/crash check |
| D183 | medium | test-gap | Whether a file dropped directly on a page has an objectID (and so an element hash) is unverified; needs a live get_page_content of such a page. | src/OneNoteMcp.Xml/PageWriteScan.cs l.167-168 comment; spine l.687 |
| D184 | low | design-question | A page-level one:MediaFile is still unhashed because its pathCache is not excluded from ElementHash; the reason is inferred, awaiting a probe and Phill's confirmation. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:687, 1018 (Deferred row) |

### D. Design questions for Phill (decision each, 5 items)

| Id | Severity | Kind | What is still wrong | Evidence |
|---|---|---|---|---|
| D109 | medium | design-question | Nothing returns a protected page's lastModifiedTime without a read grant (create_page returns no basis), so such writes can only run noBasis; the AD-17/AD-21 decision is still pending. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:1037 (Deferred row still Open); src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs has no basis/lastModified |
| D121 | low | design-question | No journal listing tool exists, so a caller that lost meta.journalEntryId cannot find an entry by page or time. | no list_journal/ListJournal in src or plugin (rg); deferred-work.md:412 |
| D128 | low | design-question | Cloud notebook create stays closed (Enabled => false) although the 2026-11-15 sitting's cloud probe passed (read-back key matched the URL); opening it and the OneDrive base URL are undecided. | src/OneNoteMcp.Core/CloudNotebookCreate.cs:21; epic-hierarchy-writes-and-deletes/story-create-section-section-group-and-notebook-plan.md:188 |
| D63 | low | design-question | Shared-server (localhost HTTP) mode is an unscheduled idea; every session still starts its own stdio OneNoteMcp.exe. | No HTTP transport in src/ or spine; deferred-work.md:202 only |
| D178 | low | design-question | The window check is still session-wide over all ONENOTE.EXE processes and not WER-filtered, so a second windowed OneNote (or a crashed one's window) lets a call connect while an old one is closing; fails open by decision, unmeasured which process COM binds. | src/OneNoteMcp.Com/IProcessProbe.cs:146; ComGateway.Activation.cs:80, 283 |

### E. Plugins repo: check its validator pin matches this repo (chore in sentienceAdjacent/plugins, 2 items)

| Id | Severity | Kind | What is still wrong | Evidence |
|---|---|---|---|---|
| D13 | low | test-gap | Nothing automated checks that the plugins repo's validator pin (2.1.318 in ci.yml and sync.yml) equals this repo's; only comments keep them in step. | D:/Development/sentienceAdjacent-plugins/.github/workflows/ci.yml:20-22, sync.yml:36-38 (comments only); owner Phill in plugins repo per D32 |
| D32 | low | test-gap | The cross-repo validator-pin check was moved to the plugins repo and does not exist there yet. | D:/Development/sentienceAdjacent-plugins/.github/workflows/ci.yml:20, sync.yml:36 (comments only, no check) |

## Closed items (fixed, obsolete, decided)

| Id | Verdict | Note | Evidence |
|---|---|---|---|
| D1 | fixed | Work queued after Dispose no longer hangs: Enqueue checks stopping under the queue lock and fails late items. | src/OneNoteMcp.Com/StaThread.cs:24,92 (stopping checked in Enqueue); resolved per D23 |
| D2 | fixed | The one-line stderr log format now has a test. | src/OneNoteMcp.Server/ServerSetup.cs:42 AddOneNoteMcpLogging; tests/OneNoteMcp.Tests/ServerLoggingTests.cs:12 |
| D3 | decided | A contract change without a version bump passes unit tests by design; the reviewer checks the bump size. | spine AD-14 line 634 "The test detects the change; the PR reviewer checks the size of the bump"; D25 |
| D4 | fixed | 0x80080005 now maps to ComUnavailable, not IntegrityMismatch, since the integrity check runs before COM. | src/OneNoteMcp.Com/HResultMap.cs (0x80080005 -> ComUnavailable, ServerExecFailureUserAction); D26 |
| D5 | fixed | The release job refuses a tag that differs from <Version>. | .github/workflows/release.yml:43 "Check tag against <Version>" |
| D6 | fixed | tools/list and tools/call are exercised through the MCP SDK in-process. | tests/OneNoteMcp.Tests/McpSdkBindingTests.cs:23 |
| D7 | fixed | Client cancel is its own Cancelled code (retryable for reads), and a cancelled write carries outcomeUnknown. | src/OneNoteMcp.Core/Results/ToolErrors.cs:56; src/OneNoteMcp.Core/Writes/WritePipeline.cs:252 |
| D8 | fixed | Step-7 write failures with Timeout/ComUnavailable/Cancelled/0x800706BE set outcomeUnknown; 0x80042023 maps to Timeout. | src/OneNoteMcp.Core/Writes/WritePipeline.cs:246-252; HResultMap.cs 0x80042023 -> Timeout; spine AD-7 step 7 line 215 |
| D9 | fixed | Every named OneNote HRESULT has a triaged row with its own advice; three were remapped in story 2.7. | src/OneNoteMcp.Com/HResultMap.cs:95-150; spine Deferred row "HRESULT triage / Resolved (story 2.7)" |
| D10 | fixed | NewerSchema now gives retryable:false and "update OneNoteMcp, don't edit settings.json". | src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:301-321; tests SettingsStoreTests.cs:401, CreatePageToolTests.cs:214 |
| D11 | fixed | Parse exceptions no longer carry page content into the log; the message is replaced by line/position and not attached as inner. | src/OneNoteMcp.Xml/OneNoteXml.cs:18; src/OneNoteMcp.Core/XmlReads.cs:14; D31, D53 |
| D12 | fixed | AD-3 write-side rules are built: abandoned writes are tracked and non-read calls get ComBusy while one is outstanding; busy retry for writes is confirmed in the spine. | src/OneNoteMcp.Com/ComGateway.cs:69-74,137-139,245; spine AD-3 line 94 |
| D14 | decided | Branch protection on the plugins repo would break sync.yml's direct push; AD-15 states this precondition and Phill closed it. | spine AD-15 precondition; D33 (Q4, Phill 2026-11-09) |
| D15 | fixed | Release tags are protected from deletion by a GitHub tag ruleset (the Release object itself can still be deleted). | D46: ruleset "release tags" id 26869745 on refs/tags/v* (Phill 2026-11-09) |
| D16 | fixed | Unlocked one:Column@width is excluded from the AD-17 element hash, so alternating widths no longer cause StaleWrite. | src/OneNoteMcp.Xml/ElementHash.cs:98-118 |
| D17 | fixed | get_binary_page_content on an XPSFile CallbackID returns the binaryUnavailable warning, and the tool and README say so. | src/OneNoteMcp.Core/UseCases/GetBinaryPageContentUseCase.cs:65,86; D35 |
| D18 | fixed | one:InsertedFile@pathCache is excluded from the AD-17 element hash. | src/OneNoteMcp.Xml/ElementHash.cs:98-118 |
| D19 | obsolete | The feared recycle-bin leak of protected pages does not happen: OneNote refuses to move them to Deleted Pages. | D20 (Phill, 2026-11-07, hands-on delete); section path stability later resolved in spine Deferred row (spike 2.2 constraint 9) |
| D20 | fixed | Resolution record: no recycle-bin leak for protected pages. | Phill, 2026-11-07 observation; spine Deferred row updated |
| D21 | fixed | The SectionLocked userAction and sectionLocked warning now name OneNote's add-in access option. | src/OneNoteMcp.Core/Results/ToolErrors.cs:19,69; src/OneNoteMcp.Core/UseCases/GetHierarchyUseCase.cs:74 |
| D22 | fixed | Resolution record for D21; still holds. | src/OneNoteMcp.Core/Results/ToolErrors.cs:19 AddInAccessOptionNote |
| D23 | fixed | Resolution record for D1; still holds. | src/OneNoteMcp.Com/StaThread.cs:24,92 |
| D24 | fixed | Resolution record for D2; still holds. | tests/OneNoteMcp.Tests/ServerLoggingTests.cs:12; ServerSetup.cs:42 |
| D25 | decided | Accepted by AD-14: no automated bump check. | spine AD-14 line 634 |
| D26 | fixed | Resolution record for D4; still holds. | src/OneNoteMcp.Com/HResultMap.cs 0x80080005 -> ComUnavailable |
| D27 | fixed | Resolution record for D5; still holds. | .github/workflows/release.yml:43 |
| D28 | fixed | Resolution record for D6; still holds. | tests/OneNoteMcp.Tests/McpSdkBindingTests.cs:23 |
| D29 | fixed | Cancelled code exists, and the epic-2 half (cancelled write is outcomeUnknown) is built. | src/OneNoteMcp.Core/Results/ToolErrors.cs:56; src/OneNoteMcp.Core/Writes/WritePipeline.cs:252 |
| D30 | fixed | Routing record for D10; the fix landed. | src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:301-321 |
| D31 | fixed | Resolution record for D11; still holds. | src/OneNoteMcp.Xml/OneNoteXml.cs:18; src/OneNoteMcp.Core/XmlReads.cs:14 |
| D33 | decided | Branch protection on the plugins repo closed by Phill; AD-15 states the precondition. | spine AD-15; Q4 Phill 2026-11-09 |
| D34 | fixed | The v* tag ruleset now exists. | D46 (ruleset id 26869745, checked via GitHub API 2026-11-09) |
| D35 | fixed | Resolution record for D17; still holds. | src/OneNoteMcp.Core/UseCases/GetBinaryPageContentUseCase.cs:65,86 |
| D36 | fixed | The GetHierarchyParentAsync comment says OneNote returns an empty string at a notebook. | src/OneNoteMcp.Core/IOneNoteGateway.cs:18 |
| D37 | fixed | FormatDate is gone from the spine and code. | rg FormatDate src: no match; change-story-1-6-spine-amendments |
| D38 | decided | Won't fix: ToolRunner shape and repeated ?? string.Empty accepted. | Q1, Phill 2026-11-09 (deferred-work D38) |
| D39 | fixed | Spike 2.2 settled the per-method modal behaviour: only UpdatePageContent returns 0x80042030; the map is global and AppState docs record the rest. | epic-safe-writes/spike-write-probes-part-1-pages-paths-and-search-findings.md:18,39,73; HResultMap.cs 0x80042030 -> ModalDialogOpen; spine line 114 |
| D40 | fixed | Section path stability was probed and SectionKey kept (moving a section between notebooks not tried). | spine Deferred row "Whether a section's path is stable / Resolved ... spike 2.2 constraint 9" |
| D41 | fixed | FindPages symbols, FindMeta, encrypted in search XML and the skill's list/alt claims were all probed in spike 2.2. | spike-write-probes-part-1-pages-paths-and-search-findings.md:49-51,80-83; spine Deferred row on encrypted |
| D42 | decided | Cache gives no benefit when candidates exceed cacheMaxMB; accepted with a README line. | README.md:92 (Limits); Q5 Phill 2026-11-09 |
| D43 | fixed | Unknown argument names are refused and every argument error names the argument. | src/OneNoteMcp.Server/Tools/ArgumentCheck.cs:30; ServerSetup.cs:327 CloseInputSchemas; D44, D45 |
| D44 | fixed | Partial-resolution record; the remaining half was closed by story 2.14. | D45; src/OneNoteMcp.Server/Tools/ArgumentCheck.cs:30 |
| D45 | fixed | Resolution record: argument errors checked against the schema before binding. | src/OneNoteMcp.Server/Tools/ArgumentCheck.cs:30; ServerSetup.cs:327 |
| D46 | fixed | Tag ruleset protects v* tags (deleting the GitHub Release object remains possible, by design note). | Phill 2026-11-09, ruleset id 26869745 |
| D47 | fixed | The "epic 2 first refactor" group was done in story 2.1. | D52-D58, D60, D61 (story-opening-refactor-plan.md) |
| D48 | fixed | uninstall --purge now warns with the PIDs of running servers before asking and takes the settings mutex for the removal. | src/OneNoteMcp.Server/Cli/UninstallCommand.cs:63-66,90,109-115,206-213 |
| D49 | fixed | The epic 1 retro spec/architecture wording issues were reconciled by the change proposal. | D51; change-epic-1-retro-wording.md section 4 |
| D50 | fixed | The grant CLI no longer calls OneNoteMcp.Xml directly. | rg "using OneNoteMcp.Xml" src/OneNoteMcp.Server: no match; D57 |
| D51 | fixed | Resolution record for D49. | change-epic-1-retro-wording.md section 4 |
| D52 | fixed | One shared xs:boolean IsTrue. | src/OneNoteMcp.Xml/OneNoteXml.cs (IsTrue); ElementHash.cs:118 uses it |
| D53 | fixed | One InvalidXml mapping via XmlReads.Readable. | src/OneNoteMcp.Core/XmlReads.cs:14 |
| D54 | fixed | 0x80042014 and 0x8004200F declared once in Core. | src/OneNoteMcp.Core/OneNoteHResults.cs:13 |
| D55 | fixed | The XPSFile probe no longer swallows Timeout/Cancelled. | src/OneNoteMcp.Core/UseCases/GetBinaryPageContentUseCase.cs:86 |
| D56 | fixed | ReadAccess is split into Access/ files with one shared ancestor walk (ReadAccess.cs has since grown back to 377 lines with write-era additions). | src/OneNoteMcp.Core/Access/HierarchyWalk.cs:26,55 AncestorsAsync |
| D57 | fixed | Resolution record for D50; still holds. | no "using OneNoteMcp.Xml" in src/OneNoteMcp.Server |
| D58 | fixed | Shared settings.json parse rules live in SettingsJson. | src/OneNoteMcp.Core/Settings/SettingsJson.cs:5-17 |
| D59 | fixed | grant --revoke now removes exact duplicate grant keys (parses with duplicates allowed and rewrites without them). | src/OneNoteMcp.Core/Settings/SettingsWriter.cs:292-302; SettingsJson.cs:10-15; tests/OneNoteMcp.Tests/SettingsDuplicateKeyTests.cs:100 Writer_AnExactDuplicateSectionKey_IsRevoked_BothEntriesGone_AndTheFileIsValid |
| D60 | fixed | SettingsStore.Validate is split into one reader per settings area. | src/OneNoteMcp.Core/Settings/SettingsStore.cs:148 ReadProtectedSections(root, ...) |
| D61 | fixed | Private-field naming is enforced by .editorconfig and the build. | .editorconfig:5-8 IDE1006 |
| D62 | fixed | Reconnect after a crash could start a hidden ONENOTE.EXE -Embedding; story 2.7 added WER-based crash detection so a crashing OneNote does not count as running. | src/OneNoteMcp.Com/IProcessProbe.cs:118,126 (WerFaultTarget); spine AD-3 line 97; see D82 for the pending live check |
| D64 | fixed | Unlocked Column/@width is excluded from ElementHash and the read-back. | src/OneNoteMcp.Xml/ElementHash.cs:70 (IsExcluded); tests/OneNoteMcp.Tests/XmlModelTests.cs:176 |
| D65 | fixed | InsertedFile/@pathCache is excluded from ElementHash and the read-back. | src/OneNoteMcp.Xml/ElementHash.cs:70; tests/OneNoteMcp.Tests/XmlModelTests.cs:168 |
| D66 | fixed | Duplicate grant keys make settings invalid and revoke removes every matching entry; built by story 2.6 (see D77). | tests/OneNoteMcp.Tests/SettingsDuplicateKeyTests.cs:100 |
| D67 | fixed | AD-3 crash rule written and built in ComGateway/ProcessProbe by story 2.7. | spine AD-3 line 97; .memlog.md:197; src/OneNoteMcp.Com/IProcessProbe.cs:118 |
| D68 | fixed | Epic 2 ticket text has since been refreshed: decision 3 carries a Superseded note, entry 7 names crash detection, entry 4 names 0x800706BE, entry 9 drops the image refusal. | epic-safe-writes/epic-safe-writes.md:78-79; epic-safe-writes/tickets.toml:45,80,124 |
| D69 | fixed | NewerSchema SettingsInvalid is non-retryable with update advice. | src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:301 (NewerSchemaUserAction) |
| D70 | fixed | Ambiguous failures at the COM call set outcomeUnknown. | src/OneNoteMcp.Core/Writes/WritePipeline.cs:246,309 (IsAmbiguous) |
| D71 | fixed | Remaining part (ComBusy while an abandoned write is outstanding, busy retry confirmed) built by story 2.7. | src/OneNoteMcp.Com/ComGateway.cs:246,285 (ThrowIfWriteOutstanding); D80 |
| D72 | fixed | Notebook is re-resolved under the lock for every write (stories 3.4/3.12). | tests/OneNoteMcp.Tests/CreatePageToolTests.cs:1278 (ASectionMovedToAnotherNotebookAfterStep1_IsWriteNotGranted...); src/OneNoteMcp.Core/Writes/MoveContainerWrite.cs:20 |
| D73 | fixed | Lock kept until an abandoned write settles, and pipelines return ComBusy after step 2 while one is outstanding. | src/OneNoteMcp.Core/Writes/LockAndDeadline.cs:42,74,86; spine line 739 |
| D74 | fixed | AD-14 now records create_page as a named exception. | spine architecture-onenote-mcp.md:628 |
| D75 | fixed | AD-7 refusals carry their own userAction. | src/OneNoteMcp.Core/Writes/PageWrite.cs:417 (OmittedUserAction); DeletePageContentWrite.NotAnOutlineUserAction |
| D76 | fixed | AD-6 now states a typed name matching a different notebook is WriteNotGranted. | spine architecture-onenote-mcp.md:174 |
| D77 | fixed | Revoke removes exact and case-only duplicate keys. | tests/OneNoteMcp.Tests/SettingsDuplicateKeyTests.cs:100 |
| D78 | fixed | uninstall --purge warns about other servers and removes settings only under the settings lock. | src/OneNoteMcp.Server/Cli/UninstallCommand.cs:88; src/OneNoteMcp.Server/Program.cs:60 |
| D79 | fixed | Every named HRESULT has an explicit triage row. | src/OneNoteMcp.Com/HResultMap.cs (Triaged); HResultMapTests |
| D80 | fixed | Abandoned-write tracking and ComBusy for non-read calls built. | src/OneNoteMcp.Com/ComGateway.cs:246,285 |
| D81 | fixed | Write lock held in the background until the abandoned call settles or the deadline. | src/OneNoteMcp.Core/Writes/LockAndDeadline.cs:74,86 (ReleaseWhenSettledAsync) |
| D83 | fixed | AD-9 no longer has the untriaged sentence, AD-3 has the WER rule, AD-18 keeps the lock and checks for abandoned writes after step 2. | spine lines 97, 257, 500, 739 (rg "untriaged" finds nothing) |
| D84 | fixed | The three HRESULTs are remapped (story 5.4) in spine and code. | spine:500; src/OneNoteMcp.Com/HResultMap.cs:119,131,143; tests/OneNoteMcp.Tests/HResultMapTests.cs:41-43 |
| D85 | decided | 30 s call timeout kept for every write, cloud creates included. | spine AD-3 line 81 (Phill, 2026-11-12, 2.7 plan Q1) |
| D86 | fixed | Live PageWriteGatewayIntegrationTests run passed. | story-write-gateway-timeouts-and-error-map-plan.md:170 (2026-11-12, 2 of 2 passed) |
| D88 | fixed | Live UpdatePageContentIntegrationTests and the manual checks passed. | story-update-page-content-plan.md:171-175 (2026-11-12 sitting, stress harness 0 mismatches); story-delete-page-content-plan.md:143 (5/5) |
| D87 | fixed | Spine sync recorded the page-write rules (AD-7/9/16/17/18). | spine line 739 (abandoned-write check after step 2, create_page included); AD-7 PageWrite section |
| D89 | decided | Merged-table probe printed merge kept; no AD-4 amendment. | deferred entry D88/D89; spine sync 2026-11-13 |
| D91 | fixed | Guide skill description covers writes and the WriteNotGranted row names every write tool. | plugin/skills/guide/SKILL.md (description, tool table); story 2.12 |
| D92 | fixed | Abandon race fixed: WorkItem records who abandoned it. | src/OneNoteMcp.Com/StaThread.cs:245 (WasAbandoned); WorkItemGiveUpTests |
| D93 | fixed | Security-review rules (readBackWithheld, network paths, step-4 WriteNotGranted) are in the spine. | spine:236 (path refusals), :250 (readBackWithheld) |
| D95 | fixed | Image/attachment and date-only recipes are in the guide skill. | plugin/skills/guide/references/page-writes.md (recipes 4, 5); story 2.12 |
| D96 | decided | Probe showed HTMLBlock fetches local files; AD-7 refuses a new or changed HTMLBlock. | spine:241 |
| D97 | fixed | pathCache churn handled: issued pathCaches remembered, file without objectID belongs to its OE. | src/OneNoteMcp.Server/ServerSetup.cs:91 (IssuedPathCaches); story-update-page-content-plan.md:186 |
| D98 | decided | HTMLBlock refusal is an AD-7 rule; the one:T allowlist stays as a stopgap pending a double-decoding probe (tracked in the spine Deferred). | spine:241; story-update-page-content-plan.md:199-200 |
| D99 | decided | delete_page_content read-grant rule, one Outline per call and journal entry are in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:207,230,231 |
| D100 | fixed | The 2.9+2.10 sitting ran; the remaining cloud date-basis deletes were run later (see D114). | deferred-work D114; 2026-11-14 sitting |
| D101 | fixed | Guide skill WriteNotGranted/outcomeUnknown text now covers every write tool. | plugin/skills/guide/SKILL.md:32-33 |
| D102 | fixed | Date-only/no-read-grant rule lives in CheckWithoutReadGrant, with delete overriding it. | src/OneNoteMcp.Core/Writes/PageWrite.cs:152,591,907 |
| D103 | fixed | Integration test now asserts the StaleWrite message names the Outline objectID. | tests/OneNoteMcp.Integration/UpdatePageContentIntegrationTests.cs:112-113 |
| D104 | decided | AD-14 records create_page as the naming exception. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:630 |
| D105 | decided | AD-6 records that a name matching another notebook makes no grant. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:174 |
| D106 | decided | AD-9 triage, AD-3 WER detection and AD-18 lock hold are in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:97; AD-9 Triage; AD-18 |
| D107 | fixed | The three HRESULT remaps are in AD-9 and built in HResultMap. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:500; src/OneNoteMcp.Com/HResultMap.cs:119,131,143 |
| D108 | decided | AD-3 keeps 30 s for cloud creates; a Deferred row tracks coverage. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:1006 |
| D110 | fixed | All noBasis/partialBasis warnings use the "read internally; nothing of it is returned" wording, and the guide skill matches. | src/OneNoteMcp.Core/Writes/PageWrite.cs:253; HierarchyBasis.cs:64,72; SoftDeleteUseCase.cs:403; PermanentDeleteUseCase.cs:414; plugin/skills/guide/SKILL.md:272 |
| D111 | fixed | update_page_content strips z from sent Positions; live check passed 2026-11-16. | src/OneNoteMcp.Xml/SentPositions.cs:30; deferred-work.md:606 |
| D112 | fixed | Guide skill and README note OneNote's leftover %TEMP% {GUID}.bin copies. | README.md:101; plugin/skills/guide/SKILL.md:151 |
| D113 | fixed | Cloud steps D4c/d ran at the 2026-11-14 sitting (D114). | deferred-work D114 |
| D115 | decided | AD-8 Binaries states the pathCache-only {GUID}.bin rule. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:425 |
| D117 | decided | AD-8 covers failure after the page exists, template deletion and a locked chosen section. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:418,424,428 |
| D118 | decided | AD-9 journalEntryId and AD-8 binaryMissing/restoredElsewhere/meta are in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:419,425,471 |
| D119 | decided | AD-7 Create names rule recorded for sections (5.3 extension tracked under D124). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:260-261; .memlog.md:276 |
| D120 | decided | AD-8 Reading the entry, Building the page and parentNamePath rules are in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:417,423,424 |
| D123 | fixed | The create tools and OpenHierarchy create by parent ID / notebook path are in the spine. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:389,630 |
| D129 | decided | A group's existing name and missing parent folders stay unprobed, but the server never sends either (alreadyExisted before COM; folder must exist). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:263 |
| D130 | fixed | The 5.3 live sitting ran 2026-11-15 and passed (plus the dotted group-name fix). | epic-hierarchy-writes-and-deletes/story-create-section-section-group-and-notebook-plan.md:184-188 |
| D131 | fixed | Spine uses rename_hierarchy_object/reorder_hierarchy_children throughout. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:156,266,882; epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md l.120 |
| D132 | fixed | The HierarchyWrite row no longer offers add; create tools are the only create path. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:390 |
| D136 | fixed | AD-8 records hierarchy snapshots, protected-section DPAPI marking and NotSupported restore of hierarchy entries. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:410,414 |
| D137 | fixed | HResultMap remaps 0x80042017, 0x80042024 (no backoff) and 0x8004202E. | src/OneNoteMcp.Com/HResultMap.cs:119,131,143 |
| D140 | fixed | Spine renamed delete_hierarchy to delete_hierarchy_object/permanently_delete_hierarchy_object (basis-closure detail not spelled out). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:306,435,629; epic-round-2-external-and-structural/story-spine-amendments-for-round-2-plan.md l.120 |
| D147 | fixed | Rebase onto 5.4's HierarchySnapshot is done; the protected-pages-dropped rule is in AD-8. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:410; HierarchySnapshot.TakeAsync |
| D158 | fixed | Backup-folder and export-root sections count as protected for every read check. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:406, 456; tests/OneNoteMcp.Tests/ProtectedByPathTests.cs |
| D159 | fixed | The one-target-reader merge shipped (backlog story "One reader for the target of a delete or change", done); DeleteTargets kept its name for the leftover helpers by design. | backlog/story-one-reader-for-the-target-of-a-delete-or-change-plan.md:34,75; git grep finds no DeleteTargets.Root/Page or PermanentDeleteTarget (main session, 2026-11-27) |
| D160 | fixed | Live sitting P1-P11 ran 2026-11-15 and passed; P9 led to BackupFailed for empty sections. (Spine l.324 still says the empty-group delete was never run, a small doc leftover.) | epic-hierarchy-writes-and-deletes/story-permanent-delete-plan.md:241 |
| D161 | fixed | Skill recipe 4, SKILL.md and README say file by path, inline only for small images in hand. | D161 entry; plugin/skills/guide/references/page-writes.md recipe 4 |
| D162 | fixed | update_page_content description now prefers a full local path and keeps one:Data for small images. | src/OneNoteMcp.Server/Tools/UpdatePageContentTool.cs:34 |
| D163 | fixed | delete_page_content description now says any page change since the read fails with StaleWrite. | story 2.13; src/OneNoteMcp.Server/Tools/DeletePageContentTool.cs |
| D164 | fixed | Both live skill/README checks ran 2026-11-15 and passed. | D164 entry; Inbox thread 2.12, 2026-11-15 |
| D165 | fixed | Declined-path userActions no longer invite sending the file inline. | story 2.13; src/OneNoteMcp.Core/Access/FilePathDialog.cs (DeclinedUserAction, NoDialogUserAction) |
| D166 | fixed | The z re-stack is handled: z is cut from every sent one:Position before UpdatePageContent. | src/OneNoteMcp.Xml/SentPositions.cs:13; src/OneNoteMcp.Core/Writes/PageWrite.cs:573; backlog 2 live check passed 2026-11-16 |
| D167 | fixed | ErrorCode doc comments now say checked before the SDK binds and list the AD-21 writes for ReadNotGranted. | src/OneNoteMcp.Core/ErrorCode.cs:28-45 |
| D168 | fixed | 5.7 live checks and stress harness passed at the 2026-11-15 sitting. | D168 entry; 5.7 plan Verification |
| D169 | fixed | Create-then-rename for a dotted group was proven live 2026-11-15. | D169 entry |
| D170 | fixed | Confirmed live that renaming a group ends a contained protected section's read grant; docs aligned. | D170 entry; README and references/hierarchy-writes.md recipe 2 |
| D172 | fixed | Probe ran and the stop-sending-z story was built (backlog 2). | src/OneNoteMcp.Xml/SentPositions.cs; backlog/story-stop-sending-z-in-the-outline-positions-we-write-plan.md |
| D173 | fixed | CreatePageIntegrationTests gives its store a journal folder in its temp folder. | tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs:414-419 |
| D174 | fixed | The cleaning-up bug was reproduced and fixed by refusing to connect when OneNote has no window; live check passed 2026-11-16. | backlog/bug-onenote-shows-cleaning-up-after-a-restart-while-a-server-is-connected-plan.md:6 (status done), l.139-141 |
| D176 | fixed | Backlog 2 live check passed at the 2026-11-16 sitting. | backlog/story-stop-sending-z-in-the-outline-positions-we-write-plan.md, Live check note |
| D177 | fixed | Page-level images, ink and inserted files with an objectID now get element hashes (backlog 5, built 2026-11-18). | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:687, 720 |
| D179 | fixed | The DocumentedByRound2Entry10 exemption set is gone; 3.10 documented the round-2 tools. | no DocumentedByRound2Entry10 in src/tests; story 3.10 |
| D185 | fixed | 3.10 sitting ran parts A-D and passed on 2026-11-19. | story-skill-readme-and-harness-for-round-2-plan.md, Sitting results (2026-11-19) |
| D187 | decided | The moved filtered list can be evicted on its own; the spine accepts it, and the reader repeats the search on ResultExpired. | _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md:774 ("can be evicted on its own ... ResultExpired ... means repeat the search"); src/OneNoteMcp.Core/LargeResults/ResultDelivery.cs:69-74 |
| D190 | decided | Phill chose a shared helper each write calls, leaving WritePipeline without a partial-copy hook. | backlog/story-one-shared-recovery-path-for-the-move-and-restore-writes-plan.md, Q3 (Phill 2026-11-21) |
| D191 | fixed | navigate_to description no longer claims keyboard focus or the windowless sentence. | src/OneNoteMcp.Server/Tools/NavigateToTool.cs:24 |
| D192 | fixed | Same as D191; skill and README aligned. | src/OneNoteMcp.Server/Tools/NavigateToTool.cs:24; NavigateToToolTests |
