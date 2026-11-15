---
title: 'Refactor sweep (epic 2)'
type: 'refactor'
ticket: '13'
created: '2026-11-15'
status: 'built'
baseline_revision: '8652c1948dca9194835f4dd134f11be017cdec4d'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Epic 2's builds and reviews left cleanup items in `deferred-work.md` marked for this sweep: wording in two tool descriptions and three `userAction` strings that disagree with the guide skill (one of them invites routing an image around a declined file-path dialog), `ErrorCode` doc comments that drifted from spine AD-9/AD-21, and an integration test whose assertion can't tell a hash refusal from a date refusal. The owner also named the `z` re-stack issue and a test-journal hazard.

**Approach:** Text, comment and test-assertion changes only; no logic, schema, error code, retryability or COM change. In scope:
1. `FilePathsUnconfirmed.DeclinedUserAction` drops "or, for an image, send it as inline one:Data (base64) instead" (deferred-work l.559). Assumption (low risk, same reason): the race-case `WriteNotGranted` userAction in `PageWrite` (unconfirmed path after the page changed) drops its "Or, for an image, send it as inline…" sentence too.
2. `update_page_content` description: a file on disk goes by its full local path; inline `one:Data` only for a small image already in hand (l.550; matches `plugin/skills/guide/references/page-writes.md` recipe 4 and SKILL.md l.18). Assumption: `PageWrite.PathUserAction` and `RefusedHtmlUserAction` get the same alignment (a refused network/device/relative path: ask the user for a local copy).
3. `delete_page_content` description: "the page changed since that read (its date, or that Outline) fails with StaleWrite" (l.553).
4. `ErrorCode.InvalidArgument` doc: the input-schema check runs before the SDK binds the call (story 2.14, AD-9); `ErrorCode.ReadNotGranted` doc also names the writes AD-21 gates (l.565).
5. `UpdatePageContentIntegrationTests.StaleBasis_OnAPageThatIsntDisplayed_IsStaleWrite` also asserts the message names the Outline's objectID, as 2.10's twin does (l.348). Compiled only; never run here.

Out of scope, returned as questions: the `z` re-stack (l.379, l.562; every code option changes what is sent or every hash, so it is a behaviour change); the protected-section `noBasis` wording (l.375; five call sites share the phrase). Already done on main, no change: the test-journal guard (`tests/OneNoteMcp.Tests/TestEnvironment.cs`, commit 8c3d852, with `TestEnvironmentTests`). Not cleanup (bugs or design, left in deferred-work): l.186, l.198, l.237, l.318.

</intent-contract>

## Implementation Notes

Oneshot: about 30 changed lines across six source/test files plus `deferred-work.md`; all text, comments or one assertion.

- Files: `src/OneNoteMcp.Core/Access/FilePathDialog.cs` (both userActions), `src/OneNoteMcp.Core/Writes/PageWrite.cs` (`PathUserAction`, `RefusedHtmlUserAction`, race-case `WriteNotGranted`), `src/OneNoteMcp.Server/Tools/UpdatePageContentTool.cs`, `src/OneNoteMcp.Server/Tools/DeletePageContentTool.cs`, `src/OneNoteMcp.Core/ErrorCode.cs`, `tests/OneNoteMcp.Integration/UpdatePageContentIntegrationTests.cs`, `deferred-work.md` (five entries marked resolved).
- Decision beyond the Intent's wording (low risk, same alignment): `NoDialogUserAction` keeps the inline route but now says "If the user agrees, a small image can go inline", matching recipe 4's "A client that can't show dialogs" line, so the Verification grep for the old clause finds nothing.
- `DeclinedUserAction` also gains "Don't send the file another way unless the user asks for that", recipe 4's own words.
- No unit test asserted any changed string; the contract snapshot strips descriptions, so neither changed.

## Plan Change Log

## Review Triage Log

### 2026-11-15 — Review pass
- verdicts: 5 findings — high 0, medium 1, low 3, false 0, maybe-false 0 (1 rejected: its fix edits this plan)
- findings:
  - `[medium]` `[patch]` Spine AD-7 l.200-201 still prescribe the inline `one:Data` route after a declined path and with no dialog, so the new userActions drift from the binding spine — confirmed at the spine lines; the code follows Phill's 2.12 Q5 (a) and recipe 4, so the fix is the project's usual spine-sync entry: added "Story 2.13 refactor sweep" entry to `deferred-work.md` (spine edits go through a spine story).
  - `[low]` `[reject]` Verification's manual grep expects a hit in `NoDialogUserAction` that no longer exists — true, but its fix edits this build's plan (Implementation Notes already record the grep finds nothing).
  - `[low]` `[patch]` `ErrorCode.InvalidArgument` doc omits the AD-9 remap of `0x80042017` hrInvalidName while deferred-work calls it aligned — confirmed (spine l.335, l.352); added the remap to the comment.
  - `[low]` `[patch]` `update_page_content` description limited inline to "a small image you already have as data", while `NoDialogUserAction` and recipe 4 also allow it with no dialog if the user agrees — confirmed; description now adds "or, if the user agrees, on a client that can't show dialogs".
  - `[low]` `[patch]` `PathUserAction`'s "ask the user for a local copy" is also used for `InvalidCharacters` and `TooManyLinks` refusals (`PageWrite.Unconfirmed`, l.613-615) — confirmed; text now also names control, invisible and wildcard characters and asks for a local copy at a plain local path.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass (descriptions are stripped from the contract snapshot, so it stays unchanged).
- `dotnet build spikes/stress-harness -c Release` -- expected: builds. The stress harness live run (MCP Test, "0 mismatches") needs OneNote and is left to Phill.

**Manual checks (if no CLI):**
- `grep -rn "for an image, send it as inline" src` finds only `NoDialogUserAction` (a client without dialogs; the skill allows inline there if the user agrees).

## Auto Run Result

- **Summary:** cleanup only, no logic change. Wording that disagreed with the guide skill (the declined-path userAction no longer suggests sending the image inline; the no-dialog, path-refusal, HTML-refusal and race-case userActions and the `update_page_content` / `delete_page_content` descriptions now match recipe 4 and the real stale check), `ErrorCode` doc comments aligned with AD-9/AD-21, and the 2.9 stale-write integration test now proves the hash caught the change.
- **Files:**
  - `src/OneNoteMcp.Core/Access/FilePathDialog.cs` — declined and no-dialog userActions.
  - `src/OneNoteMcp.Core/Writes/PageWrite.cs` — `PathUserAction`, `RefusedHtmlUserAction`, race-case `WriteNotGranted` userAction.
  - `src/OneNoteMcp.Server/Tools/UpdatePageContentTool.cs` — image/file sentence of the description.
  - `src/OneNoteMcp.Server/Tools/DeletePageContentTool.cs` — stale-check sentence of the description.
  - `src/OneNoteMcp.Core/ErrorCode.cs` — `InvalidArgument` and `ReadNotGranted` doc comments.
  - `tests/OneNoteMcp.Integration/UpdatePageContentIntegrationTests.cs` — asserts the StaleWrite message names the Outline (compiled, not run).
  - `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — five entries resolved; new AD-7 spine-sync entry.
- **Review:** quick lens, 5 findings: 4 patched (1 medium, 3 low), 0 deferred, 1 rejected (its fix edits this plan: the manual grep line expects a hit that the no-dialog rewording removed; the grep now finds nothing, as Implementation Notes say).
- **Follow-up review recommended:** false (patched: high 0, medium 1, low 3).
- **Verification:** `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings 0 errors; `dotnet test tests/OneNoteMcp.Tests` 2886/2886; `dotnet build spikes/stress-harness -c Release` builds; `grep -rni "send it as inline" src` finds nothing.
- **Not run (safety limits):** integration suite, stress-harness live run on MCP Test (the ticket's "0 mismatches"); no code path a read uses changed, but the live run is Phill's.
- **Residual risks:** userAction and description text is model-facing; a model may phrase its next step differently. The `z` re-stack and the protected-section `noBasis` wording stay open as questions; the test-journal guard was already on main (8c3d852).
