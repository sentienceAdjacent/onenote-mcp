---
title: 'Hash page-level images, ink and files'
type: 'feature'
ticket: '5'
created: '2026-11-19'
status: 'built'
baseline_revision: 'bab93bbaac730b4fe7f6cea10f40bec44fa823f8'
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

**Problem:** AD-17's `elementHashes` cover only `one:Outline` and `one:OE` (`src/OneNoteMcp.Xml/PageBasis.cs`), so an image, ink drawing or file placed directly on the page, outside any Outline, has no hash, and a stale write that sends one is caught only by the page-wide date check. Found by backlog 2's sitting check, 2026-11-16.

**Approach:** Hash the page-level objects the 2013 schema allows beside an Outline (`one:Image`, `one:InkDrawing`, `one:InsertedFile`, `one:MediaFile`; not `one:FutureObject`) when they carry an `objectID`, in the read basis and in a large page part's basis, and make a sent one a hash target of `update_page_content`, so a changed one is refused with the element-level `StaleWrite`. Outline and OE hashing, the hash function and the read-back rules don't change (ticket boundary). Test-first against a synthetic fixture.

</frozen-after-approval>

## Decisions

- AD-14 (Phill, 2026-11-19, via the coordinator): no minor version bump. Adding hashes for elements that had none is additive behaviour; the schema and the contract snapshot are unchanged.

## Implementation Notes

- Oneshot: about 30 lines of code across three Xml/Core files plus tests and a fixture.
- `MediaFile` was first included as the schema's other file kind, then dropped after review: its `pathCache` isn't excluded from the hash as an `InsertedFile`'s is, so its hash would change with no edit (changing that is a hash-rule change, outside this ticket). `FutureObject` isn't content.
- Test-first: a synthetic fixture (`synthetic/page-level-objects-Basic.xml`, in the manifest and XSD-valid) and `PageLevelObjectHashTests`, plus `UpdatePageContentToolTests.AStalePageLevelImage_IsStaleWrite_NamingIt_NotOnlyThePageWideCheck`; 7 ran red (the tool test's stale write went through), then green. `LargeResultTests.ExpandOfAPageLevelImage_CarriesItsOwnHash` came with the review fixes and wasn't run red.
- One shared rule, `PageBasis.IsHashed`, used by `PageBasis.From`, `ElementSpans.Page` (part and expand bases) and `PageWriteCheck` (hash targets). Docs: spine AD-17 l.611 and l.711 and the deferred page-level-deletes row; the guide (`SKILL.md` l.95, `page-writes.md` l.14, l.19); `read_result`'s description; comments.
- Delete targets don't change: `delete_page_content` still deletes only Outlines (AD-7; the spine's deferred row on page-level deletes stays).

## Review Triage Log
- quick lens, 7 findings, each checked against the code:
  - medium, patched: a page-level `MediaFile` was hashed with its `pathCache` (OneNote-set; excluded only for `InsertedFile` in `ElementHash.IsExcluded`), so its hash could change with no edit. Dropped from the hashed set.
  - maybe-false, deferred: live, OneNote may give a page-level inserted file no `objectID` (`PageWriteScan.cs` l.167–168 records that for files it writes), and then it gets no hash. Settled by a live read of a page with a file dropped on the canvas.
  - low, patched: the oversized-page merge rule (`SKILL.md` l.95, `page-writes.md` l.19) named only Outlines.
  - low, patched: "an expand of an Outline or OE carries its own hash" (`read_result` description, `ResultResponses` comment).
  - low, patched: the spine's deferred row on page-level deletes still said the hashes were missing. Whether adding keys to `elementHashes` is an AD-14 contract change (pre-1.0 minor bump) is left to the coordinator.
  - low, patched: no test of the part path; added `ExpandOfAPageLevelImage_CarriesItsOwnHash`.
  - false: a change to an image's pixels, ink or a file's bytes alone isn't caught. That is AD-17's existing binary exclusion (`ElementHash.BinaryElements`), not this change.
- Fable final review of the spine edit (low effort, read-only, `bmad-review`), 12 findings, each checked against the spine and code. Applied: F1 (l.626 and l.644, the comparison rule, still named only Outlines and OEs), F2 (l.704), F3 (the two conditions: a direct child of `one:Page`, with an `objectID`), F6 (a Deferred row for the dropped-file `objectID` question), F7 (the no-bump decision at the rule), F8 (date, attribution, a memlog entry), F10 (what the hash catches), F11 and F12 (wording). For the owner: F4 (MediaFile dropped from the frozen Intent), F5 (MediaFile `pathCache` is inferred, and would already affect OE hashes), F9 (`one:OCRData` in an image's hash, a recalled premise).
