---
epic: epic-round-2-external-and-structural
date: 2026-11-19
verdict: accepted-with-open-items
criteria: declared
headless: true
---

# Retrospective: Round 2 — exports, move and merge

**Accepted with open items** by Phill on 2026-11-19 (Inbox). The tag, action item 1, is tracked as the open item for Done when 5. The headless run's machine verdict was **rejected**, because Done when 5 wasn't met; Phill's decision overrides it (see the Acceptance verdict section). Every code criterion is met in the evidence.

## Epic summary

- **Epic.** `epic-round-2-external-and-structural`, at `main` 4abcaa1. Tickets, in build order:
  - done: 3.1, 3.2, 3.3, 3.4, 3.14, 3.12, 3.5, 3.6, 3.13, 3.7, 3.15, 3.8, 3.10, 3.11;
  - dropped: 3.9 (merge_files; E4 was closed by a dated Decision, see epic file l.38).
  
  `pending_tickets` is empty, and no ticket is still at `built`.
- **Ranges.** Each plan runs from its `baseline_revision` to the next baseline in ancestry order. Sibling baselines were ordered by commit date (3.12/3.13, 3.14/3.7, 3.5/3.15). The last range was cut at 082201a, the merge of 3.11 (PR #104), which leaves out the later board chores c3e131b and 786e30d and the backlog-only commits 13bfe79, ddf3245 and 70f2483.
  - Whole epic: 2843d5d..082201a. 238 commits (70 merges, 49 of them measured), 263 files, +38,896 / −3,821 lines; src +9,589, tests +22,026.
  - Per-plan ranges and `git_evidence.py` totals:

    | Ref | Range | Commits | Files | +/− |
    |---|---|---|---|---|
    | 3.1 | 2843d5d..afe3101 | 50 | 92 | 6518/912 |
    | 3.2 | afe3101..38e5b54 | 8 | 12 | 359/152 |
    | 3.3 | 38e5b54..b6ae4fa | 7 | 28 | 2760/258 |
    | 3.4 | b6ae4fa..2d26988 | 1 | 20 | 2970/121 |
    | 3.12 | 2d26988..1b0384e | 3 | 3 | 112/2 |
    | 3.13 | 1b0384e..2af944a | 20 | 36 | 3776/159 |
    | 3.14 | 2af944a..935ed20 | 15 | 37 | 3209/233 |
    | 3.7 | 935ed20..64c3f29 | 30 | 45 | 5095/388 |
    | 3.5 | 64c3f29..4be192b | 8 | 7 | 181/27 |
    | 3.15 | 4be192b..556875a | 35 | 95 | 7606/697 |
    | 3.6 | 556875a..2982032 | 21 | 32 | 3774/211 |
    | 3.8 | 2982032..e97afd7 | 33 | 69 | 5263/472 |
    | 3.10 | e97afd7..9299716 | 11 | 22 | 649/94 |
    | 3.11 | 9299716..082201a | 2 | 14 | 258/226 |
  - **Attribution caveat.** Lanes ran in parallel, so per-plan ranges don't line up with tickets. 3.12's feature commit (265c9fc) and 3.13's (eaa2497) fall in the 3.14 range, and the 3.12 and 3.5 ranges hold only plan and docs commits. The whole-epic range also carries backlog 1–13 work. The aggregate views therefore use the whole-epic range. Merges measured (49) fall short of merges in range (70), so churn sums undercount.
- **Evidence inventory.**
  - Available: the epic file (Done when 1–6, E1–E10); every plan with its Review Triage Log, Verification and Plan Change Log; one story file (3.14); the spine and memlog; git history.
  - Missing:
    - session logs (not in the repo, so the process lessons rest on the Review Triage Logs only);
    - retrospectives for epics 2 and 5;
    - a .NET dependency-graph tool run (the architecture delta comes from `.csproj` diffs and `using` greps).

## Findings

Sources are relative to the repo root; `E/` is `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/`.

### Duplication map

- **D1. The partial-copy re-read is written three times, and the copies have drifted apart.**
  - Sources: `src/OneNoteMcp.Core/Writes/MovePageWrite.cs:443`, `MoveContainerWrite.cs:534`, `RestoreFromRecycleBinWrite.cs:725`.
  - Drift: the name comparison is `Ordinal` in the page and restore copies but `OrdinalIgnoreCase` in the container copy. The page and restore copies hide copies' IDs in a protected target; the container copy doesn't check.
  - 3.11 (0d66102) consolidated nearby helpers but not this one.
  - Disposition: **defer** (refactor story). Lesson: the plan of the second user of a pattern should name the helper it extracts, not a copy.
- **D2. `SettledWarning` and `SettledUserAction` exist three times.**
  - Sources: `MergeSectionsWrite.cs:86,473`, `MoveContainerWrite.cs:105,580`, `RestoreFromRecycleBinWrite.cs:131,762`.
  - 3.11 listed it as "Not in this sweep" (`E/story-refactor-sweep-plan.md:88`).
  - Disposition: **defer**, together with D1.
- **D3. Sibling writes read shared constants from `MovePageWrite`.**
  - The constants are `PartialCopyHResult`, `OperationName` and `ProtectedPositionUserAction`; they are read at `MoveContainerWrite.cs:226,229,240` and `RestoreFromRecycleBinWrite.cs:266,619`.
  - Disposition: **defer**, with D1. Lesson: shared HRESULTs and advice belong in one shared place (`HierarchyWriteSteps`, `ToolErrors`).

### Size growth

- **S1. `RestoreFromRecycleBinWrite.cs` reached 800 lines in one story.**
  - New files over the epic: `RestoreFromRecycleBinWrite.cs` 800, `MoveContainerWrite.cs` 607, `MovePageWrite.cs` 525, `MergeSectionsWrite.cs` 498, `PublishUseCase.cs` 458, `OpenPackageUseCase.cs` 409, `SetPageLevelWrite.cs` 379.
  - It is one operation with four concerns (matching, protected placement, partial-copy re-read, settle). D1 and D2 would shrink it.
  - Disposition: **accept**, and watch.
- **S2. Tests outgrow source about 2.3 to 1.** The contract snapshot `tests/OneNoteMcp.Tests/Contract/tool-contract.json` alone grew 4,182 lines. Disposition: **accept**.

### Architecture delta

- **A1. Layering held: checked and clean.**
  - No `src` `.csproj` reference changed.
  - Core and Xml have no `using OneNoteMcp.Com` or `using OneNoteMcp.Server`.
  - New composition stays inside Core: restore reuses `MoveContainerWrite` (`src/OneNoteMcp.Core/UseCases/RestoreFromRecycleBinUseCase.cs:76`).
  - Disposition: **accept**.

### Pattern divergence

- **P1. Each write runs its own step-7 settle and partial-copy recovery, not the pipeline.** `WritePipeline` already owns `IsAmbiguous` and the settle-time lock hold. Disposition: **defer**, with D1.
- **P2. "Sent, then abandoned: may or may not have applied" was rebuilt in three stories.**
  - 3.14: placement (`E/story-make-a-page-a-subpage-plan.md`, Review Triage Log).
  - 3.5: Fable finding 4, which calls itself "the 3.14 pattern" (`E/story-restore-from-recycle-bin-plan.md`, Review Triage Log).
  - 3.8: a cancel during the page wait.
  
  Disposition: **accept**. Lesson: make it an explicit AD-7 step-7 rule and a planning checklist line.

### Spec-to-implementation reconciliation

- **R1. E3 and Done when 1 don't match the as-built `open_package`.**
  - The epic says the package unpacks "into a new folder under an export root" (`E/epic-round-2-external-and-structural.md:37,55`).
  - As built (3.8 Q3), it unpacks into the opened-copies folder under `%LOCALAPPDATA%`, and a full path in an export root is refused (3.8 plan, Verification S2).
  - The spine is already reconciled.
  - Disposition: **fix now** (proposed reconciliation of the epic file).
- **R2. Done when 5 is not met: no release tag.**
  - `Directory.Build.props:4` reads `0.9.0-rc.1`, but the newest tag is `v0.8.0-rc.2` (`git tag`). The epic Notes expect Phill to tag after the sweep (l.133).
  - Disposition: **fix now**.
- **R3. 3.10's live verification isn't recorded.**
  - The plan records sitting part A only (`E/story-skill-readme-and-harness-for-round-2-plan.md:104`). Parts B (the harness on MCP Test) and C (E8's five unlocked paths) have no results.
  - `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md:634` still says "Pending 3.10's live step".
  - The sitting did run, since bugs 14–16 were filed from it (13bfe79).
  - Done when 4's unlocked paths therefore rest on unit tests plus an unrecorded sitting.
  - Disposition: **fix now** (record the results).
  - **Resolved (2026-11-19, 18c9a98):** the plan's Verification now has "Sitting results (2026-11-19)": parts A–D passed, E8's paths were run live with the Vault unlocked (C1–C6), and the harness had 0 mismatches. `deferred-work.md` marks the entry resolved.
- **R4. 3.7's sitting S1 wasn't re-run after the locked-section refusal (1ac37c3).** Source: `E/story-publish-notebooks-and-section-groups-plan.md:142`. Disposition: **fix now**: re-run it, or record an acceptance.
- **R5. Open backlog bugs touch the epic's requirements.**
  - Backlog 14: publish on a locked protected section answers `ReadNotGranted` (E2, E8).
  - Backlog 15: the search inline cap (AD-20).
  - Backlog 16: restore offers a used journal entry (E7).
  
  All three are filed (PR #103). Disposition: **defer**.
- **R6. 3.15's path-mode tests are missing.** The gaps are `update_page_content`, the `create_page` subpage path, `set_page_level`, `restore_from_journal` under a root, and `one:OpenSections` through a read tool. Sources: `deferred-work.md:618`; re-listed in `E/story-refactor-sweep-plan.md:88`. Disposition: **defer**.
- **R7. E5 says a move "drops moved pages from the search cache", but `MoveContainerWrite` drops none.**
  - The cache holds text keyed by page ID and `lastModifiedTime` (`src/OneNoteMcp.Core/UseCases/PageTextCache.cs:6-7`), so nothing goes stale.
  - This is inferred, not tested.
  - Disposition: **accept**; reword E5.
- **R8. 3.11's Verify asks for "harness 0 mismatches"; only the fake-contract run was done.** Source: `E/story-refactor-sweep-plan.md:72`. A live run needs OneNote. Disposition: **accept**.
- **Met with evidence:**
  - E1 (export-root guard and target);
  - E2, plus the section-EMF refusal (3.13 Plan Change Log);
  - E4 (dated Decision);
  - E5 for pages;
  - E6 (source untouched; the hierarchy snapshot stands);
  - E7;
  - E9;
  - E10 (3.14 sitting passed).

### Diff-scope review (bmad-review: adversarial, edge-case, verification-gap)

Scope: `git diff 2843d5d..4abcaa1 -- src tests`, weighted toward the boundaries between tickets.

- **V1. The re-resolve under the lock (3.12) is untested for page writes and for `restore_from_recycle_bin` (verification gap).**
  - Location: `src/OneNoteMcp.Core/Writes/WritePipeline.cs:215-218`. It serves `PageWrite.cs:104` (update and delete page content, soft and permanent delete) and `RestoreFromRecycleBinWrite.cs:229-230`.
  - Only create, move, merge, set-level and update-hierarchy tests cover a notebook that changed mid-call.
  - A regression would re-open the epic-2 create-page race for page writes, unseen.
  - Disposition: **fix now** (add the tests).
- **V2. The opened-copies sweep deletes a copy by folder creation time (adversarial).**
  - Location: `src/OneNoteMcp.Core/Writes/OpenedCopies.cs:157-161` (`Directory.GetCreationTimeUtc`) and `:140-145` (`Directory.Delete(…, recursive: true)`).
  - A copy opened more than 7 days ago, edited since, and now closed is removed whole at the next server start, edits included.
  - The sweep follows Phill's cleanup answer (3.8). Whether "age" meant creation time or last change is unverified.
  - Disposition: **open question** for Phill.
- **V3. `ReResolvesNotebooks` is an opt-out from a security guarantee with nothing enforcing its replacement.**
  - Location: `src/OneNoteMcp.Core/Writes/WriteOperation.cs:24`. Only `MovePageWrite.cs:131` and `MoveContainerWrite.cs:147` override it, each re-checking through `HierarchyWriteSteps.CheckMoveNotebook`.
  - Disposition: **defer** (latent).
- **V4. Restore's refusals say "password-protected" for a section protected only by its path.**
  - Location: `RestoreFromRecycleBinWrite.cs:472` (`Protection.IsProtected`) and the messages at `:518-522`, `:533`.
  - Merge, move and publish use `Protection.Phrase`; restore doesn't.
  - Disposition: **fix now** (small).
- **V5. A section protected by path at restore time but not at delete time loses its by-name match.**
  - `HierarchySnapshot.cs:104` writes `SectionKey` only when the section was protected at the time. So `SoftDeleteEntries.cs:239-242` finds no entry, and `TargetOf` answers `ObjectNotFound`.
  - Phill accepted this outcome for 3.5 on 2026-11-18 (`E/story-restore-from-recycle-bin-plan.md`, Decisions).
  - Disposition: **accept**.
- **V6. `FileExists` messages name the export root.**
  - Location: `src/OneNoteMcp.Core/Writes/ExportTarget.cs:82,186`, against that class's own doc at `:23` ("never a root").
  - `meta.outputPath` shows the target deliberately, so either the message or the doc is wrong.
  - Disposition: **fix now** (align them).
- **V7. Q7's restore-then-move is two pipeline runs, with the lock released in between.**
  - Location: `src/OneNoteMcp.Core/UseCases/RestoreFromRecycleBinUseCase.cs:72-74`. One call can take two deadlines and show a second grant dialog.
  - The outcome is still reported correctly.
  - Disposition: **accept** (as Phill decided Q7).

### Process themes (Review Triage Logs)

- **T1. The security review caught three AD-21 leaks that the quick lens had passed.**
  - 3.4: a `position` refusal leaked the page count (PR #67).
  - 3.12: grant keys missed percent-encoding (PR #72).
  - 3.5: a section matched by name inherited a read grant.
  
  Each fix led to a sibling fix in older code (backlogs 7 and 8). Lesson: add an AD-21 "what can a caller with no read grant learn?" probe to the quick lens or to the plan template's Boundaries.
- **T2. Live sittings found what `FakeGateway` couldn't.**
  - 3.3 S1 (a locked section's page listed);
  - 3.13 S2 (a section's EMF holds one page);
  - 3.7 (the pack dialog);
  - 3.15 S1 (loose section, now backlog 12);
  - 3.10 (backlog 14–16).
  
  Lesson: teach the fake each newly observed OneNote behaviour in the same patch.
- **T3. Fable reviews drive most spine fixes, even at low effort.**
  - 3.2: 14 findings.
  - 3.14 spine sync: 16 findings, 5 must-fix.
  - 3.5 draft: 15 findings, 8 must-fix; 3.5 spine sync: 14 findings, 6 must-fix.
  - 3.6 draft: 13 findings, 5 must-fix.
  
  Keep them.
- **T4. Some tests were written after the code, despite every plan's TDD line.**
  - 3.7: a notebook-exemption test that couldn't fail.
  - 3.8: 3 of 7 review tests passed before their patch.
  - 3.14 and 3.5: tool tests were written after the code, then mutation-checked.
  - 3.15: path-mode tests deferred.
  
  Lesson: triage should ask for a red-run or mutation note on any guard test.
- **T5. Builds departed from frozen plan text five times, each recorded in its plan:**
  - 3.14: the step-1 grant order (Phill);
  - 3.5: three journal entries, not two;
  - 3.7: the recycle-bin fallback;
  - 3.8: `completedSteps` in the message;
  - 3.10: one acceptance criterion.
  
  Accept.
- **T6. Parallel lanes break baseline-to-baseline attribution (see the Epic summary).** Lesson: record each plan's final merge commit (an `end_revision`) when it is marked done.

## Behavior verification

This run exercised no runtime behavior: it is a headless retrospective with no OneNote access. The plans record the live sittings:
- passed: 3.3, 3.4, 3.12, 3.13, 3.14, 3.5, 3.6, 3.7 (S1 not re-run after 1ac37c3, R4), 3.15 and 3.8;
- 3.10: part A only recorded (R3).

## Previous-retro follow-through

Nothing to follow through on: no retrospective file exists for the previous epic in build order, `epic-hierarchy-writes-and-deletes`. Epic 2 (`epic-safe-writes`) has none either. The epic 1 retrospective (`epic-read-only-release-retrospective.md`) was not re-checked against this epic.

## Action items

All are proposed; nothing was applied.

1. **Cut the release for Done when 5:** tag `v0.9.0-rc.1` through the epic 1 pipeline (R2). Owner: Phill.
2. **Record 3.10's sitting parts B and C** in its plan, and close `deferred-work.md:634` (R3). Owner: main session, from Phill's sitting notes. **Resolved (2026-11-19, 18c9a98).**
3. **Re-run 3.7's sitting S1 on the locked-section build, or record its acceptance** (R4). Owner: Phill, with the main session.
4. **Reconcile E3 and Done when 1 in the epic file** to the as-built `open_package` destination, an opened-copies folder (R1). This is a spec reconciliation. Owner: the main session, applied with Phill's approval.
5. **Reword E5's cache sentence** to pages only (R7). This is a spec reconciliation. Owner: the main session, with Phill's approval.
6. **Add re-resolve tests for page writes and `restore_from_recycle_bin`** (V1). This is remediation. Owner: the next build lane (one small backlog story).
7. **Use `Protection.Phrase` in restore's refusals** (V4), **and align `ExportTarget`'s `FileExists` messages with its doc** (V6). This is remediation. Owner: the next build lane (one small backlog bug).
8. **A refactor story for D1, D2, D3 and P1:** one shared partial-copy re-read, one `settled` text, shared constants out of `MovePageWrite`, and step-7 recovery moved toward the pipeline. Owner: a backlog story for Phill to schedule.
9. **Process: an AD-21 "no read grant" probe** in the quick lens or the plan template (T1). Owner: Phill (BMAD customization).
10. **Process: teach the fake each newly observed OneNote behaviour, and require a red-run or mutation note for guard tests** (T2, T4). Owner: Phill (review checklist).
11. **Process: record `end_revision` on a plan when it is marked done** (T6). Owner: Phill (bmad-ticket customization).
12. **Settle the opened-copies sweep's notion of "age"** (V2): creation time or last change. Owner: Phill (a decision, then a backlog bug if it changes).

## Acceptance verdict

- **Machine verdict: rejected.** The criteria are declared (Done when 1–6). `pending_tickets` is empty.
  - Done when 1, 2, 3, 4 and 6 are met in the evidence:
    - 1, with its wording out of date (R1);
    - 4, through unit tests, the 3.5, 3.6 and 3.7 integration sittings, and the 3.10 sitting, whose parts B and C aren't recorded (R3).
  - Done when 5 is not met: the round-2 methods all have a tool or a dated Decision (E4), and the skill and README were updated (3.10), but no release has shipped (R2).
  - No blocking code finding is open: V1–V7 are deferrable or small.
- **Human decision (Phill, 2026-11-19, Inbox): accepted-with-open-items.** It overrides the machine verdict. The open item is action item 1, the `v0.9.0-rc.1` tag for Done when 5, which stays tracked. The other open findings are ticketed or deferred, as the Action items show.

## Open questions

1. **Acceptance:** answered. Phill accepted the epic with open items on 2026-11-19, with the tag tracked.
2. **V2:** should the opened-copies sweep age a copy by creation time or by last change?
3. **R4:** does 3.7's S1 need a re-run after the locked-section refusal, or is its unit coverage enough?

## Assumptions

- The epic reference `epic-round-2-external-and-structural` resolved to the folder of the same name. `pending_tickets` was empty; no confirmation was invented.
- The last plan's range was cut at 082201a (the 3.11 merge), not at HEAD, which leaves out later board and backlog commits.
- Sibling baselines were ordered by commit date where neither was an ancestor of the other.
- The machine verdict **rejected** was rendered without a human decision, on Done when 5 alone.
- Every action item above is proposed, not applied. Findings from subagents (aggregate views, diff review) were re-checked at their primary sources before routing. Checked directly: R2 (tags and `Directory.Build.props:4`), R3 (`deferred-work.md:634`), V2 (`OpenedCopies.cs:157-161`), V4 (`RestoreFromRecycleBinWrite.cs:518-522`).
