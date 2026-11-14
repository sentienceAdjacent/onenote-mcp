---
title: 'delete_page_content'
type: 'feature'
ticket: '10'
created: '2026-11-12'
status: done
baseline_revision: '0f67f9588fd6cf7eeba0617286cc371fda489d22'
warnings: ['oversized']
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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
- Protected section (Q1): a read grant for the section held at the call's start (`ReadAccess.HoldsReadGrant`, a `once` grant included, never prompting) is required; without one, `ReadNotGranted` before the page is read. So every message that names IDs is made with a read grant held.
- Cache eviction whatever the outcome; messages and logs carry IDs and counts only (AD-9, AD-13). Contract snapshot updated; `<Version>` stays `0.9.0-rc.1`.
- Merge `origin/story/2.9-update-page-content` again before implementing (it gains the security-review fixes).

**Never:** forking 2.9's basis or read-back code; OE, image or ink deletes (spine Deferred); several targets per call (Q2); `force: true` or automatic retries; `restore_from_journal` (2.11); skill recipes beyond a tool row (2.12); spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

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
| Protected, no read grant | write grant only | no page read, no dialog, no COM call | `ReadNotGranted`, `userAction`: read the page with `get_page_content` first |
| Protected, read grant held | stored or `once` grant | as unprotected; `meta.basis` returned | — |

## Decisions (Phill, 2026-11-12, plan approved, kept whole; all recommendations)

- **Q1 (a):** in a password-protected section, `delete_page_content` needs the section's read grant held at the call's start (never prompting); otherwise `ReadNotGranted` before the page is read, with `userAction`: read the page with `get_page_content` first (which asks). Closes the hash and ID-kind oracles. Story 5.2 records the AD-21 amendment; no spine edit here.
- **Q2 (a):** `delete_page_content(pageId, objectId, basis?)`: one Outline per call, one `DeletePageContent` call, one journal entry.
- **Q3 (a):** the result carries `meta.basis` from the read-back's piBasic read, as 2.9 Q2; left out when the read-back fails (and, by Q1, there is always a read grant when the page was read in a protected section).
- **Size:** keep the full plan (about 3,000 tokens; single goal, as 2.9).
- 2.9's later security decisions (date-only basis without a read grant, DOCTYPE refused, file paths user-gated) arrive by re-merging `origin/story/2.9-update-page-content`; with Q1 the date-only rule is moot for deletes.

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
- [x] `src/OneNoteMcp.Xml/PageWriteCheck.cs` + tests -- `DeleteTarget`, `StillPresent`.
- [x] `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- lift the basis check and `AfterCallFailed` into `PageWrite`; `UpdatePageContentWrite` calls them (its tests stay green unchanged); add `DeletePageContentWrite`.
- [x] `src/OneNoteMcp.Core/UseCases/DeletePageContentUseCase.cs`, `src/OneNoteMcp.Server/Tools/DeletePageContentTool.cs`, `ServerSetup.cs` -- use case and tool.
- [x] `tests/OneNoteMcp.Tests/DeletePageContentToolTests.cs`, `FakeGateway.cs` -- every matrix row, the Q1 behaviour, lock free after each outcome, cache entry dropped, journal entry written.
- [x] Contract snapshot, tool lists, binding test, stress harness, `plugin/skills/guide/SKILL.md` and `README.md` tool rows (call only when the user asks to remove a block of a page).
- [x] `tests/OneNoteMcp.Integration/DeletePageContentIntegrationTests.cs` -- opt-in; not run here.
- [x] `deferred-work.md` -- resolve l.253's 2.10 half; add the spine-sync entry (AD-21: `delete_page_content` needs the read grant in a protected section, Q1; AD-17: its result carries `meta.basis`, Q3).

**Acceptance Criteria:**
- Given any refusal in steps 3–5, when the pipeline ends, then `DeleteCalls` is empty, no journal entry exists, and the lock is free.
- Given the lift, when `UpdatePageContentToolTests` runs unchanged, then it passes.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass; `dotnet test tests/OneNoteMcp.Integration` without the variable passes or skips.

## Implementation Notes

- The re-merge before implementing brought nothing: `origin/story/2.9-update-page-content` was still `7d172f4`, already in this branch.
- `PageWrite.CheckBasis(basis, targets)` holds the shared basis check (returns the date for COM; collects `noBasis`/`partialBasis` in `Warnings`); `AfterCallFailed` and `NoBasisUserAction` moved up too. `UpdatePageContentToolTests` passes unchanged.
- Q1 is a `NeedsReadGrant` hook in the shared step 3; the grant check now runs before `SectionLocked` (as reads order it), so a locked protected section without a grant gives a delete `ReadNotGranted`. No change for update (it never needs the grant). Flagged in deferred-work for 5.2.
- An empty `objectId` is refused in the use case before the pipeline (no lock, no read), naming the argument.
- Verification (lead, after review patches): build `-warnaserror` 0/0; unit 1890/1890; integration without the variable 154 passed, 46 skipped; stress harness builds.

- Merge of `origin/story/2.9-update-page-content` at `0f38576` (2.9's security fixes; lead, 2026-11-12): conflicts in `PageWrite.cs` and `deferred-work.md`. 2.9's date-only rule lives in its `CheckWithoutReadGrant` hook, not in the basis check, so `PageWrite.CheckBasis` needed no change. The `NeedsReadGrant` hook above was replaced by that hook: `DeletePageContentWrite` overrides it to refuse always (Q1) and overrides `AuthorizeAsync` to check it in step 1 before the write grant, as AD-7 step 1 orders it at 5.2's `18dbcdb`; the shared step 3 re-checks it under the lock before `SectionLocked`. Test added: `ProtectedWithoutAReadGrant_AndNoWriteGrant_IsReadNotGranted_BeforeTheWriteGrantDialog`. Build 0/0; unit 1953/1953; integration without the variable 154 passed, 46 skipped; stress harness builds.

## Plan Change Log

## Review Triage Log

### 2026-11-12 — Review pass 1 (quick lens)
- verdicts: 4 findings — high 0, medium 1, low 3, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `plugin/skills/guide/SKILL.md:204`: the `ReadNotGranted` row tells Claude to retry so the dialog appears, but a `delete_page_content` retry never asks (Q1) — confirmed at the row; added the read-first sentence for `delete_page_content`.
  - `[low]` `[patch]` `DeletePageContentIntegrationTests.StaleHash_OnAPageThatIsntDisplayed_IsStaleWrite` asserted only `StaleWrite`, so a date mismatch would pass too and the live check wouldn't show the hash caught it — it now also asserts the message names the Outline (only the hash refusal does); 2.9's twin test noted in deferred-work.
  - `[low]` `[patch]` the re-merge of `origin/story/2.9-update-page-content` brought nothing (its tip `7d172f4` is in this branch), and the lift moves the code 2.9's date-only fix edits into `PageWrite.CheckBasis` — a real merge hazard with no code to fix now; a deferred-work note tells the merger to apply it in the shared method (moot for deletes under Q1). Re-checked before the final commit.
  - `[low]` `[reject]` an empty `objectId` is refused in the use case before the pipeline, so step 1 and the cache eviction don't run — the matrix row says "no read"; nothing is read or written, so no cache entry can be stale. The fix would add a pipeline pass for a call that does nothing.

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
- MCP Test's protected section with the write grant only: `ReadNotGranted`, no dialog, nothing deleted; with a read grant: the delete goes through.

**Joint 2.9 + 2.10 live sitting (2026-11-12 evening, the build at `b4e5a7c`): all MCP Test checks passed; the MCP Test Cloud steps were deferred.**
- `ONENOTE_MCP_INTEGRATION=1` `DeletePageContentIntegrationTests` with `UpdatePageContentIntegrationTests` on MCP Test: 5/5 passed.
- D1: delete of an Outline (it held an attachment) with a fresh full basis is ok, and the new `meta.basis` drops the Outline and its OE. Its journal entry (`0512009f`) is a page snapshot with `binaryComplete: false` and the attachment in `missingBinary`, as 2.8's Q4 decided.
- D2: an OE's ID is `InvalidArgument` with the `removeObjectIds` hint, nothing deleted.
- D3 (Ballast Vault): with no read grant, a delete is `ReadNotGranted` with no dialog (D3a). Phill then granted read through the dialog on a `get_page_content` (D3b), and the delete was ok (D3c), with a DPAPI-sealed journal entry (`b8d52502`, `protected: true`).
- D4, date-only basis (the spine's Deferred row), MCP Test: on a displayed page (D4a) and an undisplayed one (D4b), the delete was ok with `partialBasis` and no 0x80042010; `lastModifiedTime` advanced on the displayed page and stayed frozen on the undisplayed one. A date one minute stale on the displayed page was `StaleWrite` from the server's own date check, before OneNote was called (`retryable: false`), so OneNote's own 0x80042010 path stays unexercised.
- D4c/d on MCP Test Cloud: not run. The notebook hadn't synced since 6:21 PM (0xE01901F7; Probes 0xE000145C "server busy") during a Microsoft outage, before any cloud call. They move to the next sitting (deferred-work), which also starts by checking `list_grants` for D3b's Ballast Vault read grant.
- 2026-11-14 sitting: D4c/d ran on MCP Test Cloud. Both deletes were ok with `partialBasis`, with no 0x80042010 and no dialog, and sync was clean before and after. The displayed page's date advanced, and the undisplayed page's date stayed frozen, the same as D4a/b. See the resolved entry in deferred-work.
