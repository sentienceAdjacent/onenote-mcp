---
title: 'Rename grant-location check matches percent-encoded keys'
type: 'bugfix'
ticket: '8'
created: '2026-11-17'
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

**Problem:** `RenameHierarchyWrite.GrantLocationRefusal` (`src/OneNoteMcp.Core/Writes/HierarchyWrite.cs`, ~l.420-450) asks `ReadAccess.HoldsReadGrantAt`, an exact (case-insensitive) key match. A stored grant whose cloud key spells a space `%20` where the renamed path doesn't (or the reverse) is missed, so a rename could move a section onto a granted location and inherit its grant (AD-21). A guard only: in the 3.12 sitting (2026-11-17) OneNote reported an MCP Test Cloud section path with plain spaces.

**Approach:** In `GrantLocationRefusal`, call `ReadAccess.HoldsReadGrantAtLocation` (from #72, matching by `GrantKeys.SameLocation`: trimmed, URL-decoded both sides, ignoring case) in place of `HoldsReadGrantAt`, exactly as `MoveContainerWrite.GrantLocationRefusal` (l.524-526) does. The one method serves both the pre-lock check (`PreCheckAsync`) and step 4's strict re-check, so both change. The "section stays at its own key" exclusion keeps `GrantKeys.Comparer` (exact, ignoring case), as the move keeps it: see Open Question 1. Update the method's summary to say keys are matched percent-decoded. Unit tests in `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs` (helpers `ReadGrantAt`, `AssertNamesNothingProtected`, `Self`/`Sections`, cloud fixture `CloudPath` = `https://d.docs.live.net/0123abcd/Cloud/`, `CloudSectionId` "Web", `CloudGroupId` "Shared"):
- Rename cloud section Web to "New Name" with a stored grant at `{CloudPath}New%20Name.one`: `InvalidArgument`, no `UpdateHierarchyCalls`, no journal entry, nothing protected named.
- The reverse spelling: a cloud section whose folder is percent-encoded (`…/My%20Cloud/Web.one`) renamed to "New", with the grant stored decoded at `…/My Cloud/New.one`: refused the same way.
- Section group Shared holding a section (override `Sections(CloudGroupId, …)`), renamed so the inner section's new key matches a `%20`-encoded stored grant: `InvalidArgument` from the pre-lock check (it comes before the OneDrive group-rename `NotSupported`), nothing renamed.
- A live `once` window at the encoded key refuses too (`HoldsReadGrantAtLocation` covers windows).
- Existing rename tests pass unchanged.

</frozen-after-approval>

## Open Questions

1. **The "stays at its own key" comparison.** The ticket suggests `GrantKeys.SameLocation` there too. Options: (a) keep `GrantKeys.Comparer` (exact, ignoring case), as the move does; (b) switch to `SameLocation`. (b) fails open on a local path: renaming section "My Notes" to "My%20Notes" really moves it to a new file and key `…\My%20Notes.one`, but `SameLocation` decodes both to the same string and would skip the check, so a grant stored at that literal key would be handed over. (a) fails closed: that same rename with the section's own grant at `My Notes.one` is refused as a false positive, a rare case the user can resolve by picking another name. Case-only renames already pass under (a). Recommended: (a).

## Implementation Notes

Oneshot: a one-call substitution in one method plus four unit tests, about 60 lines.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass
