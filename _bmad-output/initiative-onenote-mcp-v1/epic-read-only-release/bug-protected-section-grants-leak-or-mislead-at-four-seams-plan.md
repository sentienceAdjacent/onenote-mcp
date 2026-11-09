---
title: 'Protected-section grants leak or mislead at four seams'
type: 'bugfix'
ticket: '22'
created: '2026-11-09'
status: 'built'
baseline_revision: 'f79fb93a21cf95c9f7303f81edcaf5bed26252f4'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick', 'security']
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

**Scope added by Phill (2026-11-09, review pass 1):**
- **S2:** the plugin guard also blocks the whole OneNoteMcp app-data folder. Writes and edits to any file in it are denied, and so are shell commands that name it. Without this, the model can write a grant file under another name and rename it to `settings.json`.
- The version goes to `0.8.0-rc.2`.

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
- [x] `ReadAccess.cs`: R1, the `Overlaps` fail-closed rule, and `WholeResult: true` for a search started at a section or a page. R3, throw the lapsed refusal with `retryable: false`.
- [x] `OneNoteException.cs`, `ToolErrors.cs`: an optional retryable override. Every other code keeps the table's value.
- [x] `SettingsGuard.cs`: deny the uninstall/purge invocation with its own reason.
- [x] `FindPagesUseCase.cs`: evict protected pages from the cache.
- [x] Tests, one per matrix row:
  - R1: three separate tests (search at a section, search at a page, no index), plus the still-valid and rooted-unrelated rows.
  - R2: deny and allow theories.
  - R3: the flag and userAction on each lapsed path, plus a `ToolErrorsTests` override case.
  - R5: eviction, both withheld and granted.
- [x] Spine AD-6 sentence and README: name the purge. Add a memlog entry.

**Acceptance Criteria:**
- Given the fix, when the unit and integration suites run, then both pass, and every new test fails on `main` at `1efd814`.
- Given the stress harness on MCP Test against the rebuilt server, when it runs, then it shows 0 mismatches with protected sections still withheld.

## Implementation Notes

- **R1:** `ResultGrantCheck.Overlaps` treats a part as overlapping when the grant is `WholeResult`, when the tree has no index (`Root.Element is null`), or when `Find(sectionId)` is null. `CheckSearchScopeAsync` marks the start section's grant `WholeResult: true` for a search started at a protected section or at one of its pages (`WholeResult` helper). The section-root case is caught twice: by `WholeResult`, and by `Find` returning null for the root.
- **R3:** `OneNoteException` gains `bool? retryable`; `ToolErrors.From` uses it in place of the table when set, and an unknown outcome still forces false. Only the lapsed refusal in `ResultGrantCheck.Check` sets it, and only to false when an `hours` grant has passed its `expiresAt` (review fix S1): a revoked grant (`untilRevoked`, or `hours` before its end) holds again after a new grant, so it keeps the table's `true`. The refusal message says the part "may hold pages of" the section (review fix Q3), since fail-closed can't always place it.
- **R2:** `PurgeInvocation` is `GrantInvocation`'s shape with `uninstall`; checked after the settings and grant checks, with its own `PurgeReason`. Review fix S2: the guard also denies any file write into a settings folder (the resolved settings path's folder and `%APPDATA%\OneNoteMcp`) and any shell command naming one, including `$APPDATA`, `${APPDATA}`, `$env:APPDATA` and `%APPDATA%` forms, so a file can't be written there and renamed over `settings.json`. `Reason` now speaks of the folder.
- **R5:** `cache.Remove(page.Id)` at the top of the loop whenever `broad.IsProtected(page.SectionId)`, so withheld, name-only and granted pages are all evicted. It doesn't count toward `searchCache.evicted` (that counts capacity evictions).
- **Tests:** the existing lapsed tests (`ReadResult_PartFromAResultMadeUnderALapsedGrant_*`, `ReadResult_AfterARevoke_*`) now assert `Grants.Lapsed` (retryable false, `LapsedUserAction`). `VaultHarness.Read` takes `run`. The still-valid search test skips the offset read: at `inlineMaxChars: 200` an offset part has no room for a body, which is unrelated to grants.
- **Review fixes (Phill chose all, 2026-11-09):** spine AD-6 (folder block, Limits: the guard is best effort and Claude Code only), AD-9 (exception override; expired `hours` refusal not retryable), AD-19 (asks for `purge` or takes `--yes`; only the user runs it), AD-20 (fail-closed rule); memlog decision and event entries; guide skill `ReadNotGranted` row (after a new grant, repeat the original call); README skill sentence, Security bullet (folder) and agent step 6 (the plugin's guard blocks it; the step 2 command with the installed exe's path). `<Version>` is `0.8.0-rc.2`; no contract change, so contract stays 0.8.
- **Docs beyond the plan's list:** README "For agents installing this" step 6 told agents to run `uninstall --purge --yes` themselves, which the hook now denies; it now says to give the user the command. The guide skill's never-run bullet names the uninstall block.
- **Fails before the fix:** on a baseline worktree (HEAD `f79fb93`, whose `src/` and `tests/` equal `main` at `1efd814`, with only compile stubs: `PurgeReason` and an ignored `retryable` parameter), 19 tests fail: all R1/R3/R5 defect tests, the 7 purge-deny cases and the `ToolErrorsTests` override case. The rows that describe today's behaviour (grant still valid, no-index under a valid grant, the uninstall-mention allow cases) pass there by design.


## Plan Change Log

## Review Triage Log

**Pass 1 (2026-11-09):**
- Three reviews ran: Fable on the AD-6 edit (5 findings), the quick lens (8) and a security review (2). The security review found no remaining path to protected data through `read_result`.
- Phill chose all of them in `review/1-22-findings.md`, and option (a) for S2.
- All went to impl-1-22 as patches.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| F1 | AD-6 Limits never say the guard is best effort | medium | patch | Spine Limits section vs the cites at `SettingsGuard.cs:15-16` and README:73 |
| Q1 | AD-9's `ReadNotGranted` row and the AD-20 re-check bullet lag the code | medium | patch | Spine:241 and :456 |
| Q2 | The skill tells the model to retry a lapsed `read_result` | medium | patch | `SKILL.md:199` |
| F2 | AD-19 doesn't say who purges, or the confirm rule | low | patch | Spine Uninstall section; `UninstallCommand.cs:47,105-109` |
| F3 | The memlog entry under-describes the edit | low | patch | `.memlog.md:158` |
| F4 | Wording: "with every grant" | low | patch | Spine:129 |
| F5 | README:45 lags the skill | low | patch | `SKILL.md:79` |
| Q4 | README step 6 overstates the block for non-plugin installs | low | patch | README:203 |
| S1 | `retryable: false` is wrong after an untilRevoked revoke and re-grant | low | patch | `Holds` checks the key only for untilRevoked |
| Q3 | The refusal message asserts a location the check didn't establish | low | patch | The fail-closed `Overlaps` |
| S2 | The model can give itself a grant through a renamed file (since 1.20) | medium | patch (scope added by Phill) | `SettingsGuard.cs:110-114,143` |
| x1 | "Every new test fails on main" isn't met as written | false | reject | The plan's wording: tests for the still-valid rows can't fail on main |
| x2 | The no-index test duplicates the absent-section rule | low | reject | It still pins the behaviour criterion 5 asks for, and a fix would only add complexity |
| x3 | The Fable review is pending | false | reject | It ran; see F1–F5 |

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests`: all pass
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration`: unchanged (87 passed, 7 skipped)

**Stress harness run (2026-11-09, before the review fixes):** `pack-plugin.ps1` then the harness on MCP Test against `dist/publish/OneNoteMcp.exe` (written 17:38Z, server 0.8.0-rc.1 built from the fixed working tree): 620 comparisons, 0 mismatches, 2 `unstableRead`, 0 `changedDuringRun`; the 1 password-protected section withheld (3 pages withheld, `ReadNotGranted` on its page reads); 31 stored results reassembled through `read_result`, max text block 100,000 chars, 0 over the inline threshold. Exit 0.

**Manual checks:**
- Stress harness on MCP Test: `pwsh -File scripts/pack-plugin.ps1`, then the harness per `spikes/stress-harness/README.md`. Expect 0 mismatches.
- A name grep on the diff finds nothing.
- The Fable final review of the AD-6 edit is done, and Phill has chosen which findings to fix.

**Final run after the review fixes (2026-11-09, team lead):**
- `dotnet build -warnaserror`: 0 warnings.
- Unit tests: 1273 passed. Integration tests: 87 passed, 7 skipped.
- `pack-plugin.ps1` built `0.8.0-rc.2`, and `claude plugin validate --strict` passes.
- Stress harness on MCP Test against that exe: 620 comparisons, 0 mismatches, 2 unstableRead, 31 stored results, none over 100,000 characters.
