---
title: 'A write counts a read grant only from when it was given'
type: 'bugfix'
ticket: '9'
created: '2026-11-17'
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

**Problem:** Every write that needs a protected section's read grant "held at the call's start" asks `ReadAccess.HoldsReadGrant(section, callStart)` or `HoldsReadGrantAt(key, callStart)`, which only check that a grant is unexpired at `callStart`. A stored grant saved, or a `once` window opened, after the call started still counts. Measured in the backlog 7 build (2026-11-17): a FakeGateway test whose step-3 read moved the clock 1 s and opened a `once` window saw the reorder go through. The spine already says what should happen (AD-7/AD-21: "no grant that would let a read of that section pass at the call's start"); the code doesn't.

**Approach:** A grant counts for a write only when it was given at or before the call's start and is unexpired then: a stored grant's `GrantedAt <= at`, and a `once` window's `GrantedAt` (set when it opens) `<= at`. One new `ReadAccess` method carries this, and every write-side "held at the call's start" check uses it. The location checks a rename or move makes (`HoldsReadGrantAtLocation`, and `HoldsReadGrantAt` beneath it) keep counting any unexpired grant: they decide where a section may land, and must not miss a grant given a moment ago.

## Boundaries & Constraints

**Always:** Fail closed. Reads, `read_result`, `list_grants` and the grant dialog are unchanged. Messages and `userAction`s are unchanged: a grant given mid-call gets the same `ReadNotGranted` as no grant, and a retry passes. `GrantedAt` is read as stored (UTC, ms precision, truncated, so a grant given before the call never reads as later; a stored grant given under 1 ms after the call's start can still count, so tests move the clock by at least 1 ms).

**Never:** Touch OneNote in tests. Prompt from a write. Change the settings file's shape. Change `HoldsReadGrantAtLocation` or the location semantics of `HoldsReadGrantAt`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Grant before the call | stored or `once`, `GrantedAt <= callStart`, unexpired | Passes, as today | None |
| Grant given during the call | stored grant saved, or `once` window opened, in the step-3 read hook after `callStart` | Refused, as with no grant | `ReadNotGranted` (or the counts-only / withheld path where that caller degrades instead of refusing) |
| Expired at call start | `hours` grant expired before `callStart` | Refused, as today | `ReadNotGranted` |
| Location check | rename or move onto a key whose grant appears after the call's start | Still refused, as today | `InvalidArgument` |
| Stored `grantedAt` in the future | a clock stepped back, or a hand-edited file | Reads pass; every write that needs the grant is refused until the clock passes it (accepted, fail-closed) | `ReadNotGranted` |
| Grant re-given mid-call | a grant held at call start is replaced (new `GrantedAt`) during the call | See Open Questions 1 | |

</frozen-after-approval>

## Open Questions

1. **A grant re-given during the call** (fills the matrix's last row before approval). `OnceWindows.Open` replaces a window, and saving a stored grant for the same key replaces it, both with a new `GrantedAt`. A grant held at the call's start and renewed mid-call then reads as given after it. Options: (a) accept it: the write is refused fail-closed and a retry passes. This is a known departure from the spine's literal wording (a read would have passed at the call's start), and the window is longer than it looks: the call's start is taken before the write-grant dialog, so a call can last minutes; (b) keep the earlier `GrantedAt` when a window is replaced while still open (a stored grant can't keep it without changing the file's meaning). Recommended: (a); (b) adds state to fix a case nobody will meet.
2. **Spine and memlog.** The spine's wording already says "no grant that would let a read of that section pass at the call's start", so the fix brings the code to it, except for 1(a)'s renewed-grant case. Options: (a) no spine edit; the plan and the commit record the change; (b) add one clause to AD-21 "Where the check runs": "a grant counts from when it was given (`grantedAt`, or a `once` window's opening)", plus a memlog event. Two other lanes (3.8, 3.5) are editing the spine and memlog now. Recommended: (a) if Phill takes 1(a) as a recorded departure in this plan; (b) if he wants the spine to state the rule exactly. Either way the team lead sequences any spine edit.

## Code Map

- `src/OneNoteMcp.Core/Access/ReadAccess.cs` l.129-151 -- `HoldsReadGrant(section, at)` (writes only), `HoldsReadGrantAt(key, at)` (location semantics, also under `HoldsReadGrantAtLocation` l.159); `Stored()` l.260 checks expiry only. Both stored grants and windows are `ProtectedSectionGrant` with `GrantedAt` (`Settings/SettingsSnapshot.cs` l.110).
- `src/OneNoteMcp.Core/Access/OnceWindows.cs` -- `IsOpen(key, now)` checks expiry only; `Open` replaces. Windows are created in `GrantDialog.cs` l.105 with `GrantedAt = now`.
- Write-side callers to switch (all pass the call's start): `Writes/PageWrite.cs` l.118, l.187 (page writes, `delete_page_content`); `UseCases/PermanentDeleteUseCase.cs` l.243, l.322; `UseCases/PublishUseCase.cs` l.422; `Writes/HierarchyWrite.cs` l.648 (reorder); `Writes/MovePageWrite.cs` l.432 (position); `UseCases/CreatePageUseCase.cs` l.329 (subpage, 3.14); `Writes/SetPageLevelWrite.cs` l.344. Leave `HierarchyWrite.cs` l.478 and `MoveContainerWrite.cs` l.341, l.526 (location checks) alone.
- Tests: `tests/OneNoteMcp.Tests` -- the backlog 7 hook pattern (`gateway.OnGetHierarchy` advancing `clock` and opening a window with `readAccess.CheckSectionAsync(... FakePrompt.Accept(..., Once))`) in `UpdateHierarchyToolTests.cs`; each caller's own test file holds its protected-section fixtures.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/Access/ReadAccess.cs`, `OnceWindows.cs` -- add `HoldsReadGrantGivenBy(string key, DateTimeOffset at)`: a stored grant or open window for `key` with `GrantedAt <= at` and unexpired at `at`; `OnceWindows` gains the matching query. `HoldsReadGrant(section, at)` uses it (it has write callers only). Fold `HoldsReadGrantAt` into `HoldsReadGrantAtLocation` (or make it private), keeping its location meaning, so no write can call it again. -- one place for the rule.
- [ ] `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` l.432, `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` l.329, `src/OneNoteMcp.Core/Writes/SetPageLevelWrite.cs` l.344 -- switch `HoldsReadGrantAt(…, callStart)` to `HoldsReadGrantGivenBy`. The six `HoldsReadGrant` sites (`PageWrite.cs` l.118, l.187; `PermanentDeleteUseCase.cs` l.243, l.322; `PublishUseCase.cs` l.422; `HierarchyWrite.cs` l.648) change through it with no edit. -- every "held at the call's start" check at once.
- [ ] `tests/OneNoteMcp.Tests/ReadGrantTimingTests.cs` (new; `ReadAccess` over a temporary settings store and a fake clock, as `ProtectedSectionToolTests.cs` builds it, with `ProtectedSectionKit.cs` helpers) -- unit tests for the matrix's first three rows on both stored grants and windows, the future-`grantedAt` row, and `HoldsReadGrantAtLocation` still true for a grant given after `at`. Stored and window grants are both covered here. -- the rule itself.
- [ ] `tests/OneNoteMcp.Tests/{UpdatePageContentToolTests,DeletePageContentToolTests,PermanentDeleteToolTests,PublishToolTests,UpdateHierarchyToolTests,MoveHierarchyObjectToolTests,CreatePageToolTests,SetPageLevelToolTests}.cs` -- one test per refusing caller with a `once` window opened in the step-3 read hook after the call's start (as backlog 7 measured it), seen failing first; plus the two degrade paths: an `update_page_content` with no hash and no `objectID` whose grant arrives mid-call gives counts-only warnings, `readBackWithheld` and no `meta.basis`, and the permanent delete's l.243 path the same way. In `UpdateHierarchyToolTests.cs` replace the l.929-931 comment ("can't be told apart") with the reorder's new test. -- the Verify line.

**Acceptance Criteria:**
- Given a write that needs a read grant, when the grant is given after the call starts, then the write answers as if there were none; when it was given before, the write passes as today.
- Given the build, when `dotnet build -warnaserror` and the unit tests run, then both pass with no existing test's meaning changed.

## Verification

TDD: write each test first and watch it fail before the code change. Unit tests only: never touch OneNote, never set the integration-test variable, never touch the server's per-user settings or data folders.

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

## Implementation Notes

- The ticket's caller list and Verify line predate 3.14's `create_page` subpage and `set_page_level` checks and say every check refuses; this plan covers those two and the two degrade paths. The ticket is refreshed when it is next refined (a build never writes a ticket file).

## Plan Change Log

## Review Triage Log

Fable final review (low effort, 2026-11-17), 9 findings, all checked against the code and applied: F1 tests for the two degrade paths (`PageWrite` `NamesIds`, permanent delete l.243); F2 file paths and the true site count (three edited sites, six through `HoldsReadGrant`); F3 the matrix's last row is filled from Open Questions 1's answer before approval; F4 Open Questions 1(a) named as a departure from the spine's literal wording, with the real window (the call's start precedes the write-grant dialog); F5 future `grantedAt` row; F6 the sub-millisecond note and ≥1 ms clock moves; F7 `HoldsReadGrantAt` folded or made private; F8 the stale reorder test comment; F9 per-caller tests use a `once` window, stored grants in the unit tests, and the ticket refresh noted.
