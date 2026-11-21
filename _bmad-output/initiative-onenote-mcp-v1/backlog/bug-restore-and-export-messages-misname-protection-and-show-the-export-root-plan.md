---
title: 'Restore names protection by its reason; FileExists wording matches ExportTarget docs'
type: 'bugfix'
ticket: '19'
created: '2026-11-21'
status: 'draft'
route: 'oneshot'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `restore_from_recycle_bin` refuses a section protected only by its path with "password-protected" texts and password-only advice, while merge, move and publish word it by reason (`Protection.Phrase`). Separately, `ExportTarget`'s class doc says messages "name only the caller's own path, never a root", but its `FileExists` messages name the full resolved target, root included (retro V4, V6).

**Approach:**
- `src/OneNoteMcp.Core/Writes/RestoreFromRecycleBinWrite.cs`: carry the section's `EncryptedMarker` on `Item` (beside `Encrypted`, which stays AD-21's `IsProtected`), and build the four protected refusals in `TargetOf` (the three `ObjectNotFound` texts and the protected-elsewhere `InvalidArgument`) with `Protection.Phrase(marked)`, e.g. "Section {id} {phrase}, and it has no journal entry, so …". Reword `ProtectedElsewhereUserAction` and `ProtectedOrphanUserAction` from "a password-protected section" to "a protected section (password-protected, or under the opened-copies folder, an export root or the backups folder)". Codes, retryability and behaviour are unchanged.
- `FileExists`: per Open Question 1.
- Tests in `tests/OneNoteMcp.Tests/RestoreFromRecycleBinToolTests.cs`: a path-protected section (export root set via `TestSettings.SetExportRoots`, as the existing by-path tests do) gets the path wording and not "is password-protected" on the orphan and the sent-elsewhere refusals; a marked section keeps "is password-protected". Plus one `FileExists` test in `tests/OneNoteMcp.Tests/PublishToolTests.cs` (and `OpenPackageToolTests.cs`) pinning whichever rule Q1 picks.

**Never:** change error codes, retry flags, the refusal logic, or tool descriptions (`RestoreFromRecycleBinTool.cs` lines 36, 45, 47 say "password-protected" too, but they are the public contract, `tool-contract.json`; out of this bug's scope).

</frozen-after-approval>

## Open Questions

1. **`FileExists`: drop the root from the message, or change the doc?**
   - (a) Drop the root: `ExportTarget.cs:82,186` and `OpenPackageUseCase.cs:213` say the caller's own path. The doc stays.
   - (b) Change the doc: `ExportTarget.cs:23` becomes "Messages name the full target path, as `meta.outputPath` does, and never carry page content (AD-9)." README line 179 adds that the message names the full path. Messages stay.

   Recommended: (b). Evidence: AD-9 in the spine has no rule about roots, only "Messages never contain page content", so the doc's "(AD-9)" cites nothing that backs "never a root". Every other publish and open_package message already names the full canonical path: `PublishUseCase.cs:213` ("The output path is {target}", which `PublishTool.cs:41` tells the model to rely on after an `outcomeUnknown` failure), `PublishUseCase.cs:343`, `ExportTarget.cs:82` (the HTML `_files` folder), `OpenPackageUseCase.cs:131-134, 202, 213, 248, 253, 275`. `meta.outputPath` returns the full path on success (AD-11), and `OpenedNotAllowedUserAction` points the model at it. The root isn't secret (it's in `settings.json`, and the README names the default). Option (a) would leave the doc still false for those other messages unless they all change, one of them inside a tool description (a contract change). The full path is also what the user needs to find the clashing file.

## Implementation Notes

Oneshot: about 60-90 lines across one source file, the doc line (or three messages), README and two test files.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass; the new restore tests fail on the old wording (red run noted)
