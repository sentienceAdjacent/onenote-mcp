---
type: sprint-change-proposal
status: approved
approved: '2026-11-05 (Phill; Fable final review run, findings 1-6 applied)'
created: '2026-11-05'
trigger: stories 1.5 and 1.6
scope: minor
mode: batch
---

# Sprint Change Proposal: spine amendments from stories 1.5 and 1.6

## 1. Issue summary

Building stories 1.5 and 1.6 against real OneNote showed three places where the spine, the initiative or the board says something the code now contradicts. Phill has already decided all three. This proposal brings the documents in line with the code.

1. **AD-17's hash exclusions are incomplete.** AD-17 removes only `lastModifiedTime`, `creationTime` and `lastModifiedBy`. Story 1.6 also removes:
   - `selected`, `lastModifiedByInitials` and `lastModifiedByResolutionID` (Phill, 2026-11-04). They record selection or authorship, not content.
   - `isCurrentlyViewed` and `isUnread` (Phill, 2026-11-05). Being viewed or unread mustn't stop a write.

   Evidence: `src/OneNoteMcp.Xml/ElementHash.cs` `ExcludedAttributes`, pinned by `XmlModelTests.ExcludedAttributes_AreAd17sPlusPhillsDecision`. On 2026-11-05 a live notebook-list read returned `isCurrentlyViewed="true" isUnread="true"` on a notebook. The story 1.6 capture probe recorded which attributes OneNote changed on untouched elements (`tests/OneNoteMcp.Tests/Fixtures/manifest.json`, `probe`). The only one outside the list was `Size@width` on the outline whose text was edited: OneNote widened the box as the text grew. That outline's hash changes anyway, because its text changed. Layout stays in the hash (Phill, 2026-11-05). `XmlModelTests.LayoutOnlyChange_ChangesTheOutlineAndThePage_NotItsOes` shows the consequence: a resize or a move alone changes the Outline's hash and `pageHash`, and no OE's.
2. **AD-6 says `GetHierarchyParent` throws at a notebook. It doesn't.** It returns an empty string. Evidence: the 2026-11-04 capture, `tests/OneNoteMcp.Tests/Fixtures/mcp-test/parent/notebook.txt` (empty, no error). Microsoft's documentation and `reviews/review-verification.md` say it throws. The port comment `IOneNoteGateway.GetHierarchyParentAsync` was corrected in 1.6.
3. **`FormatDate` doesn't exist in the OneNote 15 type library.** Neither the 29-slot hand-declared `IApplication` nor the PIA has it. Nothing needs it, because dates are UTC ISO-8601 at the tool boundary (Consistency Conventions). Story 1.5 shipped nine Read methods without it (Phill, 2026-11-04).

Issue type: technical facts found during implementation. No requirement changes.

## 2. Impact analysis

- **Epics.** Epic 1 (read-only release) is in progress. Only story 1.7's wording changes. Epic 2 (safe writes) uses AD-17's hash and AD-6's resolution. `ElementHash` already implements the amended AD-17, and the gateway already returns the empty string the amended AD-6 describes. The parent-chain walk itself is epic 2's work. Epics 3 and 4 aren't affected. No epic is added, removed or resequenced.
- **Stories:**
  - 1.6 is done and already matches.
  - 1.7 (planned, no file) drops `format_date`, and its `get_hierarchy_parent` wording changes.
  - Epic 2's future write stories inherit the amended AD-17 and AD-6.
- **Artifacts:**
  - Spine: AD-6, AD-7's `Read` row and AD-17.
  - Initiative: R1.
  - Epic 1's `tickets.toml`: entry 7. Epic 1's Notes: a line recording the amendments.
  - `deferred-work.md`: three entries close.
- **Technical.** No code change. The code is already right in every case, and this proposal only changes the documents.
- **Not changed.** AD-16 says XSD validation becomes a hard gate "only once a story proves it accepts partial-update XML". Story 1.6 proved this for `UpdatePageContent` shapes only, and epic 1's Notes record that. The rule is conditional and still correct, so it needs no edit; epic 2 applies it.

## 3. Recommended approach

**Direct adjustment.** Edit the documents in place and keep every AD ID stable. Effort is low, risk is low, and the timeline doesn't change. A rollback isn't needed and the MVP isn't affected.

## 4. Detailed change proposals

### Architecture spine (`architecture-onenote-mcp/architecture-onenote-mcp.md`)

**A. AD-17, the first sub-bullet under "The hash"** (4-space indent; the new text replaces it with sub-bullets at the same indent)

OLD:
> - `ElementHash` is lowercase hex SHA-256 over the C14N of the element, with `lastModifiedTime`, `creationTime` and `lastModifiedBy` attributes and binary data removed.

NEW:
> - `ElementHash` is lowercase hex SHA-256 over the inclusive C14N (no comments) of the element's subtree. Two things are removed throughout the subtree first:
>   - Attributes that record time, authorship, selection or view state, not content: `lastModifiedTime`, `creationTime`, `lastModifiedBy`, `lastModifiedByInitials`, `lastModifiedByResolutionID`, `selected`, `isCurrentlyViewed` and `isUnread`.
>   - The binary elements `one:Data` and `one:CallbackID`, so that a piBasic read and a piBinaryData read of a local page hash the same.
> - Layout stays in the hash: `one:Size` and `one:Position` are content. Resizing or moving an Outline, with no text change, changes that Outline's hash and `pageHash`, but no OE's. So a page delete, or an edit that names that Outline, made with an older basis returns `StaleWrite`. An edit that names only its OEs goes through.

Rationale: Phill's decisions of 2026-11-04 and 2026-11-05 (keep layout), and story 1.6's implementation. The second bullet keeps a later story from quietly adding layout to the list. The evidence is `XmlModelTests.LayoutOnlyChange_ChangesTheOutlineAndThePage_NotItsOes`. OneNote also widens an Outline when an edit makes its text longer, which the capture probe saw. That widening changes nothing extra, because the Outline's hash already changes with its text.

**B. AD-6, "Notebook resolution", first bullet**

OLD:
> a `GetHierarchyParent` chain, where the throw at the notebook marks the end of the chain, or a hierarchy lookup.

NEW:
> a `GetHierarchyParent` chain, where the empty string OneNote returns at the notebook marks the end of the chain, or a hierarchy lookup. An exception from `GetHierarchyParent` is an ordinary error (AD-9) and never ends the chain. (Observed 2026-11-04 on the local MCP Test notebook, `tests/OneNoteMcp.Tests/Fixtures/mcp-test/parent/notebook.txt`. Microsoft's documentation says it throws.)

Rationale: the observed behaviour. Putting the exception rule in the spine means a real failure is never mistaken for reaching the notebook, and no two builders treat it differently.

**C. AD-7 table, `Read` row** (replace the fragment inside the row's COM-methods cell)

OLD: `` …, `GetSpecialLocation`, `FormatDate`; the server-only `list_grants` ``
NEW: `` …, `GetSpecialLocation`; the server-only `list_grants` ``

**D. Frontmatter:** set `updated: '2026-11-05'`. `status` stays `final`. Add three `decision` entries to the memlog through `memlog.py`.

### Initiative (`initiative-onenote-mcp-v1.md`)

**E. R1:** remove `` , `FormatDate` `` from the list of Read methods.

### Epic 1 board (`epic-read-only-release/tickets.toml`)

**F. Entry 7 ("Hierarchy read tools"), `description`**

OLD:
> Adds get_hierarchy at every scope, get_hierarchy_parent (the throw at a notebook ends the chain), get_special_location, format_date, get_hyperlink_to_object and get_web_hyperlink_to_object, …

NEW:
> Adds get_hierarchy at every scope, get_hierarchy_parent (an empty result at a notebook ends the chain), get_special_location, get_hyperlink_to_object and get_web_hyperlink_to_object, …

### Deferred work (`deferred-work.md`)

**G.** Remove the three entries this proposal resolves: the AD-17 amendment, the AD-6 wording and the `FormatDate` removal.

### Epic 1 (`epic-read-only-release/epic-read-only-release.md`)

**H. Notes:** add one line after the existing AD-17 note:
> - Decision (2026-11-05, Phill; AD-6, AD-7 and AD-17 amended): AD-17's hash also ignores selection, authorship and view state, and keeps layout. AD-6's parent chain ends at the empty string OneNote returns at a notebook. `FormatDate` is dropped from AD-7, R1 and story 7. See `change-story-1-6-spine-amendments/`.

## 5. Implementation handoff

- **Scope: minor.** The developer agent applies edits A to H directly on branch `change/story-1.6-spine-amendments`.
- **Board changes for `bmad-ticket`:** none beyond entry 7's description (edit F). No stories are added, removed, resequenced or rescoped.
- **Success criteria:**
  - `FormatDate` no longer appears in the spine, R1 or `tickets.toml`.
  - AD-6 matches `IOneNoteGateway.GetHierarchyParentAsync`.
  - AD-17's attribute list equals the expected array in `XmlModelTests.ExcludedAttributes_AreAd17sPlusPhillsDecision`.
  - The spine's `updated` is `2026-11-05`, and its memlog has three new `decision` entries.
  - Epic 1's Notes have the amendment line.
  - The three deferred-work entries are gone.
  - Phill commits the branch when he's happy with it.
