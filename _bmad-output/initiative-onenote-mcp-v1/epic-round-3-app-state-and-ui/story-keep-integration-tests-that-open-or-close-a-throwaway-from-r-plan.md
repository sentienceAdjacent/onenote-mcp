---
title: 'Keep integration tests that open or close a throwaway from running at once'
type: 'bugfix'
ticket: '14'
created: '2026-11-26'
status: 'built'
baseline_revision: '013316364406053959e8b349503ea0deef222d8d'
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

**Problem:** In story 4.11's live sitting (2026-11-26), `ONENOTE_MCP_INTEGRATION=1` filtered to SyncHierarchy, CloseNotebook and OpenNotebook skipped `CloseNotebook_ThrowawayB_ClosesIt_ReopensByPath_AndItsWriteGrantIsActiveAgain` ("Open the throwaway notebook ... first"): xUnit ran `OpenNotebookIntegrationTests` in parallel, and it closed and reopened the same throwaway b. Alone, the CloseNotebook filter passed 2 of 2.

**Approach:** Test-only. One shared xUnit collection, `ThrowawayOpenState`, holds every integration test class that opens (by path, by package, or by creating one) or closes a notebook, so they never run at once; a reflection guard test, which runs without the variable, fails when such a class is outside it. No product code changes; no test's assertions change.

Decisions (approved by Phill with the story, 2026-11-26):
- Membership is by what a class reaches, not by which throwaway it names: any test class whose IL (its own methods, its nested state machines, lambdas and helper gateways, and every method of the integration assembly they call, such as `TestNotebook`'s, `ProbeSession`'s and the probes' static steps) calls `TestNotebook.CloseNotebookAsync`, `OpenExistingAsync`, `CreateThrowawayNotebookAsync`, `CloseOpenedCopyAsync`, `OpenPackageAsync`, `OpenBackupAsync`, a gateway's `CloseNotebookAsync`, `OpenHierarchyAsync`, `CreateNotebookAsync` or `OpenPackageAsync`, or constructs `CloseNotebookUseCase`, `OpenNotebookUseCase` or `OpenPackageUseCase`. Today that is CloseNotebook, OpenNotebook, OpenPackage, CreateHierarchy and UpdateHierarchy, plus thirteen scenario probe classes that open or close through `ProbeSession` and their static steps (AppStateLocal, AppStateCloud, AppStateNav, OneDriveCrash, OpenPackage, ProtectedDelete, HierarchyLocal, HierarchyCloud, NotebookDelete, Round2Local, Round2Cloud, Round2ProtectedLocal, Round2ProtectedCloud); joining costs nothing, since a scenario runs alone. `TestNotebookGuardTests` is exempt by name, with its reason: it runs the wrappers' COM lambdas on a recording fake, never on OneNote. A direct `IApplication.OpenHierarchy` doesn't count, since most callers create sections with it; notebook opens and creates go through `TestNotebook`'s wrappers, which do. Readers of throwaway a (Merge, Move, Publish, RestoreFromRecycleBin) stay out: none of the members closes a, and OpenPackage's copy opens as `<package>-unpacked` (round-2 epic file, from 5.1's constraints), so it never clashes with adoption by a's exact name. SyncHierarchy and QuickFiling touch only MCP Test and stay out. The collection doesn't disable parallelization: its members run one at a time, beside the other classes.

</frozen-after-approval>

## Implementation Notes

Oneshot: about 160 lines, nearly all one new file (a collection definition and one guard test class) plus eighteen one-line attributes.

- New `tests/OneNoteMcp.Integration/ThrowawayOpenState.cs`: `[CollectionDefinition]` `ThrowawayOpenState` (`Name = "Throwaway open state"`) and `ThrowawayOpenStateGuardTests`, which decodes each test class's IL (operand sizes from `System.Reflection.Emit.OpCodes`, so no byte-pattern guessing) and resolves every `InlineMethod` operand. A second test pins the scan: it must find CloseNotebook and OpenNotebook and not SyncHierarchy.
- The scan's first run (before any attribute) named ten classes: the five integration classes and five probe classes. The probes had `[Fact]`s after all (gated by `ONENOTE_MCP_SCENARIO`), so the plan's probe exclusion was wrong; they joined (intent updated before review).
- `[Collection(ThrowawayOpenState.Name)]` added to those ten classes; no other line changes.
- Review (quick) found the scan stopped at the class boundary, so probes opening through top-level helpers escaped. The scan now follows calls into this assembly's methods (memoised, with async state machines and lambdas). That flagged eight more probe classes, which joined, and `TestNotebookGuardTests` (calls `TestNotebook.CloseCall`/`OpenExistingCall` lambdas against a recording fake), now exempt by name. Merge, Move, Publish, RestoreFromRecycleBin, SyncHierarchy and QuickFiling are still not flagged.
- Guard check: with the attribute removed from `OpenNotebookIntegrationTests` (first version) and from `CloseNotebookIntegrationTests` (final version), the guard failed naming that class; restored, it passes.
- Final suites (variable unset): build 0 warnings; unit 4214 passed, 3 skipped (4217); integration 417 passed, 137 skipped (554, two of them the new guard tests).

## Review Triage Log

- Quick review, pass 1 (high 0, medium 1, low 1, false 0, maybe-false 0):
  - medium, patch: the guard read only the class and its nested types, so probe classes that open or close through top-level helpers (AppStateCloud, AppStateNav, NotebookDelete, Round2Cloud, Round2Protected*, Hierarchy*) were outside the collection. Fixed: transitive scan within the assembly; those classes joined.
  - low, patch (comment only): `IApplication.OpenHierarchy` isn't a marker, and the doc comment overstated coverage. Counting it would flag every section-creating class; the doc comment now says it isn't counted and why.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror --artifacts-path <scratch>` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests --artifacts-path <scratch>` and `dotnet test tests/OneNoteMcp.Integration --artifacts-path <scratch>` (variable unset) -- expected: all pass; the guard test passes
- Remove the `[Collection]` attribute from one member, rerun the guard filter -- expected: it fails naming that class; restore it

**Live sitting (Phill present; the build never runs this):**
1. OneNote running at the same integrity level, MCP Test and `MCP Test Throwaway 1112-034716 b` open, an MCP Test page on screen.
2. In the repo root: `$env:ONENOTE_MCP_INTEGRATION=1; dotnet test tests/OneNoteMcp.Integration --filter "FullyQualifiedName~SyncHierarchy|FullyQualifiedName~CloseNotebook|FullyQualifiedName~OpenNotebook"`
3. Expected: 6 tests, all passed and none skipped (CloseNotebook 2, OpenNotebook 2, SyncHierarchy 2). A skip that says "Open the throwaway notebook ..." is the clash coming back. Then `Remove-Item Env:ONENOTE_MCP_INTEGRATION`.
