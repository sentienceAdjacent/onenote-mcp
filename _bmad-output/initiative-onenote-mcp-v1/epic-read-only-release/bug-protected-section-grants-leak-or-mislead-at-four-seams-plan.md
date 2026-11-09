---
title: 'Protected-section grants leak or mislead at four seams'
type: 'bugfix'
ticket: '22'
created: '2026-11-09'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The epic 1 retrospective found four defects where AD-21's read grants meet older code. Each one is in the ticket `bug-protected-section-grants-leak-or-mislead-at-four-seams.md`, with its reproduction and acceptance criteria 1–6.
- **R1:** a stored broad result is still served after its grant ends.
- **R2:** the plugin hook allows `uninstall --purge`.
- **R3:** a lapsed grant is reported as retryable.
- **R5:** protected page text stays in the search cache.

**Approach:**
- The stored-result grant check fails closed.
- The hook denies the purge.
- A lapsed-grant refusal carries an explicit `retryable: false`.
- Search evicts protected pages from the cache.
- Each fix gets tests that fail before it and pass after it.

## Boundaries & Constraints

**Always:**
- **Fail closed (AD-21):** when `read_result` can't tell whether a stored part holds a granted section's pages, it treats the part as holding them.
- **Spine and memlog:** amend AD-6's guard sentence to name the purge. That spine edit gets the Fable final review before the PR. Record it with `memlog.py append`.
- **Unit tests only:** they use `VaultHarness`/`FakeGateway`, and none touches OneNote.
- **Names:** never quote real notebook names. MCP Test names are fine.

**Never:**
- Changing the per-code retryable table, or any other code's flag.
- Registering the span tree's root ID as a `ref`. The root stays `/`, as AD-20 requires.
- Changing what a still-valid grant allows.
- Changing tool arguments or the contract snapshot.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error handling |
|---|---|---|---|
| Search at a granted section | `find_pages` at the Vault, under an hours or untilRevoked grant; result stored; grant revoked or expired | Every `read_result` part (`expand /`, `ref`, `run`) fails | `ReadNotGranted`, `retryable: false` |
| Search at a page | Same as above, but starting at a Vault page | Same as above | Same as above |
| Stored without an index | A broad result stored with only `/`, under a grant that has since lapsed | Every part fails | Same as above |
| Granted section absent from the result | The grant lapsed, and the section has no element in the result | Every part fails (fail closed) | Same as above |
| Grant still valid | Any of the rows above, before the grant ends | Parts are served as today | — |
| Unrelated parts of a rooted broad result | A notebook-rooted hierarchy whose lapsed section is a child element | Only parts overlapping that section fail, as today | — |
| Hook: purge | A Bash or PowerShell command running `OneNoteMcp[.exe] uninstall …`, in every form the grant check recognises | Denied, saying the user runs it | — |
| Hook: mention | `echo uninstall`, or a path that merely contains `uninstall` | Allowed | — |
| Cache: protected page | A cached page whose section is now protected, withheld or granted (including one whose every term is in its name) | Evicted on the next search | — |

</frozen-after-approval>

## Code Map

- **R1, the check:**
  - `src/OneNoteMcp.Core/Access/ReadAccess.cs:521-530`, `ResultGrantCheck.Overlaps`: the fail-open `Find(...) is null`. Fail closed when the tree has no index, meaning only `/` exists (`Root.Element is null`), or when `Find` returns null.
  - `:484-510`, `Check`: already loops over the grants.
  - `:190-220`, `CheckBroad`: sets `WholeResult: false`.
  - `:227-248`, `CheckSearchScopeAsync`: for a page start (:239) and a section start (:245), mark the start section's grant `WholeResult: true`.
- **R1, background:**
  - `ElementSpans.cs:206-207`: the root is `/`, and its ID isn't indexed.
  - `ElementSpans.cs:173-178`: the `Text` fallback.
  - `ResultDelivery.cs:58-65` and `:85-92`: how stored results are built.
  - `SearchResults.cs:61` passes `broad.ResultGrants`.
  - `GetHierarchyUseCase.cs:54-60` also uses `CheckBroad`, so the no-index rule covers it too.
  - `ReadResultUseCase.cs:86,96,116` are the only callers of `Check`, so nothing else changes.
- **R2:**
  - `src/OneNoteMcp.Server/Hooks/SettingsGuard.cs:135` has `GrantInvocation`.
  - `:114` has `RunsGrant`, and `:51-55` is `Decide`.
  - `:22-24` has `GrantReason`, and `:12-14` is the class doc.
  - Add a `PurgeInvocation` regex for `uninstall` (a bare `uninstall` only prints usage, so denying it is harmless) and a `PurgeReason`.
- **R3:**
  - `src/OneNoteMcp.Core/OneNoteException.cs`: add an optional `retryable` override.
  - `src/OneNoteMcp.Core/Results/ToolErrors.cs:35`: use the override when it's set.
  - `ReadAccess.cs:504-507`: throw with `retryable: false`. `LapsedUserAction` (:90-92) already says to repeat the original call.
- **R5:**
  - `src/OneNoteMcp.Core/UseCases/FindPagesUseCase.cs`: at the top of the page loop (before `:80`), evict when `broad.IsProtected(page.SectionId)`.
  - `PageTextCache.Remove` (`PageTextCache.cs:127-136`) already exists.
- **Tests:**
  - `tests/OneNoteMcp.Tests/ProtectedSectionToolTests.cs`, existing:
    - `ReadResult_*` :552-600
    - `ResultGrantCheck_ABroadResult_*` :603, the template for the no-index test
    - `FindPages_NeverCachesProtectedPages_EvenUnderAGrant` :129
  - `ProtectedSectionKit.cs`:
    - `VaultHarness(inlineMaxChars: 200)` forces a result to be stored (:292).
    - `.Grant`, `.Writer.RevokeReadGrant`, `.FindPages`, `.FindMeta`, `.Read`, `Clock`.
    - `Grants.NotGranted` asserts `retryable: true` (:408). Give lapsed refusals a `retryable: false` variant.
    - `VaultGateway.SearchAsync` (:199-209) gives a section root for a search at `V.VaultId`, and a page root for one at `V.VaultPageId`.
  - `SettingsGuardTests.cs:53-69`: the deny and allow theories.
  - `ToolErrorsTests.cs`: the table stays. Add one test for the override.
- **Docs:**
  - Spine AD-6: the guard bullet names the purge too.
  - `README.md`: the sentence on what the plugin blocks.
  - `plugin/skills/guide/SKILL.md`, only if it lists what the guard denies.

## Tasks & Acceptance

**Execution:**
- [ ] `ReadAccess.cs`: R1, the `Overlaps` fail-closed rule, and `WholeResult: true` for a search started at a section or a page. R3, throw the lapsed refusal with `retryable: false`.
- [ ] `OneNoteException.cs`, `ToolErrors.cs`: an optional retryable override. Every other code keeps the table's value.
- [ ] `SettingsGuard.cs`: deny the uninstall/purge invocation with its own reason.
- [ ] `FindPagesUseCase.cs`: evict protected pages from the cache.
- [ ] Tests, one per matrix row:
  - R1: three separate tests (search at a section, search at a page, no index), plus the still-valid and rooted-unrelated rows.
  - R2: deny and allow theories.
  - R3: the flag and userAction on each lapsed path, plus a `ToolErrorsTests` override case.
  - R5: eviction, both withheld and granted.
- [ ] Spine AD-6 sentence and README: name the purge. Add a memlog entry.

**Acceptance Criteria:**
- Given the fix, when the unit and integration suites run, then both pass, and every new test fails on `main` at `1efd814`.
- Given the stress harness on MCP Test against the rebuilt server, when it runs, then it shows 0 mismatches with protected sections still withheld.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests`: all pass
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration`: unchanged (87 passed, 7 skipped)

**Manual checks:**
- Stress harness on MCP Test: `pwsh -File scripts/pack-plugin.ps1`, then the harness per `spikes/stress-harness/README.md`. Expect 0 mismatches.
- A name grep on the diff finds nothing.
- The Fable final review of the AD-6 edit is done, and Phill has chosen which findings to fix.
