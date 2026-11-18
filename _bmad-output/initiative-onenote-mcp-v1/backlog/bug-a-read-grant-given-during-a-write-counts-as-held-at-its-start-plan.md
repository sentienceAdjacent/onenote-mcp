---
title: 'A write counts a read grant only from when it was given'
type: 'bugfix'
ticket: '9'
created: '2026-11-17'
status: 'built'
baseline_revision: '030499a91a3cd56301000431bad84e6960cf5280'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every write that needs a protected section's read grant "held at the call's start" asks `ReadAccess.HoldsReadGrant(section, callStart)` or `HoldsReadGrantAt(key, callStart)`, which only check that a grant is unexpired at `callStart`. A stored grant saved, or a `once` window opened, after the call started still counts. Measured in the backlog 7 build (2026-11-17): a FakeGateway test whose step-3 read moved the clock 1 s and opened a `once` window saw the reorder go through. The spine already says what should happen (AD-7/AD-21: "no grant that would let a read of that section pass at the call's start"); the code doesn't.

**Approach:** A grant counts for a write only when it was given at or before the call's start and is unexpired then: a stored grant's `GrantedAt <= at`, and a `once` window's `GrantedAt` (set when it opens) `<= at`. One new `ReadAccess` method carries this, and every write-side "held at the call's start" check uses it. The location checks a rename or move makes (`HoldsReadGrantAtLocation`, and `HoldsReadGrantAt` beneath it) keep counting any unexpired grant: they decide where a section may land, and must not miss a grant given a moment ago.

## Boundaries & Constraints

**Always:** Fail closed. Reads, `read_result`, `list_grants` and the grant dialog are unchanged. Messages and `userAction`s are unchanged: a grant given mid-call gets the same `ReadNotGranted` as no grant, and a retry passes. `GrantedAt` is read as stored (UTC, ms precision, truncated, so a grant given before the call never reads as later; a stored grant given under 1 ms after the call's start can still count, so tests move the clock by at least 1 ms).

**Decisions (Phill, 2026-11-17):** Q1 (a): a grant renewed during the call counts as given after its start, so the write is refused and a retry passes. Q2 (a): no spine edit; this plan records it as a deliberate departure from the spine's literal wording ("no grant that would let a read of that section pass at the call's start"), since a renewed grant did let a read pass then.

**Never:** Touch OneNote in tests. Prompt from a write. Change the settings file's shape. Change `HoldsReadGrantAtLocation` or the location semantics of `HoldsReadGrantAt`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Grant before the call | stored or `once`, `GrantedAt <= callStart`, unexpired | Passes, as today | None |
| Grant given during the call | stored grant saved, or `once` window opened, in the step-3 read hook after `callStart` | Refused, as with no grant | `ReadNotGranted` (or the counts-only / withheld path where that caller degrades instead of refusing) |
| Expired at call start | `hours` grant expired before `callStart` | Refused, as today | `ReadNotGranted` |
| Location check | rename or move onto a key whose grant appears after the call's start | Still refused, as today | `InvalidArgument` |
| Stored `grantedAt` in the future | a clock stepped back, or a hand-edited file | Reads pass; every write that needs the grant is refused until the clock passes it (accepted, fail-closed) | `ReadNotGranted` |
| Grant re-given mid-call | a grant held at call start is replaced (new `GrantedAt`) during the call | Refused, as with no grant; a retry passes (Phill, 2026-11-17, Q1 a) | `ReadNotGranted` |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Access/ReadAccess.cs` l.129-151 -- `HoldsReadGrant(section, at)` (writes only), `HoldsReadGrantAt(key, at)` (location semantics, also under `HoldsReadGrantAtLocation` l.159); `Stored()` l.260 checks expiry only. Both stored grants and windows are `ProtectedSectionGrant` with `GrantedAt` (`Settings/SettingsSnapshot.cs` l.110).
- `src/OneNoteMcp.Core/Access/OnceWindows.cs` -- `IsOpen(key, now)` checks expiry only; `Open` replaces. Windows are created in `GrantDialog.cs` l.105 with `GrantedAt = now`.
- Write-side callers to switch (all pass the call's start): `Writes/PageWrite.cs` l.118, l.187 (page writes, `delete_page_content`); `UseCases/PermanentDeleteUseCase.cs` l.243, l.322; `UseCases/PublishUseCase.cs` l.422; `Writes/HierarchyWrite.cs` l.648 (reorder); `Writes/MovePageWrite.cs` l.432 (position); `UseCases/CreatePageUseCase.cs` l.329 (subpage, 3.14); `Writes/SetPageLevelWrite.cs` l.344. Leave `HierarchyWrite.cs` l.478 and `MoveContainerWrite.cs` l.341, l.526 (location checks) alone.
- Tests: `tests/OneNoteMcp.Tests` -- the backlog 7 hook pattern (`gateway.OnGetHierarchy` advancing `clock` and opening a window with `readAccess.CheckSectionAsync(... FakePrompt.Accept(..., Once))`) in `UpdateHierarchyToolTests.cs`; each caller's own test file holds its protected-section fixtures.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Access/ReadAccess.cs`, `OnceWindows.cs` -- add `HoldsReadGrantGivenBy(string key, DateTimeOffset at)`: a stored grant or open window for `key` with `GrantedAt <= at` and unexpired at `at`; `OnceWindows` gains the matching query. `HoldsReadGrant(section, at)` uses it (it has write callers only). Fold `HoldsReadGrantAt` into `HoldsReadGrantAtLocation` (or make it private), keeping its location meaning, so no write can call it again. -- one place for the rule.
- [x] `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` l.432, `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` l.329, `src/OneNoteMcp.Core/Writes/SetPageLevelWrite.cs` l.344 -- switch `HoldsReadGrantAt(…, callStart)` to `HoldsReadGrantGivenBy`. The six `HoldsReadGrant` sites (`PageWrite.cs` l.118, l.187; `PermanentDeleteUseCase.cs` l.243, l.322; `PublishUseCase.cs` l.422; `HierarchyWrite.cs` l.648) change through it with no edit. -- every "held at the call's start" check at once.
- [x] `tests/OneNoteMcp.Tests/ReadGrantTimingTests.cs` (new; `ReadAccess` over a temporary settings store and a fake clock, as `ProtectedSectionToolTests.cs` builds it, with `ProtectedSectionKit.cs` helpers) -- unit tests for the matrix's first three rows on both stored grants and windows, the future-`grantedAt` row, and `HoldsReadGrantAtLocation` still true for a grant given after `at`. Stored and window grants are both covered here. -- the rule itself.
- [x] `tests/OneNoteMcp.Tests/{UpdatePageContentToolTests,DeletePageContentToolTests,PermanentDeleteToolTests,PublishToolTests,UpdateHierarchyToolTests,MoveHierarchyObjectToolTests,CreatePageToolTests,SetPageLevelToolTests}.cs` -- one test per refusing caller with a `once` window opened in the step-3 read hook after the call's start (as backlog 7 measured it), seen failing first; plus the two degrade paths: an `update_page_content` with no hash and no `objectID` whose grant arrives mid-call gives counts-only warnings, `readBackWithheld` and no `meta.basis`, and the permanent delete's l.243 path the same way. In `UpdateHierarchyToolTests.cs` replace the l.929-931 comment ("can't be told apart") with the reorder's new test. -- the Verify line.

**Acceptance Criteria:**
- Given a write that needs a read grant, when the grant is given after the call starts, then the write answers as if there were none; when it was given before, the write passes as today.
- Given the build, when `dotnet build -warnaserror` and the unit tests run, then both pass with no existing test's meaning changed.

## Verification

TDD: write each test first and watch it fail before the code change. Unit tests only: never touch OneNote, never set the integration-test variable, never touch the server's per-user settings or data folders.

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

## Implementation Notes

- Built 2026-11-17. `ReadAccess.HoldsReadGrantGivenBy(key, at)` and `OnceWindows.OpenedBy(key, at)` carry the rule; `HoldsReadGrant` goes through it; `HoldsReadGrantAt` is folded into `HoldsReadGrantAtLocation` (location meaning kept). Three sites switched (MovePageWrite l.432, CreatePageUseCase l.329, SetPageLevelWrite l.344); location checks untouched.
- Tests: `ReadGrantTimingTests.cs` (13 unit tests), `MidCallGrant` helpers in `ProtectedSectionKit.cs`, one mid-call-window test per refusing caller, the two degrade paths, and three re-given-under-the-lock tests that make the step-3/step-4 rechecks (PageWrite l.118, PermanentDelete l.322) the refusing check. With the old rule put back, every new-behaviour test failed (15, then the 4 recheck/degrade tests); with the fix, 3362 passed.
- A once window can't be re-given while still open (a read passes without asking); the re-given case is a window that lapses mid-call and is reopened, refused per Q1 (a).
- The recheck under the lock can refuse only when step 1 passed, i.e. a grant held at the start and re-given mid-call; with no grant at the start, step 1 refuses first. So page writes' recheck tests use the re-given case, not backlog 7's step-3 window (the reorder has no step-1 check).
- Publish (l.422), move position (l.432), set_page_level (l.344) and create_page (l.329) rechecks each have a re-given-under-the-lock test too (team lead, 2026-11-17), each seen failing on the old rule.
- The ticket's caller list and Verify line predate 3.14's `create_page` subpage and `set_page_level` checks and say every check refuses; this plan covers those two and the two degrade paths. The ticket is refreshed when it is next refined (a build never writes a ticket file).

## Plan Change Log

## Review Triage Log

Quick lens, pass 1 (2026-11-17): 1 medium patched, 1 low patched, 1 false.

- **medium, patched:** the per-caller tests opened the window on the call's first `GetHierarchy`, so for page writes and the permanent delete step 1 refused and the under-lock rechecks (PageWrite l.118 refusal, PermanentDelete l.322) had no regression test. Added three re-given-under-the-lock tests (the only state in which the recheck refuses), each seen failing on the old rule.
- **low, patched:** the `NoHashOrObjectId` degrade test now opens the window under the lock, after step 1, so `NamesIds` is set at l.118 (`Assert.Single(holds)`).
- **false:** soft delete of a page (`SoftDeletePage : PageWrite`) needs no read grant: a page in a protected section is refused `NotSupported` before any grant check.

Fable final review (low effort, 2026-11-17), 9 findings, all checked against the code and applied: F1 tests for the two degrade paths (`PageWrite` `NamesIds`, permanent delete l.243); F2 file paths and the true site count (three edited sites, six through `HoldsReadGrant`); F3 the matrix's last row is filled from Open Questions 1's answer before approval; F4 Open Questions 1(a) named as a departure from the spine's literal wording, with the real window (the call's start precedes the write-grant dialog); F5 future `grantedAt` row; F6 the sub-millisecond note and ≥1 ms clock moves; F7 `HoldsReadGrantAt` folded or made private; F8 the stale reorder test comment; F9 per-caller tests use a `once` window, stored grants in the unit tests, and the ticket refresh noted.
