---
title: 'update_page_content'
type: 'feature'
ticket: '9'
created: '2026-11-12'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
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

</frozen-after-approval>

## Open Questions

1. **Tool arguments.** (a) `update_page_content(pageId, xml, basis?, removeObjectIds?)`: the root `ID` must equal `pageId`; naming an OE for removal covers its nested OEs. (b) No `pageId`: the page is the XML root's `ID`. (c) As (a), but every nested OE of a removed OE must be named too. **Recommended: a** (every other tool takes the ID; step 1, logs and the cache key on it; (c) adds a refusal with no safety gain, since nested OEs can't outlive their parent).
2. **A fresh basis in the result.** (a) `meta.basis` from the read-back's piBasic read, so the model can make a second edit without a re-read; omitted when the read-back fails and in a protected section without a read grant (AD-21 gives such a write no basis). (b) No basis; the model re-reads before the next edit. **Recommended: a** (taken under the lock right after the write, so it's exact; the date stays OneNote's own string).
3. **The XSD gate (AD-16: "Story 2.9 settles it").** (a) Hard gate: validate against the vendored 2013 XSD in step 4; `InvalidXml` names line, position and the element's name, never the validator's text (it can quote content). Evidence: every captured fixture validates (`XmlModelTests.EveryXmlFixture_ValidatesAgainstTheXsd`); story 1.6's partial shapes validate and OneNote accepted five of them (research row 9); the live sitting confirms with real edits. (b) Stand-in: well-formed, 2013 namespace, `objectID` pattern only; OneNote's `0x80042001` catches the rest. **Recommended: a**; the next spine story records AD-16 as settled. Risk: XSD stricter than OneNote on some real page; the live check on MCP Test is the guard.
4. **An OE sent under a different Outline than step 3 read it in (a move).** (a) Refuse with `InvalidArgument`; the `userAction` says to send it without its `objectID` in the new Outline and name it for removal from the old one. (b) Allow it; the read-back reports the re-created OE (`readBackDiffers`, old and new IDs), as AD-7 anticipates. **Recommended: a**: spike 2.2 only probed both Outlines being sent, so what OneNote does when the source Outline isn't sent is unknown; refusing is deterministic and loses nothing, since the ID changes either way. Adds one AD-7 validation rule (spine sync later).
5. **The title's OE.** AD-17 compares only the hashes of sent Outlines, so an edit to the title (`one:Title/one:OE`, no Outline) has only the date check, which can't catch a second writer on a page that isn't displayed. (a) Also compare the basis hash of each sent OE with an `objectID` outside any sent Outline, with the same partial-basis rule; stricter than AD-17's text, recorded by the next spine story. (b) As AD-17 says. **Recommended: a.**
6. **Editing inside a merged table (spine Deferred: "Story 2.9 probes an edit inside one on MCP Test before relying on it; if the merge is lost, amend AD-4").** (a) No special handling in code; an opt-in integration probe (merged table written through the raw seam in `xs2026` as spike 2.2 did, a cell edited through `update_page_content`, re-read in `xs2026`) runs in the live sitting; if the merge is lost, a follow-up story amends AD-4 and adds a refusal or warning. (b) Refuse every sent Outline holding a table until the probe runs. **Recommended: a** ((b) blocks all table edits for a case 2013 XML can't even show).

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
- [ ] `src/OneNoteMcp.Xml/ElementHash.cs` + `XmlModelTests` -- the two AD-17 exclusions; a locked width stays in the hash.
- [ ] `src/OneNoteMcp.Xml/PageWriteCheck.cs`, `SchemaValidation.cs` + tests -- step 4, step-5 targets, read-back comparison, schema gate per Q3; fixtures under `Fixtures/synthetic/`.
- [ ] `WriteOperation.cs`, `WritePipeline.cs` + `CreatePageToolTests` -- abandoned-write check after step 2; read-back hook; read-back failure and deadline never fail the write.
- [ ] `ReadAccess.cs` -- non-prompting grant check.
- [ ] `src/OneNoteMcp.Core/Writes/PageWrite.cs`, `UseCases/UpdatePageContentUseCase.cs`, `ToolWarnings.cs` -- the operation and use case.
- [ ] `ArgumentCheck.cs`, `UpdatePageContentTool.cs`, `ServerSetup.cs` -- the tool; nested-object argument check.
- [ ] `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` -- every matrix row against `FakeGateway`, plus lock released on every outcome, cache entry dropped, journal entry written (temp journal), protected-without-grant messages carry no `objectID`.
- [ ] Contract snapshot, binding count, harness and `NavigateToToolTests` write lists, `plugin/skills/guide/SKILL.md` and `README.md` tool rows (call only when the user asks to change a page).
- [ ] `tests/OneNoteMcp.Integration/UpdatePageContentIntegrationTests.cs` -- opt-in: fresh edit lands, stale basis on an undisplayed page → `StaleWrite`, journal entry per write, merged-table probe (Q6). Not run here.
- [ ] `deferred-work.md` -- resolve l.208, l.211, l.244, l.253 (2.9 half), the 2.7 forward note; add the spine sync (AD-16 settled, AD-7 move rule, AD-17 title OE, per answers).

**Acceptance Criteria:**
- Given any refusal in steps 3–5, when the pipeline ends, then `FakeGateway.UpdateCalls` is empty, no journal entry exists, and the lock is free.
- Given a write that succeeded and a read-back that throws, when the result is built, then it is `ok: true` with `readBackFailed`.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass; `dotnet test tests/OneNoteMcp.Integration` without the variable passes or skips.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
