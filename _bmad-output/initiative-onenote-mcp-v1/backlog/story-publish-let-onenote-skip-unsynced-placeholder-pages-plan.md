---
title: 'publish: a clearer refusal for a notebook with an unsynced placeholder page'
type: 'bugfix'
ticket: '11'
created: '2026-11-17'
status: 'built'
baseline_revision: 'e5c1e2bae8091476191a9c8b3752f633ed223bc9'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `publish` refuses a notebook package (`onepkg`) holding a section whose `areAllPagesAvailable` is `false` (3.7 S3). Its message says the section "isn't fully downloaded to this PC", and its `userAction` says to open those sections so they download. The backlog 10 sitting (2026-11-17) showed the flag marks a page stub added from another computer that never synced: nothing on this PC can download it, so both are wrong.

**Approach:** Keep the refusal exactly as it is and correct only its words, in `PublishUseCase` (message and `userAction`), the `publish` tool description, the code comments that give the old reason, and the spine's three places that state it (AD-7 Formats by scope's S3 reason, AD-11 Exports, AD-21 Exports). New advice: a page in that section was added on another computer and hasn't synced, so this PC can't download it and packing would leave it out; sync it from the computer that added it, or delete its placeholder page in OneNote, then try again; or export the other sections one by one. Messages keep naming section IDs only (AD-9).

**Decision (Phill, 2026-11-17):** build only the clearer refusal. The `allowUnsyncedPages` opt-in is dropped: its review showed OneNote's dialog blocks the COM call, so writes freeze (`ComBusy`) until the user answers, and a Yes after a timeout writes a package that lacks the pages with nobody told. If unsynced pages come up often, it can return as its own story.

**Never:** Change the refusal's behaviour, code or order. Touch OneNote in tests. Edit spine lines other than the three named, so the diff merges cleanly beside the 3.15 and 3.5 lanes. Record a real notebook, section or page name or ID anywhere.

</frozen-after-approval>

## Implementation Notes

Oneshot: wording only, about 40 lines across code, tests and three spine bullets. Files: `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` (l.25-27, 30, 66-68, 222, 270, 378-381 comments; l.399 message; l.68 `NotebookNotDownloadedUserAction`), `src/OneNoteMcp.Xml/HierarchyNode.cs` l.20-21 comment, `src/OneNoteMcp.Server/Tools/PublishTool.cs` l.37, `tests/OneNoteMcp.Tests/PublishToolTests.cs` (the S3 theory l.768-783 asserts the new message and `userAction`), the spine l.313, l.480, l.750, the memlog, and the ticket (Notes, then its body rewritten to this scope after review). The earlier opt-in plan, its five questions and its 18-finding Fable review are in this file's git history (commit before this one).

## Verification

TDD: change the S3 test's expectations first and watch it fail before the wording changes. Unit tests only: never touch OneNote, never set the integration-test variable, never touch the server's per-user settings or data folders.

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

## Review Triage Log

Quick lens, pass 1 (2026-11-17): 3 patched, 1 deferred, 1 noted.

- **patched:** spine l.480 had dropped the still-open "section- or page-scope export … untested (backlog 10)" sentence; restored beside the new one.
- **patched:** spine l.480's spliced clause chained two colons; now a full stop before "In S3".
- **patched:** the ticket still described the dropped opt-in (title, Description, ACs, Verify, hitl); rewritten to the wording-only scope (now not hitl, risk low), Notes kept.
- **deferred:** spine AD-7 table row l.326 still says "not fully downloaded"; left out to keep the diff to the three named lines beside the 3.15 and 3.5 lanes, and recorded in the ticket's Boundaries as a follow-up.
- **noted:** the frozen block was rewritten to Phill's 2026-11-17 decision (relayed by the team lead); his yes on the build is still needed before merge.
