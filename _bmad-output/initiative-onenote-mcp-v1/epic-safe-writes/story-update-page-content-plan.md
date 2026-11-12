---
title: 'update_page_content'
type: 'feature'
ticket: '9'
created: '2026-11-12'
status: 'built'
baseline_revision: '7204aa67603a9e0689a585df0b56caf1fe26ea7d'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
warnings: ['oversized']
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** No tool edits a page. The `PageWrite` class (AD-7) has a gateway method (2.7) and a snapshot step (2.8) but no operation: no basis check, no whole-Outline validation, no read-back, and AD-17's two newer hash exclusions (`pathCache`, unlocked column `width`) aren't built.

**Approach:** A `PageWrite` operation class for `UpdatePageContent` through `WritePipeline`, shared with 2.10, plus a thin `update_page_content` tool. Step 3 reads the section and the page (piBasic); step 4 validates (schema gate, page ID, referenced `objectID`s, whole Outlines); step 5 compares the basis; step 6 calls `PageSnapshot`; step 7 calls the gateway; a new read-back step between 7 and 8 reports warnings and never fails the write.

## Boundaries & Constraints

**Always:**
- AD-7 order and rules exactly; step 1 is `WriteAccess.CheckAsync(pageId)` (write grant alone, also in a protected section; never the read dialog). Right after step 2, every pipeline fails `ComBusy` (retryable, nothing read) while `gateway.WaitForAbandonedWritesAsync` isn't complete (AD-3, deferred-work 2.7 forward note).
- Step 3: the owning section (fresh, `locked`, `encrypted`, recycle bin) then piBasic page XML. Not a page, or a page in the recycle bin → `ObjectNotFound` before COM (as 2.4 Q8); locked → `SectionLocked`.
- Step 4: root is a 2013 `one:Page` whose `ID` equals `pageId`; every sent `objectID` exists in step 3's read (`UnknownObjectId`); each sent Outline with an `objectID` lists every OE step 3 read under it, nested and table-cell OEs included, unless removal names it or an ancestor (`InvalidArgument` with its own `userAction`, deferred-work l.253). A removal name that isn't an OE under a sent Outline, or that is also sent → `InvalidArgument`.
- Step 5 (AD-17): a basis date must equal step 3's (compared as UTC instants) or `StaleWrite`; the caller's date, never the live one, goes to COM. Each sent Outline's basis hash must equal step 3's or `StaleWrite`; a missing hash skips that check with `partialBasis`; no basis → step 3's date, `noBasis`. Hashes for unsent elements are ignored.
- Read-back (under the lock, after a successful call): piBasic re-read; per sent element with an `objectID` and per positional OE in each sent Outline, compare the attributes sent (skip `ElementHash`'s exclusions, unlocked column `width`, and an OE `style` dropped because it equals its QuickStyleDef); report `readBackDiffers` (`objectID` + attribute name, or old and new IDs), `imageLost` (Outline ID, counts), `readBackFailed`. At most 20 warnings per code, then one summary.
- No read grant for a protected section (checked at call start, `once` included, never prompting): refusals and warnings carry counts only, never an `objectID` (AD-7, AD-21).
- `ElementHash` excludes `one:InsertedFile@pathCache` and `width` on a `one:Column` without `isLocked="true"` (AD-17).
- Cache eviction of `pageId` whatever the outcome. Messages and logs: IDs, counts, attribute and element names only (AD-9, AD-13).
- Contract: tool-contract snapshot updated; `<Version>` stays `0.9.0-rc.1` (no release since `v0.8.0-rc.2`, as 2.4 Q3).

**Never:** `delete_page_content` or `DeletePageContent` in the use case (2.10); `restore_from_journal` (2.11); skill recipes beyond a tool row (2.12); `force: true` or any automatic write retry (CLAUDE.md, OneMore differs); spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Fresh edit | basis from a read, whole Outline sent | `ok`, one journal entry, `UpdatePageContent` with the basis date | — |
| Stale hash | sent Outline changed since the read | no snapshot, no COM write | `StaleWrite` |
| Stale date | basis date ≠ step 3's | no COM write | `StaleWrite` |
| No basis | `basis` omitted | write runs with step 3's date | warning `noBasis` |
| Partial basis | sent Outline's hash missing | that check skipped | warning `partialBasis` |
| OE omitted | Outline lacks an OE step 3 read, not named for removal | no COM write | `InvalidArgument`, own `userAction` |
| OE removed | omitted OE named for removal | `ok` | — |
| Unknown ID | sent `objectID` not on the page | no COM write | `UnknownObjectId` |
| ID mismatch | root `ID` ≠ `pageId` | no COM write | `InvalidArgument` |
| Bad schema | XML fails the gate | no COM write | `InvalidXml`, line and position only |
| Ignored attribute | `quickStyleIndex` changed on an existing OE | `ok` | `readBackDiffers` (ID, attribute) |
| Image gone | sent Outline's image missing after | `ok` | `imageLost` |
| Read-back fails | re-read throws | `ok` | `readBackFailed` |
| Protected, no read grant | write grant only | `ok`, no dialog; refusals/warnings with counts only | — |
| Abandoned write outstanding | after step 2 | no read, no write | `ComBusy`, retryable |
| Step-7 ambiguous failure | `Timeout` etc. | — | `outcomeUnknown`, no read-back |
| Snapshot fails | journal unwritable | no COM write | `JournalFailed` |
| OE moved | OE sent under an Outline other than the one step 3 read it in | no COM write | `InvalidArgument`, re-create/remove `userAction` |
| Stale title | sent title OE's hash ≠ basis | no COM write | `StaleWrite` |

## Decisions (Phill, 2026-11-12, plan approved whole; all six recommendations)

- **Q1 (a):** `update_page_content(pageId, xml, basis?, removeObjectIds?)`; the root `one:Page@ID` must equal `pageId` (else `InvalidArgument`); naming an OE for removal removes its nested OEs too.
- **Q2 (a):** the result carries `meta.basis` from the read-back's piBasic read; omitted when the read-back fails, and in a protected section without a read grant.
- **Q3 (a):** hard XSD gate against the vendored 2013 XSD in step 4; `InvalidXml` names line, position and the element's name only, never the validator's text. AD-16's settlement is recorded by story 5.2.
- **Q4 (a):** an OE sent under an Outline other than the one step 3 read it in is refused with `InvalidArgument`; the `userAction` says to send it without its `objectID` in the new Outline and name it for removal from the old one. AD-7 as amended by 5.2 (Phill 2026-11-12, 2.9 Q4).
- **Q5 (a):** step 5 also compares the basis hash of each sent OE with an `objectID` outside any sent Outline (in practice the title's OE), with the same partial-basis rule. AD-17 as amended by 5.2 (Phill 2026-11-12, 2.9 Q5).
- **Q6 (a):** no merged-table code; an opt-in integration probe (merged table written through the raw seam in `xs2026`, a cell edited through the pipeline, re-read in `xs2026`) runs in the live sitting; if the merge is lost, a follow-up amends AD-4.
- Lane decisions stand: the abandoned-write check after step 2 applies to every pipeline (AD-3); a recycle-bin page is `ObjectNotFound` (as 2.4 Q8); no `<Version>` bump.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/WritePipeline.cs:122-175` -- after step 2: the abandoned-write check (`gateway.WaitForAbandonedWritesAsync(…).IsCompleted`). After a successful step 7: a new virtual `WriteOperation.AfterCallAsync(TResult, ct)` (read-back) under the lock; any exception or deadline there becomes the operation's own warning, never a failure. Ambiguous-failure handling unchanged.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs` -- add the read-back hook (default: returns the result unchanged). `CreatePageUseCase` keeps the defaults.
- `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` -- the shape to copy (private nested operation, `XmlReads.Readable`, ID-only messages). New `UpdatePageContentUseCase.cs` beside it; the `PageWrite` steps live in `src/OneNoteMcp.Core/Writes/PageWrite.cs` so 2.10 adds its delete variant there.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs:48` -- step 1, reused. `HierarchyWalk.OwningSectionAsync` (`:23`) for step 3's section. `ReadAccess` -- add a non-prompting `HoldsReadGrant(HierarchyNode section)` built on `Stored` + `windows.IsOpen` (as `CheckBroad` does).
- `src/OneNoteMcp.Core/Journal/PageSnapshot.cs:40` -- step 6: `TakeAsync(new PageSnapshotRequest(correlationId, "update_page_content", OperationClass.PageWrite, pageId, basicXml))`; registered in `ServerSetup.cs:126`.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs:61` -- `UpdatePageContentAsync(pageXml, expectedLastModified)`; the gateway already refuses bad dates. `src/OneNoteMcp.Xml/OneNoteDates.cs` -- `ToUtc` for the date comparison.
- `src/OneNoteMcp.Xml/` -- `ElementHash.cs:20-93`: the two exclusions. New `PageWriteCheck.cs` (pure, unit-testable): whole-Outline/ID/removal/move checks over sent and read XML, the hashes of the sent targets, and the read-back comparison; `PageBasis.From` and `ImageCount` reused. `SchemaValidation.Validate` (Q3) gains a content-free variant (line, position, element local name).
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- codes `noBasis`, `partialBasis`, `readBackDiffers`, `imageLost`, `readBackFailed`; `Basis` already exists (Q2). `PageBasisResult.cs` -- input twin `PageBasisArgument` in Server.
- `src/OneNoteMcp.Server/Tools/UpdatePageContentTool.cs` -- thin, like `CreatePageTool.cs`; annotations readOnly false, destructive true, idempotent false, openWorld false. `Tools/ArgumentCheck.cs:117-149` -- `Mismatch` recurses into an object's `properties` and `additionalProperties`, so a bad `basis` member is our `InvalidArgument`, not an SDK binding error (message names the argument, never a value or member).
- `src/OneNoteMcp.Server/ServerSetup.cs:116,143` -- register use case and tool.
- Tests: `CreatePageToolTests.cs` (pipeline harness, `TimedOutPageWrite` at ~:883), `FakeGateway.cs:152-183` (`UpdateCalls`, `AbandonedWrites`; add a per-page XML override so read-back sees the written page), `XmlModelTests.cs` (hash), `McpSdkBindingTests.cs`, `ToolContractTests.cs:86`, `Contract/tool-contract.json`, `NavigateToToolTests.cs:120` `WriteTools`, `PluginPackagingTests`; `spikes/stress-harness/Calls.cs:70` `WriteTools`, `Program.cs:88` count.
- `tests/OneNoteMcp.Integration/PageWriteGatewayIntegrationTests.cs`, `TestNotebook.EnsureTestTargetAsync`, `WriteProbes/LocalWriteProbes.cs` (raw-seam helpers) -- the opt-in live tests.
- OneMore: `Models/Page.cs`, `PageEditor.cs` and "Editing Text" for the merge/whole-Outline shape only; we keep the basis check and `force: false`.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/ElementHash.cs` + `XmlModelTests` -- the two AD-17 exclusions; a locked width stays in the hash.
- [x] `src/OneNoteMcp.Xml/PageWriteCheck.cs`, `SchemaValidation.cs` + tests -- step 4, step-5 targets, read-back comparison, schema gate per Q3; fixtures under `Fixtures/synthetic/`.
- [x] `WriteOperation.cs`, `WritePipeline.cs` + `CreatePageToolTests` -- abandoned-write check after step 2; read-back hook; read-back failure and deadline never fail the write.
- [x] `ReadAccess.cs` -- non-prompting grant check.
- [x] `src/OneNoteMcp.Core/Writes/PageWrite.cs`, `UseCases/UpdatePageContentUseCase.cs`, `ToolWarnings.cs` -- the operation and use case.
- [x] `ArgumentCheck.cs`, `UpdatePageContentTool.cs`, `ServerSetup.cs` -- the tool; nested-object argument check.
- [x] `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` -- every matrix row against `FakeGateway`, plus lock released on every outcome, cache entry dropped, journal entry written (temp journal), protected-without-grant messages carry no `objectID`.
- [x] Contract snapshot, binding count, harness and `NavigateToToolTests` write lists, `plugin/skills/guide/SKILL.md` and `README.md` tool rows (call only when the user asks to change a page).
- [x] `tests/OneNoteMcp.Integration/UpdatePageContentIntegrationTests.cs` -- opt-in: fresh edit lands, stale basis on an undisplayed page → `StaleWrite`, journal entry per write, merged-table probe (Q6). Not run here.
- [x] `deferred-work.md` -- resolve l.208, l.211, l.244, l.253 (2.9 half), the 2.7 forward note; add the spine sync (AD-16 settled, AD-7 move rule, AD-17 title OE, per answers).

**Acceptance Criteria:**
- Given any refusal in steps 3–5, when the pipeline ends, then `FakeGateway.UpdateCalls` is empty, no journal entry exists, and the lock is free.
- Given a write that succeeded and a read-back that throws, when the result is built, then it is `ok: true` with `readBackFailed`.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass; `dotnet test tests/OneNoteMcp.Integration` without the variable passes or skips.

## Implementation Notes

- Step 4's check order: schema gate, root `one:Page@ID`, unknown `objectID`s, an `objectID` on another kind of element (`InvalidArgument`, `WrongElementUserAction`), moved OEs, removals, whole Outlines. Moves come before removals so a moved OE that is also named for removal gets the move `userAction`.
- Step 3 reads the page's own node (`GetHierarchy` at `self`) for "not a page" and its `isInRecycleBin`, then the section (recycle bin, locked) before the piBasic read; a locked section is refused without reading the page.
- The read-grant check for messages is `ReadAccess.HoldsReadGrant(section, callStart)`, with `callStart` taken when the use case starts (`ReadAccess.Now()`).
- The read-back compares numbers by value and `true`/`1`, `false`/`0` by meaning; a basis date that isn't a date is `InvalidArgument`; a null `elementHashes` value counts as missing. Recorded in `deferred-work.md` for 5.2.
- `ServerSetup.Close` also closes the input schema of an object argument that declares its members (`basis`), and `ArgumentCheck.Mismatch` refuses an undeclared member there; dictionaries (`elementHashes`) stay open. Contract snapshot: only `update_page_content` added.
- The three existing 2.7 abandoned-write tests in `CreatePageToolTests` now start the abandoned write during step 7 (the check after step 2 would otherwise refuse them first); `AFailureBeforeStep7_…` became `AnAbandonedWriteOutstanding_AfterStep2_IsComBusy_…`.
- Integration tests compile and skip without `ONENOTE_MCP_INTEGRATION`; they weren't run (Never list).
- After review pass 1's patches (lane, 2026-11-12): step 3 now walks to the section first (recycle bin, locked, read grant), then the page's own node, then piBasic, as Always says; the move refusal covers every sent element with an `objectID` except Outlines; the read-back pairs new Outlines with the after-page Outlines that step 3 didn't read and the call didn't send, in document order, comparing the first min(count) pairs; `InvalidXml` names `one:<element>` only in the 2013 namespace. Verification: build `-warnaserror` 0/0; unit 1840/1840; integration without the variable 154 passed, 44 skipped; stress harness builds.

## Plan Change Log

## Review Triage Log

### 2026-11-12 — Review pass 1 (quick lens)
- verdicts: 7 findings — high 0, medium 2, low 4, false 0, maybe-false 1
- findings:
  - `[medium]` `[patch]` `plugin/skills/guide/SKILL.md:18` still told Claude to refuse any edit, contradicting the new tool row and intro — reworded: page edits go through `update_page_content` when the user asks; move, rename and delete stay unavailable.
  - `[low]` `[patch]` `PageWriteCheck.Validate`: the move refusal (Q4) looked only at OEs, so an existing Table/Row/Cell sent into another Outline passed step 4 and step 5 — the same-container test now covers every sent element with an `objectID` except Outlines; test added. Rare in use, but the fix is a selector change.
  - `[medium]` `[patch]` `PageWriteCheck.CompareReadBack`: new Outlines (sent without an `objectID`) were never read back, so `imageLost` and ignored attributes on added content went unreported (AD-7 "an image in a sent Outline") — step 3's XML is passed in; new sent Outlines pair in document order with the after-page Outlines that are neither read in step 3 nor sent, and are compared like matched ones; tests added. Routed patch rather than bad_plan: the plan's "each sent Outline" already covers it, and the fix is local to `PageWriteCheck` (an optional argument, no contract change).
  - `[maybe-false]` `[patch]` `PageWrite.ReadAsync` read the page's node before the section, against the plan's "section first"; if OneNote doesn't list a locked section's pages, the caller would get `ObjectNotFound` instead of `SectionLocked`. Unverified live (step 1's `GetHierarchyParent` chain may fail first either way); reordered to match the plan; the live sitting can confirm.
  - `[low]` `[reject]` a page with no `lastModifiedTime` and no basis date takes a snapshot and then fails at step 7 with the gateway's `InvalidArgument` — every page read so far carries the attribute (fixtures, spike 2.2); handling it would add a guard for a state not shown reachable, and the gateway already refuses loudly.
  - `[low]` `[patch]` `SchemaValidation.Problems`/`SchemaPlaces`: the `InvalidXml` message prefixed every element with `one:`, even one in another namespace — `one:` only for the 2013 namespace, otherwise the local name and "(not in the OneNote 2013 namespace)".
  - `[low]` `[defer]` `ComGatewayLifecycleTests.AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked` failed once in a full run (expected 0, actual 1) and passed on reruns — pre-existing timing test, `ComGateway` not in this diff; logged in deferred-work.

## Design Notes

- Positional read-back: inside a matched sent Outline, OE lists are walked in order at each level. A sent OE without an `objectID` is new, so only its sent attributes are compared; a sent ID that comes back different is `readBackDiffers` with both IDs. A count mismatch is one `readBackDiffers` on the Outline.
- Style exemption: a sent OE `style` absent after the write is exempt when each declaration (`font-family`, `font-size` in pt, `color`) equals the read-back QuickStyleDef for the OE's `quickStyleIndex`; any other declaration isn't exempt. `&nbsp;` forms need no rule: text isn't compared.
- Assumptions (not questions): the abandoned-write check applies to `create_page` too (AD-3 says "new non-read pipelines"); same code, earlier.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet test tests/OneNoteMcp.Integration` (no `ONENOTE_MCP_INTEGRATION`) -- pass or skip.
- `dotnet build spikes/stress-harness -c Release` -- builds.

**Manual checks (live sitting with Phill, not run by the build):**
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter UpdatePageContentIntegrationTests` on MCP Test: a fresh edit lands; a stale basis on a page that isn't displayed returns `StaleWrite`; each write leaves a journal entry; the merged-table probe records whether the merge survives (Q6).
- From Claude Code, Phill asks for one edit on an MCP Test page: it lands, `meta.basis` (Q2) is returned, and a second edit with it succeeds.
- A `quickStyleIndex` change on an existing OE returns `readBackDiffers`.
- An edit in MCP Test's protected section with only the write grant: no dialog, `ok`, and a refusal names no `objectID`.
- Stress harness on MCP Test: 0 mismatches (the hash change touches read bases).
