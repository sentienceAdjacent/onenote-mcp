---
title: 'Synthetic size fixture in MCP Test'
type: 'feature'
ticket: '17'
created: '2026-11-06'
status: 'built'
baseline_revision: 'ee2e9743fa1bff9afa638b87f783117926b7a660'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The real notebooks in spike 1.15 never exercised `one:Meta`, inserted files, binaries that actually come back, protected sections, or sizes past the client's ~50,000-character limit at will. 1.16/1.18 need a fixture that can be scaled past any real notebook.

**Approach:** An opt-in hitl scenario in the integration suite (`ONENOTE_MCP_SCENARIO=ballast`) generates a synthetic "Ballast" fixture in MCP Test through the existing guarded `TestNotebook.WriteAsync`, idempotently. The stress harness gains an encrypted-section count so it can report the protected section. Its check is the harness run on MCP Test.

## Boundaries & Constraints

**Always:**
- Every write goes through `TestNotebook.WriteAsync` (live guard: target inside MCP Test). Only MCP Test is written or read for content.
- All content is generated: made-up words from a fixed vocabulary, noise PNGs, random-byte attachments written to a temp folder and deleted after. No real content, notebook, employer or person named in committed files.
- Generation is idempotent: a rerun adds only what's missing; a page is complete only once its completion Meta is on it, and an incomplete one is recycled and rebuilt.
- Sizes past spike 1.15's real maxima by default (page XML ≥ 1,000,000 chars; notebook pages-scope listing ≥ 100,000 chars; each image ≥ 500 KB), scalable with `ONENOTE_MCP_BALLAST_SCALE` (integer ≥ 1).

**Never:** NavigateTo, close/kill/start OneNote, set a password through COM (impossible), write to the protected section once it's encrypted, `--root-pages` on the harness, change server code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| First run | scenario set, MCP Test open, no Ballast | creates section `Ballast` (oversized + binaries pages), group tree `Ballast Tree`, section `Ballast Vault`; prints counts and the hitl step | — |
| Rerun, complete | everything present | no writes; prints counts | — |
| Rerun, partial | a page without completion Meta | that page recycled (guarded) and rebuilt | — |
| Vault encrypted | `encrypted="true"` on Vault | Vault untouched; reported as protected | locked → no read attempted |
| Scenario unset | normal integration run | skipped | — |
| OneNote not running | — | fails the assert, no start | — |

</frozen-after-approval>

## Code Map

- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `EnsureAsync`, `WriteAsync` (guard), `TestNotebookGuard.FindSectionId`. Reuse; don't change.
- `tests/OneNoteMcp.Integration/FixtureCapture.cs` -- pattern to copy: `RequireScenario`, `Gateway()`, seed as partial `UpdatePageContent` (`xs2013`, `DateTime.MinValue`, force false), `OpenHierarchy(... cftSection)`, recycle via `DeleteHierarchy(..., deletePermanently:false)`. Don't change.
- `tests/OneNoteMcp.Integration/HierarchyToolsIntegrationTests.cs:38` -- asserts 0 warnings ("MCP Test has no locked section"); breaks once the Vault locks. Relax to: warnings are exactly one `sectionLocked` per locked section, and each locked section is `Ballast Vault`.
- `src/OneNoteMcp.Xml/SchemaValidation.cs` -- `Validate(xml)` for unit tests of generated XML.
- `src/OneNoteMcp.Core/UseCases/FindPagesUseCase.cs` -- locked sections' pages are absent from the hierarchy, so search tests are unaffected by a locked Vault.
- `spikes/stress-harness/XmlFacts.cs` (`SectionNode`, `Model`), `Run.cs` (`HierarchyAsync` notebook counts), `README.md` -- add `sectionsEncrypted`. Existing counts already cover Meta, inserted files, and `textOver50kBytes` per tool.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Integration/BallastContent.cs` -- pure builders: vocabulary text, oversized page body (outlines + one large table + page Meta), tree page body, vault page body, image outline, inserted-file outline, minimal PNG encoder (noise RGB, zlib, CRC32), tree plan (depth × sections × pages), names -- unit-testable without OneNote.
- [x] `tests/OneNoteMcp.Integration/BallastFixture.cs` -- the `ballast` hitl scenario: ensure section/group/pages idempotently through `WriteAsync`; one binary per update; completion Meta last; temp attachment folder cleaned up; output counts and the Vault hitl instruction (IDs and counts only).
- [x] `tests/OneNoteMcp.Integration/BallastContentTests.cs` -- unit tests: every generated page XML is XSD-valid, oversized body ≥ target, PNG signature/IHDR/IEND CRC correct, tree plan sizes, determinism, names contain no vocabulary collisions with harness JSON keys.
- [x] `tests/OneNoteMcp.Integration/HierarchyToolsIntegrationTests.cs` -- relax the warning assertion as above.
- [x] `spikes/stress-harness/XmlFacts.cs`, `Run.cs`, `README.md` -- `sectionsEncrypted` count.

**Acceptance Criteria:**
- Given OneNote running with MCP Test, when `ONENOTE_MCP_INTEGRATION=1 ONENOTE_MCP_SCENARIO=ballast` runs, then the fixture exists in MCP Test only and a rerun makes no writes.
- Given Phill has password-protected `Ballast Vault`, when the stress harness runs on MCP Test, then `summary.json` shows a get_page_content and a get_hierarchy `textOver50kBytes` ≥ 1, a find_meta call with `toolPages` ≥ 1, `insertedFiles` > 0, `sectionsEncrypted` ≥ 1, and 0 mismatches; and a case-insensitive grep of `run.jsonl`/`summary.json` for every Ballast name, Meta name and vocabulary word finds nothing.
- Given the normal integration run (scenario unset), when it runs, then it passes with the Vault locked or unlocked.

## Implementation Notes

- Files: new `BallastContent.cs`, `BallastFixture.cs`, `BallastContentTests.cs` (34 unit tests); changed `HierarchyToolsIntegrationTests.cs` and the harness's `XmlFacts.cs`, `Run.cs`, `README.md`.
- Completion Meta `ballast-complete` carries `v1;scale=N` on scale-dependent pages, so changing the scale rebuilds them.
- Fix during build-session verification: `AssertLockedWarnings` built element names from the namespace string (`Namespace2013 + "Section"`), which throws; it now uses `OneNoteXml.One`.
- Real run (2026-11-06, scale 1): first build 1,064 guarded writes, 506 pages, 1 m 35 s; oversized page 2,672,889 chars piBasic; pages-scope listing 158,079 chars. A rerun made 0 writes (checked 506 pages, 19 s). Full integration suite with the flag: 86 passed, 7 skipped.
- Harness on MCP Test before the Vault was protected: 620 comparisons, 0 mismatches, 2 `unstableRead`; `textOver50kBytes` get_page_content 2, get_hierarchy 1, find_meta 5, find_pages 20, get_binary_page_content 8; find_meta `toolPages` up to 506; `insertedFiles` 6, `binariesReturned` 9/9; content grep of 70 terms (48 vocabulary words, every Ballast/fixture name, Meta names, notebook name, user name, folder) found 0 hits.
- New OneNote quirk: both `unstableRead`s are the Files page, attribute `one:InsertedFile/@pathCache`, which changes on every read. Logged in `deferred-work.md` for epic 2 (AD-17 hash).

- After the owner password-protected the Vault (2026-11-06): its section ID changed on protection. The ballast rerun reported `encrypted=True, locked=True`, made 0 writes and read no Vault content (503 pages checked). Harness: 617 comparisons, 0 mismatches, 2 `unstableRead` (pathCache); `sectionsEncrypted` 1, `sectionsLocked` 1 (3 `sectionLocked` warnings); `textOver50kBytes` get_page_content 2, get_hierarchy 1; find_meta `toolPages` up to 503; `insertedFiles` 6; binaries 9/9 returned. Content grep: 0 hits. Integration with the flag, Vault locked: 86 passed, 7 skipped.

## Review Triage Log

Pass 1 (quick lens): 0 high, 0 medium, 3 low, 1 false, 0 maybe-false (+1 low rejected).
- low, rejected -- rerun's "no writes" is printed, not asserted. A real but manual check: the real rerun showed `writes 0`. Closing it needs a new opt-in mode (added complexity), so it's rejected under the low-finding rule.
- false -- "image ≥ 500 KB unverified after OneNote re-encodes". The harness measured get_binary_page_content text blocks up to 1,295,557 bytes, and 8 of 9 binaries over 50k, so the returned images are about 970 KB.
- low, patch -- `BallastFixture` doc comment overstated the recycle rule. Corrected.
- low, patch -- committed comments and output named the owner personally (the plan's "no person named"). Replaced with "the owner".
- low, patch -- the harness README's count list read as complete. Now "among them", and it adds `images`, `insertedFiles`, `pagesWithMeta`, `distinctMetaNames` and `binaries*`.

## Design Notes

Assumptions, approved by the team lead on Phill's behalf (routine):
1. Opt-in scenario flag rather than the default suite (answers the ticket's unknown: generation takes minutes).
2. Names use the prefix `Ballast`, which appears nowhere in harness output keys, so the content grep stays meaningful.
3. Shape: tree depth 5, 4 sections per level, 25 pages each (500 pages), every 5th page carries Meta; 8 noise PNGs 512×512 and 6 random 512 KB attachments; oversized page ~4,000 paragraphs plus a 300-row table.
4. The harness reads the root notebook list (names stay in memory, never recorded), as spike 1.15 approved; `--root-pages` is not used.

## Verification

The implementing agent runs only the build and the two `dotnet test` commands without the integration flag; the build session runs the real-OneNote checks (OneNote is shared, and only MCP Test may be touched).

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` -- pass (integration skips without the flag; Ballast unit tests run).
- `ONENOTE_MCP_INTEGRATION=1 ONENOTE_MCP_SCENARIO=ballast dotnet test tests/OneNoteMcp.Integration --filter BallastFixture` -- creates, then a rerun reports no writes.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` -- passes.
- Harness: `pack-plugin.ps1`, build, `StressHarness --notebook-name "MCP Test" --out <temp>` -- exit 0 with the AC numbers.
