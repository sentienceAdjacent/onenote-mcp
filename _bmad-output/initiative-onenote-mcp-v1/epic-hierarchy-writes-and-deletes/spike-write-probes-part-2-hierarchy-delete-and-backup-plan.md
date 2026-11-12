---
title: 'Write probes, part 2: hierarchy, delete and backup'
type: 'chore'
ticket: '1'
created: '2026-11-10'
status: 'in-review'
baseline_revision: '1bf82b7b8637fc6dee0e47b780a1212d6bf3d0b0'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/spike-write-probes-part-1-pages-paths-and-search-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Entry 2 must amend AD-7, AD-8, AD-9 and AD-21 before any hierarchy write story, but the behaviour they rest on was never measured: `OpenHierarchy` create, `UpdateHierarchy` add/rename/reorder, `DeleteHierarchy` soft and permanent on each kind of node, deletes in protected sections, the `Publish` backup route, which date `DeleteHierarchy` accepts, and the HRESULTs and modal-dialog behaviour of each hierarchy method. Five spine Deferred rows wait on this spike.

**Approach:** Extend spike 2.2's probe code (`tests/OneNoteMcp.Integration/WriteProbes/`, not a fork) with opt-in scenarios run live with Phill present. Every write goes through `TestNotebook`'s guard, tightened for deletes and hierarchy XML. Answers go into a findings doc, one row per question, and into the spine memlog as constraints. No `src/` change.

## Boundaries & Constraints

**Always:**
- Every write goes through `TestNotebook.WriteAsync` and its guard (AD-16): a fresh hierarchy read, the target inside MCP Test or MCP Test Cloud. All dates UTC.
- Deletes target only objects this run created (an ID set the session keeps, re-resolved by name after a soft delete gives a page a new ID). The guard refuses `DeleteHierarchy` and any `UpdateHierarchy` rename on the MCP Test or MCP Test Cloud notebook IDs themselves, and on any object the run didn't create (the fixtures: Seed, Probes, Ballast *, Text/Table/Image Page, Group A).
- `UpdateHierarchy` XML: its root and every ID in it must be inside a test notebook; a `one:Notebook` root only for a throwaway notebook. Reorder and partial-sibling probes run inside a throwaway section group.
- Notebook-level deletes run only on throwaway notebooks, never on MCP Test or MCP Test Cloud.
- `Publish` writes only under `%LOCALAPPDATA%\OneNoteMcp-Test\probes\publish\`, outside the repo.
- Each probe records method, args shape, HRESULT, outcome and a read-back (hierarchy, and the file system for permanent deletes and backups). One question, one row. The OneDrive account ID is written `<cid>`.
- Steps needing Phill (modal dialog, password-protecting and unlocking a section, showing a page, closing an opened backup) pause with a printed instruction that the team lead relays.
- The crash breaker (`PinnedOneNoteProbe`) stays on: a crash stops the scenario.

**Never:**
- Write, rename, reorder or delete anything outside the two test notebooks and the run's throwaways; touch Ballast Vault.
- NavigateTo; start, close or kill OneNote; `CloseNotebook` on MCP Test or MCP Test Cloud.
- Move through `UpdateHierarchy`, `MergeSections`, `MergeFiles` (epic 3), except the one accidental-move row below.
- Change `src/` or the spine text (entry 2 amends it); only the memlog gets lines.
- Commit real notebook names or content.

## I/O & Edge-Case Matrix

| Area | Question (one findings row each) |
|---|---|
| Create | `OpenHierarchy` cftSection and cftFolder by parent ID + relative name (local, cloud): ID, path, focus. An existing name: opens it or errors? Parent locked, unknown, a section (not a container)? cftNotebook at an empty path, a non-empty path, an existing notebook's path |
| HierarchyWrite | `UpdateHierarchy` add (Section/SectionGroup without ID), rename (section, section group, throwaway notebook: `name` or `nickname`?), reorder of sections and of pages. A partial child list: omitted siblings kept or deleted? A known ID placed under another parent: moved, ignored or refused? Does the parent's `lastModifiedTime` move after each? |
| Soft delete | Page, section, section group (local, cloud), throwaway notebook: where it lands, new ID or not, `isInRecycleBin`/`isRecycleBin`, files on disk |
| Permanent delete | Same kinds: gone from hierarchy and disk (local), from OneDrive (cloud, read-only check by Phill); an item already in the recycle bin |
| Protected | Soft delete of a page in an unlocked protected section (recycle bin, refused, or permanent?); a protected section soft-deleted whole: `encrypted` kept in the recycle bin, locked and unlocked; its permanent delete |
| Backup | `Publish` pfOneNote of a section, section group and throwaway notebook (local, cloud): file produced, extension, size, time; an existing target file. Can the backup bring the item back after its permanent delete? |
| Dates | `DeleteHierarchy` with the page-XML date, the hierarchy date (cloud, where they differ), stale, `DateTime.MinValue`; displayed and not; a section with its hierarchy date and stale |
| Modal dialog | HRESULT per method (`OpenHierarchy`, `UpdateHierarchy`, `DeleteHierarchy` both modes, `Publish`) with a dialog open |
| HRESULTs | Each error seen per method, with its trigger, mapped to AD-9 |

## Decisions (Phill, 2026-11-10, plan review)

- Q1 (a): the probe creates each throwaway local notebook, `MCP Test Throwaway <stamp>`, with `OpenHierarchy` cftNotebook under `%LOCALAPPDATA%\OneNoteMcp-Test\`, after `EnsureFolderIsOurs` finds the folder empty. The guard accepts it only by that exact name, a path under that folder and an ID this run created. `MCP Test Throwaway` joins the names the repo may contain.
- Q2 (b): Phill creates `MCP Test Throwaway Cloud` in OneDrive Documents from OneNote at a pause; the suite never creates a cloud notebook. The probe finds it by exact name and a `d.docs.live.net` path, backs it up, soft-deletes it, then permanently deletes it. Phill then checks OneDrive's web recycle bin and reports; no probe reads OneDrive.
- Q3 (b): the protected-section probes run on a throwaway section in MCP Test and one in MCP Test Cloud, which Phill password-protects, locks and unlocks at pauses.
- Q4 (b): a backup is checked as a file (exists, non-empty, extension, size, `Publish` time), then, after the original's permanent delete, opened with `OpenHierarchy` (a path in the probes folder, never a notebook's) and compared with the pre-delete read: page titles, count, and `encrypted` for the protected one. Phill closes it in OneNote at a pause.
- Q5 (b): `Publish` tries `pfOneNote`; wherever it fails, `pfOneNotePackage` is tried too.
- Q6 (b): the modal probe holds File > Options open, then the section password prompt. Other dialogs stay Deferred.
- Q7 (a): keep the full plan (about 2,700 tokens).

</frozen-after-approval>

## Code Map

- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `TestNotebook.WriteAsync` (:122-151) and `TestNotebookGuard.Contains` (:278-288). `Contains` is true for the notebook root itself, so a `DeleteHierarchy` on MCP Test's ID would pass today: add the delete/rename refusal and a throwaway-notebook lookup beside `FindCloudNotebookId` (:204), not inside it. `EnsureFolderIsOurs` (:291) is the empty-path check for a throwaway notebook.
- `tests/OneNoteMcp.Integration/TestNotebookGuardTests.cs` -- pure guard tests; add the new refusals here.
- `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs` -- `StepAsync`, `TryWriteAsync`, `PauseAsync` (pause file, `skip`), `RequireScenario`, `SectionIdAsync`, `PageIdByTitleAsync` (skips recycle-bin pages; soft-delete read-backs need a variant that includes them). Add `OpenHierarchyAsync`, `UpdateHierarchyAsync`, `DeleteHierarchyAsync`, `PublishAsync` wrappers and the created-ID set here.
- `WriteProbes/LocalWriteProbes.cs`, `CloudWriteProbes.cs` -- scenario shape (stamped names, one `StepAsync` per area, `s.Finish()`); `DatesProbe.cs` -- the date pattern to mirror for `DeleteHierarchy`; `ProbeRecorder.cs` -- JSONL rows, `<cid>` redaction; `PinnedOneNoteProbe.cs` -- crash breaker; `ProbeXml.cs` -- XML helpers (add hierarchy builders).
- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- `UpdateHierarchy` (:27), `OpenHierarchy` (:32), `DeleteHierarchy` (:39), `Publish` (:99); `Enums.cs` -- `CreateFileType`, `PublishFormat.pfOneNote = 0`, `pfOneNotePackage = 1`. Reach them via `ComGateway.InvokeAsync`. Don't edit.
- `tests/OneNoteMcp.Integration/BallastFixture.cs:124-129` -- the owner instruction for password-protecting a section by hand (COM can't): reuse its wording for the throwaway protected section.
- Spike 1 findings rows 9-12, 19-24 -- already known: OpenHierarchy notebook/section create, `UpdateHierarchy` add keeps siblings and section rename keeps the ID, soft page delete gives a new ID, soft section delete keeps the ID, permanent page delete, stale date ignored on a page not displayed, `Publish` over an existing file `0x8004201a`. Re-confirm briefly; don't re-probe at length.
- Spine (on `story/2.3-spine-amendments`, main checkout): AD-3 (300 s `Publish` timeout), AD-7 (Create/HierarchyWrite/SoftDelete/PermanentDelete rows), AD-8, AD-9, AD-17 (hierarchy-node date), AD-21, and Deferred rows for whole-notebook delete, section-group permanent delete, protected page soft delete, `Publish` backup, `DeleteHierarchy` date, protected section deleted whole.
- `_bmad/scripts/memlog.py append --path <spine>/.memlog.md --type constraint --text "..."` -- memlog lines. The memlog may conflict with the 2.3 branch; append at the end only.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Integration/TestNotebook.cs` + `TestNotebookGuardTests.cs` -- guard: refuse delete/rename of the test notebooks' own IDs; `UpdateHierarchy` XML check; throwaway-notebook lookup (Q1, Q2); tests for each refusal and look-alike.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs`, `ProbeXml.cs` -- hierarchy wrappers, created-ID set (deletes refuse anything else), recycle-bin lookups, publish folder; helper tests in `ProbeHelpersTests.cs`.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/` -- scenarios `write-probes-hierarchy-local`, `-cloud`, `write-probes-protected-delete`, `write-probes-notebook-delete`, covering every Matrix row.
- [ ] Live runs (team lead's session, Phill present, after implementation and review) -- run each scenario; relay pauses. Not part of implementation.
- [ ] `epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md` (after the live runs) -- 2.2's structure; one row per Matrix question; Not probed and Evidence gaps.
- [ ] Spine `.memlog.md` (after the live runs) -- one `constraint` line per finding that changes an AD, naming it.

**Acceptance Criteria:**
- Given the guard, when a delete or rename targets MCP Test, MCP Test Cloud, a fixture, or anything the run didn't create, then it refuses before any COM call.
- Given the findings doc, when entry 2 reads it, then every Matrix question has a result or a stated reason it wasn't probed, and each of the five Deferred rows has an answer.
- Given the staged diff, when grepped for the account ID and real notebook names, then nothing matches.

## Implementation Notes

- Implementation writes code and unit tests only. Never set `ONENOTE_MCP_INTEGRATION` or `ONENOTE_MCP_SCENARIO`, and never call OneNote: every live run happens later in the team lead's session.
- Every `PauseAsync` instruction must stand alone (what to do, in which notebook and section, what to leave as it is), because the team lead relays them to Phill in one batch. Keep step names stable, so `ONENOTE_MCP_PROBE_STEPS` can re-run one.
- 2026-11-10, implemented by the build session directly: the step-3 subagent left no changes and couldn't be messaged.
- **The guard.**
  - `TestNotebookGuard.Contains` still accepts a notebook's own ID, because creates name the notebook as the parent. The delete and rename refusals sit in front of it:
    - `CheckDelete` refuses MCP Test, MCP Test Cloud, and any ID not in `ProbeSession.Created`.
    - `CheckHierarchyXml` finds every `ID` with a regex, so malformed XML is checked too. Each must be in `Created` and be neither test notebook.
  - All of these go through `TestNotebook.GuardedAsync`. It also accepts a target this run made that a full hierarchy read finds nowhere, which lets the stale-ID probes run.
  - `Created` grows from these sources, and only these:
    - new IDs from `OpenHierarchy`, and only an ID no allowed notebook held before the call;
    - `CreateNewPage`;
    - throwaway notebooks;
    - the cloud throwaway's tree, once adopted;
    - `FindByNameAsync`, and only for names that start with `probe ` (no fixture name does).
- **Throwaways.**
  - A local throwaway is a folder directly in `%LOCALAPPDATA%\OneNoteMcp-Test`, fixed whatever `ONENOTE_MCP_TEST_DIR` says. Its `name` and its folder must each be one of the exact names this run gave it (review finding 8).
  - The cloud throwaway is matched by its exact name and its `d.docs.live.net` path.
  - Both are re-checked live on every write.
- **Timeouts.** The new scenarios first set `callSeconds` to 300 (AD-3's `Publish` timeout). After run 1 they use 60 (`ProbeSession.ProbeCallSeconds`), see below.
- **Run 1 (2026-11-11, `write-probes-hierarchy-local-20261111-033107`), stopped by the team lead.** Phill had shown two pages in `probe dates s`. The next section-level date row then blocked about 3 minutes behind OneNote's "OneNote is syncing changes" dialog, until Phill pressed Cancel. The call after that froze OneNote's UI.
  - The log shows the `DeleteHierarchy` attempt itself took 104 ms (row `dates-local-section-stale`, 03:39:51). So the block came in the setup just before it: the `OpenHierarchy` create of an empty sibling section, or the reads around it.
  - Fixes, all in `tests/`:
    - The section date rows run first, in their own group, before any page is shown.
    - `ProbeSession.EnsureNothingShownAsync` runs before every `OpenHierarchy` under a probe object, `UpdateHierarchy`, hierarchy-level `DeleteHierarchy` and `Publish`. It runs before the basis reads, so its pause can't come between a read and its call. If anything in the probe area (the run group, a throwaway notebook, or a recycled probe section) is `isCurrentlyViewed`, Phill clicks MCP Test > Seed or MCP Test Cloud > Probes > Text Page. Each check writes a `<step>-shown-check` row.
    - Page-level deletes keep their shown page.
    - Modal probes turn the check off while their dialog is open.
    - Every call times out at 60 s. A `Timeout`, or a `ComBusy` that took the whole timeout, trips `PinnedOneNoteProbe`, writes `<step>-call-timeout`, asks Phill to Cancel any dialog, and stops the scenario. The trip comes first, so no second OneNote can start.
    - The re-run's first row, `previous-run-note`, records run 1.

- **Backups.**
  - Section tries: `pfOneNote` `.one`, then `pfOneNotePackage`.
  - Section-group and notebook tries: `.one`, then a folder name, then the package.
  - A `.onepkg` is unpacked with `OpenPackage` into the publish folder before `OpenHierarchy`. The plan didn't name this; Q4's open-and-compare needs it for a package.
  - A permanent delete after a backup runs only if a backup succeeded (AD-7).
- **Create.** One extra row: a relative name with `..\` from a nested group, which still lands inside the notebook. It shows whether AD-7's "relative name" has to refuse `..`.
- **Not changed, worth knowing.**
  - 2.2's `PathProbes.IsCandidate` matches any notebook whose name starts with "MCP Test", so a throwaway or an opened backup left open would show in its captures.
  - Each guarded call re-reads the test notebooks' trees. With MCP Test's Ballast pages, a scenario makes many slow reads.

## Plan Change Log

## Review Triage Log

Quick lens, iteration 0 (2026-11-10). Verdicts: high 0, medium 5, low 4, false 0, maybe-false 1. On the team lead's instruction, routes are proposed, not applied: Phill picks.

| # | Finding | Verdict | Evidence | Proposed route |
|---|---|---|---|---|
| 1 | `update` runs `partial-children` (which omits s3 and the nested group) before `accidental-move` (which needs both) | medium | `HierarchyProbes.UpdateAsync` order: reorder-sections, partial-children, reorder-pages, accidental-move. If OneNote deletes the omitted siblings, the move row reads a recycled or gone s3. 2.2 hit the same trap | patch: run `accidental-move` before `partial-children` |
| 2 | `ModalAsync`: a throw between the open and close pauses skips the close instruction. Guard reads inside `TryAsync` get recorded as the write method's HRESULT | medium | No try/finally around the close pause. `CreateChildAsync` calls `KnownIdsAsync` outside `TryValueAsync`. `TestNotebook.*Async` guard reads run inside the probe's `TryAsync`. Reads with the password dialog open were never tried | patch: close pause in `finally`; one plain `GetHierarchy` row before the calls, so a blocked read shows as its own row |
| 3 | The protected section's permanent delete runs even when its backup failed | low | `ProtectedDeleteProbes` `{p}-permanent-delete` doesn't check `backup`; the Implementation Notes say otherwise. The delete still answers the Matrix row on a throwaway | patch: keep the delete; record `backupSucceeded` in its row |
| 4 | Design Notes say every section, group or notebook permanent delete follows a `Publish` of it; several don't (recycled items, the gone-parent and stale-ID setups, throwaways a and c) | low | True as stated. No harm: all are run-made throwaways, and the delete rows stand alone. Fix is more publishes (complexity) or editing this plan | reject (low, fix adds complexity or edits the plan) |
| 5 | The cloud notebook's permanent delete is skipped if its soft delete takes it out of the open list | medium | `NotebookDeleteProbes.BackupDeleteOpenAsync(softFirst: true)` writes `notProbed` when the notebook isn't open, and the cloud has one throwaway (Q1's own rationale expects this) | patch: if it's no longer open, pause for Phill to reopen it from OneDrive (File > Open), then the permanent delete; if it can't be reopened, the row says so |
| 6 | No OneDrive check for cloud page, section or section-group permanent deletes | medium | The Matrix asks for "from OneDrive (cloud, read-only check by Phill)". Only `nb-cloud-onedrive-check` exists; `Disk` returns `local:false` for URLs | patch: one pause at the end of `write-probes-hierarchy-cloud` asking Phill to check `MCP Test Cloud/probe run <stamp>` on OneDrive and in its recycle bin |
| 7 | `create-parent-gone`: `OpenHierarchy` with a deleted parent ID could create the section outside the test notebooks, and the read-back looks only in the test notebook | maybe-false | Untested. If OneNote falls back to a default or current location, it could write to a real notebook, which breaks the hard rule. The guard can't stop what OneNote does with a parent that doesn't exist | patch: drop this one probe (a deletion); the findings doc lists it under Not probed |
| 8 | The throwaway root follows `ONENOTE_MCP_TEST_DIR`'s parent, not Q1's fixed `%LOCALAPPDATA%\OneNoteMcp-Test`; the guard accepts any `MCP Test Throwaway ` prefix, not the exact name | medium | `ThrowawayRoot` = parent of `ResolveFolder`; `IsLocalThrowaway` checks the prefix. Q1 (frozen) says "that exact name, a path under that folder". `Created` limits the risk | patch: pin the root to `%LOCALAPPDATA%\OneNoteMcp-Test`; accept only the exact names this run gave each throwaway (created name, plus the one rename it sends) |
| 9 | Notebook and protected steps return silently when an earlier step's state is missing (a step-filter re-run writes no row) | low | `nb-*` steps `return` when `a`/`c` is null; `ProtectedDeleteProbes.RunAsync` returns after a failed setup | patch: write a `notProbed` row naming the missing setup step |
| 10 | `backup-<label>-existing-file` is skipped with no row when only the `.onepkg` fallback worked | low | `if (File.Exists(existing))` with no else | patch: else branch writes `notProbed` |

Phill's picks (2026-11-10, relayed by the team lead): apply 1, 2, 3, 5, 6, 7, 8, 9 and 10 as proposed; reject 4. Applied by the build session the same day:

- 1: `partial-children` now runs last in `update`.
- 2: the close pause is in a `finally`; a plain `GetHierarchy` row comes first; the read-backs can't throw.
- 3: `{p}-permanent-delete-backup` records `backupSucceeded`.
- 5: the `nb-cloud-reopen` pause, then the notebook is adopted again and the permanent delete runs.
- 6: the `onedrive-check` pause ends `write-probes-hierarchy-cloud`.
- 7: `create-parent-gone` is dropped and logs `notProbed`.
- 8: `TestNotebook.ThrowawayRoot` is fixed at `%LOCALAPPDATA%\OneNoteMcp-Test`. The guard takes, per throwaway, only the exact names this run gave it (`ProbeSession.AllowThrowawayName`, called before the rename).
- 9: `notProbed` rows name the missing setup step.
- 10: the else branch is added.

Build: 0 warnings. Tests: 1305 unit tests passed; offline integration 145 passed, 33 skipped.

Run-1 fixes review (2026-11-11). Reviewer: context-free agent `review-5-1-run1-fixes`. It reviewed only the diff of the run-1 fixes. All findings were checked against the code, and the clear ones were applied by the build session.

| # | Finding | Verdict | Route |
|---|---|---|---|
| R1 | A blocked call that ends as `ComBusy` (busy retries to the deadline) didn't stop the scenario | medium | applied: `ProbeSession.Blocked` treats a `ComBusy` within 10 s of the timeout as blocked; `StepAsync` and `SetupAsync` stop on `Timeout` or `ComBusy` |
| R2 | The modal probe's `SafeAsync` and plain read swallowed a timeout, so the stop came from the breaker with no row | medium | applied: `SafeAsync` rethrows `Timeout` and `ComBusy`; the read is checked |
| R3 | `CreateThrowawayAsync`, `ReopenThrowawayNotebookAsync` and the scenario setup outside steps bypassed the clean stop | medium | applied: checked; setup runs through `SetupAsync` |
| R4 | On a timeout the probe's own row was never written | medium | applied: the `call-timeout` row carries the step, the method and the attempt (code, ms, message) |
| R5 | Basis dates and "before" snapshots were read before the shown-check pause, so a navigation could change them | medium | applied: the check runs before the reads in `DeleteRowAsync`, `NotebookDeleteRowAsync`, the section date rows, `WriteRowAsync` and (added after the team lead relayed the same report) `HResultsAsync`'s `goneDate`. Left as is: `UpdateAsync`'s `s1Before`/`subBefore` and the backup page-name summaries are read before a possible pause; neither holds a basis date, and navigation doesn't change page names |
| R6 | Clicking away can re-lock a protected section, and the row would still say unlocked | medium | applied: the unlock pauses tell Phill to click away and report a re-lock; the summary and `before` are read after the check |
| R7 | Some hierarchy-level calls skip the shown check | low | partly applied: the protected scenario checks the local run group before the cloud pass (`{p}-end`). Not applied: creating or reopening a throwaway notebook, opening a backup, and the stale-ID publish, whose targets have no probe area that could be on screen |
| R8 | `Run1Note` and the docs claimed more than the code does | low | applied: wording now lists what is and isn't checked |
| R9 | Shown-check rows couldn't be told apart, and no row was written when nothing was shown | low | applied: `<step>-shown-check` rows always written, with target and area |
| R10 | `StopOnTimeoutAsync` paused before tripping the breaker | medium | applied: it trips first |
| R11 | Stale docs: the constructor comment, Implementation Notes, no plan entry | low | applied: comment and notes updated. The Change Log stays for review loopbacks, so the entry is in Implementation Notes |

## Design Notes

Order inside each scenario: create, then hierarchy writes, then soft deletes, then backup, then permanent deletes, so each step uses objects the earlier ones made. A permanent delete of a section, section group or notebook always follows a `Publish` of the same object, mirroring AD-7's `PermanentDelete` row, so the backup row and the delete row come from one object.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- pass.
- `dotnet test tests/OneNoteMcp.Integration` (without `ONENOTE_MCP_INTEGRATION`) -- the guard and helper tests pass; every OneNote test and scenario skips.

**Manual checks:**
- After the runs, MCP Test's and MCP Test Cloud's fixtures (Seed, Probes, Ballast *, the cloud pages) are unchanged, and the existing integration suite still passes.
