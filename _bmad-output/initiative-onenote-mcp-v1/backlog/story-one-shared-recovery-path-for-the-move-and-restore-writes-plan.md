---
title: 'One shared recovery path for the move, section-move and restore writes'
type: 'refactor'
ticket: '17'
created: '2026-11-21'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `MovePageWrite`, `MoveContainerWrite` and `RestoreFromRecycleBinWrite` each carry their own `PartialCopyFindingsAsync` after `0x80042004`, and they have drifted: page copies match names `Ordinal`, the container copy `OrdinalIgnoreCase`; the page and restore copies hide copy IDs in a protected target, the container copy doesn't check. The `settled` warning has three texts, and the sibling writes borrow `PartialCopyHResult`, `OperationName` and `ProtectedPositionUserAction` from `MovePageWrite`.

**Approach:** One Core helper does the best-effort re-read and builds the findings sentence for all three; one `settled` builder and user action serve the section move, the restore and the merge; the borrowed constants move to one shared class. Behaviour is unchanged except where the copies disagreed.

## Boundaries & Constraints

**Always:** each write sends the same XML, keeps its error code, HRESULT, `outcomeUnknown: true`, `retryable: false`, journal entries and the `FailureNote` naming them; the cloud settle still runs before the re-read (3.12 Q4); a re-read failure only adds to the message (never replaces the original failure); a protected target section gives counts only (AD-21 Messages); no name is ever quoted.

**Never:** new recovery behaviour; changes to `WritePipeline` step 7 beyond what Q3 decides; the merge's own failure re-read except the `settled` text; a touch to OneNote or integration tests.

## I/O & Edge-Case Matrix

| Scenario | State after `0x80042004` | Findings say |
|---|---|---|
| Page move, copy in unprotected target | 1 new page, same name | still/no longer in source; "1 new page(s) with its name (<id>)" |
| Page move or page restore, protected target | 1 new page | count only, no ID |
| Section move, copy in target | new section, same name | "1 new section(s) with its name (<id>)" |
| Section restore | by ID only | "<id> is in / isn't in <target>" |
| Re-read throws | either section | "<id> couldn't be re-read (<why>)", original error kept |
| Name differs only in case | page vs section | per Q1 |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` l.263-268 catch, l.443-486 `PartialCopyFindingsAsync` (Ordinal, protected → count); constants l.49, l.52, l.94.
- `src/OneNoteMcp.Core/Writes/MoveContainerWrite.cs` l.240-247 catch (settle first), l.534-577 findings (OrdinalIgnoreCase, adds "holds <id> and"), l.105 `SettledUserAction`, l.580 `SettledWarning`; uses `MovePageWrite.OperationName` l.226/229.
- `src/OneNoteMcp.Core/Writes/RestoreFromRecycleBinWrite.cs` l.273-279 catch, l.742-776 findings (target only; section by ID, page as the page move), l.131/779 settled; borrows `ProtectedPositionUserAction` l.636.
- `src/OneNoteMcp.Core/Writes/MergeSectionsWrite.cs` l.86/473 settled only.
- `src/OneNoteMcp.Core/Writes/MoveReadBack.cs` (`Why`, `NewPageId` Ordinal) and `CloudSettle.cs` (`Time`, `QuietlyAsync`): reuse.
- `Protection.IsProtected(HierarchyTarget)` is true only for a Section target, so the container copy's missing check can never fire today (notebook/group targets; protected sections and groups holding one are refused at l.408).
- Tests: `MoveHierarchyObjectToolTests` l.679, `MoveContainerToolTests` l.204/526/554, `RestoreFromRecycleBinToolTests` l.433/1020, `MergeSectionsToolTests` l.266.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/Writes/PartialCopyReRead.cs` (new) -- `internal static`: `HResult`, `FindingsAsync(gateway, protection, verb, source?, target, objectId, kind, name, ct)`: optional source re-read by ID, target re-read for new same-kind same-name children (comparison per Q1), object-by-ID in target, counts only when the target is protected -- one copy
- [ ] `src/OneNoteMcp.Core/Writes/MoveWrites.cs` (new) -- `OperationName`, `ProtectedPositionUserAction` moved from `MovePageWrite` -- one shared place
- [ ] `src/OneNoteMcp.Core/Writes/CloudSettle.cs` -- `SettledUserAction` + `SettledWarning(string subject)` -- one text
- [ ] the four writes -- call the helpers, delete the copies and per-class settled constants
- [ ] `tests/OneNoteMcp.Tests/PartialCopyReReadTests.cs` (new) -- pin the message for page move, section move, page restore, section restore; protected target counts only for each; the Q1 comparison; a re-read that throws
- [ ] the four tool test files -- point at the shared constants; list each assertion changed and why in Implementation Notes

**Acceptance Criteria:**
- Given each of the three writes hits `0x80042004`, when the re-read runs, then the error code, HRESULT, `outcomeUnknown` and journal note are unchanged and the findings come from `PartialCopyReRead`.
- Given a cloud section move, restore or merge, then its `settled` warning carries `CloudSettle.SettledUserAction`.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then 0 warnings and all pass.

## Open Questions

1. **Name comparison.** (a) `Ordinal` everywhere; (b) `OrdinalIgnoreCase` everywhere; (c) by kind in the one helper: pages `Ordinal`, sections and groups `OrdinalIgnoreCase`. Evidence: OneNote allows same-named pages (permanent-delete plan review, l.265), and a copy keeps the exact title; `MoveReadBack.NewPageId` and OneMore's `FindMovedPageId` (`SearchServices.cs` l.190, `==`) match pages exactly. Sections are `.one` files: OneNote refuses a sibling's name (`0x80042017`, spike 5.1) and our clash checks (`HierarchyRules.Named`, `MoveContainerWrite` l.467, restore l.664) ignore case. (c) changes nothing that runs today. Recommended: (c).
2. **One `settled` text.** (a) one user action, message templated by subject ("The move of X", "The restore of X", "The merge of section S into section D"); (b) one fixed message naming no object. Recommended: (a).
3. **"Toward the write pipeline".** (a) shared helper called from each write's `CallAsync` catch, `WritePipeline` untouched; (b) a `WriteOperation` hook the pipeline calls on `0x80042004`, so it owns the rethrow with `outcomeUnknown`. (b) changes the pipeline for every write. Recommended: (a), record (b) as deferred work.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass
