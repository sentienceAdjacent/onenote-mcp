---
title: 'XML model, vendored XSD, ElementHash and FakeGateway'
type: 'feature'
ticket: '6'
created: '2026-11-04'
status: done
baseline_revision: '694bb57a67f7e6258da34d4e61fcbf79dfbac6c4'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `OneNoteMcp.Xml` holds only the namespace constant. Stories 7–9 need a vendored 2013 XSD, the AD-17 basis (`lastModifiedTime`, `pageHash`, `elementHashes`), an image-count check for `meta.binaryUnavailable`, and a `FakeGateway` that serves every read method from real captured XML. Epic 2's validation gate waits on whether the XSD accepts partial-update XML.

**Approach:** Vendor the Wayback XSD byte-for-byte with provenance and SHA-256. Add schema validation, `ElementHash`/`PageBasis` and `ImageCount` to Xml. Capture fixtures through an opt-in integration scenario that seeds a `Fixtures` section in MCP Test and records every read method, sanitised. Rebuild `FakeGateway` over a fixture manifest. Test partial-update documents against the XSD and record the result as a dated Decision in the epic's Notes.

## Boundaries & Constraints

**Always:**
- XSD: the Wayback snapshot bytes unchanged (47,961 bytes, SHA-256 `fcddc26ac8fa925a4dac86e0b3cfc451445e2728757d4804ed1e0e1cbfb2a29d`, checked 2026-11-04). An embedded resource with a `PROVENANCE.md` beside it. OneMore's current copy differs only in line endings and two trailing blank lines (LF, 46,672 bytes, `fbb4710b…`); record that. A unit test pins the SHA-256.
- `ElementHash`: lowercase hex SHA-256 over inclusive C14N (`XmlDsigC14NTransform`, no comments) of the element subtree, after removing the excluded attributes and binary elements throughout the subtree. Only Xml computes it.
- `PageBasis.From(pageXml)`: `lastModifiedTime` is the root `one:Page` attribute as the exact string. `pageHash` is over the root. `elementHashes` covers every `one:Outline` and `one:OE` keyed by `objectID`.
- Fixtures come only from MCP Test, plus the one sanitised cloud capture. The sanitiser keeps only the MCP Test notebook in notebook-scope XML and drops `UnfiledNotes`/open-section siblings. It replaces the user-profile path prefix with `C:\Users\mcp-test` and sets author attributes to `MCP Test`/`MT`. The capture refuses to write a file that still holds `Environment.UserName`.
- Seed writes go only through `TestNotebook.WriteAsync` (guarded). The capture never touches another notebook except the cloud read, which is read-only.
- `FakeGateway` keeps its `HierarchyXml`/`Failure` scripting so existing tests pass unchanged. An unknown key throws `OneNoteException(ObjectNotFound)`, like OneNote.
- **Decision (Phill, 2026-11-04): hash exclusions.** The hash also ignores `selected`, `lastModifiedByInitials` and `lastModifiedByResolutionID`. These record selection state or authorship, not content. A deferred-work entry routes the AD-17 amendment to `bmad-correct-course`. The capture probe checks which attributes OneNote changes on untouched siblings after a write. Any attribute it finds beyond this list is reported to Phill, not added silently.
- **Decision (Phill, 2026-11-04): cloud capture from a page Phill creates.** He makes a throwaway page in a cloud notebook in OneNote for the web, with an image, and doesn't open it in desktop OneNote. The capture reads only that page, read-only, and the sanitiser maps its IDs, text, title and authors to placeholders. Phill reviews the file before commit. If desktop already holds the image binary, record that and fall back to a hand-built fixture from spike 1's evidence, marked as hand-built.
- **Decision (Phill, 2026-11-05): view state excluded.** The hash also ignores `isCurrentlyViewed` and `isUnread`: being viewed or unread mustn't stop a write. Added to the same AD-17 amendment entry. (A notebook read on 2026-11-05 showed both attributes on a notebook element.)
- **Decision (Phill, 2026-11-04): plan kept whole** (about 2,000 tokens). The fixtures are what verifies this story.

**Never:**
- No tools, no `meta` wiring: stories 7–9. No date parsing to `DateTime` and no write-side validation gate: epic 2.
- No validation at runtime on reads (AD-4 returns XML unchanged).
- No copying OneMore code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Date-only change | same page, only `lastModifiedTime`/`creationTime`/`lastModifiedBy` differ | equal `pageHash` and `elementHashes` | — |
| Text change | one `one:T` CDATA differs | that OE, its ancestors' OEs, its Outline and `pageHash` change; siblings don't | — |
| Binary vs basic | same local page read piBasic and piBinaryData | equal hashes (`one:Data`/`one:CallbackID` removed) | — |
| Cloud image | piBasic counts N images, piBinaryData fewer | `ImageCount.Missing` = the difference | — |
| No objectID | Outline/OE without `objectID` | skipped in `elementHashes` | — |
| Bad XML | not well-formed, or wrong namespace | `SchemaValidation` returns errors; hashing throws `OneNoteXmlException` (Core maps it to `InvalidXml`) | — |
| Partial update | page with new Outline (no IDs), or one existing OE only | validation result recorded, either way | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Xml/OneNoteXml.cs` -- keep `Namespace2013`; add `XNamespace One`.
- `src/OneNoteMcp.Xml/OneNoteMcp.Xml.csproj` -- net10.0 with no references yet. Add `System.Security.Cryptography.Xml` 10.0.14 (the version matches the .NET stack) and `EmbeddedResource` for the XSD. Xml must not reference Core: Core references Xml. So Xml throws its own `OneNoteXmlException`, and callers in Core map it to `InvalidXml`. `ErrorCode.InvalidXml` exists.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- nine read methods, unchanged.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- rewrite: a fixture-backed implementation; `HierarchyXml`/`Failure`/`Calls` stay for `GetHierarchyToolTests`.
- `tests/OneNoteMcp.Tests/OneNoteMcp.Tests.csproj` -- add `Fixtures/**` as content copied to output (or read via the existing `RepoRoot` metadata in `RepoPaths.cs`).
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- reuse `EnsureAsync`, `WriteAsync`, `TestNotebookGuard`; add a `Fixtures` section seed beside `Seed`.
- `tests/OneNoteMcp.Integration/LifecycleScenarioTests.cs` -- reuse the `RequireScenario` pattern (`ONENOTE_MCP_SCENARIO`).
- `src/OneNoteMcp.Com/ComGateway.cs` -- `InvokeAsync` raw seam for seed writes; read methods for capture. Don't change.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/Schema/OneNoteApplication_2013.xsd`, `PROVENANCE.md` -- vendor the Wayback bytes, recording the URL, the retrieval date, the SHA-256 and the OneMore comparison.
- [x] `src/OneNoteMcp.Xml/SchemaValidation.cs` -- `Validate(string xml) → IReadOnlyList<string>` against the embedded XSD (cached `XmlSchemaSet`).
- [x] `src/OneNoteMcp.Xml/ElementHash.cs`, `PageBasis.cs` -- per Always and the matrix.
- [x] `src/OneNoteMcp.Xml/ImageCount.cs` -- `Count(xml)` and `Missing(basicXml, binaryXml)`.
- [x] `tests/OneNoteMcp.Integration/FixtureCapture.cs` + `FixtureSanitiser.cs` -- scenario `capture-fixtures`. It seeds pages in MCP Test: nested OEs, a table, a tag, a local PNG image, a `one:Meta`, and a subpage. Each seed is a partial `UpdatePageContent`, so it also shows OneNote accepting partial XML. The capture records every read method into `tests/OneNoteMcp.Tests/Fixtures/` with `manifest.json` (call → file). It probes callbackID stability and the attributes that change on untouched siblings after an edit. Scenario `capture-cloud` finds the one section named `MCP Cloud Test 2` in the notebook `Phill's Notebook`, which must have a cloud (https) path (a read-only `GetHierarchy`; it refuses when there are zero or several matches), and reads its first page per the cloud Decision.
- [x] `tests/OneNoteMcp.Tests/FakeGateway.cs` -- serve every port method from the manifest.
- [x] `tests/OneNoteMcp.Tests/XmlModelTests.cs` -- XSD SHA pin; every fixture validates; the matrix rows; partial-update documents validated; a fixture privacy check (no `C:\Users\` other than `mcp-test`, author attributes are the placeholder only).
- [x] `tests/OneNoteMcp.Tests/FakeGatewayTests.cs` -- each port method returns its fixture byte for byte; an unknown ID gives `ObjectNotFound`.
- [x] `epic-read-only-release.md` Notes -- dated Decision: does the XSD accept partial-update XML, with the test names. Also record `xs2026` as still deferred.
- [x] `research-onemore-lessons.md` -- update row 9 with the SHA and size difference and the partial-update result.

**Acceptance Criteria:**
- Given the fixtures, when the unit suite runs in CI with no OneNote, then every fixture validates against the vendored XSD and `FakeGateway` serves all nine methods.
- Given two captures of one page whose only difference is an edit elsewhere on the page, when hashed, then the untouched Outlines' `elementHashes` are equal.
- Given the epic file, when 1.6 is built, then its Notes hold a dated Decision on partial-update acceptance.

## Implementation Notes

- 2026-11-04 capture: OneNote accepted all five partial seeds and all validate against the XSD; the widened probe (root Page, ancestors and ID-less elements, all but the edited OE) found one change, `Size@width` on the edited OE's own Outline as its text grew, reported to Phill and not excluded; the untouched Outline's hash stayed equal; the image callbackID was stable across three reads.
- Observed: `GetHierarchyParent` on a notebook returns an empty string, not an error (AD-6 and the port comment say it throws). `GetBinaryPageContent` with an unknown callbackID on a local page fails `0x8004200F`, the same code as a cloud image with no binary.
- Observed (cloud, 2 images): piBinaryData drops each `one:Image` but keeps its `one:OE`, now empty. `GetBinaryPageContent` fails `0x8004200F` (recorded as `Internal`, AD-9).
- `GetSpecialLocation(UnfiledNotesSection)` points into Phill's own cloud notebook: its OneDrive account ID and folder names are placeholders. Resolution-ID author attributes are also set to the placeholder.
- `.gitattributes` marks `*.xsd` and `Fixtures/**` `-text`: `core.autocrlf=true` would otherwise break the SHA pin and the byte-for-byte fixtures. `ONENOTE_MCP_RESEED=1` soft-deletes the Fixtures section so seeds are recreated.

## Plan Change Log

## Review Triage Log

**Pass 1 (quick, 2026-11-05):** 9 findings: 0 high, 4 medium, 4 low, 0 false, 1 maybe-false. Routes: 8 patch, 1 defer.

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | `ElementHash.Compute(string)` accepts a wrong namespace | low | patch | `Compute` only parses; the matrix's Bad XML row requires it to throw. Add the namespace check plus a test. |
| 2 | Probe compares attributes only on elements with `objectID` | medium | patch | It misses the root Page, ID-less descendants and ancestors, so `changedAttributesOnUntouched: []` claims more than it measured. Widen the comparison and re-run the capture. |
| 3 | `pageHash` includes root view-state (`isCurrentlyViewed`, `isUnread`…) | maybe-false (medium if true) | patched (Phill, 2026-11-05: excluded) | Every fixture is from a page that wasn't being viewed. A read of a page open in OneNote would settle it. It's Phill's call, the same kind as the `selected` decision. |
| 4 | Cloud fixture keeps the real `{E…}` segment of the page ID | low | patch | Confirmed in `cloud/page-Basic.xml`. `MapGuids` maps only the GUID part, against the cloud Decision. Map every segment and re-run `capture-cloud`. |
| 5 | Sanitiser tests never run in CI | medium | patch | CI runs only `tests/OneNoteMcp.Tests`. Add a CI step for the Integration project; the opt-in tests skip there. |
| 6 | `GetHierarchyParent` returns "" at a notebook, but the port comment and AD-6 say it throws | medium | patch | Fix the port comment and add a deferred-work entry for `bmad-correct-course`. |
| 7 | Epic Decision overclaims the hard-gate precondition | medium | patch | `UpdateHierarchy`/`DeletePageContent` aren't covered. Narrow the wording to `UpdatePageContent` shapes, or also validate the subpage payload. |
| 8 | `SchemaValidation` returns raw XSD messages that can quote content | low | patch | AD-13 forbids logging them. Strip the values, or document "never log" on the method. |
| 9 | Mixed line endings in `manifest.json`; dead `FixtureManifest.Save` | low | patch | `git ls-files --eol` shows `w/mixed`. Use `

Patches 1, 2 and 4–9 are applied, and both captures were re-run. Verification after the patches: build 0 warnings; unit 685/685; integration 35 pass + 20 skipped without OneNote, 49 pass + 6 skipped (hitl) with OneNote. The wider probe found `Size@width` changing on the edited OE's own Outline, which reflowed. The untouched Outline was unchanged. That's reported to Phill and not added to the exclusions.
` consistently and delete the dead method. |

## Design Notes

- **Why partial XML may fail the XSD:** `ObjectID` is a pattern type and `objectID` may be `use="required"` on some elements. A new Outline legitimately has none. The test documents are a new Outline with no IDs, an existing OE with its ID and changed text, and a Page with a title only.
- **Basis comparability:** hashes compare only between reads of the same `PageInfo` kind for cloud pages, because piBinaryData drops missing images. Stories 8 and epic 2 compute the basis from piBasic. Note this in the `PageBasis` doc comment.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, including the new Xml and FakeGateway tests.
- `ONENOTE_MCP_INTEGRATION=1 ONENOTE_MCP_SCENARIO=capture-fixtures dotnet test tests/OneNoteMcp.Integration --filter FixtureCapture` -- expected: fixtures written; probe results printed.

**Manual checks:**
- Phill reviews the sanitised cloud fixture and the `git diff` of `Fixtures/` before commit.
