---
title: 'Rename grant-location check matches percent-encoded keys'
type: 'bugfix'
ticket: '8'
created: '2026-11-17'
status: 'in-review'
baseline_revision: '402bcd1f0995ef4972584c06a51cabf96c089da5'
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

**Problem:** `RenameHierarchyWrite.GrantLocationRefusal` (`src/OneNoteMcp.Core/Writes/HierarchyWrite.cs`, ~l.420-450) asks `ReadAccess.HoldsReadGrantAt`, an exact (case-insensitive) key match. A stored grant whose cloud key spells a space `%20` where the renamed path doesn't (or the reverse) is missed, so a rename could move a section onto a granted location and inherit its grant (AD-21). A guard only: in the 3.12 sitting (2026-11-17) OneNote reported an MCP Test Cloud section path with plain spaces.

**Approach:** In `GrantLocationRefusal`, call `ReadAccess.HoldsReadGrantAtLocation` (from #72, matching by `GrantKeys.SameLocation`: trimmed, URL-decoded both sides, ignoring case) in place of `HoldsReadGrantAt`, exactly as `MoveContainerWrite.GrantLocationRefusal` (l.524-526) does. The one method serves both the pre-lock check (`PreCheckAsync`) and step 4's strict re-check, so both change. The "section stays at its own key" exclusion keeps `GrantKeys.Comparer` (exact, ignoring case), as the move keeps it: see the Decision below. Update the method's summary to say keys are matched percent-decoded. Unit tests in `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs` (helpers `ReadGrantAt`, `AssertNamesNothingProtected`, `Self`/`Sections`, cloud fixture `CloudPath` = `https://d.docs.live.net/0123abcd/Cloud/`, `CloudSectionId` "Web", `CloudGroupId` "Shared"):
- Rename cloud section Web to "New Name" with a stored grant at `{CloudPath}New%20Name.one`: `InvalidArgument`, no `UpdateHierarchyCalls`, no journal entry, nothing protected named.
- The reverse spelling: a cloud section whose folder is percent-encoded (`…/My%20Cloud/Web.one`) renamed to "New", with the grant stored decoded at `…/My Cloud/New.one`: refused the same way.
- Section group Shared holding a section (override `Sections(CloudGroupId, …)`), renamed so the inner section's new key matches a `%20`-encoded stored grant: `InvalidArgument` from the pre-lock check (it comes before the OneDrive group-rename `NotSupported`), nothing renamed.
- A live `once` window at the encoded key refuses too (`HoldsReadGrantAtLocation` covers windows).
- Existing rename tests pass unchanged.

**Decision (Phill, Inbox 2026-11-17):** the "stays at its own key" exclusion keeps `GrantKeys.Comparer` (exact, ignoring case), not `GrantKeys.SameLocation`: `SameLocation` fails open on a local path whose name contains `%20`, while the exact comparison fails closed (a rare false refusal). The reasoning: renaming local section "My Notes" to "My%20Notes" really moves it to the new file and key `…\My%20Notes.one`, but `SameLocation` decodes both keys to the same string, so a grant stored at that literal key would be handed over.

</frozen-after-approval>

## Implementation Notes

Oneshot: a one-call substitution in one method plus four unit tests, about 60 lines.

- TDD (2026-11-17): the four new `UpdateHierarchyToolTests` failed first (the renames went through, or the group got the OneDrive `NotSupported`), then passed after the change. Full unit run: 3171 passed, 0 failed; build 0 warnings.
- Changed: `HierarchyWrite.cs` `GrantLocationRefusal` (one call, summary updated to record Phill's exact-comparer decision); tests only otherwise.
- The group case is refused in step 1 (no lock taken, `holds` empty), ahead of step 4's OneDrive group-rename `NotSupported`.

## Verification

TDD: write each test first and watch it fail before the code change. Unit tests only: never touch OneNote, never set the integration-test variable, never touch the server's per-user settings or data folders.

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

## Review Triage Log

Quick lens, pass 1 (2026-11-17): 1 medium (HALT), 2 low (patched).

- **medium, HALT for Phill:** a case-only rename of a cloud section whose stored path is percent-encoded (`…/My%20Notes.one`, own grant there, renamed to "my notes") is now refused. `RenamedLocations` builds the new key from the raw name (`…/my notes.one`), the exact own-key comparison sees two keys, and `HoldsReadGrantAtLocation` finds the section's own grant by decoding. Before this change the rename went through. Reachable only if OneNote reports encoded cloud paths, which the 3.12 sitting didn't see. Smallest fix, outside Phill's decision so not applied: treat a rename as staying put when old and new names are equal ignoring case (fail-closed for a local `%20` name, since those names differ).
- **low, patched:** the frozen block's "see Open Question 1" pointed at a deleted section; now points at the Decision, which carries the reasoning.
- **low, patched:** two new tests checked only the error code; they now assert the grant-location message, and the once-window test asserts no journal entry.
