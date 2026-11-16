---
title: 'Write probes, part 3: exports, move, merge and recycle-bin restore'
type: 'chore'
ticket: '1'
created: '2026-11-15'
status: 'in-progress'
baseline_revision: '2843d5df33dc1bd9f90ce45abf5286ca2b4de2ae'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-plan.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Entry 2 (spine amendments for round 2) can't word AD-7's `ExternalWrite` and the move, merge and restore paths of `HierarchyWrite` until the epic Notes' "Unknown, settled by entry 1" questions have a measured answer. The open ones are: the other `Publish` formats per scope, export edge cases, `OpenPackage` targets, `MergeFiles`, `MergeSections`, page, section and group moves, protected moves, recycle-bin restore, and the modal-dialog results and HRESULTs of each method.

**Approach:** Extend 5.1's probe code in `tests/OneNoteMcp.Integration/WriteProbes/` (no fork) with five opt-in scenarios that the main session runs live with Phill. Every write goes through `TestNotebook`'s guard, which this spike widens only for merges, `MergeFiles`, a reused throwaway notebook and the run notebooks' recycle bins. Answers go into a findings doc, one row per question, and into the spine memlog as constraints. No `src/` change.

## Boundaries & Constraints

**Always:**
- Every COM write goes through `TestNotebook`'s guard (AD-16). That means a fresh hierarchy read, with every target inside MCP Test, MCP Test Cloud, or a throwaway notebook that this run made or adopted by exact name. Every ID named in `UpdateHierarchy` XML must be in the run's `Created` set. All dates are UTC.
- `Publish`, `OpenPackage` and `MergeFiles` read and write only under `%LOCALAPPDATA%\OneNoteMcp-Test\probes\publish\` (`ProbeSession.PublishDir`), and each `Publish` goes into a fresh, empty subfolder, so that extra files show.
- Each probe writes one JSONL row per question. The row holds the method, the shape of its arguments, the HRESULT, the time taken, and a read-back from the hierarchy (and from the file system for exports). The OneDrive account ID is written `<cid>`.
- A Sync Now check after every cloud step (`HierarchyProbes.SyncChecked`). Report `0xE0000644` at once.
- A shown check before every hierarchy write and every `Publish` (`EnsureNothingShownAsync`). Each call has a 60 s limit (180 s on cloud). The crash breaker (`PinnedOneNoteProbe`) stays on.
- Every step that needs Phill pauses with a standalone instruction, which the main session relays.
- The probes rely on earlier findings and don't re-probe them. Each such finding is named in Design Notes and cited in the findings doc.

**Never:**
- Touch anything outside the two test notebooks, the throwaways and the run's own objects, or touch Ballast Vault. On the Office 365 tenant, never touch any notebook except `MCP Test Tenant`. Never use NavigateTo or `CloseNotebook`, and never start, close or kill OneNote.
- Open a protected section's `.one` backup with `OpenHierarchy` (doing so writes plaintext; 5.1 constraint 17). Packages are byte-scanned only after `expand.exe` extracts them, never by opening them in OneNote, except row O5.
- Send `DateTime.MinValue`, or retry a write automatically.
- Change `src/` or the spine text. Only the memlog gets lines, and entry 2 amends the ADs.
- Commit real notebook names, the publish folder's files, or any content they hold.

## I/O & Edge-Case Matrix

Each row is one findings-doc question. L = MCP Test, C = MCP Test Cloud, P = the protected scenario.

| Id | Area | Question |
|---|---|---|
| X1 | Export | `Publish` of a page, a 3-page section (one subpage) and a notebook in pfPDF, XPS, Word (`.docx`), MHTML, HTML, EMF, OneNote2007 and OneNotePackage, L and C. For each: HRESULT, time, when the file first appears and when it is stable (polled up to 120 s), and the files and folders it wrote |
| X2 | Export | Across every `.onepkg` from X1 and XP1–2: does any never land within 120 s? |
| X3 | Export | A partial file from a `Publish` that timed out: does it appear and grow after the timeout, and does a retry to the same path then get `FileExists`? (`write-probes-r2-timeout`, L) |
| XP1–2 | Export, P | A notebook package holding an unlocked (XP1), then a locked (XP2), protected section, L and C: the HRESULT, and whether that section is encrypted inside the package (extracted with `expand.exe`, then byte-scanned as in run 5) |
| O1–4 | OpenPackage | Into an existing empty folder (O1), a folder holding `probe-marker.txt` (O2), and a folder this package already filled (O3). O4 records, for every row: whether the foreground window kept focus, the path out, the notebook opened, and whether its pages are listed at 0 s and at 5 s |
| O5 | OpenPackage, P | XP1's and XP2's packages opened: is the section `encrypted` and `locked`, and does OneNote ask for the password (Phill observes)? |
| F1–3 | MergeFiles | Three versions of one section (base; client with edit A; server with edit B), L and C: the HRESULT and the target file. Locally the target is then opened and merged into an empty run section (S3's route) to see whether both edits survive (F1). Three unrelated `.one` files (F2). A target that already exists (F3) |
| S1–2 | MergeSections | A source (2 pages and a subpage) merged into a destination with 1 page. S1: within a notebook. S2: across notebooks (L to throwaway a, C to MCP Test Cloud Throwaway, and L↔C per Q1). For each: does the source survive, empty or in the recycle bin? Do the pages get new IDs? What happens to order, dates and the subpage level? |
| S3 | MergeSections | An unprotected section's `.one` backup is opened (its pages are listed at 0 s and 5 s), then merged into an empty run section, L and C. Do the pages show? What happens to the opened section? |
| SP | MergeSections, P | Unlocked protected into plain; plain into unlocked protected; locked source; locked destination. Where the pages land, `encrypted`, and the HRESULT, L and C |
| M1 | Move | A page moved to another section of the same notebook, L and C, using three XML shapes: (a) the target's XML with the page added at position 1; (b) the source's XML without the page; (c) OneMore's two calls, (a) then (b), only if (a) left the page where it was. Record the ID (OneMore expects a new one), name, dates, `pageLevel`, position, both sections' dates, and whether the page appears in Deleted Pages |
| M1d | Move | A parent page with a subpage, moved by M1's working shape: does the subpage follow? |
| M2 | Move | A page moved to a section of another notebook: L to throwaway a, C to MCP Test Cloud Throwaway, and L↔C per Q1 |
| M3 | Move | A section moved to another notebook (into a run group, and to the throwaway's root): the same targets as M2, recording its ID, path, dates and both parents' dates |
| M4 | Move | A section group (one section inside) moved to another group in the same notebook, and to another notebook: the IDs and paths of the group and its section. On C, Phill checks OneNote for the web |
| MP | Move, P | A page moved out of, and into, an unlocked protected section, then a locked one. A protected section moved, unlocked and locked, to another group and to another notebook. Record whether `encrypted` survives, and its `locked` state, ID and path (that is, its `SectionKey`), L and C |
| R1 | Restore | A recycled page: send the original section's XML with the recycled ID listed. Is it restored? What ID does it end with, and has it left Deleted Pages? If that doesn't restore it, use OneMore's second call (Deleted Pages without the page), but only if M1(b) showed omitted pages are kept |
| R2 | Restore | A recycled page sent to a section the caller names, in the same notebook and in another notebook |
| R3 | Restore | A recycled section, back into its run group by the group's XML: is its ID kept? |
| RP | Restore, P | A protected section soft-deleted whole, which is then locked in the recycle bin: restore it while locked; Phill then unlocks the recycled copy and the restore is tried again. Does `encrypted` survive, and do the ID and path change? L and C |
| R5 | Restore | What triggers `0x8004202E` hrRecycleBinEditFailed: renaming a recycled section; moving a live run section into `OneNote_RecycleBin`; `UpdatePageContent` on a recycled page; reordering run pages in Deleted Pages (only if M1(b) showed omitted pages are kept) |
| D | Modal | With File > Options open (L): a plain `GetHierarchy`, `Publish` pfPDF, `OpenPackage`, `MergeSections`, `MergeFiles`, and an M1-shape move |
| LB | Labels | On `MCP Test Tenant`: first a baseline `Publish` of a page (pfPDF and pfOneNote) with no label. Then, once Phill has applied a sensitivity label, `Publish` of a page and of the section in all 9 formats. Which formats return `0x80042031` hrPublishFormatUnsupportedForLabels, and which succeed? (`write-probes-r2-label`) |
| H | HRESULTs | Every code seen, per method, with its trigger, mapped to AD-9 |

## Decisions (Phill, 2026-11-15, plan review, relayed by the coordinator)

- Q1 (a): cross-storage moves and merges between MCP Test and MCP Test Cloud: a page move, a section move and a merge in each direction, as the last steps of the cloud run.
- Q2 (a): reuse `MCP Test Throwaway Cloud` if it is still open.
- Q3 (a): adopt `MCP Test Throwaway 1112-034716 a` by its exact name.
- Q4 (a): the timed-out export (X3) runs as its own scenario, last in sitting A.
- Q5 (changed from the recommendation): probe `0x80042031`. Phill has an Office 365 tenant. These rules come from the coordinator, for safety:
  - The probes never touch Phill's existing tenant notebooks.
  - At a pause, Phill creates a new notebook on the tenant, named exactly `MCP Test Tenant`, with one section. The guard adopts it by that exact name and an `https` path that isn't `d.docs.live.net`. Its own small scenario, `write-probes-r2-label`, runs at the end of sitting B.
  - Phill applies a sensitivity label only if his tenant offers one. If it doesn't, the row is recorded as not probed, with the reason.
  - Evidence: the paths and URLs of tenant notebooks carry the tenant's SharePoint address and Phill's account. Every row, echo line and findings entry records the notebook only as "tenant notebook", and the privacy grep must pass.
- Q6 (a): two sittings of about 75 min, local then cloud.
- Q7 (a): keep the full plan.

</frozen-after-approval>

## Code Map

- `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs` -- reuse these as they are: `StepAsync`, `PauseAsync` (the pause file and `skip`), `RequireScenario`, `CreateChildAsync`, `MustCreateChildAsync`, `CreateProbePageAsync`, `UpdateHierarchyAsync`, `DeleteHierarchyAsync`, `PublishOneAsync`, `OpenBackupAsync`, `AdoptCloudThrowawayAsync`, `FindByNameAsync` (adds `probe `-named IDs to `Created`), `NodeAsync`, `TreeAsync`, `Disk`, `Foreground`, `EnsureNothingShownAsync`, `StopOnTimeoutAsync`, and `PublishDir` (:448). Add the new wrappers here.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `PublishAsync` (:231), `OpenPackageAsync` (:279, which already allows any destination under `PublishDir`), `OpenBackupAsync` (:259), `UpdateHierarchyAsync` (:181) through `CheckHierarchyXml` (:669), `EnsureTargetsAsync` (:399), `EnsureUnder` (:716), `IsLocalThrowaway` (:548), `IsCloudThrowaway` (:565). Add the guarded `MergeSectionsAsync` and `MergeFilesAsync` here, and the throwaway adoption by exact name.
- `WriteProbes/HierarchyProbes.cs` -- `ProbeTarget`, `Stamp`, `RunGroupAsync`, `SyncChecked` (:126), `ModalAsync` (close pause in a `finally`), `OpenAndCompareAsync`. `CloudJamRepro.CheckSyncAsync` (:148).
- `WriteProbes/ProtectedPublishProbes.cs` -- `WaitForStableFileAsync` (:141) and `Scan` (:168), the byte scan. `ProtectedDeleteProbes.cs` -- the protect, lock and unlock pause wording, finding the section again by name after its ID changes (5.1 constraint 15), and `RecycleViewAsync`.
- `WriteProbes/NotebookDeleteProbes.cs` -- making a throwaway notebook and `FillAsync`. `ProbeXml.cs` -- `Hierarchy`, `Node`, `Children`, `PageNames`, `ById`. `ProbeRecorder.cs` -- writing rows and the `<cid>` redaction.
- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- `Publish` (:99), `OpenPackage` (:106), `MergeFiles` (:140), `MergeSections` (:166). `Enums.cs` `PublishFormat` (:55, values 0–8). Call them through `ComGateway.InvokeAsync`, and don't edit them.
- `_bmad/scripts/memlog.py append --path <spine>/.memlog.md --type constraint --text "..."` -- adds the memlog lines, appended at the end only.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Integration/TestNotebook.cs` + `TestNotebookGuardTests.cs` -- add four guarded calls, with a test for each refusal and look-alike:
  - `MergeSectionsAsync`: both IDs must be in `Created` and pass `EnsureTargetsAsync`. Alternatively, the source may be an ID in a new `Opened` set, recorded only by `OpenBackupAsync` for a path under `PublishDir`.
  - `MergeFilesAsync`: all four paths must be under `PublishDir`.
  - `AdoptThrowawayNotebookAsync(exactName)`: refuses unless both the name and the folder under `ThrowawayRoot` match exactly.
  - An optional allowed set for `CheckHierarchyXml`, holding only MCP Test's and MCP Test Cloud's `isRecycleBin` group and `isDeletedPages` section IDs, read live. Only R1's second call and R5 pass it.
  - `TestNotebookGuard.IsTenantTest(name, path)`: true only for exactly `MCP Test Tenant` with an absolute `https` path whose host isn't `d.docs.live.net`. Adopting it through the throwaway mechanism lets `EnsureTargetsAsync` accept it and nothing else on the tenant.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/ProbeSession.cs` -- add:
  - wrappers for `MergeSectionsAsync`, `MergeFilesAsync` and `OpenPackageIntoAsync`;
  - `PublishFreshAsync` (a fresh subfolder, then the landing poll: first seen, stable, the files written);
  - `ExtractPackageAsync` (`expand.exe -F:*` into `PublishDir`);
  - `RecycleBinIdsAsync`;
  - growing `Created` from the new page IDs in a merge's destination.
  - for the label scenario, a recorder redaction that replaces every `http(s)` URL, and the tenant notebook's path, with `tenant notebook`, in rows and in echo lines alike.
  Helper tests go in `ProbeHelpersTests.cs`, and the redaction gets its own tests.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/` -- new files `Round2ExportProbes.cs`, `Round2MergeProbes.cs`, `Round2MoveProbes.cs`, `Round2RestoreProbes.cs`, `Round2ProtectedProbes.cs`, `Round2TimeoutProbe.cs` and `Round2LabelProbe.cs`. Their `[Fact]`s are the scenarios `write-probes-r2-local`, `-cloud`, `-protected`, `-timeout` and `-label`, which together cover every Matrix row. Steps run in this order: export, open-package, merge-files, merge-sections, move, restore, then modal (L only). M1(b) runs before R1's second call and R5's reorder, and the cross-storage steps (Q1) come last.
- [ ] Live sittings (the main session's, with Phill present, after implementation and review) -- the Sitting script below. This isn't part of implementation.
- [ ] `epic-round-2-external-and-structural/spike-write-probes-part-3-exports-move-merge-and-recycle-bin-restore-findings.md` (after the sittings) -- follows 5.1's structure: one row per Matrix question, then HRESULTs, Not probed, Evidence gaps, "Relied on, not re-probed", Open before final, and Left behind.
- [ ] Spine `.memlog.md` (after the sittings) -- one `constraint` line per finding that changes an AD, naming the AD.

**Acceptance Criteria:**
- Given the guard, when a merge, a `MergeFiles` call, an adoption, or an `UpdateHierarchy` names an ID or path outside the run's objects, the allowed recycle bins and `PublishDir`, then it refuses before any COM call.
- Given the findings doc, when entry 2 reads it, then every Matrix row has a result or a stated reason it wasn't probed, and every earlier finding relied on is named.
- Given the staged diff, when it is grepped for the account ID and real notebook names, then nothing matches.

## Sitting script

Run from the repo root with OneNote already running, with `$env:ONENOTE_MCP_INTEGRATION='1'` and `$env:ONENOTE_MCP_SCENARIO='<scenario>'`, then `dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~Round2`. Rerun a single step with `ONENOTE_MCP_PROBE_STEPS`.

**Before either sitting,** Phill confirms that `MCP Test Throwaway 1112-034716 a` and `MCP Test Throwaway Cloud` are open (Q2, Q3), and leaves a page outside the probe area on screen. The protected scenario runs as two facts, `write-probes-r2-protected-local` and `write-probes-r2-protected-cloud`, so that each sitting runs its own pass.

**Sitting A, local (about 75 min):**

| Scenario | What it runs | Pauses for Phill | Estimate |
|---|---|---|---|
| `write-probes-r2-local` | X1, X2, O1–4, F1–3, S1–3, M1–4, R1–3, R5, then D | open File > Options, then close it (D); close the `-unpacked` notebooks and the opened backups | about 30 min |
| `write-probes-r2-protected-local` | XP1–2, O5, SP, MP, RP on `MCP Test Throwaway <stamp> p` and a run section in MCP Test | protect the `probe vault` sections; lock; say whether a password prompt appeared (O5); unlock the recycled copy; close the opened packages | about 30 min |
| `write-probes-r2-timeout` | X3 (a Word export of a 30-page section at a 1 s limit), then the `timeout-retry` step at 60 s | Cancel any dialog if the breaker trips | about 10 min |

**Sitting B, cloud (about 75 min plus the label scenario):**
- `write-probes-r2-cloud`: X1–2, F1, S1–3, M1–4, R1–3 and R5, then the L↔C steps last (Q1). A Sync Now check follows each step, and an OneNote-for-the-web check follows M4.
- `write-probes-r2-protected-cloud`: the protect, lock and unlock pauses again, with a Sync Now check after each step.
- `write-probes-r2-label` (Q5), last: Phill creates `MCP Test Tenant` on the tenant at its first pause, then applies a label, or answers `skip` if none is offered.

**In total,** about 2.5 h of Phill's time. Most of it is the 25 to 30 pauses, at 1 to 2 minutes each, and the guarded reads (5.1 found reads slow with MCP Test's Ballast pages).

**Sitting B, revised after sitting A (2026-11-16):**

1. `write-probes-r2-cloud` as planned; `MergeFiles` (F1) and `pfOneNote2007` (X1) are skipped and logged notProbed.
2. `write-probes-r2-protected-cloud` as planned; the locked moves are skipped (they crashed locally).
3. `write-probes-r2-protected-local` again, for sitting A's leftovers: O5 (open XP1's and XP2's packages) and RP (protected restore). It rebuilds its sections, so Phill protects eight sections and locks them once more.
4. `write-probes-r2-local` with `ONENOTE_MCP_PROBE_STEPS=modal-options,close-opened`, for D's move with File > Options open (lost to the crash). `MergeFiles` is skipped there too.
5. `write-probes-r2-label` last; `pfOneNote2007` is skipped on the tenant notebook.

## Implementation Notes

- Implementation writes code and unit tests only. Never set `ONENOTE_MCP_INTEGRATION` or `ONENOTE_MCP_SCENARIO`, never call OneNote, and never read or list `%APPDATA%\OneNoteMcp`, `%LOCALAPPDATA%\OneNoteMcp` or `%LOCALAPPDATA%\OneNoteMcp-Test` on this machine. The main session runs every live sitting later.
- Every `PauseAsync` instruction must stand on its own: what to do, in which notebook and section, and what to leave as it is. Keep step names stable, so that `ONENOTE_MCP_PROBE_STEPS` can rerun a single step.
- 2026-11-15, implemented (code and unit tests only; nothing ran against OneNote). Judgement calls the plan didn't settle:
  - **X3's 1 s limit.** A gateway's call limit is fixed when it activates, and a 1 s guard read of MCP Test (Ballast pages) would time out first. So the guard's reads run on the session's gateway at 60 s, and only the `Publish` call goes through a second gateway with its own 1 s limit and its own crash breaker (`ProbeSession.CreateSideGateway`, `TestNotebook.PublishAsync`'s `callGateway`). Its timeout poisons only that gateway; `timeout-retry` then runs on the session's gateway at 60 s.
  - **Notebook scope** (X1, O1–O4, XP, D) publishes a throwaway only, never MCP Test or MCP Test Cloud whole: those hold Ballast Vault.
  - **Landing poll.** Up to 120 s after an ok, 5 s after a failure (48 X1 publishes would otherwise wait 120 s per error). "Stable" is a non-empty folder with no empty file, unchanged for 1 s; times count from the call's return.
  - **Protected order.** The lock state drives it: protect eight sections at one pause; unlocked XP1, SP and MP cases; lock (Ctrl+Alt+L); locked XP2, SP and MP cases; O5 (local only, since the Matrix doesn't mark it L/C); RP. O5 opens a package with `OpenPackage` and, only if that opens no notebook, its unpacked folder with `OpenHierarchy`, as 5.1 did.
  - **Move shape.** M2, M1d, MP and the Q1 page moves use M1's working shape, or (c) when this run hasn't learnt it; D uses it, or (a). M1(b)'s answer (`Round2Context.OmittedPagesKept`) gates R1's second call and R5's reorder; a rerun that skips `move` logs them `notProbed`.
  - **F3** merges into a copy of F1's target, so F1's open-and-merge still opens what `MergeFiles` wrote. F1's target is also byte-counted for both edits before it is opened.
  - **Adopting** `MCP Test Throwaway 1112-034716 a` adds only the notebook's ID to `Created`, not what run 4 left in it. If it or `MCP Test Throwaway Cloud` isn't open, a pause asks Phill to open it, or `skip` (the cross-notebook rows then log `notProbed`).
  - **Subpages** are made by `UpdatePageContent` with `pageLevel="2"` on the page root (the page XML's date), falling back to `UpdateHierarchy` with the section's page list; a setup row says which worked.
  - **Setup soft deletes** send the hierarchy date (5.1 constraint 1) after a 2 s settle (constraint 2), and throw rather than send `MinValue`. `CreateProbePageAsync`'s setup `UpdatePageContent` now sends the new page's own page-XML date too (it sent `MinValue` in 5.1).
  - **Label privacy.** URL redaction is on before the first row, and also covers the pause file and console. The label row logs the section's attribute names, never values, and the pause asks Phill whether the label encrypts, not its name: either could identify the organisation.

## Sitting A results (2026-11-16, Phill; raw rows in `%LOCALAPPDATA%\OneNoteMcp-Test\probes\write-probes-r2-*-20261116-*.jsonl`, for the findings doc)

- **Three calls end ONENOTE.EXE (`0x800706BE` RPC_S_CALL_FAILED within 2-3 s), each reproduced:** `Publish(pfOneNote2007)` of a page (twice: first run and a replay Phill watched); `MergeFiles` on three published copies of one section (three times: F1, its replay, and in D with File > Options open); `UpdateHierarchy` moving a page out of a **locked** section (MP, shape c, once). OneNote relaunched itself after most crashes; after crash 2's replay it didn't, and on the next starts it showed its safe-start prompt ("We couldn't start OneNote the last time we tried", offering Delete Notebook Cache) twice. The probes now skip all three (`ONENOTE_MCP_PROBE_FORMATS`, `ONENOTE_MCP_PROBE_MERGEFILES=1` and `ONENOTE_MCP_PROBE_LOCKED_MOVES=1` re-enable them).
- **X1 (local):** page and section publish ok in PDF, XPS, Word, MHTML, HTML, EMF and OneNotePackage; a notebook only as OneNotePackage (others `0x80042006`/`0x80042014`).
- **O1-O3:** `OpenPackage` ok into an empty folder, a folder with a marker file, and a filled folder.
- **S1/S2:** `MergeSections` within MCP Test (after copying 2 of 3 pages, dropping the subpage) and to throwaway a (nothing copied) both `0x8004201F` hrMergeFailed; S3 (an opened `.one` backup into an empty run section) ok; in D (Options open) a run-section merge was ok.
- **M1-M4:** a page move with shape (a) alone moves it with a new ID, and M1(b) showed omitted pages are kept; a parent page moved without its subpage, which stayed behind promoted to level 1 (M1d; corrected from the rows by the findings draft); page to throwaway a ok (M2); section into a group ok; section to throwaway a's root raised OneNote's "hasn't finished downloading" dialog (Phill chose Continue) and the call returned `0x80042004` hrSectionDoesNotExist and left a copy of the section in the target; section groups moved within and across notebooks ok (M4).
- **R1-R3, R5:** a recycled page is restored by the section's XML (new ID, leaves Deleted Pages), also into a named section in the same and another notebook; a recycled section is restored into its group; renaming a recycled section, moving a live section into `OneNote_RecycleBin` and reordering Deleted Pages all succeeded (no `0x8004202E`); `UpdatePageContent` on a recycled page is `0x8004200C` hrPageReadOnly.
- **D (File > Options open):** `GetHierarchy`, `Publish(pfPDF)`, `OpenPackage` and `MergeSections` all succeeded; the M1-shape move didn't run (crash before it): an evidence gap.
- **Protected (local, partial):** unlocked: XP1 package, merges both ways, page moves out and in, section moves to a group and to another notebook all ok; locked: XP2 package ok, both merges `0x8004201B` hrSectionEncryptedAndLocked; locked moves crash (above). O5 (opening the packages) and RP (restore) didn't run: moved to sitting B.
- **X3 (timeout):** a 30-page Word export passed the 1 s limit with the file already complete (13,514 bytes, no growth); a retry to the same path is `0x8004201A` hrFileAlreadyExists and leaves it untouched.
- **Also at this sitting:** backlog 2's live check passed, and the "cleaning up" bug reproduced: a connected server that has made a call keeps ONENOTE.EXE alive after its window closes (it ended one second after the server closed).

## Plan Change Log

## Review Triage Log

**Quick lens, iteration 0 (2026-11-15).** Verdicts: high 0, medium 6, low 4, false 0, maybe-false 0. Every finding was checked against the code. All ten were routed to patch and sent back to the implementer (impl-3-1), who applied them the same day. Afterwards: the build was clean with `-warnaserror`; `OneNoteMcp.Tests` passed 2,886; `OneNoteMcp.Integration` passed 217 and skipped 92, with all six Round2 facts skipping offline. Status goes back to `in-progress` until the live sittings and the findings doc are done.

| # | Finding | Verdict | Evidence | Route |
|---|---|---|---|---|
| 1 | `CreateProbePageAsync` sends `DateTime.MinValue` on its setup `UpdatePageContent` | medium | `ProbeSession.cs` ~538. The plan's Never list has no setup exception, and every Round2 page, the tenant label page included, is made this way. This settles the implementer's judgement call: the plan's own rule wins | patch: send the page-XML root date |
| 2 | R1's second call can send an empty Deleted Pages list | medium | `Round2RestoreProbes.cs` 142-143: `ours` is usually empty at R1, and `Hierarchy` then renders a childless `Section` that the recycle-bin allowance passes. M1(b) tested a list naming some pages, not an empty list | patch: `notProbed` when empty |
| 3 | The M4 web check runs before the Sync Now check | medium | The web-check pause sits inside the `move` body. `SyncChecked` pauses after the body returns and never asks about `MCP Test Throwaway Cloud` | patch: the web-check pause asks for Sync Now on both notebooks first |
| 4 | The source shown check is discarded in `MovePageAsync` and `MoveNodeAsync` | medium | Lines 126 and 195 ignore the bool. `UpdateHierarchyAsync` checks only the XML root (the destination), so a cross-notebook move can go ahead with the source on screen | patch: refuse with `NotShownRefusal` |
| 5 | M1(c) re-sends shape (a) | medium | Line 40 calls `MovePageAsync(..., "c")`, which sends (a) again before (b). That is an automatic resend the plan forbids, and the result is mislabelled | patch: skip (a) for m1c |
| 6 | `AdoptTenantAsync` adds the tenant notebook's ID to `Created` | low | `CheckDelete` and `CheckHierarchyXml` would then accept the tenant notebook itself. No probe sends such a call, but the guard is wider than "accept it and nothing else", and the fix is one deleted line | patch: drop `Created.Add` |
| 7 | The label scenario has no Sync Now check | low | `label-baseline` and `label-publish` write to a cloud notebook. The Always rule asks for a Sync Now check after every cloud step | patch: a check-sync pause after each |
| 8 | X3's summary fields mislead | low | `FirstSeenMs == 0` counts as "appeared after", and changes to other files count as growth. The raw data is logged, and the fix is a direct correction | patch |
| 9 | `Finish`'s assert message is not redacted | low | It joins raw `ex.Message` texts, which in the label scenario could carry the tenant URL into test output. Direct correction | patch |
| 10 | The close-opened pause names copies by live section names | low | The opened `.one` copies share their names with run sections, so the pause doesn't stand on its own (Implementation Notes). The fix is a wording change | patch |

**Matrix test audit.** Every Matrix row is a live-probe question, answered by an opt-in scenario that skips without `ONENOTE_MCP_INTEGRATION`. No row can run offline, which is the same as 5.1. Each row maps to a `Round2*` step:
- X1–X2: `export`.
- X3: `timeout-*`.
- XP, O5, SP, MP, RP: `<l>-unlocked`, `<l>-locked`, `<l>-open-packages`, `<l>-restore`.
- O1–4: `open-package`.
- F: `merge-files`.
- S1–3: `merge-sections`.
- M1–4: `move`, and `cross-storage` for Q1.
- R1–3 and R5: `restore`.
- D: `modal-options`.
- LB: `label-*`.
- H: `hresult-summary`.

The offline parts all have unit tests that ran and passed: the guard refusals, redaction, the landing poll, `expand.exe` refusals, the recycle-bin reader and the XML builders. The Matrix answers themselves come from the sittings.

## Design Notes

**Relied on, not re-probed** (the findings doc cites each):
- `Publish`:
  - a page to PDF, `.docx` and MHTML, and over an existing file (spike 1 rows 23–24);
  - pfOneNote for a page and a section, a section group failing in every form, a notebook only as `.onepkg`, a package landing late, and a missing folder raising a modal (5.1 constraints 6, 18);
  - an unlocked protected section published in the clear (5.1 constraint 17);
  - `0x80042006` comes from an empty section, not from a dialog, and `Publish` succeeded under File > Options (5.6 sitting P5, P9). That settles the epic's question about `0x80042006` under a modal.
- `UpdateHierarchy`:
  - a section moves to another group of the same notebook, keeping its ID and date, and the move syncs; partial section lists keep their siblings; a modal returns `0x80042030` (5.1 constraints 4, 11);
  - a group rename on cloud didn't sync (5.1 constraint 5).
- Soft delete:
  - where soft-deleted items go, a group dissolving, and a protected section locked in the recycle bin (5.1 constraints 8, 16);
  - a recycled page keeps its name, dates and `pageLevel`; a protected page's raw soft delete deletes it outright, so no recycled protected page can exist to restore; plain and protected sections keep their IDs in the recycle bin (5.5 sitting L1, L4, L10).
- `OpenPackage` unpacks into a new folder and opens `<package>-unpacked` without `encrypted` (5.1 constraints 7, 17, 18).

**Prior art (OneMore, checked 2026-11-15; research-note rows 17–20).**
- OneMore publishes pages only. It deletes the target and creates the folder first, never waits for the file, and never uses section or notebook scope, MHTML, XPS, EMF or OneNote2007.
- It never calls `OpenPackage` or `MergeFiles`.
- It imports a `.one` with `OpenHierarchy` then `MergeSections`, because the opened section has `areAllPagesAvailable=false`. It never closes the source.
- Its "ID Stability" TechNote measured a page moved by `UpdateHierarchy`, to another section or another notebook: only that page's ID changed, and dates, title and content were kept. M1 and M2 check that on our machine.
- It has no recycle-bin restore.
- Microsoft names `0x8004202E` hrRecycleBinEditFailed ("Edit failed in the Recycle Bin") and `0x80042031` hrPublishFormatUnsupportedForLabels.

**Why `expand.exe` for packages.** A `.onepkg` is a CAB file, so scanning its raw bytes for text fails, and opening it in OneNote counts as a write. The built-in `expand.exe` extracts the package into `PublishDir` without OneNote, and then run 5's `Scan` reads the bytes. The package and the extracted files stay in `PublishDir` and are listed under Left behind, as in 5.1.

**Order hazards.** If M1(b) shows that a partial page list deletes the pages it leaves out, then R1's second call and R5's reorder log `notProbed`. Otherwise they could remove other recycled pages from MCP Test's Deleted Pages.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: no warnings.
- `dotnet test tests/OneNoteMcp.Tests` and `dotnet test tests/OneNoteMcp.Integration` (without `ONENOTE_MCP_INTEGRATION`) -- expected: the guard and helper tests pass, and every scenario skips.
- `git diff --cached | grep -iE '<private-name list>'` -- expected: no hits.
