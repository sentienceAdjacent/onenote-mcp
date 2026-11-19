---
title: 'Refactor sweep (epic 3)'
type: 'refactor'
ticket: '11'
created: '2026-11-19'
status: 'built'
baseline_revision: '92997163731e2d17af7cae514171110379024ef5'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/story-refactor-sweep-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 3's builds left no frontmatter `deferred:` entries; the cleanup they asked for in their notes already shipped (`CloudSettle`, `MoveReadBack`, `JournalTargets`, `HierarchyBasis`). What remains is duplication the round-2 hierarchy writes introduced, found by reading the code, plus one deferred test-setup item: the AD-16 schema gate for `UpdateHierarchy` XML copied five times, `ReadChildrenAsync` four times, `WithNote` three times, `Invalid` five times, `SetPageLevelWrite.Why` a full copy of `MoveReadBack.Why`, `MoveContainerWrite.SettleTime` an alias of `CloudSettle.Time` with two forwarding `SettleQuietlyAsync` wrappers, two identical `CheckNotebook` bodies, and `CreatePageIntegrationTests` building `SettingsStore(path)` with no journal folder although `create_page` with `parentPageId` journals since 3.14 (deferred-work l.590, "next cleanup that touches epic 2's tests").

**Approach:** Code motion and test setup only, each identical in behaviour for every input: no message, error code, retryability, schema, COM call, tool contract or spine change. Existing suites are the guard (same counts before and after, all green); the harness's contract check and the plugin validation stay green.

## Boundaries & Constraints

**Always:**
1. One helper for the AD-16 gate, `UpdateHierarchyXml.EnsureValid(xml)` (or a name of that kind in `Core/Writes`), throwing exactly today's `Internal` exception and message; used by `HierarchyWrite`, `MoveContainerWrite`, `MovePageWrite`, `RestoreFromRecycleBinWrite`, `SetPageLevelWrite`. `CreatePageUseCase`'s own gate (a different message) stays.
2. One `ReadChildrenAsync(gateway, id, ct)` (the `GetHierarchy` Children read plus `XmlReads.Readable(HierarchyTarget.Read)`), used by the four writes.
3. `WithNote(message)` once, as a protected method on `WriteOperation` (where `FailureNote` lives), same `is { } note` test; `WritePipeline.WithNote` (it tests `IsNullOrEmpty`) is left alone.
4. One `Invalid(message, userAction)` (`InvalidArgument`, `retryable: false`) for the five writes.
5. `SetPageLevelWrite` uses `MoveReadBack.Why`; the moves' forwarding `Why` wrappers go.
6. `CloudSettle.Time` everywhere: `MoveContainerWrite.SettleTime` and the two `SettleQuietlyAsync` wrappers go; doc crefs and tests follow.
7. One `CheckNotebook` for the two moves, each passing its own `userAction` text.
8. `CreatePageIntegrationTests` gives its `SettingsStore` a journal folder in its temporary folder, as `CreateHierarchyIntegrationTests` does (compiled only; never run here).
9. `deferred-work.md`: the l.590 entry marked resolved by this sweep.

**Never:** a change to any message, `userAction`, error code, retryability, warning text, XML sent to COM, tool contract, spine, settings or the guide; the `settled` warning texts (they differ per tool), `WritePipeline.WithNote`, `CreatePageUseCase`'s schema gate, the grant-location and partial-copy logic, or the loose-section refusal order.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` l.177–183; `MoveContainerWrite.cs` l.491–498; `MovePageWrite.cs` l.414–421; `RestoreFromRecycleBinWrite.cs` l.669–676; `SetPageLevelWrite.cs` l.319–326 -- the AD-16 gate copies (identical text).
- `ReadChildrenAsync`: `MergeSectionsWrite.cs` l.473–477, `MoveContainerWrite.cs` l.606–610, `MovePageWrite.cs` l.506–510, `RestoreFromRecycleBinWrite.cs` l.771–775 (each uses its own `services.Gateway`).
- `WithNote`: `MoveContainerWrite.cs` l.617, `MovePageWrite.cs` l.525, `RestoreFromRecycleBinWrite.cs` l.787; base `WriteOperation.cs` l.57 (`FailureNote`).
- `Invalid`: `MovePageWrite.cs` l.536, `MoveContainerWrite.cs` l.625, `MergeSectionsWrite.cs` l.493, `RestoreFromRecycleBinWrite.cs` l.792, `SetPageLevelWrite.cs` l.383.
- `Why`: `SetPageLevelWrite.cs` l.386–391 = `MoveReadBack.Why` (`MoveReadBack.cs` l.34–39); wrappers `MovePageWrite.cs` l.542, `MoveContainerWrite.cs` l.631.
- Settle: `MoveContainerWrite.cs` l.46, l.125, l.286, l.557, l.614; `RestoreFromRecycleBinWrite.cs` l.152, l.303, l.731, l.779; tests `CloudSettleTests.cs` l.18, `MoveContainerToolTests.cs` l.200–201, l.360, l.566, `RestoreFromRecycleBinToolTests.cs` l.703.
- `CheckNotebook`: `MovePageWrite.cs` l.442–452, `MoveContainerWrite.cs` l.540–550 (`GrantKeys.Comparer` is `OrdinalIgnoreCase`, `GrantKeys.cs` l.10).
- `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs` l.199, l.242; pattern `CreateHierarchyIntegrationTests.cs` l.252–253.

## Tasks & Acceptance

**Execution:**
- [x] Record the unit and integration test counts on the baseline.
- [x] Items 1–7 in `src/OneNoteMcp.Core/Writes/`; tests that name `SettleTime` follow item 6.
- [x] Item 8 in `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs`.
- [x] Item 9 in `deferred-work.md`.

**Acceptance Criteria:**
- Given the branch, when the unit and integration suites run, then every test passes and the counts equal the baseline's.
- Given the branch, when the harness's `--contract-only` check runs against a `contract-ok` fake, then it passes on 29 tools; and `claude plugin validate --strict` on the packed stage passes.
- Given `git diff` of `src/`, when every changed string literal is listed, then none differs from the baseline's (messages and texts unchanged).

## Implementation Notes

- Built directly (no implementation subagent, per the coordinator's standing rule). Baseline at 9299716: unit 3783 passed, 3 skipped (3786); integration 220 passed, 126 skipped (346). After the sweep: the same counts, all green; build 0 warnings.
- New `src/OneNoteMcp.Core/Writes/HierarchyWriteSteps.cs` (`EnsureSchemaValid`, `ReadChildrenAsync`, `Invalid`, `CheckMoveNotebook`), each moved unchanged; `WriteOperation.WithNote` (protected). The five writes lose their copies; `SetPageLevelWrite` and the moves call `MoveReadBack.Why`; `MoveContainerWrite.SettleTime` and both `SettleQuietlyAsync` wrappers are gone (`CloudSettle.Time`, `CloudSettle.QuietlyAsync`), with the tests that named `SettleTime` and `CloudSettleTests`' alias check updated. Net 113 lines added, 226 removed across 12 files.
- `CheckMoveNotebook` uses `GrantKeys.Comparer` for both moves: `MovePageWrite` compared with `StringComparison.OrdinalIgnoreCase`, the same comparison.
- No string literal in `src/` changed (checked over the diff's changed lines).
- `CreatePageIntegrationTests` gets `Store(path)` with a temporary journal (compiled only). Harness `--contract-only` against a `contract-ok` fake: "contract ok: 29 tools". `claude plugin validate --strict dist/stage/onenote`: passed.

## Plan Change Log

## Review Triage Log

- 2026-11-19, quick lens: no behaviour change found (each moved message, code, retryability, userAction, call order, gateway and comparison checked). One finding: the baseline counts weren't written in the plan (low, patched: recorded in Implementation Notes).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` and `tests/OneNoteMcp.Integration` (no variables) -- all pass, counts as on the baseline.
- The harness's `--contract-only` against a `contract-ok` fake -- exit 0; `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Not in this sweep (each changes a message, an order or a rule, or adds coverage; left for a later story if wanted):** the restore's `settled` text saying "after such a move"; one text for the three `settled` warnings; `CreatePageUseCase`'s gate message; `WritePipeline.WithNote`'s empty-note test; a shared `NotebookChanged` refusal; the loose-section refusal order (deferred-work l.624); a page-level `MediaFile` hash (l.630); entry 15's missing path-mode use-case tests (l.618).
