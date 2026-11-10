---
title: 'Opening refactor'
type: 'refactor'
ticket: '1'
created: '2026-11-09'
status: 'built'
baseline_revision: 'ee7597fa6d94011ef96410cb2114af5de82f5a54'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick', 'security']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/deferred-work.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release-retrospective.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The epic 1 retrospective found duplicated and oversized code exactly where epic 2's write code will land beside it: D1–D5, P2, P3, S2, S3 and A3 in `deferred-work.md`'s "epic 2 first refactor" group. Write grants, a second settings writer path and new XML reads would copy each duplicate once more.

**Approach:** One behaviour-neutral cleanup pass: deduplicate, split `ReadAccess.cs` and `SettingsStore.Validate`, move the grant CLI's Xml call behind Core, and add an `.editorconfig` naming rule. The single behaviour change is P3: the XPSFile probe stops swallowing `Timeout` and `Cancelled`.

## Boundaries & Constraints

**Always:**
- Error codes, tool contracts, the contract snapshot, settings file format and every COM call sequence stay the same. No `<Version>` bump.
- Grant decisions don't change: who can read what, `once` windows, expiry and `grantedAt`/`expiresAt` timestamps (the CLI keeps taking `now` before the hours prompt).
- The parent walks stay lazy: the same gateway calls in the same order, each walk keeps its own stop rule and its own depth-overflow handling.
- No lock is held and no gateway call is in flight while the grant dialog is open.
- Bug 1.22's behaviour stays: `WithholdAll`, `WholeResult`, `ResultGrantCheck.Overlaps` failing closed, `retryable: false` only for an expired `hours` grant.
- `SettingsStore.Validate`'s field order stays (the first failing field wins; `protectedSections` last; the duplicate-only snapshot uses defaults and omits readGrants).

**Never:** R4 (purge mutex), unknown-argument rejection, or anything that adds write behaviour; those are entries 5 and 6. No changes to the spine.

## Decisions (Phill, 2026-11-09, plan review)

- **D4, exact duplicate keys:** keep today's behaviour, with the difference explicit in `SettingsJson.Options(allowDuplicateProperties)`. A `deferred-work.md` entry routes the fix to entry 6 (which owns revoke) and the rule to entry 3's spine amendments. The latent bug: an exact duplicate inside `protectedSections` is "duplicate-only" to the store, so revoke runs, but the writer's load forbids duplicates and throws `SettingsInvalid`.
- **P2:** `camelCase` for private fields across the whole repo (src and tests). Rename the Com and test `_x` fields.
- **D2:** `list_grants`' InvalidXml message takes the common text. Code unchanged.
- **Size:** keep the full plan. The P2 renames are their own last commit, so the security review can read the `ReadAccess` split without them.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Xml/{LockedSections,SearchResultFilter,EncryptedSections}.cs` (`:20`, `:160`, `:55`) -- three identical `IsTrue` (`value is "true" or "1"`); `HierarchyNode.cs:41` calls the EncryptedSections one.
- `src/OneNoteMcp.Xml/OneNoteXml.cs` -- internal `Parse`, `RequireRoot`; home for `IsTrue`.
- InvalidXml mapping sites -- `GetHierarchyUseCase.cs:84`, `ReadAccess.cs:474`, `GrantCommand.cs:234`, `SearchResults.cs:71`, `GetPageContentUseCase.cs:77`, `FindPagesUseCase.cs:159`, `ListGrantsUseCase.cs:90` (calls `XDocument.Parse` directly, catches `XmlException`, message `"... (line N)."`). Leave `LargeResults/ResultDelivery.cs:85` (turns it into a warning).
- `GetHierarchyToolTests.cs:210` -- the only test asserting the message text (StartsWith).
- `NavigateToUseCase.cs:15`, `GetBinaryPageContentUseCase.cs:29,32` -- `ObjectDoesNotExist` 0x80042014 twice, `BinaryObjectDoesNotExist` 0x8004200F; `Com/HResultMap.cs:35,97` (internal).
- `GetBinaryPageContentUseCase.cs:89-96` -- `IsXpsFileAsync` catches every `OneNoteException` → false → line 72 rethrows the original ObjectNotFound.
- `PageContentToolTests.cs:255-353, 472` -- XPSFile tests; `ScriptedGateway` has only `binaryFailure`.
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs:5,147-177,193,227-237,265` -- the only Server file importing Xml; builds its own `ProtectedSectionGrant`; `Describe` renders `NB > G1 > Name`.
- `src/OneNoteMcp.Core/Access/ReadAccess.cs` (551 lines) -- `TargetAccess`/`BroadAccess` 14-60; walks `OwningSectionAsync` 267-293 and `LocationAsync` 435-461; once windows 307-331 (`windows` + `windowGate`); `AskAsync` 333-405; `Save` 408-432; grant built at 324 and 388-395; `ResultGrantCheck` 487-551.
- `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs` -- dialog location rendering; leave as is.
- `src/OneNoteMcp.Core/Settings/SettingsStore.cs` -- `Validate` 93-229 by area: schemaVersion 100-119, allowSilentActivation 121, timeouts 132, journal 143, results 153, search 163, readGrants 171, exportRoots 179, protectedSections 198-210, snapshots 212-228; `JsonDocument.Parse` 71 (duplicates allowed, last wins); `expiresAt` parse 277.
- `src/OneNoteMcp.Core/Settings/SettingsWriter.cs:153-163,176` -- `JsonNode.Parse` with `AllowDuplicateProperties = false`; the same `expiresAt` parse.
- `Directory.Build.props` -- `TreatWarningsAsErrors`, `AnalysisLevel=latest`, no `EnforceCodeStyleInBuild`; no `.editorconfig` anywhere.
- Fields: `_x` in Com (ComGateway 12, StaThread 11) and ~41 test fields; `x` in Core, Server, Xml (~44) and ~17 test fields.
- Safety net -- `ProtectedSectionToolTests` (~53, with `ProtectedSectionKit`), `GrantCommandTests` (17), `ProtectedSectionXmlTests` (13), `SettingsStoreTests` (31), `ReadGrantSettingsTests` (18), `ToolErrorsTests`; integration `ProtectedSectionIntegrationTests`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Xml/OneNoteXml.cs` + the three callers -- D1: one internal `OneNoteXml.IsTrue`, the copies deleted.
- [ ] `src/OneNoteMcp.Core/XmlReads.cs` (new) + the seven sites -- D2: one internal `Readable<T>(Func<T> read, string what)` producing `"{what} returned XML the server can't read: {msg}"`, never attaching the inner exception. ListGrants goes through `OneNoteXml.Parse` via a public Xml entry point it needs (keep the rest of Xml internal).
- [ ] `src/OneNoteMcp.Core/OneNoteHResults.cs` (new) -- D5: public `ObjectDoesNotExist`, `BinaryObjectDoesNotExist`; both use cases use them. `HResultMap` keeps its name table.
- [ ] `src/OneNoteMcp.Core/UseCases/GetBinaryPageContentUseCase.cs` -- P3: the probe still treats a failed page read as "not an XPSFile", except `Timeout` and `Cancelled`, which propagate.
- [ ] `tests/OneNoteMcp.Tests/PageContentToolTests.cs` -- P3: `ScriptedGateway` gains `pageFailure`; tests with binary ObjectNotFound (0x80042014) plus page Timeout → Timeout, plus page Cancelled → Cancelled. Run them on the old code first and record that they fail.
- [ ] `src/OneNoteMcp.Core/Access/` -- S2/D3: split `ReadAccess.cs` into `AccessDecisions.cs` (`TargetAccess`, `BroadAccess`), `HierarchyWalk.cs` (internal: one lazy parent iterator, `OwningSectionAsync`, `LocationAsync`, each keeping its own stop and overflow rule), `OnceWindows.cs` (internal: the dictionary and its lock together), `GrantDialog.cs` (internal: `AskAsync`, `Save`), `ResultGrantCheck.cs`, and what remains in `ReadAccess.cs`. Add one `ProtectedSectionGrant` factory taking `now` and `via`, used by the once window, the dialog and the CLI. Public surface used by tools and Server stays.
- [ ] `src/OneNoteMcp.Core/Access/ProtectedSections.cs` (new) + `GrantCommand.cs` -- A3: a public Core `Find(string hierarchyXml)` returning a Core record (notebook name, notebook path, groups, section name, key, locked) through `Readable`; GrantCommand calls it, drops its catch and its `using OneNoteMcp.Xml`. Its `Describe` output stays byte-identical.
- [ ] `src/OneNoteMcp.Core/Settings/SettingsJson.cs` (new) + store + writer -- D4: shared `TryParseUtc` for `expiresAt` and `Options(bool allowDuplicateProperties)`; the store passes true, the writer false, so today's difference stays visible.
- [ ] `src/OneNoteMcp.Core/Settings/SettingsStore.cs` -- S3: `Validate` becomes one private static per area in the current order, with `ReadProtectedSections(root, out bool duplicate)`.
- [ ] `.editorconfig` (new) + `Directory.Build.props` + renames -- P2: naming rule for private fields at `warning`, `EnforceCodeStyleInBuild=true`, the minority renamed as decided below. Add nothing else to `.editorconfig` that fires in the build.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- mark D1–D5, P2, P3, S2, S3, A3 resolved with evidence; route the open question below's outcome.

**Acceptance Criteria:**
- Given the unchanged suites, when run after the refactor, then they all pass with no test edited except the renames, the `GetHierarchyToolTests.cs:210` text if it changes, and the new P3 and P2-driven edits.
- Given `ScriptedGateway` with a binary ObjectNotFound and a page Timeout, when `get_binary_page_content` runs on the old code, then it returns ObjectNotFound; on the new code, then it returns Timeout.
- Given `grep -rn "using OneNoteMcp.Xml" src/OneNoteMcp.Server`, when run, then it finds nothing.
- Given a field named against the rule, when `dotnet build -warnaserror` runs, then it fails with IDE1006.

## Implementation Notes

- Implemented by a subagent in five commits (7c35137, ad1136a, ab1ce32, d3c453f, 45ea6de), with P2's renames last. `.editorconfig` also carries `dotnet_diagnostic.IDE1006.severity = warning`: without it the command-line build ignores the rule's severity. It is the same rule, not a new one.
- Message text: besides `list_grants` (Phill's decision), the page-read InvalidXml message now reads "GetPageContent for page {id} returned XML the server can't read: …" (was "GetPageContent returned XML for page {id} that the server can't read: …"). That follows from the plan's single `"{what} returned …"` format. The code is unchanged, and no test asserted the old text.
- `spikes/` has its own `Directory.Build.props`, so its `_x` fields aren't renamed or checked.
- Verification by the implementer: build with 0 warnings; 1304 unit tests passed; integration 87 passed and 7 skipped; stress harness on MCP Test 620 comparisons, 0 mismatches.

## Review Triage Log

Pass 1 (quick lens plus the security review the ticket requires): 0 high, 0 medium, 4 low, 0 false, 0 maybe-false. The security review found no change in who can read what (decision paths, walks, grant fields, once windows, leaks, new public surface, renames).

| # | Lens | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|---|
| 1 | quick | P3's Cancelled case can't happen in production; the test and the deferred-work entry overstate the fix | low | patch | Nothing in Com/Core throws `OneNoteException(Cancelled)` (only `ToolRunner.cs:72`); a cancelled probe surfaces as `OperationCanceledException` (`StaThread.cs:278`, `ComGateway.cs:502`), which the old catch never caught. Patch: an `OperationCanceledException` test on the real path; reword the doc comment and the deferred-work entry; keep the filter as the plan says. |
| 2 | quick | `.editorconfig` doesn't check private static fields (`_s` builds clean) | low | rejected | No static field violates the convention today, and the plan's task said to add nothing else to `.editorconfig` that fires in the build. Fixing it adds rules. |
| 3 | quick | `EncryptedSection.SectionGroups` assumes `Location[0]` is the notebook whenever `NotebookName` is set | low | rejected | Only `EncryptedSections.From` sets `NotebookName`, and it builds `Location` to match. The only hand-built instance (`ReadAccess.cs:178`) leaves `NotebookName` null. The fix changes the public record's shape. |
| 4 | quick | deferred-work's A3 entry cites `GrantCommand.cs:220`; the method is at 219 | low | patch | Confirmed by grep. Patch: `:219`. |
| 5 | security | P3 is a behaviour change inside a refactor story | false | — | Deliberate: the plan's intent names it as the one behaviour change. |

Phill, 2026-11-09, findings review (`review/2.1-review-findings.md`): keep findings 2 and 3 rejected, and keep the new page-read message text (Implementation Notes). No further fixes; mark built.

## Design Notes

The walks share an iterator, not a result: fetching the whole chain eagerly would add COM calls and surface errors from higher ancestors. The CLI keeps computing location from the Sections XML, not the gateway walk, so its COM calls don't change. Exact duplicate keys are a spec gap (AD-21 only covers case-only ones), so this story keeps the store/writer difference explicit rather than choosing.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` -- all pass (OneNote running, MCP Test open).
- The stress harness on MCP Test (`spikes/stress-harness/README.md`) -- 0 mismatches.

**Manual checks:**
- A security review of the `ReadAccess` split before merge confirms no change in who can read what.
