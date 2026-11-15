---
title: 'Refactor sweep (epic 5)'
type: 'refactor'
ticket: '8'
created: '2026-11-15'
status: 'built'
baseline_revision: 'ad712afb42864699e4baf9b957cf0188cd03d3f3'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred:
  - summary: >-
      CreatePageIntegrationTests builds SettingsStore(path) with no journal folder, the same latent exposure 5.8 removed from CreateHierarchyIntegrationTests.
    evidence: |-
      tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs l.199 and l.242; CreatePageUseCase writes no journal today, so nothing reaches the user's journal folder. Epic 2 code, outside this epic-5 sweep.
    location: >-
      tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs:199
    severity: low
---

<intent-contract>

## Intent

**Problem:** Epic 5's builds left one cleanup item for this sweep: the "share points still open" after 5.6's rebase onto 5.5 (deferred-work l.538; 5.6 plan Plan Change Log): one target reader for both deletes (5.5's `DeleteTargets`, 5.6's `PermanentDeleteTarget`, and the subpage count in both) and the refusals common to both deletes in one place. The Xml layer also carries four identical copies of the element-to-`HierarchyKind` switch and three copies of the recycle-bin attribute tests, and the 5.4 build noted that 5.3's `CreateHierarchyIntegrationTests` builds `SettingsStore(path)` with no journal folder, so it would fall back to the user's real journal folder if a create ever journaled (5.4 plan Implementation Notes, Incident).

**Approach:** Code-motion and test-setup changes only, every one identical in behaviour for every input; no message, error code, retryability, schema or COM change. In scope:
1. One internal `HierarchyElements` helper in `OneNoteMcp.Xml` holding `KindOf`, `IsRecycleBin` and `InRecycleBin` (today `HierarchyChildren`'s internals), used by `HierarchyChildren`, `HierarchyTarget`, `HierarchyNode`, `DeleteTargets`, `PermanentDeleteTarget`, `RecycleBin.IsBin` and `NotebookSections` in place of their private copies. Each replacement is the same expression: `PermanentDeleteTarget`'s and `HierarchyTarget`'s recycle-bin test is `IsRecycleBin` as written; `RecycleBin.IsBin` is `KindOf == SectionGroup && IsRecycleBin`; `NotebookSections` applies its test to `one:SectionGroup` ancestors only, where it equals `IsRecycleBin`; every `isInRecycleBin || isDeletedPages` test is `InRecycleBin`.
2. `OneNoteXml.ParseRoot(xml)` for the repeated `Parse(xml).Root ?? throw new OneNoteXmlException("The XML has no root element.")` (same exception, same text).
3. `CreateHierarchyIntegrationTests` gives each `SettingsStore` a journal folder inside its temporary folder, as `UpdateHierarchyIntegrationTests` does. Compiled only; never run here.
4. Doc comments that point at "the share-point refactor" now point at the open question below; `deferred-work.md`'s 5.6 share-point entry records what this sweep did and what is left.

Out of scope, returned as questions (each changes behaviour or a public message): merging the target records and the subpage count (the three `pageLevel` parses and two recycle-bin rules differ on XML OneNote never writes); moving the refusals common to both deletes into one place (their messages and `userAction`s differ, e.g. subpages and notebook). Not cleanup (spine syncs, seams, live sittings; left as they are): every other 5.3–5.6 entry and all three 5.7 entries.

</intent-contract>

## Implementation Notes

Oneshot: about 100 changed lines across nine source/test files plus `deferred-work.md`; all mechanical, behaviour-identical substitutions.

- Files: new `src/OneNoteMcp.Xml/HierarchyElements.cs` (moved from `HierarchyChildren`'s internals); `HierarchyChildren.cs`, `HierarchyUpdate.cs` (`HierarchyTarget`), `HierarchyNode.cs`, `DeleteTargets.cs`, `PermanentDeleteTarget.cs` (copies removed; summary now points at the open question), `RecycleBin.cs` (`IsBin` = section group and `IsRecycleBin`, doc says why it differs), `NotebookSections.cs`; `OneNoteXml.cs` (`ParseRoot`), used at 13 sites, `ElementHash.cs` included; `tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs` (`Store(settingsPath)` with a temp journal, three call sites); `deferred-work.md` (the 5.6 share-point entry: partly done, the rest as questions).
- `ElementSpans.cs` l.262 keeps its own no-root `throw` (a statement inside other flow, not the `Parse(...).Root ?? throw` expression), so the manual grep also finds it there.
- Not merged, by design: the three `pageLevel` parses and the record types (see the Intent's questions). The 5.4 Incident note's unit-test half needs nothing: unit tests are isolated by `TestEnvironment` (env overrides), so only the integration file changed.
- Surprise: Git Bash `sed -i` rewrote CRLF working files as LF; restored to CRLF before the build (the index holds LF either way).
- Verification: build 0 warnings 0 errors; unit tests 2886/2886 (same count as before; no test pinned a changed symbol); stress harness builds. Integration tests compiled, not run.

## Plan Change Log

## Review Triage Log

### 2026-11-15 — Review pass
- verdicts: 5 findings — high 0, medium 0, low 5, false 0, maybe-false 0
- findings:
  - `[low]` `[reject]` Verification's manual grep expects only `OneNoteXml.cs`, but `ElementSpans.cs` l.262 also matches — true (Implementation Notes already say so); its fix edits this build's plan.
  - `[low]` `[reject]` Implementation Notes say "nine source/test files"; the diff touches eleven code files — true; its fix edits this build's plan (the Files bullet lists all eleven).
  - `[low]` `[patch]` `using System.Xml.Linq;` left unused in `NotebookSections.cs` and `HierarchyChildren.cs` after their helpers moved — confirmed (no `X*` type named in either); both lines removed.
  - `[low]` `[patch]` `DeleteTargets.cs` l.8 described `IsRecycleBin` as `isRecycleBin` on any element, while the code is `RecycleBin.IsBin` (section groups only) — confirmed; the doc now says a section group with `isRecycleBin`, or named `OneNote_RecycleBin`, and points at `RecycleBin.IsBin`.
  - `[low]` `[defer]` `CreatePageIntegrationTests` l.199, l.242 build `SettingsStore(path)` with no journal folder — confirmed, pre-existing epic 2 code, latent (creates don't journal); added to frontmatter `deferred`.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot unchanged.
- `dotnet build spikes/stress-harness -c Release` -- expected: builds. The harness's live "0 mismatches" run needs OneNote and is left to Phill.

**Manual checks (if no CLI):**
- `grep -rn '"SectionGroup" => HierarchyKind.SectionGroup' src` finds one hit (`HierarchyElements`).
- `grep -rn 'The XML has no root element' src` finds only `OneNoteXml.cs`.

## Auto Run Result

- **Summary:** cleanup only, no behaviour change. The Xml hierarchy readers share one `HierarchyElements` (`KindOf`, `IsRecycleBin`, `InRecycleBin`) instead of four kind switches and three recycle-bin tests; `OneNoteXml.ParseRoot` replaces 13 copies of the no-root throw; 5.3's create integration tests keep their journal in their temp folder; the 5.6 share-point entry in `deferred-work.md` records what was done and what is left as questions.
- **Files:**
  - `src/OneNoteMcp.Xml/HierarchyElements.cs` — new; the shared element checks.
  - `src/OneNoteMcp.Xml/HierarchyChildren.cs`, `HierarchyUpdate.cs`, `HierarchyNode.cs`, `DeleteTargets.cs`, `PermanentDeleteTarget.cs`, `RecycleBin.cs`, `NotebookSections.cs` — use it; private copies and two unused `using`s removed; `DeleteTargets` and `PermanentDeleteTarget` docs corrected.
  - `src/OneNoteMcp.Xml/OneNoteXml.cs` — `ParseRoot`; `ElementHash.cs` uses it.
  - `tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs` — `Store(settingsPath)` with a temp journal (compiled, not run).
  - `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — 5.6 share-point entry: partly done, rest as questions.
- **Review:** quick lens, 5 findings, all low: 2 patched (unused `using`s, stale `DeleteTargets` doc), 1 deferred (`CreatePageIntegrationTests` has the same latent journal exposure; epic 2 code), 2 rejected (their fixes edit this plan: the manual grep also finds `ElementSpans.cs`, and the file count says nine where eleven changed).
- **Follow-up review recommended:** false (patched: high 0, medium 0, low 2).
- **Verification:** `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings 0 errors; `dotnet test tests/OneNoteMcp.Tests` 2886/2886; `dotnet build spikes/stress-harness -c Release` builds; the kind-switch grep finds only `HierarchyElements.cs`.
- **Not run (safety limits):** integration suite and the stress harness's live "0 mismatches" run on MCP Test; no read or write logic changed, but the live run is Phill's.
- **Residual risks:** none known; every substitution is the same expression. Open questions: merging the three target records and `pageLevel` parses, and sharing the delete refusals (both change behaviour or messages).
