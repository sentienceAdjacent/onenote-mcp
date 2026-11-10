---
title: 'Write probes, part 1: pages, paths and search'
type: 'chore'
ticket: '2'
created: '2026-11-09'
status: 'built'
baseline_revision: 'cd131117ba6a6e39ab9d6131c1ff33c00dbc3dc7'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Entry 3 has to amend AD-4, AD-6/21, AD-7, AD-9 and AD-17 before any write story, but the behaviour those rules rest on was never measured: page-write merge semantics, which date the write methods accept, two hash quirks, merged cells, cloud paths, and four search questions (Q5).

**Approach:** Opt-in probe scenarios in the integration project, run live on MCP Test and MCP Test Cloud with Phill present, every write through `TestNotebook`'s guard, widened to find (never create) MCP Test Cloud. The answers go into a findings doc, one row per question, and into the spine memlog as constraints. No `src/` change.

## Boundaries & Constraints

**Always:**
- Every write goes through `TestNotebook.WriteAsync` and its guard (AD-16): a fresh hierarchy read, the target inside MCP Test or MCP Test Cloud. All dates UTC.
- The guard finds MCP Test Cloud by exact name plus a `d.docs.live.net` path ending in `/MCP Test Cloud/`, case-insensitive (the notebook and its sections differ in case). Nothing creates it.
- The OneDrive account ID never lands in the repo: logs and the findings doc write `<cid>`. Raw JSONL logs go outside the repo; the findings doc cites probe names.
- Each probe records method, args shape, HRESULT, outcome and a read-back. One question, one row.
- Probes needing Phill (modal dialog, close/reopen, rename) pause with a printed instruction; I relay them in this session.
- The cloud image probe runs first in the cloud session, after a read confirms `binaryUnavailable`. Phill doesn't open Image Page beforehand.

**Never:**
- Write outside the two test notebooks; NavigateTo; start, close or kill OneNote.
- `UpdateHierarchy`, `OpenHierarchy`, `DeleteHierarchy`, `Publish` (epic 5 entry 1 owns them), except reads.
- Change `src/` or the spine itself (entry 3 amends it); only the memlog gets lines.

## I/O & Edge-Case Matrix

| Area | Question (one findings row each) |
|---|---|
| Merge | A partial `UpdatePageContent` omitting an OE: kept? An OE without objectID: added? Does a replace need `DeletePageContent`? |
| Ignored edits | Which edits to existing objects are silently ignored (OE moved between Outlines, table column count, Position/Size, quickStyleIndex)? Does a read-back catch each? |
| Cloud image | Replacing the Outline holding Image Page's undownloaded image: kept, dropped, or error? |
| Side effects | Untouched children's `lastModifiedTime`; a local style duplicating its QuickStyleDef; `&nbsp;` in CDATA |
| Modal dialog | HRESULT per method (`UpdatePageContent`, `DeletePageContent`, `CreateNewPage`, `GetPageContent`, `FindPages`) with a dialog open |
| HRESULTs | Each error seen, with its trigger; try for `0x80042022` |
| Dates | Which `dateExpectedLastModified` each of `UpdatePageContent` and `DeletePageContent` accepts (page-XML value, hierarchy value, stale) when they differ, displayed and not displayed |
| Width | 20 reads of Table Page and a fresh MCP Test table: do unlocked `Column/@width` values alternate? Locked ones? |
| pathCache | 20 reads of Ballast Files: does `InsertedFile/@pathCache` change every read? |
| Merged cells | 2013 round trip of Table Page: merge survives? Does `UpdatePageContent` accept `xs2026` with `colSpan`? |
| Paths | Notebook and section `path` (local, cloud) after close/reopen, a rename in OneNote, and a rename of the local folder and the OneDrive item (run last) |
| Search (Q5) | `FindPages` with symbols; `FindMeta` reliability; `encrypted` on Ballast Vault in both XMLs; the skill's list and image `alt` claims |

## Decisions (Phill, 2026-11-09, plan review)

- Q1 (a): probe every path change: close and reopen, rename in OneNote, rename the local folder and the OneDrive item. The OneDrive rename runs last; MCP Test Cloud may need reopening after it.
- Q2 (a): the equation-selection side effect is not probed. The findings doc lists it under Not probed, and the skill gets a caveat later.
- Q3 (a): keep the full plan (about 2,000 tokens).

</frozen-after-approval>

## Code Map

- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- `TestNotebookGuard` (:133): `FindNotebookId` (:144-162) matches a local folder via `Path.GetFullPath`; `Contains` (:182-192); `WriteAsync` (:117-129) re-reads live then calls `gateway.InvokeAsync(...)`. Add a cloud lookup beside, not inside, the local path code; keep `EnsureFolderIsOurs` local-only.
- `tests/OneNoteMcp.Integration/TestNotebookGuardTests.cs` -- 13 pure guard tests; pattern for the cloud ones.
- `tests/OneNoteMcp.Integration/LifecycleScenarioTests.cs:113-200` -- `RequireScenario("...")` gating with `ONENOTE_MCP_SCENARIO`; copy for probe scenarios.
- `tests/OneNoteMcp.Integration/BallastFixture.cs` -- Ballast Files (attachments) and Ballast Vault (protected) in MCP Test.
- `src/OneNoteMcp.Com/Interop/IApplication.cs` -- `UpdatePageContent` (:68), `DeletePageContent` (:81), `CreateNewPage` (:45), `FindPages`/`FindMeta`; reach them via `ComGateway.InvokeAsync` (:237).
- `src/OneNoteMcp.Xml/ElementHash.cs:20-40`, `PageBasis.cs:36-40` -- hash exclusions; the width and pathCache probes report whether each read flips `pageHash`.
- `spikes/com-capabilities/OneNoteProbe.psm1` -- spike 1's JSONL row shape (ts, probe, method, args, outcome, verdict, hresult, ms, detail); reuse it.
- `_bmad/scripts/memlog.py append --path <spine>/.memlog.md --type constraint --text "..."` -- memlog lines; see `.memlog.md:99`.
- MCP Test Cloud (seen read-only 2026-11-09): Probes › Text Page, Table Page (column 0 locked at 105.51, columns 1-2 unlocked; a merged cell shows in 2013 XML as both texts in one cell and an emptied neighbour), Image Page (`binaryUnavailable`); Group A › Nested. Table Page's page-XML and hierarchy `lastModifiedTime` already differed.

## Tasks & Acceptance

**Execution:**
- [x] `tests/OneNoteMcp.Integration/TestNotebook.cs` + `TestNotebookGuardTests.cs` -- cloud lookup and tests: exact name, host and folder ending, case-insensitive, refuses a look-alike name or host, never creates -- the probes' only write path.
- [x] `tests/OneNoteMcp.Integration/WriteProbes/` -- scenario classes per Matrix group (`write-probes-local`, `write-probes-cloud`, `write-probes-paths`, `write-probes-search`), a JSONL recorder writing to `%LOCALAPPDATA%\OneNoteMcp-Test\probes\` with `<cid>` redaction, and printed hitl pauses -- probes need no `src/` change.
- [x] Live runs (main session, Phill present) -- run each scenario; relay pauses.
- [x] `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/spike-write-probes-part-1-pages-paths-and-search-findings.md` -- spike 1's structure; one row per Matrix question with probe name and result; "Not probed" lists anything skipped and why.
- [x] Spine `.memlog.md` -- one `constraint` line per finding that changes an AD, naming the AD.

**Acceptance Criteria:**
- Given the guard, when a target is outside both notebooks or in a look-alike, then it refuses before any COM write.
- Given the findings doc, when entry 3 reads it, then every Matrix question has a result or a stated reason it wasn't probed.
- Given the staged diff, when grepped for the account ID and the name list, then nothing matches.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` and the guard tests -- pass.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` -- still 87 passed, 7 skipped (probes skip without a scenario).

**Manual checks:**
- After the runs, MCP Test's existing integration suite still passes (probes left its seed pages intact).

## Review Triage Log

Quick lens, iteration 0 (2026-11-10).

| # | Finding | Verdict | Evidence | Route |
|---|---|---|---|---|
| 1 | Dates: nothing sets up a displayed page whose page-XML and hierarchy dates differ; the cloud date steps never ask Phill to show or hide their page | medium | `CloudWriteProbes.cs` runs `DatesProbe.RunAsync` on Text Page and Table Page with no pause; only the local probe sets display state, on fresh pages whose dates may match | patch |
| 2 | Cloud date markers carry no run stamp, so a re-run reads a leftover OE as "applied" | medium | `DatesProbe.cs` markers `date-update-{label}-{n}` and `date-target-{label}-{n}`, labels `cloud-text`/`cloud-table` fixed; `applied` uses `OeByText` | patch |
| 3 | `DeletableOe` can pick a list item on Text Page, which `skill-list-claim-ui-page` reads later | low | `DeletableOe` skips OEChildren, tables, images, files, ink, media, not `one:List` | patch |
| 4 | Modal update/delete and `hr-delete-unknown-objectid` record no read-back | medium | `detail: null` on those rows; the plan's rule is "records … a read-back" | patch |
| 5 | The HRESULT step can't trigger `0x80042022` hrFutureContentLoss | medium | its xs2010/xs2007 writes target a plain Outline and plain table; only the cloud merged-cell round trip could hit it | patch |
| 6 | Modal probe uses File > Options, which doesn't block CreateNewPage | low | `LifecycleScenarioTests.cs:145-146` records that; the probe still answers the row for that dialog. No code change: the findings doc names the dialog and cites the earlier observation | reject |
| 7 | Encrypted-Vault check sees one lock state | medium | `EncryptedVaultAsync` runs once with no pause; "reliably carries `encrypted`" (`deferred-work.md:121`) needs locked and unlocked | patch |
| 8 | Plan's expected integration counts (87/7) are stale | low | 32 new no-OneNote tests and 4 skipping scenarios: 119 passed, 11 skipped measured. Fix would edit this plan | reject |
| 9 | `TestNotebookGuard.EnsureInside` is dead code with a stale message | low | only `TestNotebookGuardTests.cs:64-65` calls it; `WriteAsync` uses `Contains` | patch |
| 10 | `ProbeXml.cs` doc comment names `ProbeXmlTests`; the class is `ProbeHelpersTests` | low | direct correction | patch |

Live run, 2026-11-10 (probe fixes, all in `tests/`, each re-run with Phill):

| # | Finding | Verdict | Evidence | Route |
|---|---|---|---|---|
| 11 | A probe crash let the gateway reconnect start a hidden `ONENOTE.EXE -Embedding` | high | `hr-read-xs2007` crashed OneNote (`0x800706BE`); the reconnect passed the process check while the crashed process was alive | patch: `PinnedOneNoteProbe` breaker, `ProbeSession` stops on trip; gateway bug to `deferred-work.md` |
| 12 | The `xs2007` read crashes OneNote | high | same run | patch: dropped from `hresults-local` |
| 13 | Modal delete targeted an OE, so `0x8004200e` hid the dialog's effect | medium | `modal-delete-page-content`, first run | patch: target an Outline |
| 14 | Untouched-children probe ran the partial update first, deleting the siblings the whole-Outline check needed | medium | `side-untouched-children-lmt`, first run | patch: order swapped |
