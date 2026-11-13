---
title: 'Rename and reorder: update_hierarchy'
type: 'feature'
ticket: '4'
created: '2026-11-13'
status: 'built'
baseline_revision: '7f762657fab50c8c8b80f6ec1a27629aad0e5de6'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Claude can create structure (5.3) but can't rename it or change its order. The `HierarchyWrite` class exists only as an enum value: there is no gateway `UpdateHierarchy`, no hierarchy journal snapshot, and three HRESULT rows still map to `Internal` though AD-9 remaps them (2.7 plan Q2).

**Approach:** `UpdateHierarchy` rename and reorder through the existing pipeline as `HierarchyWrite` (AD-7 table and its "`HierarchyWrite` validation" rules), with the tool shape Q1 picks. Core builds the `UpdateHierarchy` XML from IDs and names (as epic 3's move tool will; its Q2), validates it against the vendored 2013 XSD as a hard gate (settling AD-16 for `UpdateHierarchy`), compares the caller's `lastModifiedTime` basis, writes a `hierarchy` journal entry, calls, and reads back. Add is left to 5.3's create tools (Q2).

## Boundaries & Constraints

**Always:**
- AD-7 order. Step 1 (before the lock): argument checks (empty or duplicate IDs, the name rule, a basis carrying anything but `lastModifiedTime` is `InvalidArgument`), then the write grant on the target's notebook, the target itself when it is a notebook (`WriteAccess.CheckContainerAsync`; grant dialog as for `create_page`). No read grant is ever asked for (AD-21 Writes).
- Rename (Q3, Q4): a section's, section group's or notebook's displayed name. A notebook sets `nickname`, never `name` (spike 5.1: `name` is silently ignored). Names follow 5.3's rule (`SectionFileNames.NameProblem`), checked before COM with `InvalidArgument` and a message that never quotes the name; a section name ending `.one` (ignoring case) loses it, mirroring 5.3 Q2. A live sibling of either kind with the same name ignoring case is `InvalidArgument` (5.3 Q4; OneNote's own answer is `0x80042017`). The node's own current name doesn't count as a sibling, so a case-only rename is allowed (probe L4).
- Reorder (Q5, Q6): the caller lists every live child of the parent in the new order: sections then section groups for a notebook or group, pages for a section. The recycle bin is never listed and never sent. A missing, extra, duplicated or foreign ID is `InvalidArgument` (the "no moves" rule, AD-7), and so is a section group listed before a section. Pages: each page keeps its `pageLevel`, and a subpage must still follow its own parent's block (Q6).
- Under the lock, step 3 reads the target at `self` (rename) or the parent at `children` or `pages` (reorder), plus the siblings a rename is checked against (the parent at `children`; for a notebook, the open notebooks). Step 4 refuses before COM: the recycle bin, or anything in it (`isRecycleBin`, `isInRecycleBin`, `isDeletedPages`) as `ObjectNotFound`; a page as a rename target, or a non-container as a reorder parent, as `InvalidArgument`; a locked section (rename, or reorder of its pages) as `SectionLocked`; a section-group rename in a notebook whose `path` is a URL as `NotSupported`, with a `userAction` to rename it in OneNote (AD-7, 5.2 plan Q5).
- Basis (AD-17): `basis: {lastModifiedTime}` of the node the write targets: the renamed node, or the parent being reordered, compared by the server with step 3's read (`UpdateHierarchy` takes no date). Mismatch is `StaleWrite`; none is `noBasis` with step 3's date. The tool description says the check is weak (a rename doesn't move the renamed node's date) and that a write straight after one of ours can get a spurious `StaleWrite` (5.1 constraint 2): re-read and retry.
- Snapshot (AD-8): one `hierarchy` entry, written in step 6: the target at `self` scope for a rename, the parent's step-3 XML for a reorder. A snapshot rooted at a password-protected section is a protected entry (`SectionKey` of its pre-write path, DPAPI), whatever the scope. `restore_from_journal` already returns `NotSupported` for it.
- The XML sent is built in `OneNoteMcp.Xml`, minimal (rename: the node's element with `ID` and `name`/`nickname`; reorder: the parent element with ID-only children), and must pass `SchemaValidation` before COM; a failure there is `Internal` (our bug), never sent.
- Read-back, a step between 7 and 8: re-read the node or parent and compare the name or order with what was sent; differences are `readBackDiffers` (IDs only), a failed re-read `readBackFailed`; neither fails the write.
- No change (same exact name, same order): `ok: true`, no COM call, no journal entry, the new warning `unchanged`.
- In a protected section with no read grant held at the call's start, refusals and warnings carry counts only, never page IDs (AD-7, AD-21 Writes).
- Results: `meta.objectId` (the renamed node, or the reordered parent). No `meta.basis` (hierarchy reads carry none, AD-17).
- `HResultMap.Triaged`: `0x80042017` → `InvalidArgument`, `0x80042024` → `ComBusy` (retryable, at once, no backoff: it isn't in `ComGateway.IsBusy`), `0x8004202E` → `ObjectNotFound`; each keeps its userAction.
- Tool contract snapshot updated; `<Version>` stays `0.9.0-rc.1`. Tool rows only in the guide skill and README (recipes are entry 7's). Spine sync goes to `deferred-work.md`.

**Never:** moves (an ID under a parent it isn't already under; epic 3); add through `UpdateHierarchy` (Q2); changing any attribute but `name`/`nickname` and child order (no `color`, `pageLevel`, `isCollapsed`, `path`); page renames (a page's name is its title, `update_page_content`); reordering notebooks (Q7); `NavigateTo`; automatic retries; any grant write; editing `tickets.toml`; spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

Tool names are Q1 (b)'s; under (a) or (c) the rows hold, with the arguments carried in the XML or the single tool.

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Rename section | section ID, `Plans` | `UpdateHierarchy(<one:Section ID name="Plans"/>)`; read back | No error expected |
| Rename notebook | notebook ID, `Trips` | sends `nickname`; folder and grant unchanged | No error expected |
| Reorder sections | group ID, all children, sections first | parent sent with ID-only children in order | No error expected |
| Reorder pages | section ID, every page, subpage blocks whole | applied, `pageLevel` kept | No error expected |
| Ungranted notebook | any | grant dialog, then as above or `WriteNotGranted` | no lock, no COM |
| Stale basis | basis date ≠ step 3's | `StaleWrite` | no snapshot, no COM |
| Same name / same order | | `ok: true`, `unchanged` | no COM, no entry |
| Sibling's name, either kind | `plans` beside `Plans` | `InvalidArgument`, name not quoted | before COM |
| Bad name | `a/b`, `..`, `x?`, `CON`, ` x`, `OneNote_RecycleBin` | `InvalidArgument` | before the lock |
| Reorder list wrong | missing, extra, duplicate, foreign ID; group before section; subpage split from parent | `InvalidArgument` (counts only when withheld) | before COM |
| Recycle bin or in it | target or parent | `ObjectNotFound` | before COM |
| Locked section | rename, or reorder its pages | `SectionLocked` | before COM |
| Group rename on OneDrive | URL notebook | `NotSupported` + `userAction` | before COM |
| Modal dialog open | | `ModalDialogOpen` (`0x80042030`) | retryable |
| COM timeout | | `outcomeUnknown: true` | no retry |

</intent-contract>

## Answers (Phill, 2026-11-13, Inbox thread "5.4 rename and reorder")

- Q1: two tools, `rename_hierarchy_object(objectId, name)` and `reorder_hierarchy_children(parentId, childIds[])`, each with the optional `basis`; Core builds the XML.
- Q2: no creating through `UpdateHierarchy`. 5.3's create tools stay the only way to create.
- Q3: sections, section groups and notebooks (a notebook's display name, `nickname`) can be renamed. A page is renamed by editing its title with `update_page_content`.
- Q4: 5.3's file-name rule for every kind, notebook display names included, through 5.3's shared name-rule entry point. A name a sibling already uses is refused, and so is a notebook display name another open notebook already uses.
- Q5: a reorder takes every child of the parent in the new order; anything else is refused.
- Q6: subpages keep their level and must stay right after their parent page: a block moves as a whole.
- Q7: no reordering of the notebook list in 5.4.
- Q8: approved with these answers.

## Build Directions (unattended build, 2026-11-13)

These settle what the answers leave to the build. They are binding for this build and sit beside the Answers; where the intent contract above predates an answer, the answer and these directions win. Items marked **Assumption** are conservative choices made without Phill; each is listed in the Auto Run Result for him to confirm.

- **Where to work.** `{project-root}` for this build is the git worktree that holds this plan: `D:\Development\OneNote COM MCP\.claude\worktrees\agent-adaa7d2a980117146` (branch `story/5.4-update-hierarchy`). Read, edit, build and test only there, with absolute paths; never touch `D:\Development\OneNote COM MCP` itself or any other worktree.

- **One name-rule entry point.** Every rename name check goes through 5.3's shared entry point, `SectionFileNames.NameProblem` (and `SectionFileNames.SectionName` to drop a trailing `.one` from a section name), the same call `CreateHierarchyUseCase` makes. Write no parallel validator and no extra character rules in 5.4: 5.3 is being extended on its own branch with name hygiene (control, bidi, zero-width and line-separator characters, names over 128 characters) inside that entry point, and 5.4 inherits it by rebase. Share the sibling-name and recycle-bin checks with `CreateHierarchyUseCase` by pulling them into a helper both use, not by copying.
- **Notebook display-name clash. Assumption.** "Another open notebook already uses" the name means another open notebook's `nickname` or `name`, ignoring case (refuses more; cheap to relax). The renamed notebook's own names don't count.
- **Section `.one`.** A section rename strips one trailing `.one` (ignoring case) with `SectionFileNames.SectionName`, then checks the stem with `NameProblem`, and sends the stem as `name`. Read-back compares the read `name` with the sent stem.
- **Tool names and arguments (Q1).** `rename_hierarchy_object(objectId, name, basis?)` and `reorder_hierarchy_children(parentId, childIds, basis?)`, `basis` taking the same shape as the existing write tools' basis argument restricted to `lastModifiedTime` (anything else is `InvalidArgument`).
- **Subpage blocks (Q6).** A page block is a page plus the contiguous run of pages after it with a higher `pageLevel`, as read in step 3. The submitted order must be a permutation of whole blocks at every level: each page's level is unchanged, and every page with level > 1 must come after its block's parent with only that parent's block between them. Anything else is `InvalidArgument`. **Assumption:** a page whose `pageLevel` already jumps by more than one (an irregular level read from OneNote) is treated as belonging to the nearest preceding page with a lower level.
- **Notebook as reorder parent.** A notebook reorders its sections and section groups (sections first). The `Notebooks` root (Q7) is never a parent: an ID that isn't a notebook, section group or section is `InvalidArgument`.
- **This build never touches OneNote.** No integration tests run, no COM against a real OneNote, no starting, closing or killing OneNote, no `ONENOTE_MCP_INTEGRATION`, no server exe, no `NavigateTo`. Don't read or touch `%APPDATA%\OneNoteMcp` or `%LOCALAPPDATA%\OneNoteMcp`; don't edit settings, `CLAUDE.md` or config files. Only `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`. The L1-L9 probes are written in `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs` behind the opt-in skip and `TestNotebookGuard`, on throwaway nodes in MCP Test (MCP Test Cloud for L7) only, and are not run.
- **Known flaky test.** `ComGatewayLifecycleTests.AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked` can fail on timing; rerun once and note it.
- **Privacy.** No real notebook, employer or person names in code, tests, fixtures or docs; use "notebook A/B/C", "MCP Test" and "MCP Test Cloud".
- **No commits by the implementer.** Leave changes in the working tree; the build session commits.

## Open Questions

All closed by the answers above (2026-11-13). Kept for the record.

1. **Tool shape.** AD-5 says about one tool per COM method, and the spine and epic 3 name the tool `update_hierarchy`; but 5.3 split `OpenHierarchy` into three tools (5.3 Q1), and epic 3's move tool takes IDs and a position and builds the XML in Core (epic 3 Q2, rejecting model-written XML).
   - (a) `update_hierarchy(xml, basis?)`: the model writes 2013 hierarchy XML; the server checks every element and attribute against an allowlist (only `ID`, `name`, `nickname`, child order).
   - (b) Two tools, `rename_hierarchy_object(objectId, name, basis?)` and `reorder_hierarchy_children(parentId, childIds[], basis?)`; Core builds the XML.
   - (c) One structured `update_hierarchy(objectId, name?, childIds?, basis?)`, exactly one of `name` and `childIds`.
   Recommended: (b). Each tool states one rule set with no conditional arguments (your 5.3 Q1 reason), the model can't send an attribute OneNote would apply (`color`, `pageLevel`, `path`: OneMore changes `color` this way), and the XML builder is the one epic 3's move tool reuses. The spine's `update_hierarchy` mentions get renamed in the spine sync.
2. **Add through `UpdateHierarchy`.** Spike 5.1 added a section and a group by sending them without an ID; 5.3 now creates both with `OpenHierarchy`, with the existing-name and shared-name-space rules.
   - (a) Not offered: 5.3's create tools stay the only create path; Done-when 1 and AD-7's `HierarchyWrite` row drop "add" in the spine sync.
   - (b) Offered, with 5.3's name rules, as a new child at a position.
   Recommended: (a). Two create paths would need the same rules twice, and create-then-reorder gives the same result.
3. **What a rename covers.**
   - (a) Sections, section groups and notebooks (`nickname`). A page is renamed by editing its title with `update_page_content`.
   - (b) Pages too, by their hierarchy `name` (unprobed; a page's name is its title in the page content).
   Recommended: (a). A page rename through the hierarchy is unprobed and would bypass the page-write checks on the title.
4. **Name rules on rename.**
   - (a) 5.3's file-name rule for every kind, a notebook nickname included; a trailing `.one` dropped from a section name (as 5.3 Q2); a sibling of either kind with the same name ignoring case refused; a notebook nickname equal to another open notebook's refused.
   - (b) As (a), but a notebook nickname is free text (control characters refused only) and two notebooks may share one.
   Recommended: (a). It refuses more and is cheap to relax, one rule for every kind is simpler to describe, and two notebooks with one displayed name confuse lookups by name. Which characters OneNote accepts in a nickname is unprobed.
5. **What a reorder call takes.** OneNote reorders a partial list by putting the listed children first, in the order sent, and keeping the rest after (5.1 constraint 4).
   - (a) Every live child of the parent, in the new order (recycle bin excluded); anything else refused.
   - (b) One child and its new position among its siblings, like the move tool's position.
   - (c) Any subset, with OneNote's partial-list behaviour.
   Recommended: (a). The result is exactly what was sent, a child added since the caller's read is caught, and sorting doesn't take a chain of single moves, each liable to a spurious `StaleWrite` from the date lag after our own write (5.1 constraint 2).
6. **Pages with subpages.** Pages are a flat list with `pageLevel`; OneMore's sort moves each page with its subpages.
   - (a) `pageLevel` never changes, and every subpage must still follow its own parent's block (subtrees move whole); else `InvalidArgument`.
   - (b) Any order, `pageLevel` kept; OneNote may hang a subpage under a different parent.
   - (c) No page reorder in 5.4.
   Recommended: (a). No silent re-parenting, which is a structure change the caller didn't ask for; probe L6 checks OneNote keeps the levels.
7. **Reordering notebooks** (the user's notebook list; OneMore sorts it by sending the `Notebooks` root).
   - (a) Not in 5.4.
   - (b) In 5.4, needing a write grant on every open notebook.
   Recommended: (a). It is app-wide view state rather than notebook content, the grant model has no natural fit, and it is unprobed.
8. **Approve the plan** with these answers, so the build can start: Approve / Change.

## Live probes for the sitting

None runs in the build; each is an opt-in test in `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs` (skipped unless `ONENOTE_MCP_INTEGRATION=1`), on throwaway nodes it creates in MCP Test (cloud ones in MCP Test Cloud, Sync Now after each step).

- **L1 (verify).** Rename a throwaway section through the tool: the name lands, the ID is kept, and the journal holds the `hierarchy` entry with the old name.
- **L2 (verify).** Read the section's date, add a page to it (which moves its date), rename with the old basis: `StaleWrite`, nothing renamed.
- **L3.** Reorder section groups among themselves, and a full reorder (sections, then groups): applied in the order sent? (Deferred row "Reordering section groups among themselves".)
- **L4.** Case-only rename (`probe x` → `Probe X`): applied, `0x80042017`, or ignored?
- **L5.** Rename an unlocked protected section and a locked one: renamed, `0x8004201B`, or something else? `encrypted` kept; the read grant then shows `sectionNotOpen`.
- **L6.** Reorder pages with a subpage block moved whole: `pageLevel` kept and the subpages still under their parent.
- **L7 (cloud).** Section, page and section-group reorders on MCP Test Cloud: Phill checks each reached OneDrive in OneNote for the web (the group rename never did).
- **L8.** Rename the throwaway notebook from 5.3's test: `nickname` read back, folder unchanged, its grant still `active` in `list_grants`.
- **L9 (optional).** A rename with File > Options open returns `ModalDialogOpen` through the tool.
- Only if Q2 (b), Q3 (b), Q6 (b) or Q7 (b) is chosen: a probe of that path before it ships.

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `UpdateHierarchyAsync(string changesXml, CancellationToken ct)`, a `HierarchyWrite`.
- `src/OneNoteMcp.Com/ComGateway.cs` (`CreateSectionAsync`, l.259) -- pattern: guard before COM, `CallKind.Write`, never retried, `xs2013`. `Interop/IApplication.cs` slot 8 declares `UpdateHierarchy`.
- `src/OneNoteMcp.Com/HResultMap.cs` l.119, l.131, l.143 -- the three `Triaged` rows.
- `src/OneNoteMcp.Xml/HierarchyChildren.cs`, `HierarchyNode.cs` -- `HierarchyNode` has no `lastModifiedTime`, `isRecycleBin`, `isDeletedPages` or `pageLevel`; add a reader for a target or parent with those and its live children (pages with `pageLevel`). `OneNoteDates` parses dates; `SchemaValidation.Problems` is the XSD gate (every hierarchy attribute is optional in the 2013 XSD, so minimal XML passes).
- `src/OneNoteMcp.Core/SectionFileNames.cs` -- `NameProblem`, `SectionName` for the rename rule.
- `src/OneNoteMcp.Core/UseCases/CreateHierarchyUseCase.cs` (`CreateInParent`, l.246) -- operation shape, sibling-name and recycle-bin checks to share rather than copy.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs`, `WritePipeline.cs` -- `CheckBasisAsync`, `SnapshotAsync`, `AfterCallAsync`/`AfterCallFailed` host steps 5, 6 and the read-back; no runner change expected.
- `src/OneNoteMcp.Core/Writes/PageWrite.cs` (`CheckBasis`, l.227) -- `noBasis`/`StaleWrite` wording and date comparison to mirror for a date-only basis.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` (`CheckContainerAsync`), `ReadAccess.HoldsReadGrant` -- grant, and whether messages must be counts only.
- `src/OneNoteMcp.Core/Journal/PageSnapshot.cs`, `JournalEntry.cs`, `JournalStore.cs` -- add `HierarchySnapshot` and `JournalEntry.HierarchyKind = "hierarchy"`; `SectionKey` set means DPAPI in `JournalStore.Write`.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- `unchanged`.
- `src/OneNoteMcp.Server/Tools/CreateSectionTool.cs`, `PageBasisArgument.cs`, `ServerSetup.cs` (l.151, l.161) -- tool, basis argument and registration pattern.
- Tests: `FakeApplication.cs` l.88 (`UpdateHierarchy` throws today), `FakeGateway.cs`, `HResultMapTests.cs` l.120-123 and `ExpectedNonInternal`, `CreatePageToolTests.cs` l.355 (`0x80042024` now `ComBusy`), `CreateHierarchyToolTests.cs`, `JournalTests.cs`, `ToolContractTests.cs` + `Contract/tool-contract.json`, `McpSdkBindingTests.cs`.
- `tests/OneNoteMcp.Integration/WriteProbes/HierarchyProbes.cs` l.262-334, `ProbeXml.cs` -- the XML shapes spike 5.1 sent; `CreateHierarchyIntegrationTests.cs` -- opt-in pattern.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Com/HResultMap.cs`, `tests/OneNoteMcp.Tests/HResultMapTests.cs`, `CreatePageToolTests.cs` -- the three remaps.
- [x] `src/OneNoteMcp.Xml/` -- target/parent reader and the rename and reorder XML builders (+ fixture tests, XSD pass).
- [x] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs`, `FakeApplication.cs` (+ `ComGatewayLifecycleTests`) -- `UpdateHierarchyAsync`, never retried.
- [x] `src/OneNoteMcp.Core/Journal/HierarchySnapshot.cs` (new), `JournalEntry.cs` (+ `JournalTests`) -- the `hierarchy` entry, protected when rooted at a protected section.
- [x] `src/OneNoteMcp.Core/UseCases/UpdateHierarchyUseCase.cs` (new) -- rename and reorder `HierarchyWrite` operations; shared name checks pulled out of `CreateHierarchyUseCase`.
- [x] `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- `unchanged`.
- [x] `src/OneNoteMcp.Server/Tools/` new tools per Q1, `ServerSetup.cs` registration.
- [x] `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs` (new) -- every I/O matrix row, no COM and no journal on refusals and `unchanged`, counts-only messages without a read grant, snapshot before call, read-back warnings.
- [x] `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs` -- contract.
- [x] `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs` (new, opt-in) -- L1-L9, written, not run.
- [x] `README.md`, `plugin/skills/guide/SKILL.md` -- tool rows.
- [x] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync: tool names (Q1), add dropped (Q2), the XSD hard gate for `UpdateHierarchy` (AD-16), rename and reorder rules, `unchanged`, the three rows done.

**Acceptance Criteria:**
- Given a granted notebook, when a section in it is renamed, then the hierarchy shows the new name under the same ID and the journal holds a `hierarchy` entry with the old name.
- Given a basis date older than the section's current date, when it is renamed, then `StaleWrite` returns and no journal entry or COM write happens.
- Given a group with three sections, when the reorder lists them in a new order, then a hierarchy read shows that order and the result is `ok: true` with no warnings.
- Given an ungranted notebook and a client without elicitation, when any rename or reorder targets it, then `WriteNotGranted` returns with no lock taken and no COM write.
- Given OneNote answers `UpdateHierarchy` with `0x80042017`, `0x80042024` or `0x8004202E`, then the result is `InvalidArgument`, `ComBusy` (retryable) or `ObjectNotFound`.

## Implementation Notes

Implemented in one pass by the step-03 subagent (2026-11-13); build 0 warnings, `dotnet test tests/OneNoteMcp.Tests` 2570 passed.

- **Shape.** `Writes/HierarchyWrite.cs` holds the shared `HierarchyWrite` base (steps 1, 3-8) and `RenameHierarchyWrite` / `ReorderHierarchyWrite`; `UseCases/UpdateHierarchyUseCase.cs` drives both; `UseCases/HierarchyRules.cs` holds the recycle-bin and sibling-name checks now shared with `CreateHierarchyUseCase` (whose step 4 now checks the recycle bin before the kind; same error codes). The name check is `SectionFileNames.NameProblem` only, as directed.
- **Xml.** `HierarchyUpdate.cs`: `HierarchyTarget.Read` (kind, name, nickname, path, date, recycle-bin flags, encrypted, locked, live children with `pageLevel`) and the `Rename`/`Reorder` builders. `HierarchyChildren.KindOf`, `IsRecycleBin`, `InRecycleBin` became `internal` to share them.
- **Judgment calls (implementer), for Phill to confirm:**
  1. Results also carry `meta.journalEntryId` (absent when unchanged), and a step-7 failure's message names the journal entry, as page writes do (2.11 Q1). The plan listed only `meta.objectId`.
  2. When nothing would change (`unchanged`), the basis isn't checked, so `unchanged` is the only warning.
  3. A `basis` object with no date, or a node OneNote reports no `lastModifiedTime` for, gives `partialBasis` rather than `StaleWrite`.
  4. An empty `childIds` is `InvalidArgument`; duplicate and empty IDs are reported as counts only in step 1 (the read grant isn't known yet).
  5. A section rename checks the full name in step 1 and the name without `.one` in step 4, once the target is known to be a section.
- **Incident.** The first run of the new `UpdateHierarchyToolTests` built `SettingsStore(path)` without a journal folder, which falls back to the real default, and wrote 48 test entries into the real `%LOCALAPPDATA%\OneNoteMcp\journal`. The implementer fixed the test (its own temp journal folder) and deleted exactly those 48 files (recent, `snapshotKind` `hierarchy`, synthetic test notebook IDs only; no content printed); other files there were left alone, and later runs left 0 test entries there. 5.3's `CreateHierarchyToolTests` and its integration tests also build `SettingsStore(path)` without a journal folder, harmless today only because creates don't journal.
- **Integration probes (written, not run).** `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs` L1-L9, every `UpdateHierarchy` through 5.1's `TestNotebook.UpdateHierarchyAsync` guard, settings and journal in a temp folder. L5 needs two sections Phill prepares in MCP Test, "Probe 5.4 Protected" (protected, unlocked) and "Probe 5.4 Locked" (protected, locked), and skips without them; L9 needs `ONENOTE_MCP_PROBE_MODAL=1` and File > Options open; L8 makes a throwaway notebook Phill closes and deletes afterwards.

## Plan Change Log

## Review Triage Log

### 2026-11-13 — Review pass
- verdicts: 9 findings — high 0, medium 3, low 5, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` Guide skill line 18 still told the model to refuse renames, contradicting the new tool rows — reworded to cover only move and delete, naming `rename_hierarchy_object` and a page's title edit.
  - `[low]` `[patch]` Guide frontmatter description claimed notebook reorders and page renames — reworded to "rename sections, section groups and notebooks, and reorder sections, section groups and pages".
  - `[medium]` `[patch]` Probe L5 renamed the locked section only through the tool, which refuses `SectionLocked` before COM, so it could never show what OneNote does — L5 keeps the tool call and adds a raw guarded `TestNotebook.UpdateHierarchyAsync` rename of the locked section, recording the outcome.
  - `[low]` `[patch]` L5 had no try/finally around the rename-back, so a failure could leave Phill's prepared sections renamed — rename-back moved to a `finally` (`RestoreNameAsync`, guarded raw rename, logs instead of throwing).
  - `[low]` `[patch]` Rename docs said only a protected section's own rename ends its read grant; a local section group's rename changes the path of protected sections inside it too — tool description and README row now say so (the contract snapshot strips descriptions, so it is unchanged).
  - `[medium]` `[patch]` `McpSdkBindingTests` built `SettingsStore` with no journal folder, so its host's `JournalStore` pointed at the real journal while it now registers two journaling tools (the same cause as the build's incident) — it now passes a temp journal folder.
  - `[low]` `[patch]` An empty `childIds` was always refused, so an empty parent could never answer `unchanged` — the empty-list refusal was deleted; an empty list is `unchanged` for an empty parent and the existing "missing" `InvalidArgument` otherwise (new test).
  - `[low]` `[reject]` Both tools reuse `PageBasisArgument`, so the schema shows `pageHash`/`elementHashes` that step 1 always refuses — follows Build Directions ("the same shape ... restricted to lastModifiedTime"); the description says "Nothing else", a stray hash gets a clear `InvalidArgument`, and the fix adds a new public type.
  - `[false]` `[reject]` A cloud section group renamed to its own name returns `NotSupported`, not `unchanged` — every step-4 refusal (recycle bin, locked, OneDrive group) precedes the unchanged check by design, and the matrix ranks none of them over `unchanged`; nothing is sent either way.

### 2026-11-13 — Security review (automated push review, confirmed by the coordinator)
- verdicts: 1 finding — high 1
- findings:
  - `[high]` `[patch]` A rename could re-bind a read grant with no prompt. Read grants are keyed by section path (`SectionKey`; `IdAtGrant` is information only), and a rename changes the path: rename a granted protected section A away, then rename an ungranted protected section B to A's old name, and B inherits A's grant (the same through section groups, whose rename moves every section beneath). The spine's accepted risk ("a grant follows its path") covers changes outside the server, not our own tool. Fixed, test first: `RenameHierarchyWrite` computes where the rename moves each section (`RenamedLocations`: the section itself, or every section beneath a renamed group, read at `sections` scope) and refuses with `InvalidArgument` (`GrantLocationUserAction`: pick another name, or the user revokes that grant first) when any new key holds a stored read grant (`hours` not expired, or `untilRevoked`) or a live `once` window (`ReadAccess.HoldsReadGrantAt`). A section staying at its own key (case-only rename) never counts. Checked in step 1 (before the lock) and again in step 4 against step 3's fresh read; messages carry IDs and counts only (AD-21). A rename whose new locations can't be predicted is `NotSupported`. Reorders and notebook renames change no path and are unaffected (tested). Spine-sync note in `deferred-work.md`.

## Design Notes

OneMore (checked 2026-11-13) renames and reorders through `UpdateHierarchy` with the read XML edited in place: `NumberSectionsCommand` sets `name` on each `Section` of the whole notebook, skipping locked ones and the recycle bin; `SortCommand` re-sends a section's whole page list (subpage trees moved whole, `pageLevel` kept), a notebook's sections, or the `Notebooks` root sorted by `nickname`; `SectionColorCommand` changes `color` the same way, which is why Q1 (b) never sends a caller's attributes. Its wrapper logs and swallows every `UpdateHierarchy` error and checks no date; we keep AD-7's refusals, basis and read-back. Spike 5.1 sent minimal XML (rename: the node's element with `ID` and `name`; reorder: the parent with ID-only children), and the 2013 XSD makes every hierarchy attribute optional, so the hard XSD gate costs nothing on XML we build. A locked section is refused before COM (OneMore skips them too) until L5 shows what OneNote does.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot matches.

**Manual checks (if no CLI):**
- Phill's live sitting: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter UpdateHierarchy` on MCP Test (L1-L9), with Sync Now and an OneNote-for-the-web check for L7.

## Auto Run Result

**Status:** built (2026-11-13, bmad-build-auto, route full, review quick).

**Summary.** Two MCP tools rename and reorder through `UpdateHierarchy` as AD-7 `HierarchyWrite`: `rename_hierarchy_object(objectId, name, basis?)` (section, section group, notebook `nickname`; 5.3's `SectionFileNames.NameProblem`; a sibling or other-notebook name clash refused; case-only rename allowed; locked section `SectionLocked`; OneDrive section group `NotSupported`) and `reorder_hierarchy_children(parentId, childIds, basis?)` (every live child exactly once, sections before groups, subpage blocks whole with levels kept, recycle bin never listed or sent). Core builds minimal XML, the 2013 XSD is a hard gate (AD-16), the `lastModifiedTime` basis is compared under the lock (AD-17), one `hierarchy` journal entry is written in step 6 (DPAPI-protected when rooted at a protected section), and the node is re-read afterwards. No change is `ok` + `unchanged` with no COM and no entry. `HResultMap` remaps `0x80042017` to `InvalidArgument`, `0x80042024` to `ComBusy` (retryable), `0x8004202E` to `ObjectNotFound`.

**Files changed.**
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` (new) — shared `HierarchyWrite` steps, `RenameHierarchyWrite`, `ReorderHierarchyWrite`.
- `src/OneNoteMcp.Core/UseCases/UpdateHierarchyUseCase.cs`, `HierarchyRules.cs` (new) — the use case; recycle-bin and sibling-name checks shared with `CreateHierarchyUseCase` (edited to use them).
- `src/OneNoteMcp.Core/Journal/HierarchySnapshot.cs` (new), `JournalEntry.cs` — the `hierarchy` entry.
- `src/OneNoteMcp.Core/Access/HierarchyWalk.cs`, `IOneNoteGateway.cs`, `Results/ToolWarnings.cs` — container location walk, `UpdateHierarchyAsync`, `unchanged`.
- `src/OneNoteMcp.Xml/HierarchyUpdate.cs` (new), `HierarchyChildren.cs` — target/parent reader and the rename/reorder XML builders.
- `src/OneNoteMcp.Com/ComGateway.cs`, `HResultMap.cs` — the never-retried `UpdateHierarchy` call; the three remaps.
- `src/OneNoteMcp.Server/Tools/RenameHierarchyObjectTool.cs`, `ReorderHierarchyChildrenTool.cs` (new), `ServerSetup.cs` — tools and registration.
- `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs`, `HierarchyUpdateXmlTests.cs` (new), and edits to `JournalTests`, `ComGatewayLifecycleTests`, `HResultMapTests`, `CreatePageToolTests`, the fakes, the test-gateway decorators, `McpSdkBindingTests`, `ToolContractTests`, `NavigateToToolTests` and `Contract/tool-contract.json`.
- `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs` (new) — L1-L9, opt-in, written and not run.
- `README.md`, `plugin/skills/guide/SKILL.md`, `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` — tool rows, guide wording, spine-sync entries.

**Review findings.** 9 from the quick lens: 7 patched (3 medium, 4 low), 0 deferred, 2 rejected (a low: the shared basis schema shows hash fields step 1 refuses, kept per Build Directions; a false: refusals precede `unchanged` by design). See the triage log.

**Follow-up review recommended: true** (3 medium patched on a first pass). The named unverified risk: the patched probe L5 now sends a raw guarded `UpdateHierarchy` rename to a section Phill prepared (locked) and renames both prepared sections back in a `finally`; whether `TestNotebook`'s guard accepts those IDs and the rename-back restores them is unexercised until the sitting.

**Verification.** `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 2570 passed, 0 failed, after the patches. Matrix audit: every I/O matrix row has a passing test in `UpdateHierarchyToolTests` (the grant-dialog half of the "Ungranted" row runs through the shared `WriteAccess.CheckContainerAsync` path, covered by earlier stories' tests). No integration test ran; nothing touched OneNote.

**Assumptions for Phill to confirm.** Build Directions: a notebook name clash counts another open notebook's `nickname` or `name`; an irregular `pageLevel` jump belongs to the nearest earlier lower-level page. Implementer: `meta.journalEntryId` in results; `unchanged` skips the basis check; a dateless basis is `partialBasis`; the empty-list rule (now `unchanged` for an empty parent); a section's full name checked in step 1 and its stem in step 4.

**Residual risks.** Nothing ran against OneNote: L1-L9 answer the open behaviour (case-only rename, protected and locked renames, group reorders, page levels kept, cloud sync, notebook nickname). The build broke its own "don't touch" rule: a first test run wrote 48 entries into the real `%LOCALAPPDATA%\OneNoteMcp\journal`; the implementer deleted exactly those and later re-read the folder to check (Implementation Notes). 5.3's `CreateHierarchyToolTests` and its integration tests still build `SettingsStore` without a journal folder. A security review of the diff before merge isn't in this ticket's Verify but would suit a write path.

**Rebase (2026-11-13).** Rebased onto `origin/story/5.3-create-hierarchy` at `73a7891` (5.3's security-review fixes: name hygiene in `SectionFileNames.NameProblem`, the closed-notebook `.onetoc2` containment check, the empty-notebook check before the auto grant). Conflicts in `README.md` and `plugin/skills/guide/SKILL.md` (table rows) were resolved by keeping 5.3's rows and adding 5.4's. `CreateHierarchyUseCase.cs` merged cleanly. Renames inherit the hygiene through the shared `NameProblem`; the `rename_hierarchy_object` description now lists the same rules as `create_section` (control, invisible or text-direction characters; at most 128 characters), and `Rename_ABadName_IsInvalidArgument_BeforeTheLock_NothingRead` gained four cases (U+202E, U+200B, U+2028, 129 characters). After the rebase: build 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 2631 passed, 0 failed.

**Security fix and second rebase (2026-11-13).** Rebased onto `origin/story/5.3-create-hierarchy` at `92b07d3` (5.3's `TestEnvironment` module initializer points the settings, journal and log overrides at a temp folder for the whole unit-test process); no conflicts. Then the high security-review finding was fixed test first (Review Triage Log, second entry): a rename that would move a section onto a section key holding a read grant or a live `once` window is `InvalidArgument`, checked in step 1 and again in step 4; an unpredictable new location is `NotSupported`. New: `src/OneNoteMcp.Core/Access/RenamedLocations.cs`, `ReadAccess.HoldsReadGrantAt`, `HierarchyTarget.SectionPathsBelow`, `FakeGateway.OnGetHierarchy`; tests `RenamedLocationsTests` (15 cases) and 12 new `UpdateHierarchyToolTests` cases. Tool description, README and guide skill say so. After it: build 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 2660 passed, 0 failed.
