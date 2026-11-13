---
title: 'Rename and reorder: update_hierarchy'
type: 'feature'
ticket: '4'
created: '2026-11-13'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
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
- [ ] `src/OneNoteMcp.Com/HResultMap.cs`, `tests/OneNoteMcp.Tests/HResultMapTests.cs`, `CreatePageToolTests.cs` -- the three remaps.
- [ ] `src/OneNoteMcp.Xml/` -- target/parent reader and the rename and reorder XML builders (+ fixture tests, XSD pass).
- [ ] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs`, `FakeApplication.cs` (+ `ComGatewayLifecycleTests`) -- `UpdateHierarchyAsync`, never retried.
- [ ] `src/OneNoteMcp.Core/Journal/HierarchySnapshot.cs` (new), `JournalEntry.cs` (+ `JournalTests`) -- the `hierarchy` entry, protected when rooted at a protected section.
- [ ] `src/OneNoteMcp.Core/UseCases/UpdateHierarchyUseCase.cs` (new) -- rename and reorder `HierarchyWrite` operations; shared name checks pulled out of `CreateHierarchyUseCase`.
- [ ] `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- `unchanged`.
- [ ] `src/OneNoteMcp.Server/Tools/` new tools per Q1, `ServerSetup.cs` registration.
- [ ] `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs` (new) -- every I/O matrix row, no COM and no journal on refusals and `unchanged`, counts-only messages without a read grant, snapshot before call, read-back warnings.
- [ ] `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs` -- contract.
- [ ] `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs` (new, opt-in) -- L1-L9, written, not run.
- [ ] `README.md`, `plugin/skills/guide/SKILL.md` -- tool rows.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync: tool names (Q1), add dropped (Q2), the XSD hard gate for `UpdateHierarchy` (AD-16), rename and reorder rules, `unchanged`, the three rows done.

**Acceptance Criteria:**
- Given a granted notebook, when a section in it is renamed, then the hierarchy shows the new name under the same ID and the journal holds a `hierarchy` entry with the old name.
- Given a basis date older than the section's current date, when it is renamed, then `StaleWrite` returns and no journal entry or COM write happens.
- Given a group with three sections, when the reorder lists them in a new order, then a hierarchy read shows that order and the result is `ok: true` with no warnings.
- Given an ungranted notebook and a client without elicitation, when any rename or reorder targets it, then `WriteNotGranted` returns with no lock taken and no COM write.
- Given OneNote answers `UpdateHierarchy` with `0x80042017`, `0x80042024` or `0x8004202E`, then the result is `InvalidArgument`, `ComBusy` (retryable) or `ObjectNotFound`.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

OneMore (checked 2026-11-13) renames and reorders through `UpdateHierarchy` with the read XML edited in place: `NumberSectionsCommand` sets `name` on each `Section` of the whole notebook, skipping locked ones and the recycle bin; `SortCommand` re-sends a section's whole page list (subpage trees moved whole, `pageLevel` kept), a notebook's sections, or the `Notebooks` root sorted by `nickname`; `SectionColorCommand` changes `color` the same way, which is why Q1 (b) never sends a caller's attributes. Its wrapper logs and swallows every `UpdateHierarchy` error and checks no date; we keep AD-7's refusals, basis and read-back. Spike 5.1 sent minimal XML (rename: the node's element with `ID` and `name`; reorder: the parent with ID-only children), and the 2013 XSD makes every hierarchy attribute optional, so the hard XSD gate costs nothing on XML we build. A locked section is refused before COM (OneMore skips them too) until L5 shows what OneNote does.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot matches.

**Manual checks (if no CLI):**
- Phill's live sitting: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter UpdateHierarchy` on MCP Test (L1-L9), with Sync Now and an OneNote-for-the-web check for L7.
