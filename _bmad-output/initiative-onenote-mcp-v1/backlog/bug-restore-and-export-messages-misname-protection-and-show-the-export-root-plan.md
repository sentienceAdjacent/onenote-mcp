---
title: 'Restore names protection by its reason; FileExists wording matches ExportTarget docs'
type: 'bugfix'
ticket: '19'
created: '2026-11-21'
status: 'built'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
baseline_revision: '8d22d519fc853d93a866c7a3e2acab68bb3bbb07'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `restore_from_recycle_bin` refuses a section protected only by its path with "password-protected" texts and password-only advice, while merge, move and publish word it by reason (`Protection.Phrase`). Separately, `ExportTarget`'s class doc says messages "name only the caller's own path, never a root", but its `FileExists` messages name the full resolved target, root included (retro V4, V6).

**Approach:**
- `src/OneNoteMcp.Core/Writes/RestoreFromRecycleBinWrite.cs`: carry the section's `EncryptedMarker` on `Item` (beside `Encrypted`, which stays AD-21's `IsProtected`), and build the four protected refusals in `TargetOf` (the three `ObjectNotFound` texts and the protected-elsewhere `InvalidArgument`) with `Protection.Phrase(marked)`, e.g. "Section {id} {phrase}, and it has no journal entry, so …". Reword `ProtectedElsewhereUserAction` and `ProtectedOrphanUserAction` from "a password-protected section" to "a protected section (password-protected, or under the opened-copies folder, an export root or the backups folder)". Codes, retryability and behaviour are unchanged.
- `FileExists` (Decision, Phill, 2026-11-21, Inbox, option b: change the doc, keep the messages): `ExportTarget.cs:23` becomes "Messages name the full target path, as `meta.outputPath` does, and never carry page content (AD-9)."; README line 179 adds that the `FileExists` message names the full path. Messages stay as they are. Reason: every other publish and open_package message already names the full path, AD-9 has no root rule, and `meta.outputPath` shows it on success.
- Tests in `tests/OneNoteMcp.Tests/RestoreFromRecycleBinToolTests.cs`: a path-protected section (export root set via `TestSettings.SetExportRoots`, as the existing by-path tests do) gets the path wording and not "is password-protected" on the orphan and the sent-elsewhere refusals; a marked section keeps "is password-protected". Plus one `FileExists` test in `tests/OneNoteMcp.Tests/PublishToolTests.cs` (and `OpenPackageToolTests.cs`) pinning whichever rule Q1 picks.

**Never:** change error codes, retry flags, the refusal logic, or tool descriptions (`RestoreFromRecycleBinTool.cs` lines 36, 45, 47 say "password-protected" too, but they are the public contract, `tool-contract.json`; out of this bug's scope).

</frozen-after-approval>

## Implementation Notes

Oneshot: about 60-90 lines across one source file, the doc line (or three messages), README and two test files.

- `Item` gains `Marked` (`self.EncryptedMarker`); `Encrypted` stays AD-21's `IsProtected` and still drives matching and `TargetOf`. The four refusals take `Protection.Phrase(item.Marked)`.
- New test `ASectionProtectedByPath_WithASectionKeyEntry_SentElsewhere_GivesThePathReason` picks its entry by `entryId`: the fixture's own "gone" entry has no `SectionKey` and wins the by-ID match otherwise (the orphan path).
- Red run: with the old `RestoreFromRecycleBinWrite.cs` restored, 7 of 90 restore tests fail (the new one, the by-path orphan test, and the marked tests' message assertions); with the fix, all pass.
- `FileExists`: doc and README only (Decision); `PublishToolTests.AnExistingFile_IsFileExists_AndLeftUntouched` already pinned the full path, and `OpenPackageToolTests.AnExistingDestination_IsFileExists_AndLeftAlone` now does too.

## Review Triage Log

Quick review, pass 1: 4 findings; 0 high, 2 medium, 2 low, 0 false, 0 maybe-false. All patched.
- medium, patch: `ExportTarget.cs:23`'s new doc said every message names the full path, export root included; only `FileExists` does, the others name the caller's `raw` path (lines 76, 235). Reworded: the caller's path as given, except `FileExists`, which names the full resolved path, as `meta.outputPath` does.
- medium, patch: restore's "the place it was deleted from … is gone" was chosen when the entry has no `SectionKey` (`folder` null), although that place may be live (AD-8: no provable own place). Old bug, but in the text this ticket rewrites. Added a branch: "its journal entry … records no SectionKey (it wasn't protected when it was deleted), so the place it was deleted from can't be proven"; the by-path orphan test pins it.
- low, patch: `OpenPackageUseCase.cs:38` said messages name "the caller's own paths"; its `FileExists` names the resolved folder. Reworded.
- low, patch: README's `restore_from_recycle_bin` row and `plugin/skills/guide/references/hierarchy-writes.md:151` named only password-protected sections; now they include sections protected by path, matching the new `userAction` texts.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass; the new restore tests fail on the old wording (red run noted)
