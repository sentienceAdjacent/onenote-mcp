---
title: 'delete_page_content'
type: 'feature'
ticket: '10'
created: '2026-11-12'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** No tool removes a whole Outline from a page. 2.9 built the `PageWrite` steps for `UpdatePageContent` only; `IOneNoteGateway.DeletePageContentAsync` (2.7) has no caller.

**Approach:** A `DeletePageContentWrite` beside `UpdatePageContentWrite` in `PageWrite.cs`, reusing the base class's steps 1, 3 and 6, with the basis check (date + target hashes, `noBasis`/`partialBasis`) and the `readBackFailed` handling lifted from `UpdatePageContentWrite` into `PageWrite` so both share one copy. Step 4: the target is an Outline on the page; step 5: the date and the deleted Outline's hash (AD-17); step 7: `DeletePageContent` with the basis date; read-back: the Outline is gone. A thin `delete_page_content` tool.

## Boundaries & Constraints

**Always:**
- AD-7 order; step 1 `WriteAccess.CheckAsync(pageId)`; the abandoned-write `ComBusy` check after step 2 (2.9's pipeline code, unchanged); step 3 exactly as `PageWrite.ReadAsync` does it (section first, `SectionLocked`, recycle bin → `ObjectNotFound`).
- Step 4: `objectId` not in step 3's piBasic read → `UnknownObjectId`; on the page but not a `one:Outline` (an OE, the title's OE, an image, ink, a table…) → `InvalidArgument` with its own `userAction` (AD-7; deferred-work l.253's 2.10 half): OEs are removed with `update_page_content`'s `removeObjectIds`; images and ink can't be deleted in v1.
- Step 5 (AD-17): a basis date must equal step 3's as UTC instants, else `StaleWrite`; the caller's date, never the live one, goes to COM. Only the deleted Outline's hash is compared (`pageHash` and other hashes ignored); a missing hash → `partialBasis`; no basis → step 3's date, `noBasis`. A date that isn't a date → `InvalidArgument`.
- Read-back (under the lock, after success): piBasic re-read; the Outline still there → `readBackDiffers` naming it; a re-read that fails → `readBackFailed`. Never fails the write.
- Protected section: per Q1's answer. Whatever it is, no message names anything inside the page except the caller's own `objectId` argument, and only where a read grant is held.
- Cache eviction whatever the outcome; messages and logs carry IDs and counts only (AD-9, AD-13). Contract snapshot updated; `<Version>` stays `0.9.0-rc.1`.
- Merge `origin/story/2.9-update-page-content` again before implementing (it gains the security-review fixes).

**Never:** forking 2.9's basis or read-back code; OE, image or ink deletes (spine Deferred); several targets per call unless Q2 says so; `force: true` or automatic retries; `restore_from_journal` (2.11); skill recipes beyond a tool row (2.12); spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Fresh delete | Outline ID, basis from a read | `ok`, one journal entry, `DeletePageContent(pageId, id, callerDate)` | — |
| Stale hash | Outline changed since the read (page not displayed, date unchanged) | no snapshot, no COM call | `StaleWrite` |
| Stale date | basis date ≠ step 3's | no COM call | `StaleWrite` |
| No basis | `basis` omitted | runs with step 3's date | warning `noBasis` |
| Hash missing | basis without the Outline's hash | runs | warning `partialBasis` |
| Not an Outline | an OE / image / title OE ID | no COM call | `InvalidArgument`, own `userAction` |
| Unknown ID | not on the page (or already deleted) | no COM call | `UnknownObjectId` |
| Empty `objectId` | `""` | no read | `InvalidArgument` naming the argument |
| Still there after | read-back finds the Outline | `ok` | `readBackDiffers` |
| Read-back fails | re-read throws | `ok` | `readBackFailed` |
| Ambiguous step 7 | `Timeout` etc. | — | `outcomeUnknown`, no read-back |
| Snapshot fails | journal unwritable | no COM call | `JournalFailed` |
| Locked / binned / not a page | as 2.9 | — | `SectionLocked` / `ObjectNotFound` |
| Abandoned write outstanding | after step 2 | nothing read | `ComBusy` |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- move `UpdatePageContentWrite.CheckBasisAsync`'s body (`:255-332`) into a protected `PageWrite.CheckBasis(PageWriteBasis?, IReadOnlyList<string> targets)` that returns the date for COM and collects the `noBasis`/`partialBasis` warnings; move `AfterCallFailed` (`:349-363`) and `NoBasisUserAction` up too. `IdList`, `Capped`, `NamesIds`, `BasicXml`, `Invalid` are already shared. Add `DeletePageContentWrite` (`OperationName = "delete_page_content"`) below `UpdatePageContentWrite`.
- `src/OneNoteMcp.Xml/PageWriteCheck.cs` -- add a pure `DeleteTarget(readXml, objectId)` → `Outline | NotAnOutline | Unknown` (reuse `ById`), and `StillPresent(afterXml, objectId)` for the read-back. No schema gate: nothing is sent.
- `src/OneNoteMcp.Xml/PageBasis.cs` -- `PageBasis.From(BasicXml).ElementHashes` already has every Outline's hash; reused for step 5 and for `meta.basis` (Q3).
- `src/OneNoteMcp.Core/IOneNoteGateway.cs:68`, `src/OneNoteMcp.Com/ComGateway.cs:279` -- `DeletePageContentAsync` exists (UTC date, `force: false`); `0x8004200E` maps to `ObjectNotFound` (`HResultMap.cs:110`). Unchanged.
- `src/OneNoteMcp.Core/UseCases/UpdatePageContentUseCase.cs` -- the shape for `DeletePageContentUseCase.cs`.
- `src/OneNoteMcp.Server/Tools/UpdatePageContentTool.cs` -- the shape for `DeletePageContentTool.cs` (`pageId`, `objectId`, `basis?` as `PageBasisArgument`); annotations readOnly false, destructive true, idempotent false, openWorld false. `ServerSetup.cs:136,153` -- register; `ServerSetup.Close` already closes `PageBasisArgument`'s schema.
- `src/OneNoteMcp.Core/Access/ReadAccess.cs` -- `HoldsReadGrant(section, callStart)` (2.9) for Q1.
- Tests: `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` (harness: notebook `W`, protected/locked/binned sections, temp journal, own write lock) -- copy its setup into `DeletePageContentToolTests.cs`; `FakeGateway.cs:179-206` -- add `OnDelete` (like `OnUpdate`) so the read-back sees the Outline gone; `Fixtures/synthetic/update-page-Basic.xml` reused. Lists: `NavigateToToolTests.cs:120`, `ToolContractTests.cs:86`, `Contract/tool-contract.json`, `McpSdkBindingTests.cs` (bad `basis` member, as 2.9), `spikes/stress-harness/Calls.cs:70` and `Program.cs:88` (fifteen tools), `PluginPackagingTests`.
- `tests/OneNoteMcp.Integration/PageWriteGatewayIntegrationTests.cs`, `UpdatePageContentIntegrationTests.cs`, `TestNotebook.EnsureTestTargetAsync` -- shape for the opt-in live test.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Xml/PageWriteCheck.cs` + tests -- `DeleteTarget`, `StillPresent`.
- [ ] `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- lift the basis check and `AfterCallFailed` into `PageWrite`; `UpdatePageContentWrite` calls them (its tests stay green unchanged); add `DeletePageContentWrite`.
- [ ] `src/OneNoteMcp.Core/UseCases/DeletePageContentUseCase.cs`, `src/OneNoteMcp.Server/Tools/DeletePageContentTool.cs`, `ServerSetup.cs` -- use case and tool.
- [ ] `tests/OneNoteMcp.Tests/DeletePageContentToolTests.cs`, `FakeGateway.cs` -- every matrix row, the Q1 behaviour, lock free after each outcome, cache entry dropped, journal entry written.
- [ ] Contract snapshot, tool lists, binding test, stress harness, `plugin/skills/guide/SKILL.md` and `README.md` tool rows (call only when the user asks to remove a block of a page).
- [ ] `tests/OneNoteMcp.Integration/DeletePageContentIntegrationTests.cs` -- opt-in; not run here.
- [ ] `deferred-work.md` -- resolve l.253's 2.10 half; add the spine-sync entry for what Q1–Q3 decide.

**Acceptance Criteria:**
- Given any refusal in steps 3–5, when the pipeline ends, then `DeleteCalls` is empty, no journal entry exists, and the lock is free.
- Given the lift, when `UpdatePageContentToolTests` runs unchanged, then it passes.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass; `dotnet test tests/OneNoteMcp.Integration` without the variable passes or skips.

## Open Questions

1. **Protected section without a read grant** (security review of 2.9, finding 2, applied to deletes). AD-21 lists `delete_page_content` under Writes that need no read grant. But with hashes compared, `StaleWrite` versus success tells a caller whether its guessed hash matches an Outline it can't read, and `UnknownObjectId` versus `InvalidArgument` tells it whether an ID is on the page and what kind it is.
   - (a) Refuse: in a protected section, `delete_page_content` needs a read grant held at the call's start (never prompting), else `ReadNotGranted` before the page is read; `userAction`: read the page with `get_page_content` first (which asks). Closes both leaks. Costs little: the caller can only know an Outline's ID from a read, which needed the grant. Needs AD-21's Writes list amended (spine sync).
   - (b) Allow, but without a read grant refuse any `elementHashes` in the basis (date only) and give one refusal for unknown and not-an-Outline. Keeps AD-21 as written; a stale delete on an undisplayed page then goes through unnoticed.
   - (c) As 2.9 is today: compare hashes, counts-only messages. Leaves the oracle.
   Recommended: (a). It is what the recommended 2.9 rule ("without a read grant, refuse existing objectIDs") means for a delete, since every delete names an existing objectID.
2. **One target or several?**
   - (a) `objectId: string`, one Outline, one COM call, one journal entry.
   - (b) `objectIds: string[]`: one snapshot and lock, several `DeletePageContent` calls; a failure part-way leaves some deleted.
   Recommended: (a). AD-5's one tool per COM method; (b)'s partial failure needs its own reporting.
3. **`meta.basis` on the result?**
   - (a) Yes, from the read-back's piBasic read, as 2.9 Q2 (left out when the read-back fails or no read grant), so a second write needs no re-read.
   - (b) No: the caller re-reads before its next write.
   Recommended: (a), for consistency with `update_page_content`.

## Design Notes

- The lift keeps behaviour: `UpdatePageContentWrite` passes its `HashTargets` to `CheckBasis`; `DeletePageContentWrite` passes `[objectId]`.
- Spike 2.2 never saw `DeletePageContent` succeed with the page XML's date (spine Deferred "`DeletePageContent` and dates"): the code sends that date as AD-17 says; the live sitting settles it.
- No conflict with 5.2: it keeps `DeletePageContent` on the page XML's date and the deleted Outline's hash; its `DeleteHierarchy` exception doesn't apply here.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet test tests/OneNoteMcp.Integration` (no variable) -- pass or skip.
- `dotnet build spikes/stress-harness -c Release` -- builds.

**Manual checks (live sitting with Phill, not run by the build):**
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter DeletePageContentIntegrationTests` on MCP Test: a fresh delete removes the Outline and leaves a journal entry; a hash-stale delete on an undisplayed page → `StaleWrite`.
- Spine Deferred row: delete an Outline with the page XML's date on MCP Test and MCP Test Cloud, on a displayed and an undisplayed page in each (sync-check every cloud step); record whether OneNote accepts the date.
- From Claude Code: Phill asks to remove one block of an MCP Test page; it goes, and an OE ID gives `InvalidArgument` with the `removeObjectIds` hint.
- MCP Test's protected section with the write grant only: the Q1 behaviour.
