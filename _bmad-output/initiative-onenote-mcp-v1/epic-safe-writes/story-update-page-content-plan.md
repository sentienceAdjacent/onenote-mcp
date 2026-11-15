---
title: 'update_page_content'
type: 'feature'
ticket: '9'
created: '2026-11-12'
status: done
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

### 2026-11-12 — Review pass 2 (quick lens, the security-review delta since `7d172f4`)
- verdicts: 6 findings — high 0, medium 2, low 2, false 0, maybe-false 2
- findings:
  - `[low]` `[patch]` `WritePipeline` step-1 loop: a path that became new on step 1's rerun after the dialog (the page changed while the user decided) escaped as the no-dialog refusal (`retryable: false`, `NoDialogUserAction`) — after a confirmation, a newly new path is now `WriteNotGranted`, retryable, with `ChangedUserAction`; test added.
  - `[medium]` `[patch]` `FilePathGate`: a path with control characters, line or paragraph separators, bidi or zero-width formatting characters, or `< > " | ? *` reached the dialog verbatim, so the model could add lines to it or disguise a path — refused as `InvalidArgument` (`FilePathRefusal.InvalidCharacters`) before canonicalisation; tests added.
  - `[maybe-false]` `[defer]` `pathCache` gated by exact value: if it churns between reads (story 1.17 saw it; spike 2.2 didn't in 20 reads), resending an Outline with an attachment unchanged raises the dialog on a cache path, and fails on a client without elicitation. Would be medium; the follow-up live check ("resending … raises no dialog") settles it. Logged in deferred-work.
  - `[maybe-false]` `[defer]` `one:HTMLBlock` `one:Data` (an HTML string) is never scanned: if OneNote's HTML import fetches a local `src`, that copies a file into the notebook without the dialog. Would be high; needs a live probe (an `HTMLBlock` with `<img src="file:///…">` on MCP Test) or a refusal of `HTMLBlock` data. Logged in deferred-work and raised with the team lead.
  - `[low]` `[patch]` `FilePathGate.ResolveLinks` kept resolving segments under a link that led to a UNC or device path before refusing it, touching the network host — it now stops at that link and the result is refused.
  - `[low]` `[patch]` cosmetic: `var self =await` spacing in `PageWrite.ReadAsync`; an invisible U+FEFF literal in `OneNoteXml.HasDoctype` (now a named constant).

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

**Live sitting (2026-11-12, team lead with Phill, the build at `7d172f4`): all passed.**
- `UpdatePageContentIntegrationTests` on MCP Test, 3/3: a fresh edit plus a chained `meta.basis` edit; a stale basis gives `StaleWrite`; one journal entry per write. Merged-table probe: "RESULT merge kept: AD-4 needs no amendment" (Q6).
- From Phill's dev session: an edit, then a second edit from the returned `meta.basis`, both ok. A `quickStyleIndex` change was ok with `readBackDiffers`.
- In Ballast Vault (unlocked, no read grant): `create_page`, then `update_page_content` adding a new Outline, was ok with `noBasis` and no dialog.
- Stress harness on MCP Test (2.9 build): 675 comparisons, 0 mismatches.
- Observations: the page-level `lastModifiedTime` didn't advance on a page that wasn't displayed (the known quirk), but hash chaining worked. In the protected no-read-grant case the result had no `meta.basis` and no read-back warning at all; follow-up item 4 below adds `readBackWithheld`.

**Follow-up checks for the security-review build (live, with Phill, not run here):**
- File-path dialog from Claude Code on an MCP Test page: an `InsertedFile` with a new local `pathSource` raises one accept/decline dialog naming the canonical path; accept writes it, decline gives `WriteNotGranted` (retryable) with nothing written. A UNC path is `InvalidArgument` with no dialog. Resending the page with that file unchanged raises no dialog.
- In Ballast Vault without a read grant: a write carrying an `elementHashes` entry, or an existing `objectID`, gives `ReadNotGranted` with no dialog; a new Outline with a date-only basis is ok with `readBackWithheld` and no `meta.basis`.
- HTML probe: `ONENOTE_MCP_INTEGRATION=1 ONENOTE_MCP_SCENARIO=html-local-ref dotnet test tests/OneNoteMcp.Integration --filter HtmlLocalRefProbe`; record both `RESULT html-local-ref` lines. `not-fetched` for both lets a follow-up lift the HTML stopgap; `fetched` keeps it and goes into AD-7.

**Joint 2.9 + 2.10 live sitting (2026-11-12 evening, dev session with Phill, the 2.10 build at `b4e5a7c`, which contains 2.9's allowlist): all MCP Test checks passed; the MCP Test Cloud steps were deferred.**
- HTML probe: `htmlblock` **fetched** (OneNote reads a local `file:` image in an `HTMLBlock`), so the HTMLBlock refusal stays and becomes an AD-7 rule; `t-cdata` **not-fetched** (OneNote refuses `<img>` in a `one:T` with 0x80042009).
- A (Ballast Vault, no read grant): a made-up `objectID` (A1a) or a basis with an `elementHashes` entry (A1b) is `ReadNotGranted`, no dialog, nothing written. A new Outline with no basis (A2) is ok with `noBasis` + `readBackWithheld` and no `meta.basis`. The date-only-basis variant couldn't be run: without a read grant nothing returns the page's `lastModifiedTime` (deferred-work).
- B (MCP Test): a new `InsertedFile` raised one dialog; decline → `WriteNotGranted` (retryable), nothing written (B1); accept → ok (B2). Resending it unchanged (B3) raised the dialog again: OneNote names a new `pathCache` temp copy on every read, and its `InsertedFile` has no `objectID`, so both paths counted as new. Fixed after the sitting (follow-up below). A UNC `pathSource` is `InvalidArgument`, no dialog (B4).
- C: a bold `span` and an https link are ok (OneNote stores a CRLF inside the span tag) (C1); `<img src="https://…">` is `InvalidArgument`, nothing written (C2); a one-word edit of OneNote-formatted text is ok and the read-back matches (C3). C3's side effect: resending an Outline's own `z` re-stacks the Outlines and changes the other Outline's hash (deferred-work).
- Stale date on a displayed page: `StaleWrite` from the server's own date check (before OneNote is called), `retryable: false`, nothing written.
- Wording fixed after the sitting: the tool description's link rule now reads as an allowlist (`54be299`); the path userActions say "for an image" for inline `one:Data`; `partialBasis` says "its" for one ID.

## Follow-up: story 2.9 security review (Phill, 2026-11-12; spine wording by story 5.2, commit `9a33dfa`, refined by the 5.2 Fable re-check)

Implemented after the live sitting, as decided by Phill and relayed by the team lead; this section records the scope, not a change to the frozen intent.
1. **DOCTYPE (AD-16):** sent XML with a DOCTYPE is `InvalidXml`, first thing in step 1 and again in the step-4 gate; every parse of OneNote XML (`OneNoteXml.Load`, `SchemaValidation.Problems`, the scans) prohibits DTD processing with no resolver. Broken XML is also refused in step 1 (position only).
2. **Date-only basis without a read grant (AD-17):** in a protected section with no read grant at the call's start (never prompting), any element hash (`pageHash` included), any `objectID` attribute in the XML, or any `removeObjectIds` entry is `ReadNotGranted` before the page is read, with `ReadFirstUserAction`. Checked in step 1 and again in step 3 before the page read.
3. **User-gated file paths (AD-7):** a path is any `path` or `pathSource` attribute on any element, which covers `one:File@path` under each of its five XSD parents (`one:Image`, `one:InkDrawing`, `one:InkWord`, `one:XPSFile`, `one:HTMLBlock`), and `pathCache` on `one:InsertedFile`/`one:MediaFile` (gated, not stripped). It is exempt only when sent unchanged, with the same attribute, on the same existing element (same owner `objectID`; a `one:File`'s owner is its parent); every path on an element without an `objectID` is new, and without a read grant every path is new. New paths are made canonical (`GetFullPath`, links and junctions resolved); network (UNC, mapped network drive, or a link to one), device and anything not fully qualified (relative, drive-relative, URLs) are `InvalidArgument` with no dialog. The rest go to one accept/decline elicitation dialog naming each canonical path (nothing to type; AD-6's Limits apply, so a client-side hook could answer it). A client without elicitation, or no prompt, is `WriteNotGranted` with `retryable: false` and `NoDialogUserAction`; a decline or cancel is `WriteNotGranted`, retryable. Step 4 refuses any new path not confirmed in step 1 (`WriteNotGranted`, retryable).
4. **`readBackWithheld`:** in the protected no-read-grant case the read-back adds one count-free `readBackWithheld` warning (with any counts-only `readBackDiffers`/`imageLost`), and no `meta.basis`.
- **Link resolution, after the security review of `0f38576` (team lead, 2026-11-12):** links and junctions are followed one hop at a time: each link's own immediate target is read (`FileSystemInfo.LinkTarget`, never followed), made absolute against the link's folder (`\??\` and `\??\UNC\` forms normalised), and classified before anything touches it; the first network or device hop refuses the path, at most `MaxLinkHops` (8) hops, a cycle included (`TooManyLinks`, `InvalidArgument`). A followed target is walked again from its own root. After the security review of `3946c72`: a target that is rooted but not fully qualified (`\foo`, an NT-namespace path for a reparse point, `/foo`, or drive-relative `X:foo`) is never resolved against the process's drive and folder; it is refused (`NotFullyQualified`, `InvalidArgument`). Only a truly relative target (`..\x`) is resolved against the link's own folder. Tests drive it through a fake link reader that records every path asked (local → local → UNC refused without asking about the UNC hop; device hop; relative and NT-prefixed targets; the cap; a cycle) and a real junction.
- **HTML stopgap (Phill, 2026-11-12: probe it in the next sitting, refuse meanwhile):** a new or changed `one:HTMLBlock` is `InvalidArgument` (`HtmlBlockUserAction`: send text or inline `one:Data`); a new or changed `one:T` whose HTML the stopgap refuses is `InvalidArgument` (`RefusedHtmlUserAction`: link only to web, mail or OneNote URLs; use `one:InsertedFile` with its path dialog, or inline data). After the automated security review of `29f787b` found the first version (a `file:`/drive/`\\`/`//` deny-list on `src`/`href`) fragile, the scan (`HtmlStopgap`) is deny-by-default: at every stage of entity decoding (numeric and legacy references without a semicolon included), the text is refused for a `file:` URL; a resource-loading tag (img, picture, source, video, audio, object, embed, iframe, frame, link, style, script, base, meta, svg, math, form, input and the like) whatever its URL; any URL-bearing attribute (src, href, background, data, poster, lowsrc, dynsrc, srcset, action, formaction, cite, ping, longdesc, usemap and the rest) whose value, after removing tabs and newlines and trimming controls, isn't an absolute `http(s)://` or `mailto:` URL, or an `onenote:` link within the page (`onenote:#…`) or to a web notebook (`onenote:http(s)://…`; after a second automated review, never a local, relative or network notebook path) (relative, fragment and empty values included); or a `style` attribute with a backslash, an at-rule, `expression`, or any CSS function but `rgb`/`rgba`/`hsl`/`hsla` (so no `url()`, even to the web). Tags are found by an HTML5-style tokenizer (quotes, comments, `/` separators) and by a plain regex scan, and both count. All 62 `one:T` texts in the committed fixtures pass. Unchanged ones on the same existing element (same owner `objectID`, same content) are allowed, so other edits to such a page still work; without a read grant everything counts. Checked in step 1 before the path dialog, and again in step 4. Messages give counts only.
- **HTML stopgap, now an allowlist (Phill, 2026-11-12):** five automated security findings on the `onenote:` checks (local paths, `base-path=`, decoding depth, host and path forms) and a bypass review of `e0ba25b` showed that deny-lists of tags and attributes keep leaking: a comment opened inside `<textarea>` hid a tag, HTML5-only entities (`&lpar;`) and zero-padded numeric references slipped a `url(` through, and backtick quoting and `<?import` defeated both scans. Phill chose: (1) tags and attributes are an allowlist of OneNote's own formatting (a, b, br, cite, code, del, em, font, i, ins, p, s, small, span, strike, strong, sub, sup, u; style, lang, dir on all, href on a, face/size/color on font), and `<!`, `<?`, a backtick in any value, or an unknown entity left after the last decoding stage refuse the text; (2) `onenote:` web-notebook links only to OneDrive (`d.docs.live.net`) and SharePoint (`*.sharepoint.com`), plus in-page `onenote:#` links with GUID parameters; (3) the strict multi-stage decoding stays until the probe shows whether OneNote decodes twice, so text that mentions `file:` or shows HTML as escaped text is refused when changed. Text still changing after five decoding rounds is refused. All 62 fixture texts pass. `RefusedHtmlUserAction` lists what's allowed.
- **Probe:** `tests/OneNoteMcp.Integration/HtmlLocalRefProbe.cs`, opt-in with `ONENOTE_MCP_INTEGRATION=1` and `ONENOTE_MCP_SCENARIO=html-local-ref`, raw seam behind `TestNotebook`'s guard, fresh pages in MCP Test's `Probes` section: (a) an `HTMLBlock` with `<img src="file:///…png">` and a link to a marker text file, (b) a `one:T` with `<img src="file:///…png">`; reads back with piBinaryData and prints `RESULT html-local-ref <case>: fetched|not-fetched`. Not run here.
- **Attachments resent unchanged (Phill, 2026-11-12, after the live sitting's B3):** a `one:InsertedFile` or `one:MediaFile` without an `objectID` (as OneNote writes them) belongs to the OE holding it, so its `pathSource` sent unchanged on that same OE isn't new. The server remembers every `pathCache` its `get_page_content` reads returned (`IssuedPathCaches`, per page, owner and element; in memory, at most 4096, oldest forgotten first); a remembered one isn't new while that OE still holds the same kind of file element. Only `pathCache` is remembered. A new, moved or changed attachment still raises the dialog. Tests: `PageWriteSecurityTests` (OE owner, issued `pathCache`), `IssuedPathCachesTests`, `UpdatePageContentToolTests` (read → OneNote renames the temp copy → resend needs no dialog; an unread temp name still asks). Needs a live re-check of B3 in the next sitting.
- **Step-1 order:** parse with DTDs prohibited → the no-prompt read-grant check → the write grant (and its dialog) → the file paths (and their dialog), so the user is never asked anything for a call that would fail anyway.
- Files: `src/OneNoteMcp.Xml/OneNoteXml.cs` (`Load`, `HasDoctype`), `SchemaValidation.cs` (`SchemaProblem.IsDoctype`), new `PageWriteScan.cs`; `src/OneNoteMcp.Core/Writes/FilePathGate.cs`, `PageWrite.cs`, `WritePipeline.cs` (step-1 dialog loop); `src/OneNoteMcp.Core/Access/FilePathDialog.cs` (`FilePathsUnconfirmed`, `FilePathDialog`), `GrantPrompt.cs` (`AskFilePathsAsync`, `FilePathQuestion`); `src/OneNoteMcp.Core/Results/ToolWarnings.cs`; `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs`; `UpdatePageContentTool.cs` (description). Tests: `PageWriteSecurityTests.cs` (new), `UpdatePageContentToolTests.cs`, `ProtectedSectionKit.cs` (`FakePrompt` path dialog).
