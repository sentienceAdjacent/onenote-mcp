---
title: 'merge_sections'
type: 'feature'
ticket: '6'
created: '2026-11-18'
status: done
baseline_revision: '556875ae7a35c74c810e8961190c709f71d45947'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-move-a-section-or-section-group-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can't merge one section into another. E6 asks for `merge_sections`, under a move's grants and basis, reporting what became of the source section. E8 asks that nothing protected is merged out of its encryption.

**Approach:** `merge_sections(sourceId, destinationId, basis?)` is one `HierarchyWrite` through the existing `WritePipeline` (AD-5, AD-7 Merges, AD-14). Step 7 is one `MergeSections` call, a new gateway method. Spike 3.1 showed OneNote leaves the source section and its pages untouched, and puts copies under new IDs at the end of the destination, subpages kept. So the result reports the source as untouched and lists the copies' new IDs. The only snapshot is a hierarchy snapshot of the destination (AD-8; Q12's condition isn't met).

## Boundaries & Constraints

**Always:**
- **Step 1, before the lock.**
  - An empty `sourceId` or `destinationId`, a basis with a hash, and a source equal to the destination are `InvalidArgument` (AD-17).
  - An unknown ID is `ObjectNotFound`. The recycle bin itself, or a section in it, is `ObjectNotFound` too, checked before the kind (the AD-7 table: nothing in or into the recycle bin outside restore). Then both IDs must be sections; any other kind is `InvalidArgument`.
  - Each is found by its walk to its notebook, never by a `self` read first, because a locked section hides its pages (3.3 S1).
  - Then, in this order:
    - a protected source (AD-21's test: the `encrypted` marker or a path under an export root or the backups folder, entry 15), locked or not, is `NotSupported`, with a `userAction` to merge it in OneNote; this is checked first (AD-7 Merges, E8, Decision H2);
    - a locked destination is `SectionLocked`, because `MergeSections` answers `0x8004201B` and changes nothing;
    - a source with no live pages is `InvalidArgument` ("nothing to merge"), so an unprobed call is never sent (Q7 a).
  - Only then does it ask for the write grants: the source's notebook, then the destination's (AD-6). The pipeline asks once per notebook (3.4 Q3) and re-resolves both under the lock (story 3.12 ledger).
- A destination in an unlocked protected section needs only the write grant (AD-21 Writes): the copies land inside its encryption (spike 3.1 SP).
- **Step 3:** both sections are walked to their notebooks again, as the moves do (recycle-bin membership and protection by path come from that walk), and read at `Children` scope, under the lock. **Step 4** repeats every step-1 refusal against those reads.
- **Step 5, the basis:** the source section's hierarchy `lastModifiedTime`, compared by `HierarchyBasis`: optional (`noBasis`) and weak (AD-17).
- **Step 6:** one `hierarchy` journal entry of the destination (`meta.journalEntryId`). A protected destination's entry is DPAPI-encrypted, as the snapshot already does.
- **Step 7:** one `MergeSections(sourceId, destinationId)`, never retried.
  - `0x8004201F` (hrMergeFailed) keeps `Internal` and sets `outcomeUnknown` (AD-7 Merges, approved spike 3.1 rule). Before the throw, still under the lock, the destination is re-read by its ID, best effort; when either side is in a cloud notebook, the settle (Q2) comes first, cut short by the deadline. The message gives the count of new pages and their IDs (or the count alone when the destination is protected), and says to delete those copies before merging again. The advice goes in the message because `ToolErrors.From` replaces any `outcomeUnknown` error's `userAction` with "Re-read, then retry if the change is absent." (`ToolErrors.cs` l.13, l.37).
  - Every other `outcomeUnknown` failure (`Timeout`, `ComUnavailable`, `Cancelled`) gets the same best-effort re-read and findings in its message.
  - `0x8004201B` maps to `SectionLocked`. `0x80042030` maps to `ModalDialogOpen` as everywhere, but isn't expected: a merge ran with File > Options open (spike 3.1 D; spine AD-1 round-2 methods).
- **Read-back**, between 7 and 8: both sections are re-read.
  - The copies are the pages in the destination whose IDs weren't there before. If the count differs from the source's live page count, the result warns `readBackDiffers`.
  - A source that no longer reads back with its ID and all its page IDs is `readBackDiffers` too; it has never happened (spike 3.1).
  - A failed re-read gives `readBackFailed`.
- **Result** (Q1 a, Q6 a): `meta.objectId` is the source (untouched), `meta.parentId` is the destination, `meta.journalEntryId` is the destination's entry, and a new `meta.pageIds` lists the copies' new IDs in destination order. For a protected destination (AD-21's test), `meta.pageIds` is left out and messages give counts only, since a write-grant-only write returns nothing from inside the section. Messages carry IDs and counts only (AD-9).
- **Cloud** (Q2): when either section is in a cloud notebook, a 10 s settle under the write lock before the read-back, with `settled` (AD-7 Moves, M2).
- The search cache drops nothing: no existing page changes.

**Never:**
- No merge of a section group or a notebook.
- No deleting the source afterwards; that stays `delete_hierarchy_object`'s, by the user's choice.
- No page snapshot of the source (Q12's condition isn't met).
- No `MergeFiles`: E4 was dropped.
- No retry, and no `NavigateTo`.
- No skill or README text (entry 10).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Same notebook | plain source (2 pages + subpage) into plain destination | copies at the end under new IDs, subpage kept at level 2; source untouched; one entry | none |
| Other notebook | into a section of the granted throwaway | as above | none |
| Into protected | plain into an unlocked protected section | copies inside it; entry encrypted; no `meta.pageIds` | none |
| Not granted | destination's notebook not granted, no dialog | nothing sent | `WriteNotGranted` |
| Protected source | marker or path protected, locked or not | nothing sent, no grant dialog | `NotSupported` |
| Locked destination | locked section | nothing sent | `SectionLocked` |
| Recycle bin | source or destination in the bin, or the bin | — | `ObjectNotFound` |
| Not sections | a page, group or notebook ID | — | `InvalidArgument` |
| Same section | `sourceId == destinationId` | — | `InvalidArgument` |
| Stale | basis ≠ source's date | — | `StaleWrite` |
| Merge failed | `MergeSections` → `0x8004201F` after a partial copy | destination re-read, findings in message | `Internal`, `outcomeUnknown` |
| Empty source | source with no live pages | nothing sent | `InvalidArgument` |
| Count differs | read-back finds fewer copies than source pages | merged | warning `readBackDiffers` |
| Cloud | either side in a cloud notebook | merged, then 10 s settle | warning `settled` |

## Decisions (Phill, 2026-11-18, plan approved, all as recommended)

- **Q1, a.** A new optional `meta.pageIds` lists the copies' new IDs.
- **Q2, a.** A merge touching a cloud notebook settles 10 s under the write lock, with `settled`.
- **Q3, a.** `0x8004201F` is `outcomeUnknown` with the re-read findings in the message. It is never retried, and the message says to delete partial copies before merging again.
- **Q4, a.** No separate probe; the sitting's repeats gather the evidence.
- **Q5, a.** The sitting passes with at least one clean merge per case (within MCP Test, and to the throwaway) out of three runs. If a case has none, the build stops at the sitting, and a probe story runs before 3.6 is done.
- **Q6, a.** For a protected destination `meta.pageIds` is left out, with counts only.
- **Q7, a.** A source with no live pages is refused before COM with `InvalidArgument`.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IApplication.cs` l.166 -- `MergeSections(source, destination)`, already declared.
- `src/OneNoteMcp.Com/ComGateway.cs` (around `UpdateHierarchyAsync`, l.427) and `src/OneNoteMcp.Core/IOneNoteGateway.cs` (l.113) -- add `MergeSectionsAsync(sourceId, destinationId, ct)` as a write: no disconnect retry, tracked if abandoned (AD-3). `HResultMap.cs` already maps `0x8004201F` (l.127), `0x8004201B` (l.123) and `0x80042030` (l.145).
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- `MergeSectionsCalls`, a failure hook, and an `OnMergeSections` that copies the source's pages into the destination under new IDs.
- `src/OneNoteMcp.Core/Writes/MoveContainerWrite.cs` and `MovePageWrite.cs` -- the model for `MergeSectionsWrite`:
  - from both: the walk-first step 1, both grants, the protected-source refusal (`Protection().IsProtected`, `Protection.Phrase`), the recycle-bin and locked refusals;
  - modelled on `MoveContainerWrite` (l.557, `SettleQuietlyAsync`) and `RestoreFromRecycleBinWrite` (l.731): the cloud settle, using the public `MoveContainerWrite.SettleTime` (l.46). Each writer has its own private copy. With this the fourth copy, extract a shared settle helper unchanged, as the task below says;
  - modelled on `MovePageWrite` (l.463, `PartialCopyFindingsAsync`, private) and its `FailureNote`: `MergeSectionsWrite` writes its own findings re-read. `MoveReadBack.cs` holds only `NewPageId` and `Why`.
- `src/OneNoteMcp.Core/Writes/HierarchyBasis.cs` -- `CheckArgument` and `Compare`.
- `src/OneNoteMcp.Core/Journal/HierarchySnapshot.cs` -- `TakeAsync`, with `Protection()`.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- `PageIds` (Q1). `ToolWarnings.cs` -- `settled`, `readBackDiffers`, `readBackFailed`, `noBasis` and `partialBasis` exist already.
- `src/OneNoteMcp.Core/UseCases/MoveHierarchyObjectUseCase.cs`, `src/OneNoteMcp.Server/Tools/MoveHierarchyObjectTool.cs`, `src/OneNoteMcp.Server/ServerSetup.cs` -- the shapes for `MergeSectionsUseCase` and `MergeSectionsTool`. Annotations: `ReadOnly=false, Destructive=false, Idempotent=false, OpenWorld=false`.
- Tests: `McpSdkBindingTests.cs` (tool count +1), `Contract/tool-contract.json`, `ToolContractTests.cs`, `NavigateToToolTests.cs`, and `PluginPackagingTests.cs` `DocumentedByRound2Entry10` (add `merge_sections`).
- Integration: `MoveHierarchyObjectIntegrationTests.cs` (its `GuardedGateway` l.567, the Vault pause, the throwaway adoption, the 2 s settle). The guarded gateway gains `MergeSections`, refused unless both IDs were made by the run, or the destination is in an adopted throwaway.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs` -- `MergeSectionsAsync` as a tracked write -- the COM call.
- [x] `src/OneNoteMcp.Core/Writes/CloudSettle.cs` (new) -- the settle extracted unchanged from `MoveContainerWrite` and `RestoreFromRecycleBinWrite`, and used by both -- one copy, not four. Their tests pass unchanged.
- [x] `src/OneNoteMcp.Core/Writes/MergeSectionsWrite.cs` (new) -- the operation, per the Always rules.
- [x] `src/OneNoteMcp.Core/UseCases/MergeSectionsUseCase.cs` (new), `src/OneNoteMcp.Server/Tools/MergeSectionsTool.cs` (new), `src/OneNoteMcp.Server/ServerSetup.cs` -- `merge_sections`. The description says: the source stays; the copies get new IDs at the end of the destination; the basis is the source's date, and it can't detect an earlier merge of the same pair; and after any `outcomeUnknown`, the model compares the destination's pages (count and names) with the source before any retry, because the copies have new IDs.
- [x] `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- `PageIds` (Q1). `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs`, `ToolContractTests.cs`, `NavigateToToolTests.cs` and `PluginPackagingTests.cs` -- the new tool and field.
- [x] `tests/OneNoteMcp.Tests/MergeSectionsToolTests.cs` (new) -- every matrix row, the call sent, the entry, the step-1 refusals before any grant dialog, and step 4's repeat.
- [x] `tests/OneNoteMcp.Integration/MergeSectionsIntegrationTests.cs` (new) -- see S1.
- [x] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine sync: AD-5 and AD-14 (the tool), AD-7 Merges (Q1–Q7; the failure re-read is by the destination's ID, not by name as the spine now says), AD-9 (`meta.pageIds`); a `.memlog.md` entry.

**Acceptance Criteria:**
- Given two plain sections in a granted notebook, when Claude merges one into the other, then the destination gains a copy of each source page under new IDs listed in `meta.pageIds`, the source keeps its ID and pages, and one journal entry holds the destination's hierarchy as it was.
- Given a destination in another notebook that isn't granted, when Claude merges into it without a grant dialog, then nothing is sent and the result is `WriteNotGranted`.
- Given a protected source, locked or not, when Claude merges it, then nothing is sent and no grant dialog is shown: `NotSupported`.
- Given a locked destination, when Claude merges into it, then nothing is sent: `SectionLocked`.
- Given a plain source and an unlocked protected destination, when Claude merges, then the copies land inside it and its journal entry is encrypted.
- Given `MergeSections` fails with `0x8004201F` after a partial copy, when the result comes back, then it is `Internal` with `outcomeUnknown`, and the message names the copies found and says to delete them before merging again.
- Given a basis whose date differs from the source section's hierarchy `lastModifiedTime`, when Claude merges, then nothing is sent: `StaleWrite`.
- Given a source or destination in a cloud notebook, when Claude merges, then the write lock is held for the 10 s settle before the read-back, and the result carries the warning `settled`.
- Given the existing move, restore and soft-delete tests, when they run, then they pass unchanged.

## Implementation Notes

- **What was built:**
  - `MergeSectionsWrite`, `MergeSectionsUseCase` and `MergeSectionsTool`.
  - `IOneNoteGateway.MergeSectionsAsync`, a default method that throws `NotSupported`, as `PublishAsync` does. `ComGateway` implements it as a write: empty IDs are refused before COM, it is never retried, and it is tracked if abandoned.
  - `ToolMeta.PageIds`, and `CloudSettle`: the failure-path settle extracted unchanged, and now used by the move, the restore and the merge.
  - Contract: 28 tools and 71 properties; `pageIds` appears in every output schema's meta. `<Version>` is unchanged.
  - Spine: AD-5, AD-7 Merges, AD-9 and AD-14, plus two memlog entries.
- **Choices beyond the plan:**
  - After a cancel or the deadline, the destination re-read runs on its own 5 s token, so the findings outlive the spent call token.
  - A cloud merge also settles after an ordinary, unambiguous failure (for example `SectionLocked`), as a move does (3.12 Q4), so the next write waits. This is kept deliberately: it costs 10 s on a refused cloud merge and keeps every cloud section write alike.
  - The source read-back warns only when pages are missing. It doesn't check the source's ID, because a children read by `SourceId` always returns that ID (quick review).
- **TDD:**
  - Written first and seen failing:
    - `CloudSettleTests` (4): CS0103, no `CloudSettle`.
    - The four `ComGatewayLifecycleTests.MergeSections_*`: CS1061, no `MergeSectionsAsync`.
    - `MergeSectionsToolTests` (54): CS0246 first. Then, with stub steps, all 54 failed with `NotImplementedException`. With the real code, 53 passed; the one failure was bad test data (a locked section merged into itself), and the data was fixed, not the code.
  - Written after their code: the `FakeGatewayTests` port count, the contract and registration test updates and the snapshot, and the integration file, which can only compile and skip here.
  - Mutation checks on `MergeSectionsWrite`, each restored afterwards:
    - recycle-bin checks removed: 7 tests failed;
    - `pageIds` returned for a protected destination: 1 failed;
    - failure settle removed and the re-resolve opted out: 2 failed;
    - locked and empty checks removed: 4 failed.
  - **The cancel-path re-read token is now proven.** `FakeGateway.GetHierarchyAsync` fails on a spent token, as the real gateway does. With the re-read changed to use the call's token, `ACancelDuringTheCall_IsCancelled_OutcomeUnknown_WithTheReReadsFindings` failed. Restored, it passes, and the rest of the suite stays green with the stricter fake.
- **Verification (2026-11-18):** build 0 warnings; unit tests 3,650 passed, 3 skipped; integration without the variable 217 passed, 123 skipped, 0 failed. The sitting passed on 2026-11-18 (see Verification).

## Plan Change Log

## Review Triage Log

- 2026-11-18, quick lens, iteration 0: 0 high, 0 medium, 3 low, 1 false. All three lows were patched; nothing was deferred.
  - **low, patch.** The `McpSdkBindingTests` comment said 68 properties while the assertion said 71. The comment now says 71.
  - **low, patch.** The source read-back warning fired on an ID mismatch with a "0 pages missing" message. The condition is now `missing > 0` only, since a children read by `SourceId` returns that ID.
  - **low, patch.** The live S1 test didn't check that exactly one journal entry was written. It now asserts one new entry file per clean merge.
  - **false.** "The cancel path skips the settle the plan places before the re-read." With the call token spent, the settle would return at once, so nothing is lost. The spine and memlog describe what the code does.

- 2026-11-18, Fable final review of the draft, low effort: 5 must fix, 8 optional, all checked against the cited code and sources.
  - Factual fixes applied:
    - the `outcomeUnknown` advice moved into the message (`ToolErrors` replaces the `userAction`);
    - the Code Map's shared-helper claims corrected, with a settle-extraction task added;
    - the recycle-bin rule placed before the kind rule;
    - the cloud settle before the failure re-read;
    - a re-read on every `outcomeUnknown`;
    - the re-read is by ID;
    - `0x80042030` noted as not expected;
    - step 3 repeats the walk;
    - test paths moved into the tasks, and three more ACs;
    - the description covers the basis's limit and comparing before a retry;
    - the sitting's per-run rules.
  - Owner decisions added as Q5 (the sitting's pass rule), Q6 (copies' IDs in a protected destination) and Q7 (an empty source). Q4 now names both earlier local failures.

## Design Notes

Considered building the merge as an `UpdateHierarchy` page move of every source page. Going with `MergeSections`, because it copies subpages with their levels and leaves the source intact (spike 3.1 S1–S3), which a page move doesn't (M1d).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the contract snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` with no integration variable -- expected: it builds, and every live test is skipped.
- TDD: write each new test first and watch it fail before writing the code.

**Sitting steps.** The main session runs these with Phill. The build never runs them, never sets the integration variable, never touches OneNote, and never reads OneNoteMcp's app-data folders.
- **S1.** MCP Test open, `MCP Test Throwaway 1112-034716 a` open, and the Ballast Vault unlocked at the start. Run `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~MergeSectionsIntegrationTests`. In sections the run makes:
  1. A source with 2 pages and a subpage is merged into a 1-page destination in MCP Test. Expected: copies at positions 2–4 under new IDs (in `meta.pageIds`), the subpage at level 2, the source untouched, one entry. Three runs, each with fresh run sections. A `0x8004201F` is asserted as `Internal` with `outcomeUnknown` and the re-read findings, and is counted, never retried. The pass rule is Q5 (a): at least one clean merge per case.
  2. The same into a run section in the throwaway (skipped if it isn't open), three runs, same rules. Spike 3.1 ran each of these two cases locally once, and both failed (S1 after a partial copy, S2 with nothing copied).
  3. With the throwaway's grant removed, the merge is `WriteNotGranted`.
  4. At the pause Phill locks the Vault. A merge of a Vault section is `NotSupported`, a merge into it is `SectionLocked`, and no `MergeSections` call is sent.
- **S2.** Through onenote-dev, Claude merges one MCP Test section into another and one MCP Test Cloud section into another, checking the sync status at every cloud step and running Sync Now at the end. Phill checks in OneNote, and on the web for cloud, that the copies are there and the sources unchanged.

**Sitting result (2026-11-18, with Phill): passed.**
- S1: `MergeSectionsIntegrationTests` passed 3 of 3, in about 9.5 minutes including the pause.
  - Within MCP Test: three clean runs. Each put 3 copies at positions 2–4 under new IDs and left the source untouched.
  - To the throwaway: run 1 returned `0x8004201F`, handled as `Internal` with `outcomeUnknown`. The re-read found the destination still at 1 page, so nothing was copied. Runs 2 and 3 were clean. The Q5 (a) pass rule (at least one clean merge per case) was met. Observed rate of `0x8004201F` for local cross-notebook merges: 1 in 3 here.
  - Without the throwaway's grant: `WriteNotGranted`.
  - With the Vault locked at the pause: merging out of it gave `NotSupported`, merging into it gave `SectionLocked`, and no `MergeSections` call was sent.
- S2, through onenote-dev:
  - In MCP Test, a 2-page section merged into a new 1-page section: `ok`, 2 `pageIds`, no warnings.
  - In MCP Test Cloud, the same: `ok`, 2 `pageIds`, `settled`.
  - Phill confirmed that both destinations held 3 pages, that the sources were unchanged, and that there was no sync error after Sync Now.
