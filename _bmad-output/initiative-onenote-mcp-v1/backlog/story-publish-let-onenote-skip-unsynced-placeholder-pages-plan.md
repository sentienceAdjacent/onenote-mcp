---
title: 'publish: opt-in allowUnsyncedPages lets the user skip unsynced placeholder pages'
type: 'feature'
ticket: '11'
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

**Problem:** `publish` refuses a notebook package (`onepkg`) holding a section whose `areAllPagesAvailable` is `false` (3.7 S3). Its message says the section "isn't fully downloaded to this PC" and its advice says to open those sections so they download. The backlog 10 sitting (2026-11-17) showed the flag marks a page stub added from another computer that never synced: nothing on this PC can download it, so both are wrong, and the user has no way to pack the rest of the notebook in one go.

**Approach:** Keep the refusal as the default and correct its message and advice: a page in the section hasn't synced from the computer that added it, so packing would drop it; sync it from that computer, or delete its placeholder page in OneNote, then try again; or export the other sections one by one. Add an optional, user-visible `publish` argument `allowUnsyncedPages` (default false), valid only for a notebook package. With it, the server skips that one refusal and calls `Publish`; OneNote shows its modal "cannot pack … Continue anyway?" dialog, which blocks the COM call until the user answers it. On success the result carries a new warning naming the affected section IDs. A wait past `publishSeconds` is the gateway's own timeout, with AD-3's consequences (below). The server never answers or automates the dialog (AD-11).

## Boundaries & Constraints

**Always:** The opt-in is checked after the target's kind is known and after today's kind and format refusals (a section group, an unknown ID, a notebook in another format, a section as EMF keep their codes); then it is refused (`InvalidArgument`, step 1, before the lock) for anything but a notebook package. The locked-section refusal (`SectionLocked`) still comes first and the opt-in never skips it. The affected sections are read in step 1 and again under the lock; the under-the-lock set is the one the warning names, built once in `CallAsync`. Messages, warnings and `userAction`s carry section IDs only (AD-9), capped like `HierarchyWrite.IdList` (at most 20, then a count). The tool description marks the opt-in "user-visible" (AD-11) and says OneNote will show a dialog only the user can answer, and that the model passes it only when the user agreed to leave those pages out.

**Never:** Answer, dismiss or automate the dialog. Change the default refusal's behaviour (only its wording). Relax the locked-section refusal. Touch OneNote in tests. Record a real notebook, section or page name or ID in any repo file, commit, plan or findings note. Edit the spine or the guide skill here: the spine amendment goes to the team lead to sequence, and the guide's text goes to story 3.10.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Default, unsynced section | notebook package, a section (any depth) with `areAllPagesAvailable` false, no opt-in | Refused before `Publish`; new message, section ID named | `NotSupported`, new `userAction` |
| Opt-in, user answers Yes | same, `allowUnsyncedPages: true`; the fake's `Publish` writes the file | `ok`, `meta.outputPath`, warning `unsyncedPagesLeftOut` naming the affected section IDs from the under-the-lock read | None |
| Opt-in, the set changes under the lock | step 1 sees one unsynced section, the locked read sees another | The warning names the under-the-lock set | None |
| Opt-in, no answer within `publishSeconds` | the fake's `PublishAsync` throws the gateway's `Timeout` (the COM call blocked) | `Timeout`, `outcomeUnknown`; the message and `userAction` say OneNote's dialog is probably still waiting for the user, that a package landing later lacks the unsynced pages of the named sections, and not to retry until it is answered | `Timeout`, `outcomeUnknown` |
| Opt-in, user answers No | `Publish` fails with an HRESULT | Today's handling of an unmapped HRESULT (`Internal`, generic `userAction`) until the sitting measures it; Open Questions 1 | `Internal` (interim) |
| Opt-in with a locked section too | a locked section and an unsynced one | Refused before `Publish` | `SectionLocked` |
| Opt-in, nothing unsynced | every section available | Packed as today, no new warning | None |
| Opt-in on a page or section | any format | Refused in step 1, before the lock | `InvalidArgument` |
| Opt-in on a notebook in another format | format not onepkg | Today's notebook-format refusal first | `NotSupported` |

</frozen-after-approval>

## Open Questions

1. **What a No does.** Unknown until the sitting: probably an HRESULT the gateway doesn't map, which today falls to `Internal` (`HResultMap.cs` l.90-91). Options: (a) ship with that interim result, measure No in the sitting, and if it gives a stable HRESULT, map it in this ticket (an AD-9 HRESULT row, a code such as `NotSupported` with "the user declined OneNote's dialog", not retryable; not `Cancelled`, which `ToolErrors.cs` l.56 makes `outcomeUnknown` for every non-read); (b) hold the build until a sitting has measured No. Recommended: (a).
2. **When it may merge and release.** The sitting needs a stub (question 5), which may take a while. Options: (a) merge on unit tests; the ticket stays open, and no release tag includes the opt-in until the sitting has run (the wording fix can ship); (b) merge only after the sitting. Recommended: (a).
3. **A dialog nobody can see, and the gateway's state while it waits.** While the dialog is open the COM call is blocked: past `publishSeconds` the gateway times it out, resets its STA worker (`ComGateway.cs` l.897-901) and tracks an abandoned write, so until the user answers, every new non-read call returns `ComBusy`, and a second timed-out write leaves the gateway `ComUnavailable` (AD-3, spine l.78-83). If OneNote has no visible window, nobody can answer. Options: (a) accept AD-3's behaviour for the opt-in, and refuse the opt-in up front when OneNote has no visible window (the existing no-window check, backlog 3); (b) (a) plus a longer deadline for an opt-in publish; (c) accept both as known limits, documented. Recommended: (a).
4. **Spine amendment (team lead to sequence with the 3.15 and 3.5 lanes).** Proposed: AD-7 Formats by scope: correct the S3 refusal's reason (a page stub added from another computer that never synced; backlog 10 sitting) and add the opt-in ("unless `allowUnsyncedPages` is true: `Publish` runs, OneNote's dialog asks the user, and a success carries `unsyncedPagesLeftOut`; Phill, 2026-11-17, backlog 11"); AD-11 Visible calls: the opt-in is user-visible and the skill passes it only on the user's say-so; AD-11 Exports (l.480): the old advice and "untested (backlog 10)"; AD-21 Exports (l.750): "not fully downloaded" wording; AD-3: a publish that waits on a dialog by design (question 3's outcome); AD-9: the warning `unsyncedPagesLeftOut`, and No's HRESULT row once measured. Options: (a) as proposed, applied before the build; (b) Phill's edits. Recommended: (a).
5. **The stub for the sitting.** It goes in a throwaway cloud test notebook made for this sitting: never a real notebook, and not the shared cloud test notebook (a never-syncing page there risks the sync jam the project watches for). One way: add a page on a second device while it's online, then take that device offline before the page's content uploads. Options: (a) Phill makes one before the sitting; (b) the sitting waits until one turns up. Recommended: (a); if it can't be made, question 2 (a) still lets the rest ship.

## Code Map

- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- class summary l.13 ("no dialog of any kind") and l.25-27; `NotebookNotDownloadedUserAction` l.67-68; `CheckTheFileUserAction` l.84-85; `ExecuteAsync` l.123 (add `bool allowUnsyncedPages`); `PublishTarget` l.154; step 1 l.206-237 (`TheTargetItselfAsync` throws the group and not-found refusals first; l.213 the notebook-format refusal; l.222-226 the S3 check); again under the lock l.273; `CallAsync` l.332-349; the refusal l.376-403 (comments l.376-381, locked first, then the message at l.399); warnings pattern l.433.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` l.115-117 -- add `UnsyncedPagesLeftOut` beside `ExportNotEncrypted`.
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` l.44, l.299-305 -- `MaxIdsNamed` and `IdList` are `protected`; copy the rule (20, then "and N more") into a private helper in `PublishUseCase` rather than widen it.
- `src/OneNoteMcp.Xml/HierarchyNode.cs` -- the `AllPagesAvailable` doc comment's "not fully downloaded" wording.
- `src/OneNoteMcp.Com/ComGateway.cs` l.890-901 (deadline, `TrackAbandonedWrite`, `Poison`), `src/OneNoteMcp.Com/HResultMap.cs` l.90-91 (unmapped falls to `Internal`), `src/OneNoteMcp.Core/Results/ToolErrors.cs` l.56, l.66 -- read only, for the timeout and No rows.
- The no-window check (backlog 3; spine l.862) -- reuse for question 3 (a), found where the gateway applies it before a call.
- `src/OneNoteMcp.Server/Tools/PublishTool.cs` l.37 (refusal sentence), l.44-53 (signature).
- `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- S3 tests l.770-810 and `NotebookSections(...)` l.1031; notebook timeout test l.907; `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (schema carries the new property).

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- add `UnsyncedPagesLeftOut`.
- [ ] `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` -- corrected message (l.399) and `userAction`; `allowUnsyncedPages` through `ExecuteAsync` and `PublishTarget`; the step-1 refusal for anything but a notebook package, in the Always order; with the opt-in, collect unsynced section IDs (step 1, then again under the lock, the latter kept) instead of refusing; question 3's no-window refusal; on success add `unsyncedPagesLeftOut`; on the gateway's `Timeout` with the opt-in, the dialog-aware message and `userAction`; update the class summary and the comments l.25-27, l.222-223, l.376-381.
- [ ] `src/OneNoteMcp.Xml/HierarchyNode.cs` -- the doc comment's wording.
- [ ] `src/OneNoteMcp.Server/Tools/PublishTool.cs` -- the argument (description: user-visible, the dialog, only on the user's say-so, notebook package only) and the corrected refusal sentence.
- [ ] `tests/OneNoteMcp.Tests/PublishToolTests.cs` -- one test per matrix row (the No row asserts the interim `Internal`), each seen failing first; the existing S3 tests change only their expected message and `userAction`; `Contract/tool-contract.json` updated for the new property.
- [ ] Hand the team lead the guide text for story 3.10: the corrected advice, the opt-in (warn the user about the dialog; pass it only when they agreed to leave those pages out; answer nothing for them), the warning, and the timeout advice.

**Acceptance Criteria:**
- Given no opt-in, when a notebook with an unsynced section is packed, then the call is refused before `Publish` with the corrected message and advice.
- Given `allowUnsyncedPages: true` on a page, a section, or a notebook with a locked section, when `publish` runs, then it is `InvalidArgument`, `InvalidArgument` and `SectionLocked` respectively, with no `Publish` call.
- Given the opt-in and a `Publish` that times out, when the result returns, then it is `Timeout` with `outcomeUnknown` and the dialog-aware advice.
- Given the build, when `dotnet build -warnaserror` and the unit tests run, then both pass, and the existing S3 tests change only their expected message and `userAction`.
- Given the sitting, when the opt-in is used and Phill answers Yes, then the package lands, the result is `ok`, and it carries `unsyncedPagesLeftOut`.

## Sitting steps (main session with Phill; never a build)

0. Build this branch and register it for the session as `onenote-dev` (per CLAUDE.md). Precondition: the throwaway cloud test notebook with a stub (question 5). `get_hierarchy` at scope `sections` confirms one section with `areAllPagesAvailable` false (record counts only). Phill checks the notebook's sync status (healthy, or the code; stop on `0xE0000644`). Abort rule for every step: if no dialog is visible within 30 s of a call that should raise one, Phill finds it with Alt+Tab and answers No; never close or kill OneNote; stop the sitting on `ComUnavailable`.
1. `publish` the notebook as onepkg without the opt-in, to a neutral file name under the export root. Expected: `NotSupported` with the new message and advice, no dialog, no file.
2. Same with `allowUnsyncedPages: true`. While the dialog is open: a `get_hierarchy` and one harmless write attempt (a `create_section` in the throwaway notebook), recording codes only. Then Phill answers **No**. Record: result code and HRESULT, seconds, whether a file landed, whether a following `get_hierarchy` and write answer at once. Phill deletes any file and the test section.
3. Same again; Phill answers **Yes**. Record: `ok`, the warning code and how many section IDs it names (never the IDs), file size, seconds. Phill deletes the file.
4. Timeout path: Phill sets `timeouts.publishSeconds` to 30 himself, the same call runs, and the dialog is left unanswered. Record: the `Timeout` result, then a write's code (expected `ComBusy`). Phill answers No; record whether writes recover. Phill restores `publishSeconds`.
5. Section scope: export the stub's section as pdf and as onepkg, without the opt-in (page scope is moot: the stub has no ID). Record per format: dialog yes/no, result code, size, seconds; Phill answers No to any dialog and deletes the files.
6. Cleanup: Phill deletes the placeholder and the throwaway notebook (or keeps it for a rerun) and checks sync status. Record findings in `## Sitting findings` here (sizes, counts, codes and times only); map No's HRESULT per question 1 (`src/OneNoteMcp.Com/HResultMap.cs` and a test) in a follow-up commit; answer the section-scope question in the 3.7 plan's follow-up line; send them to the team lead.

## Verification

TDD: write each test first and watch it fail before the code change. Unit tests only: never touch OneNote, never set the integration-test variable, never touch the server's per-user settings or data folders.

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

## Implementation Notes

## Plan Change Log

## Review Triage Log

Fable final review (low effort, 2026-11-17), 18 findings, all checked against the code and applied: F1 the timeout is the gateway's (STA reset, abandoned write, `ComBusy`), now a matrix row and question 3; F2 dialog-aware `Timeout` advice; F3 the guide has no publish row, so its text goes to story 3.10; F4 sitting steps for the open dialog, the timeout path and an abort rule; F5 No's interim `Internal`, not `Cancelled`, and its AD-9 row; F6 a fuller spine list, "user-visible", the class summary and comments; F7 question 2 separates merge from release; F8 the stub goes in a throwaway cloud notebook, with cleanup; F9 the sitting AC states what is observed; F10 the message at l.399 and `HierarchyNode`'s comment; F11 no integration test (the sitting is the live check); F12 the no-window refusal in question 3; F13 the refusal order; F14 the under-the-lock set; F15 page scope is moot; F16 the No mapping moved under the sitting with its path; F17 ACs for the other rows; F18 the refusal's line range and `IdList` copied, not widened.
