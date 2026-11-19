---
title: 'The opened-copies sweep ages a copy by its last change'
type: 'bugfix'
ticket: '21'
created: '2026-11-19'
status: 'built'
baseline_revision: '6853b4bd9f5ef88b31815dedea186395e3d7b5e7'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** At startup the opened-copies sweep (story 3.8) deletes a marked copy that isn't open in OneNote once its folder is more than 7 days old. It measures that age from the folder's creation time alone. So a copy opened more than 7 days ago and edited since is deleted whole when the server next starts, edits included.

**Approach (Phill, 2026-11-19):** measure age from the last change in the copy: the newest of the creation and last-write times of the folder itself and of every file and folder inside it. Never delete a copy changed within 7 days. Links and junctions inside the copy count by their own times and are never followed. If any time can't be read, the copy is kept. Everything else about the sweep stays as it is: the marker, the open-notebook check, the "OneNote not read means delete nothing" rule, and the recursive delete.

</frozen-after-approval>

## Implementation Notes

- **Why oneshot:** one private method in `src/OneNoteMcp.Core/Writes/OpenedCopies.cs` (`OpenedCopySweep.IsOld`), plus its tests; about 40 lines of code.
- **The rule as built:**
  - `LastChange` walks the copy one level at a time, taking the newest `CreationTimeUtc` or `LastWriteTimeUtc` of the folder and of every entry.
  - A file's creation time counts, because a file copied in keeps its source's write time.
  - It descends only into directories that aren't reparse points, so a link or junction counts by its own times and is never followed.
  - Any `IOException`, `UnauthorizedAccessException` or `SecurityException` keeps the copy, as before.
- **TDD:** the four "kept" tests (a file written yesterday, a nested file, a file copied in by its creation time, and the folder written recently) were written first and failed: each copy was deleted by its creation time. The junction test came after the first version of the code. Its first failure was the lingering-folder effect noted below, not link-following, so its assertion now allows for that effect. As a mutation check, removing the reparse-point guard (so links are followed) makes it fail; the code was then restored. The "untouched for 8 days, deleted" case passed before and after, as a guard.
- **Fixture:** the test helper `Copy(...)` now ages every entry, not just the folder's creation time, because the new rule reads the files' times. This keeps the existing sweep tests meaning what they meant.
- **Found while testing** (predates this bug, deferred): after `Directory.Delete(copy, recursive: true)` removes a junction inside a copy, Windows can briefly refuse to remove the now-empty folder. The sweep then counts the copy as not deleted and leaves an empty folder with no marker, which later sweeps ignore. The junction test allows for it.
- **Verification (2026-11-19):** build 0 warnings; unit tests 3,789 passed, 3 skipped; the `OpenedCopies` tests passed three runs in a row, in temporary folders only.

## Review Triage Log

- 2026-11-19, quick lens: 0 high, 1 medium, 3 low, 0 false, plus 1 low rejected. All the others were patched; one item was deferred.
  - **medium, patch.** The user-facing texts still gave the old rule ("more than 7 days old"): the `open_package` tool description, `README.md` (Cleanup) and the plugin skill reference `exports-and-packages.md`. Each now says "nothing in it has changed for more than 7 days".
  - **low, patch.** Stale comments in `OpenedCopiesSweepService` and in the `OpenedCopiesTests` summary were reworded.
  - **low, patch.** The test helper `Age` had a hard-coded `Link` name check and dead code. It now walks as the production code does, aging a link as an entry and never going into it.
  - **low, patch (record).** The lingering empty folder after a junction is removed wasn't tracked. A deferred-work entry was added.
  - **low, reject.** "The junction test doesn't check the sweep's return value." On Windows the count can be 0 while the copy's contents are gone (the deferred item above). The mutation check shows the assertion still catches link-following, and a stricter one would be flaky.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the new sweep-age tests included, in temporary folders only.
- TDD: each new test written first and seen failing.

**Live check (optional, next sitting):** none needed for the rule itself. It is filesystem-only and unit-tested on real temporary folders. The sweep is never run against the real opened-copies folder.
