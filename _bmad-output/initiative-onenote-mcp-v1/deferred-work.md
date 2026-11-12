- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-scaffold-ci-and-com-tracer-bullet-plan.md`
  summary: StaThread lets work queued after Dispose starts hang forever, because its TaskCompletionSource never completes and cancellation is checked only at dequeue.
  evidence: The _stopping check and Enqueue in StaThread.InvokeAsync aren't atomic, and Loop returns once _stopping is set. Fold into story 5's lifecycle work (per-call timeouts, poison-and-replace).

## Deferred from: code review of story-result-envelope-error-codes-operation-classes-and-tool-contr-plan (2026-11-04)

- One-line stderr log format (Program.cs AddSimpleConsole SingleLine, ToolRunner.LogCall) has no test; story 4 should add a captured-logger test of the host's logging setup.
- A tool-contract change without a <Version> major.minor bump passes the unit tests; AD-14 leaves the bump check to the PR reviewer. A tools-section hash guard is possible but bypassable.
- 0x80080005 from any COM call maps to IntegrityMismatch; revisit when story 5 builds the startup/activation integrity check.
- AD-14's "release job refuses a tag that differs from <Version>" is not in entry 1.11's description; add it when 1.11 is refined.
- No automated test drives tools/call through the MCP SDK; add an in-process client test when the second tool lands (story 7).
- Cancellation is reported as Internal ("Report this error", not retryable); story 5 decides its code and retryability, and a cancelled write must carry outcomeUnknown. Reason: story 5 owns timeouts and cancellation (Phill, 2026-11-04).
- Epic 2: the write pipeline must set outcomeUnknown on a write that fails with Timeout or ComUnavailable, including OneNote's own hrTimeOut (0x80042023); HResultMap.ToException leaves it false (AD-9).
- Epic 2: triage the write-side deliberately-Internal codes (0x8004200B, 0x8004200C, 0x80042022, 0x80042024, 0x8004202E), hrDisabledByPolicy (0x8004201C, which reads can hit) and the 23 untriaged OneNote codes (for example hrInvalidName 0x80042017, hrOpeningSection, hrLegacySection) when the write tools land (AD-9).
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-settings-read-side-and-logging-plan.md`
  summary: A `NewerSchema` settings file throws `SettingsInvalid`, whose envelope says `retryable: true` and "Fix settings.json, then retry", which is wrong advice for a file a newer server wrote.
  evidence: `ToolErrors.cs:39,62` map every `SettingsInvalid` the same way; `SettingsSnapshot.EnsureWritable` throws it for `NewerSchema` with "Update OneNoteMcp to write". Unreachable until the first write tool calls `EnsureWritable`; fix it there.
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-settings-read-side-and-logging-plan.md`
  summary: `ToolRunner.LogInternal` logs the full exception, which story 1.4 now persists to the rolling file log; once XML parsing exists, a parse exception's message could quote page content into the log (AD-13).
  evidence: No `XDocument`/`XmlReader`/`XmlException` in `src` today, so nothing leaks yet. The story that adds XML parsing must strip or replace parse-exception messages before they reach `Internal` logging.
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-gateway-lifecycle-read-side-port-and-integration-harness-plan.md`
  summary: AD-3's write-side timeout rules are not built: a timed-out write reporting `outcomeUnknown: true`, and "while an abandoned write is still outstanding, new non-read pipelines return `ComBusy`". Epic 2's first write story adds them.
  evidence: Story 1.5 puts no write methods on `IOneNoteGateway` (plan, Never). `ComGateway` counts abandoned workers but doesn't record whether the abandoned call was a write; the raw seam `ComGateway.InvokeAsync(..., retryOnDisconnect: false)` used by the integration suite's guarded writes only skips the disconnect retry. Busy (`RPC_E_CALL_REJECTED`, `RETRYLATER`) is currently retried with backoff for writes too, on the basis that a rejected call never ran; epic 2 should confirm that.
- source_plan: `plan-marketplace-repo-and-plugin-rename.md`
  summary: Nothing checks that the Claude Code validator pin (2.1.318) in sentienceAdjacent/plugins (ci.yml, sync.yml) stays equal to onenote-mcp's.
  evidence: Fable review of AD-15, finding 10. PluginPackagingTests only compares ci.yml and release.yml in this repo. The catalog's copies are kept in step by comments alone.
- source_plan: `plan-marketplace-repo-and-plugin-rename.md`
  summary: Branch protection on sentienceAdjacent/plugins main would break sync.yml's direct bot push. It would need a PR-based sync, or a bypass for github-actions.
  evidence: Fable review of AD-15, finding 13. sync.yml pushes straight to main with GITHUB_TOKEN. AD-15 now states the precondition.
- source_plan: `plan-marketplace-repo-and-plugin-rename.md`
  summary: Nothing stops a pinned release from being deleted. The catalog would point at a 404 until a newer release exists.
  evidence: Fable review of AD-15, finding 12. AD-15 now says a published release is never deleted, but that rule is by convention only, with no check or tag protection.

## Deferred from: spike 1.15 real-notebook run (2026-11-06)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-do-the-read-tools-hold-up-on-a-large-real-notebook-plan.md`
  summary: Epic 2: OneNote returns one of two `one:Table/one:Columns/one:Column/@width` value pairs, alternating on each `GetPageContent` (piBasic) of the same page, so an AD-17 basis taken from one read never matches the next read of that page (inferred from strict A/B alternation: the basis read n and the write pipeline's step-3 read n+1 always have opposite parity), and a plain read-then-write is refused as StaleWrite every time. Seen on one page of notebook A; the trigger isn't identified.
  evidence: `--diag-page` on a real work-notebook page: six back-to-back raw reads gave `A B A B A B`. Lengths were identical; the only difference was two column widths trading 1.5658 pt with the same total. `ElementHash.cs` ignore list doesn't include `width`. Fix in the AD-17 hash rule (ignore or normalise unlocked Column/@width) when the write pipeline lands. OneMore's defects page has no record of it (checked 2026-11-06; `research-onemore-lessons.md` row 15).
- source_plan: same
  summary: `get_binary_page_content` can't read the CallbackIDs of `one:XPSFile` (File Printout backgrounds): OneNote returns ObjectNotFound (0x80042014), both raw and through the tool.
  evidence: `--diag-callbacks` on notebooks A and B: all 6 XPSFile CallbackIDs (A 4, B 2) ObjectNotFound, and no other element gave ObjectNotFound. Say so in the tool description, or map it to binaryUnavailable, in 1.21.
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-synthetic-size-fixture-in-mcp-test-plan.md`
  summary: Epic 2: `one:InsertedFile/@pathCache` changes on every `GetPageContent` of a page with attachments (piBasic and piBinaryData), so an AD-17 basis taken from one read never matches the next, and a read-then-write of such a page would be refused as StaleWrite.
  evidence: story 1.17's stress-harness run on MCP Test (2026-11-06): the Ballast Files page (6 inserted files) classified `unstableRead` on both reads, first difference at `one:Page/one:Outline/one:OEChildren/one:OE/one:InsertedFile`, attribute `pathCache`, offset 1646. Fix in the AD-17 hash rule (ignore `pathCache`) when the write pipeline lands; check OneMore's defects page first.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-read-grant-for-password-protected-sections-plan.md`
  summary: Pages deleted from a password-protected section may land in the notebook recycle bin's "Deleted Pages" section, which OneNote doesn't mark `encrypted`, so AD-21 would treat them as unprotected (their names in broad hierarchy reads, their content through get_page_content).
  evidence: On MCP Test (2026-11-07), the recycle bin's section is not `encrypted="true"`; the search XML does mark the Vault. Settling it needs a hands-on delete of a test page in the unlocked Ballast Vault, then a look at where it lands and how it is marked. If it leaks, AD-21 needs an amendment (Fable review). Section `path` stability (close/reopen, rename, cloud vs local) is also still uncaptured.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-read-grant-for-password-protected-sections-plan.md`
  summary: Resolved the recycle-bin question about deleted protected pages (entry above): there is no leak.
  evidence: Phill, 2026-11-07. Deleting a test page in the unlocked Ballast Vault made OneNote say "Pages from password protected sections cannot be moved to Deleted Notes" and offer only a permanent delete. Afterwards MCP Test's "Deleted Pages" section had no pages, and the test page was gone. The spine's Deferred row was updated to match.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-read-grant-for-password-protected-sections-plan.md`
  summary: OneNote's option "Enable add-in programs to access password protected sections when they are unlocked" (File > Options > Advanced > Passwords) makes an unlocked protected section look locked to COM when it's off, so our sectionLocked warning / SectionLocked userAction ("unlock it in OneNote") misleads.
  evidence: Phill, 2026-11-08. With the option unticked and the Ballast Vault unlocked in the OneNote window, the integration test saw `locked=True` with 0 pages visible. Proposed fix (offered for 1.13): name the option in the sectionLocked/SectionLocked userAction and in the README's protected-sections paragraph.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-readme-uninstall-purge-and-clean-machine-install-plan.md`
  summary: The add-in access option item above is resolved in story 1.13.
  evidence: The SectionLocked userAction and the sectionLocked warning now end with `ToolErrors.AddInAccessOptionNote`, which names the option and its menu path. The README and the guide skill name it too. Tests are in ToolErrorsTests and HierarchyReadToolTests.

## Story 1.14 refactor sweep (2026-11-09)

Closes or routes every epic 1 entry above. Entries already marked "Epic 2" (write-side outcomeUnknown, HRESULT triage, AD-3's write-side timeout rules, the two AD-17 hash quirks) stay open for epic 2 as written.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-refactor-sweep-plan.md`
  summary: Resolved: the StaThread Dispose hang (first entry above).
  evidence: `StaThread.Enqueue` (`src/OneNoteMcp.Com/StaThread.cs:84-98`) checks `_stopping` and the poison under `_gate`, the same lock that queues the item, and abandons a late item with `ObjectDisposedException` instead of leaving it queued. Built in story 1.5's lifecycle work.
- source_plan: same
  summary: Resolved: the one-line stderr log format now has a test.
  evidence: The logging setup moved from `Program.cs` to `ServerSetup.AddOneNoteMcpLogging`, which Program calls. `ServerLoggingTests` builds it with Console.Out and Console.Error redirected, logs at every level, and checks that stdout stays empty and stderr gets one line per record.
- source_plan: same
  summary: Closed, accepted by AD-14: a tool-contract change without a `<Version>` bump still passes the unit tests.
  evidence: AD-14 says "The test detects the change; the PR reviewer checks the size of the bump", and `ToolContractTests` fails until the snapshot is replaced. A hash guard would be bypassable too (the entry above).
- source_plan: same
  summary: Resolved (Q3, Phill 2026-11-09): `0x80080005` now maps to `ComUnavailable`, retryable for reads, with the userAction "Restart OneNote, then retry."
  evidence: The gateway checks integrity levels before it calls COM (`ComGateway.cs:436`), so the code can only come back when the levels matched. `HResultMap.ToErrorCode` and `ToException`; `ComGatewayTests.ActivationComFailure_AfterTheIntegrityCheckMatched_IsComUnavailable_WithNamedHResult`, `HResultMapTests`. AD-9's rows are amended.
- source_plan: same
  summary: Resolved: the release job refuses a tag that differs from `<Version>`.
  evidence: `.github/workflows/release.yml:42-52` ("Check tag against <Version>") throws unless the tag is exactly `v<Version>`, before anything is built (story 1.11).
- source_plan: same
  summary: Resolved: `tools/list` and `tools/call` now run through the MCP SDK in a test.
  evidence: `McpSdkBindingTests`: an in-process client and server over pipes, on `AddOneNoteMcp` with FakeGateway. It checks the twelve listed tools against the contract snapshot, `get_hierarchy {scope:"notebooks"}` binding to an ok envelope, a misnamed `page_id` failing exactly like a missing `pageId` (an SDK isError result before the tool runs, not our Internal), and an unknown tool as a JSON-RPC InvalidParams error.
- source_plan: same
  summary: Resolved (Q2, Phill 2026-11-09): a call the client cancels returns the new code `Cancelled`, retryable for reads. An `OperationCanceledException` while the call's own token isn't cancelled stays `Internal`. Moved to epic 2: a cancelled write must carry `outcomeUnknown`, like a timed-out one.
  evidence: `ToolRunner.RunAsync` checks `cancellationToken.IsCancellationRequested`; `ToolErrors.IsRetryable` treats `Cancelled` like `Timeout` (reads only). Tests: `GetHierarchyToolTests.ClientCancellation_IsCancelled_AndRetryable_NotAThrow` and `StrayCancellation_WhileTheCallsTokenIsNotCancelled_IsInternalEnvelope_NotAThrow`. A contract change: `<Version>` 0.8.0-rc.1, snapshot `contractVersion` 0.8, AD-9 amended.
- source_plan: same
  summary: Moved to epic 2's first write story: the `NewerSchema` `SettingsInvalid` advice entry above.
  evidence: Still unreachable in epic 1: no tool calls `SettingsSnapshot.EnsureWritable`.
- source_plan: same
  summary: Resolved: the `ToolRunner.LogInternal` leak entry above. Page content can't reach the log through a parse exception.
  evidence: `OneNoteXml.Parse` (`src/OneNoteMcp.Xml/OneNoteXml.cs:25-28`) replaces the parser's message with its line and position. This story also stops attaching the parse exception as the inner exception of `InvalidXml` errors (`GetHierarchyUseCase`, `GetPageContentUseCase`, `FindPagesUseCase`, `SearchResults`, `ListGrantsUseCase`, and the same throw in `ReadAccess` and `GrantCommand`), so no raw `XmlException` travels with the error. `InvalidXml` isn't `Internal`, so `LogInternal` doesn't log it anyway.
- source_plan: same
  summary: Moved to the sentienceAdjacent/plugins repo (Q4, Phill 2026-11-09): a check there that its Claude Code validator pin (`ci.yml`, `sync.yml`) matches onenote-mcp's.
  evidence: The validator-pin entry above. This repo's `PluginPackagingTests` checks only its own `ci.yml` and `release.yml`. Owner: Phill, in the plugins repo.
- source_plan: same
  summary: Closed (Q4, Phill 2026-11-09): branch protection on the plugins repo's main.
  evidence: AD-15 states the precondition: sync.yml pushes straight to main, so protection would need a PR-based sync or a bypass for github-actions.
- source_plan: same
  summary: Open until Phill adds a GitHub tag-protection rule for `v*` tags (Q4, Phill 2026-11-09): a pinned release being deleted. Close this entry when the rule exists.
  evidence: AD-15 says a published release is never deleted; the rule backs that convention. GitHub settings are outside this repo.
- source_plan: same
  summary: Resolved in story 1.21: `get_binary_page_content` on a `one:XPSFile` CallbackID.
  evidence: On the `0x80042014` path, `GetBinaryPageContentUseCase` confirms the callbackID is an XPSFile's and returns the `binaryUnavailable` warning instead of `ObjectNotFound`. The tool description and the README's limits say File Printout documents can't be read. Tests in `PageContentToolTests`.
- source_plan: same
  summary: Resolved in story 1.6: the `IOneNoteGateway.GetHierarchyParentAsync` port comment said OneNote throws at a notebook; it returns an empty string.
  evidence: `src/OneNoteMcp.Core/IOneNoteGateway.cs:14` now says so, and AD-6 was amended (`change-story-1-6-spine-amendments`). From the XML-model story's triage, finding 6.
- source_plan: same
  summary: Resolved in story 1.6: `FormatDate` dropped from AD-7, R1 and story 7.
  evidence: It isn't in the OneNote 15 type library. The spine, R1 and `tickets.toml` no longer name it (`change-story-1-6-spine-amendments`). From the gateway-lifecycle story's plan.
- source_plan: same
  summary: Closed, won't fix (Q1, Phill 2026-11-09): `ToolRunner.RunAsync` takes the use case and its call separately, and `?? string.Empty` is repeated in tools, use cases and the gateway.
  evidence: Each tool has exactly one use case, and the extra null guard is harmless. From story 1.3's triage (finding 4, and the second pass).
- source_plan: same
  summary: Moved to epic 2: a modal dialog blocks `UpdatePageContent` (`0x80042030`) but not `CreateNewPage`. The write stories map `ModalDialogOpen` per method, and can't use `CreateNewPage` to probe for the dialog.
  evidence: Observed 2026-11-04, recorded only in `tests/OneNoteMcp.Integration/LifecycleScenarioTests.cs:145`.
- source_plan: same
  summary: Moved to epic 2's access (grants) story: whether a section's `path` is stable. Capture it beside the notebook `path` question.
  evidence: Story 1.20 didn't capture it (its plan, "Observed on MCP Test"). `SectionKey` (AD-21) assumes it is stable, as `NotebookKey` does. The spine's Deferred row now names epic 2.
- source_plan: same
  summary: Moved to epic 2, as a probe in MCP Test (Q5, Phill 2026-11-09): how `FindPages` handles symbols in a query, how reliable `FindMeta` is, whether the `FindPages` and `FindMeta` XML reliably carries `encrypted` on a protected `one:Section` (the spine's Deferred row points here), and whether the guide skill's claims about lists and image `alt` text hold. The skill took those claims from the schema, not from our fixtures.
  evidence: None of the three was probed in epic 1. Settle them before epic 2's write recipes build on them.
- source_plan: same
  summary: Accepted, with a README line (Q5, Phill 2026-11-09): the search page-text cache gives no benefit when a search has more candidate pages than `search.cacheMaxMB` holds.
  evidence: The cache drops the least recently used pages first, so a search over more pages than fit reads most or all of them again each time. README, "Limits, stated honestly".

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-refactor-sweep-plan.md`
  summary: Epic 2 (Phill, 2026-11-09, matrix row "Misnamed argument" accepted as met): reject unknown argument names, and say which argument failed to bind.
  evidence: Today the SDK (2.3.1) answers a missing or misnamed required argument with a generic "An error occurred invoking '<tool>'." and silently ignores a misnamed optional argument, so the tool runs with its default (`McpSdkBindingTests`).
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-reject-unknown-argument-names-plan.md`
  summary: Partly resolved (story 2.5): the "reject unknown argument names" half of the misnamed-argument entry above. The "say which argument failed to bind" half stays open: a misnamed required argument or an unbindable value (e.g. `get_hierarchy {scope: "bogus"}`) still gets the SDK's generic error, by AD-9 (the SDK's argument errors come first); Phill chose to handle it later (2.5 question 2, 2026-11-11): epic 2 entry 14, "Name the argument in every argument error". Every tool now refuses an argument name it doesn't declare with `InvalidArgument` (not retryable) inside the envelope, naming the argument and listing the tool's own.
  evidence: `ToolRunner.RunAsync` checks the call's argument names against the matched tool's input schema before the use case runs (`Tools/ArgumentNames.cs`). `McpSdkBindingTests`: `get_hierarchy {scop}`, `create_page {Style}` (no page created), several names, `list_grants {x}`, all 13 tools with `bogusArgument`, and an unknown name beside an unbindable value (the SDK's error first). `MisnamedArgument_FailsLikeAMissingOne_InTheSdk_BeforeTheToolRuns` is unchanged.
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-name-the-argument-in-every-argument-error-plan.md`
  summary: Resolved (story 2.14): the "say which argument failed to bind" half of the misnamed-argument entry above, so the whole entry is now closed. Before the SDK binds a call, a call-tool request filter checks its arguments against the tool's input schema: an undeclared name, a missing required argument, or a value of the wrong JSON type or outside its enum is `InvalidArgument` (not retryable) inside the envelope, in one message naming each argument and what it expects, never its value. 2.5's after-binding check moved into the filter. Every input schema declares `additionalProperties: false` (Phill, 2.5 Q1). Strict conformance (open question for Phill): values the SDK used to coerce or accept, such as `"5"` for an integer, `"Sections"` for `sections`, or `null` for an optional string argument (`startNodeId`, `pageContentObjectId`, `ref`, `expand`), are now refused.
  evidence: `ServerSetup.AddOneNoteMcp` (the filter and `CloseInputSchemas`), `Tools/ArgumentCheck.cs`, `ToolRunner.Refuse`. `ArgumentCheckTests` (each type, null unions, array items, int32 range, exact enums, message order, no values). `McpSdkBindingTests`: `get_page_content {page_id}`, missing `pageId` with empty and no arguments, `create_page {section_id}` (no page created), `get_hierarchy {scope:"bogus"}` and `"Sections"`, wrong kinds, several problems in one message, a wrong-kind value for every property of every tool (never the SDK's generic error), the unknown tool still a JSON-RPC error, and `tools/list` with `additionalProperties: false`. Contract snapshot: only that key added, in all 13 input schemas.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-refactor-sweep-plan.md`
  summary: Resolved: a pinned release being deleted (Q4). The tag ruleset now exists.
  evidence: Phill, 2026-11-09. Ruleset "release tags" (id 26869745) on sentienceAdjacent/onenote-mcp: target tag, active, includes `refs/tags/v*`, rules `deletion` and `non_fast_forward`, no bypass actors (checked through the GitHub API). It protects the tag only: deleting the GitHub Release itself from the Releases page is still possible.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release-retrospective.md`
  summary: "Epic 2 first refactor" group (Phill, 2026-11-09, retro decision 3). Clean these up before epic 2 adds write code next to them. D1, the xs:boolean `IsTrue` helper written 3 times (`LockedSections.cs:20`, `SearchResultFilter.cs:160`, `EncryptedSections.cs:55`). D2, the "XML can't be read → `InvalidXml`" mapping written 7 times, with `ListGrantsUseCase` bypassing `OneNoteXml.Parse`. D3, `ReadAccess`'s two parent walks and the two ways of building a section's location and a `ProtectedSectionGrant`. D4, settings.json parsed two ways (`SettingsStore` vs `SettingsWriter`). D5, `0x80042014` declared twice in Core. P2, private-field naming (`_x` vs `x`), with no `.editorconfig` rule. P3, the XPSFile probe in `GetBinaryPageContentUseCase.cs:93` swallowing `Timeout` and `Cancelled`. S2, split `ReadAccess.cs` (531 lines) when write grants arrive. S3, split `SettingsStore.Validate` (about 138 lines) per settings area.
  evidence: The epic 1 retrospective's aggregate views, with file:line sources in its Findings tables.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release-retrospective.md`
  summary: Moved to epic 2 (retro R4): `uninstall --purge` doesn't take the `Local\OneNoteMcp.Settings` mutex or detect a running server. A grant saved by a server still running after purge re-creates `settings.json`, and that server's in-memory `once` windows keep allowing reads.
  evidence: `UninstallCommand.cs:155-158` vs `SettingsWriter.cs:97`. Today the README's step 1 (quit every client first) is the only guard.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release-retrospective.md`
  summary: For bmad-correct-course (retro C1–C3, A2, A3). Epic Done-when 2 says "XML unchanged", but AD-4 now allows removals, withholding and parts. Its basis is page-only, though AD-17 also defines one for hierarchy nodes. The `navigate_to` boundary wording covers only fetching images. Spine AD-2 says only `ComGateway` touches the interop types, but `StaThread` holds `IApplication`. The spine diagram has no Server→Xml arrow (`GrantCommand.cs:5`).
  evidence: The epic 1 retrospective's spec and architecture tables.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/change-epic-1-retro-wording/change-epic-1-retro-wording.md`
  summary: "Epic 2 first refactor" group, added (Phill, 2026-11-09, retro A3 option b): the grant CLI calls `EncryptedSections.Find` in Xml directly, which the spine's diagram doesn't allow (Server depends on Core and Com only). Move it behind Core, which already makes the same call, with the `InvalidXml` mapping, in `ReadAccess` and `GetHierarchyUseCase`. The spine stays as it is.
  evidence: `src/OneNoteMcp.Server/Cli/GrantCommand.cs:5,227-237`.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/change-epic-1-retro-wording/change-epic-1-retro-wording.md`
  summary: The bmad-correct-course item above (retro C1–C3, A2, A3) is resolved by that change proposal. C1, C2 (option b), C3 and A2 are reconciled in the epic 1 file, the initiative, the epic 3 file, the spine (AD-2, AD-17) and the README. A3 is the refactor entry just above.
  evidence: the change proposal's section 4.

## Story 2.1 opening refactor (2026-11-09)

Closes the "epic 2 first refactor" group above (D1–D5, P2, P3, S2, S3, A3). R4 stays open for epic 2 as written.

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-opening-refactor-plan.md`
  summary: Resolved D1: one xs:boolean `IsTrue`.
  evidence: `OneNoteXml.IsTrue` (`src/OneNoteMcp.Xml/OneNoteXml.cs:33`); the copies in `LockedSections`, `SearchResultFilter` and `EncryptedSections` are gone, and `HierarchyNode` calls the shared one.
- source_plan: same
  summary: Resolved D2: one "XML can't be read → `InvalidXml`" mapping.
  evidence: `XmlReads.Readable(read, what)` (`src/OneNoteMcp.Core/XmlReads.cs:14`) gives `"{what} returned XML the server can't read: {reason}"` and never attaches the inner exception. `GetHierarchyUseCase`, `SearchResults`, `GetPageContentUseCase`, `FindPagesUseCase`, `ListGrantsUseCase`, `ReadAccess` (and `HierarchyWalk`) and `ProtectedSections` (for the grant CLI) all go through it. `list_grants` now parses through `OneNoteXml.Parse` via the new public `SectionPaths.Find`, and its message takes the common text (Phill, plan review). The page reads' `what` is `GetPageContent for page {id}`. `ResultDelivery`'s warning path is unchanged.
- source_plan: same
  summary: Resolved D5: `0x80042014` and `0x8004200F` are declared once in Core.
  evidence: `OneNoteHResults` (`src/OneNoteMcp.Core/OneNoteHResults.cs`); `NavigateToUseCase` and `GetBinaryPageContentUseCase` use it. `HResultMap` in Com keeps its name table.
- source_plan: same
  summary: Resolved P3: the XPSFile probe no longer hides a timeout. The story's one behaviour change. A cancellation was never hidden: it arrives as `OperationCanceledException`, which the old `catch (OneNoteException)` didn't catch.
  evidence: `GetBinaryPageContentUseCase.IsXpsFileAsync` catches `OneNoteException` only when its code isn't `Timeout` or `Cancelled` (`:93`). The `Timeout` case of `PageContentToolTests.ObjectNotFound_WhenTheConfirmingPageReadTimesOutOrIsCancelled_IsThatError` returned `ObjectNotFound` on the old code and passes now. Its `Cancelled` case is defensive only: no gateway throws `OneNoteException` with `Cancelled` (only `ToolRunner` creates it). `..._WhenTheClientCancelsDuringTheConfirmingPageRead_IsCancelled` covers the real path (`OperationCanceledException`) and passes on the old and new code. `..._FailsOtherwise_IsTheOriginalError` pins that any other page-read failure still gives the original `ObjectNotFound`.
- source_plan: same
  summary: Resolved S2 and D3: `ReadAccess.cs` is split, the two parent walks share one lazy iterator, and one factory makes every `ProtectedSectionGrant`.
  evidence: `src/OneNoteMcp.Core/Access/`: `AccessDecisions.cs` (`TargetAccess`, `BroadAccess`), `HierarchyWalk.cs` (`AncestorsAsync` at `:78`; `OwningSectionAsync` and `LocationAsync` keep their own stop rule and depth-overflow handling), `OnceWindows.cs` (the dictionary and its lock), `GrantDialog.cs` (`AskAsync`, `Save`), `ResultGrantCheck.cs`, and `ReadAccess.cs` (224 lines, was 551). `ProtectedSectionGrant.Create` (`src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:125`) is used by the once window, the dialog and the CLI. `ParentWalkTests` pin both walks' exact gateway call sequences, the 64-level overflow rules and that the dialog is asked only after the walk; they pass on the code before the split too. The CLI still takes its location from the Sections XML, so its COM calls are unchanged.
- source_plan: same
  summary: Resolved A3: the grant CLI no longer calls into `OneNoteMcp.Xml`. The spine stays as it is.
  evidence: Core's `ProtectedSections.Find` returns `ProtectedSection` (ID, name, key, locked, notebook name and path, groups) through `XmlReads.Readable`; `GrantCommand.ProtectedSectionsAsync` (`src/OneNoteMcp.Server/Cli/GrantCommand.cs:219`) calls it and has no catch of its own. `grep -rn "using OneNoteMcp.Xml" src/OneNoteMcp.Server` finds nothing. `Describe` is unchanged (`GrantCommandTests`); `ProtectedSectionsTests` checks the location matches `EncryptedSections`, which gained `NotebookName` and `SectionGroups`.
- source_plan: same
  summary: Resolved D4: settings.json's shared parse rules are in one place, and the store/writer difference on exact duplicate keys is kept and made explicit (Phill, plan review).
  evidence: `SettingsJson.TryParseUtc` and `SettingsJson.Options(allowDuplicateProperties)` (`src/OneNoteMcp.Core/Settings/SettingsJson.cs:16,19`); `SettingsStore` passes true, `SettingsWriter` false. `SettingsDuplicateKeyTests` pin today's behaviour, gap included, and pass on the code before the change too.
- source_plan: same
  summary: Epic 2, entry 6 (write grants: dialog, revoke and listing) owns the fix, and entry 3 (spine amendments) the rule: an exact duplicate key inside `protectedSections` can't be revoked. The store reads it as "two keys differ only in case" (`DuplicateGrantKeysOnly`), so `grant --revoke` runs, but the writer's load forbids duplicates and throws `SettingsInvalid`, so nothing is removed. AD-21 covers only keys that differ in case; it says nothing about identical ones.
  evidence: `SettingsDuplicateKeyTests.Store_AnExactDuplicateGrantKey_CountsLikeACaseOnlyDuplicate` and `Writer_AnExactDuplicateGrantKey_RefusesTheRevoke_AndLeavesTheFile`; `SettingsJson.Options`' doc comment points here. Decide the rule in entry 3, then change the store or the writer in entry 6 and update those tests.
- source_plan: same
  summary: Resolved S3: `SettingsStore.Validate` is one short method over one private static per settings area.
  evidence: `Validate` (`src/OneNoteMcp.Core/Settings/SettingsStore.cs:98`, about 37 lines) calls `ReadSchemaVersion`, `ReadAllowSilentActivation`, `ReadTimeouts`, `ReadJournal`, `ReadResults`, `ReadSearch`, `ReadReadGrants`, `ReadExportRoots` and last `ReadProtectedSections(root, out bool duplicate)`, in the old order, so the first failing field still wins. `SettingsStoreTests` and `ReadGrantSettingsTests` pass unchanged.
- source_plan: same
  summary: Resolved P2: private instance fields are camelCase across src and tests, and the build enforces it.
  evidence: `.editorconfig` (repo root) has one naming rule for private instance fields at `warning`, `dotnet_diagnostic.IDE1006.severity = warning` (the command-line build reads the severity only from there), and two `none` rules that exempt constants and static fields. `Directory.Build.props` sets `EnforceCodeStyleInBuild`. The 64 `_x` fields in Com (`ComGateway`, `StaThread`) and the tests were renamed, adding `this.` only where a local or parameter has the same name. A private field named `_x` or `BadName` now fails `dotnet build -warnaserror` with IDE1006 (checked by hand). `spikes/` keeps its own `Directory.Build.props`, so its frozen code isn't checked.

## Spike 2.2 live probe run (2026-11-10)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/spike-write-probes-part-1-pages-paths-and-search-plan.md`
  summary: Bug (AD-3, "never start OneNote silently"): the gateway's reconnect can start a hidden `ONENOTE.EXE -Embedding` after OneNote crashes. The crashed process stays alive while Windows Error Reporting writes its dump, so `ProcessProbe.IsOneNoteRunning()` still says true, and the reconnect's `CoCreateInstance` starts a new windowless instance. Phill then had to end it in Task Manager before OneNote would start normally.
  evidence: 2.2 local run, 08:15 local: APPCRASH in `mso20win32client.dll` at 08:15:13, a probe call returned `0x800706BE`, and `ONENOTE.EXE -Embedding` started at 08:15:16 (parent DcomLaunch). Path: `ComGateway.EnsureActivatedAsync` (`src/OneNoteMcp.Com/ComGateway.cs:419`) checks the process list, then activates. The probes now pin the process set and refuse a second activation (`tests/OneNoteMcp.Integration/WriteProbes/PinnedOneNoteProbe.cs`); `src/` needs its own fix (e.g. treat a reconnect as allowed only to the same or a user-started process, or detect a crashing process). Owner: an epic 2 story that touches the gateway, or a bug ticket.
- source_plan: none (Phill, 2026-11-10, in the 2.2 session)
  summary: Possibility for later, not scheduled: a shared-server mode. Today every Claude session starts its own stdio `OneNoteMcp.exe` (about 18–55 MB working set each, idle CPU near zero). One long-running server on a localhost HTTP transport (`claude mcp add --transport http ...`) would serve every session and funnel all COM through one STA thread. Costs: a new transport, something to start it (login task or tray), a required auth token (any local process can reach a port), and one failure point for every session.
  evidence: four servers measured 2026-11-10 (working set 17.9, 20.4, 48.4, 54.5 MB). Consider at the next initiative or packaging epic.

## Spine amendments, story 2.3 (2026-11-10)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-spine-amendments-from-the-page-write-probes-plan.md`
  summary: Resolved (rule) the alternating `Column/@width` item (spike 1.15 run, above): AD-17's `ElementHash` now leaves out `width` on a `one:Column` without `isLocked="true"`. Story 2.9 builds it. Built by story 2.9: `ElementHash.IsExcluded` (`XmlModelTests.UnlockedColumnWidth_IsExcluded`, `LockedColumnWidth_StaysInTheHash`); the `PageWrite` read-back skips it too.
  evidence: Phill, 2026-11-10, 2.3 plan Q2. Spike 2.2 didn't reproduce the alternation (20 reads of a cloud table and a fresh MCP Test table, one value set), but OneNote replaced the unlocked widths 80 and 120 sent on create with 37.11 and kept the locked 105.51 (`width-fresh-mcp-test-table`). A concurrent change to an unlocked width alone then goes undetected; the spine says so. The spike 1.15 evidence doesn't record whether its alternating columns had `isLocked="true"`, so the Q2 rule resolves it only if they were unlocked: a locked column that alternates would still give `StaleWrite`.
- source_plan: same
  summary: Resolved (rule) the `InsertedFile/@pathCache` item (story 1.17, above): AD-17's `ElementHash` now leaves out `one:InsertedFile@pathCache`. Story 2.9 builds it. Built by story 2.9: `ElementHash.IsExcluded` (`XmlModelTests.InsertedFilePathCache_IsExcluded`); the `PageWrite` read-back skips it too.
  evidence: Phill, 2026-11-10, 2.3 plan Q2. Spike 2.2 saw one value in 20 reads of a page with 6 attachments (`pathcache-ballast-files`), so the change isn't reproducible on demand; the attribute is a local cache path, not content.
- source_plan: same
  summary: Resolved (rule) the exact duplicate grant key item (2.1, above): exact and case-only duplicate keys in `notebooks{}` or `protectedSections{}` alike make `settings.json` `SettingsInvalid`, and `grant --revoke` runs anyway and removes every entry matching the key (AD-6, AD-21). Story 2.6 builds it and updates `SettingsDuplicateKeyTests`.
  evidence: Phill, 2026-11-10, 2.3 plan Q7. Today the writer refuses the revoke (`Writer_AnExactDuplicateGrantKey_RefusesTheRevoke_AndLeavesTheFile`), which the new rule reverses.
- source_plan: same
  summary: Resolved (rule) the hidden `ONENOTE.EXE -Embedding` after a crash (spike 2.2 live probe run, above): AD-3 now says a crashed `ONENOTE.EXE`, still listed while Windows Error Reporting writes its dump, doesn't count as running, so a reconnect falls under the first-activation rule and never starts a hidden instance silently. The exception: with `allowSilentActivation` on, a reconnect after a crash may start a windowless instance, as the setting allows. Story 2.7 builds the detection in `ComGateway`.
  evidence: AD-3 Activation (spine, story 2.3). AD-9 also gives a write that fails with `0x800706BE` `outcomeUnknown`, since that HRESULT came back from the call during which OneNote crashed.

## Deferred from: story 2.3 review (2026-11-10)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-spine-amendments-from-the-page-write-probes-plan.md`
  summary: Ticket text predates story 2.3's answers. `epic-safe-writes.md` Notes still record inception decision 3 ("both grants"), superseded by Q3 option c (write grant alone). `tickets.toml` entry 9 still lists the image refusal (dropped, Q1) and the section's read grant (Q3c); entries 3 and 11 are worded from before Q3c. Entry 7 doesn't name crashed-process detection (AD-3), and entry 4 doesn't name `outcomeUnknown` on `0x800706BE` (AD-7 step 7).
  evidence: 2.3 quick review, finding 1. A build never writes ticket files; refresh them with `bmad-ticket` before entry 4 or 7 starts.

## Story 2.4 create_page tracer (2026-11-10)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-tracer-create-page-through-the-write-pipeline-plan.md`
  summary: Resolved: the `NewerSchema` `SettingsInvalid` advice entry (story 1.4, moved to epic 2 in story 1.14). A write on a `settings.json` from a newer OneNoteMcp now fails `SettingsInvalid` with `retryable: false` and a `userAction` saying to update OneNoteMcp (the plugin) and restart the client, and not to edit `settings.json` (Phill, 2026-11-10, 2.4 plan Q7). An invalid file keeps "Fix settings.json, then retry", retryable.
  evidence: `SettingsSnapshot.EnsureWritable` and `SettingsSnapshot.NewerSchemaUserAction`, through the exception's own `Retryable` and `UserAction` (`ToolErrors.From`). Tests: `SettingsStoreTests.EnsureWritable_NewerSchema_IsNotRetryable_AndSaysToUpdateNotToEdit`, `EnsureWritable_Invalid_KeepsTheFixTheFileAdvice_AndIsRetryable`, `CreatePageToolTests.SettingsFromANewerServer_IsSettingsInvalid_NotRetryable_WithUpdateAdvice`.
- source_plan: same
  summary: Resolved: the write-side `outcomeUnknown` entries (story 1.3's "Epic 2: the write pipeline must set outcomeUnknown on a write that fails with Timeout or ComUnavailable, including hrTimeOut", and story 1.14's "a cancelled write must carry outcomeUnknown"). `WritePipeline` sets `outcomeUnknown: true` on a failure at the COM call (step 7) with `Timeout` (the gateway's call timeout, `0x80042023` hrTimeOut, or the pipeline's own deadline), `ComUnavailable`, `Cancelled` (the client's token), or HRESULT `0x800706BE`, which keeps its code. No failure before step 7 sets it. `ToolErrors.From` then forces `retryable: false` and AD-9's "re-read, then retry" userAction.
  evidence: `src/OneNoteMcp.Core/Writes/WritePipeline.cs` (`RunAsync`, `IsAmbiguous`). Tests: `CreatePageToolTests.AnAmbiguousFailureAtTheCall_IsOutcomeUnknown_NotRetryable_WithTheReReadUserAction` (Timeout, ComUnavailable `0x800706BA`, Cancelled, Internal `0x800706BE`), `ClientCancel_DuringTheCall_IsCancelled_WithOutcomeUnknown`, `DeadlineExpiry_DuringTheCall_IsTimeout_WithOutcomeUnknown`; and for "never before step 7", `AFailureBeforeTheCall_NeverSetsOutcomeUnknown_EvenATimeout`, `ClientCancel_BeforeTheCall_IsCancelled_WithoutOutcomeUnknown`, `DeadlineExpiry_BeforeTheCall_IsTimeout_WithoutOutcomeUnknown`, `AClearFailureAtTheCall_KeepsItsCode_WithoutOutcomeUnknown`.
- source_plan: same
  summary: Partly resolved: the AD-3 write-side timeout rules entry (story 1.5). A timed-out write now reports `outcomeUnknown: true` (above), and `CreateNewPage` is never retried after a disconnect. Still open for story 2.7: "while an abandoned write is still outstanding, new non-read pipelines return `ComBusy`" (`ComGateway` doesn't yet record whether an abandoned call was a write), and confirming that busy (`RPC_E_CALL_REJECTED`, `RETRYLATER`) may be retried with backoff for a write.
  evidence: the 2.4 plan's Never list (abandoned-write `ComBusy` is 2.7). `ComGateway.CreateNewPageAsync` (`retryOnDisconnect: false`); `ComGatewayLifecycleTests.CreateNewPage_IsNotRetriedAfterADisconnect_AndFailuresMapLikeAnyCall`.
- source_plan: same
  summary: Open, small: AD-7 runs the access check (step 1, the notebook walk) before the lock, and step 3 reads only the section itself. A section moved to another notebook between step 1 and step 3 would get its page under the first notebook's grant. A move needs epic 3's `UpdateHierarchy` or the user's own hand; step 3 could re-walk to the notebook under the lock if that ever matters.
  evidence: `CreatePageUseCase.CreatePage` (`AuthorizeAsync`, `ReadAsync`); AD-7's step order.

## Deferred from: story 2.4 review (2026-11-11)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-tracer-create-page-through-the-write-pipeline-plan.md`
  summary: Resolved (2.7 kept the lock, story 2.9 added the check): Story 2.7: when a step-7 COM call times out or is cancelled, `WritePipeline` releases `Local\OneNoteMcp.Write` while the call may still be running on the STA worker, so another process's write can overlap it. Harmless for `create_page`; it matters before basis-checked writes (2.9). Build it with 2.7's "abandoned write outstanding → ComBusy" rule: keep the lock (or report ComBusy across processes) until the abandoned call returns or the worker is replaced, bounded by the deadline. Story 2.9: right after step 2, while `gateway.WaitForAbandonedWritesAsync` isn't complete, every pipeline (`create_page` included) returns `ComBusy`, retryable, before it reads anything (`WritePipeline.RunAsync`; `CreatePageToolTests.AnAbandonedWriteOutstanding_AfterStep2_IsComBusy_NothingIsRead_AndTheLockIsReleasedAtOnce`, `UpdatePageContentToolTests.AnAbandonedWriteOutstanding_IsComBusy_NothingReadOrWritten`).
  evidence: 2.4 review (concurrency lens, verified): `CreatePageUseCase.cs:75` passes the linked token; `ComGateway.AwaitAsync` rethrows on cancel and the call-timeout path poisons and throws without waiting; `WritePipeline.cs:116-126` then disposes the hold.
- source_plan: same
  summary: Next spine story: AD-14 says tool names are the snake_case of the COM method, but `create_page` (for `CreateNewPage`) was Phill's choice (2.4 plan Q1) and AD-21 already uses it. Add one line to AD-14 recording the exception.
  evidence: 2.4 review (acceptance lens, verified): `CreatePageTool.cs:15`; spine AD-14 and AD-21 Writes.

## Deferred from: story 2.5 review (2026-11-11)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-reject-unknown-argument-names-plan.md`
  summary: Resolved for 2.9 (the 2.10 half stays open): Stories 2.9 and 2.10: `InvalidArgument`'s default `userAction` ("Call again using only the argument names the tool declares…") fits only unknown names. AD-9 also assigns `InvalidArgument` to AD-7's refusals (an Outline sent without an OE step 3 read; a `delete_page_content` target that isn't an Outline). The stories that throw those must set their own `UserAction` on the exception; `ToolErrors.From` already prefers it. Story 2.9's refusals carry their own `userAction`: `UpdatePageContentWrite.OmittedUserAction`, `MovedUserAction`, `RemovalUserAction`, `PageIdUserAction`, `WrongElementUserAction` (`UpdatePageContentToolTests.OeOmitted_IsInvalidArgument_WithItsOwnUserAction`, `OeMoved_IsInvalidArgument_WithTheRecreateAndRemoveUserAction`).
  evidence: 2.5 review (lead reviewer): `ToolErrors.cs:74`, `ToolErrors.From` line 37; spine AD-9 `InvalidArgument` row. Story 2.14 widened the default `userAction` to fit every input-schema error (undeclared names, missing required arguments, wrong types, enum values). The remaining point still holds: AD-7's refusals in 2.9 and 2.10 aren't schema errors and need their own `UserAction`.

## Story 2.6 write grants (2026-11-11)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-write-grants-dialog-revoke-and-listing-plan.md`
  summary: Next spine story: add a one-line AD-6 clarification under "How a grant is created": a name typed in the write-grant dialog that matches exactly one open notebook, but not the notebook the write targets, makes no grant and returns `WriteNotGranted` ("the name typed in the grant dialog didn't match this notebook").
  evidence: Phill, 2026-11-12, 2.6 plan Q2 (no spine edit in 2.6). `WriteGrantDialog.AskAsync` (the key check after the single match); `CreatePageToolTests.Dialog_NotAnsweredWithThisNotebooksName_IsWriteNotGranted_Retryable_NoGrant_NoLock_NoWrite` (`Typed("Other")`).
- source_plan: same
  summary: Resolved: the exact duplicate grant key (2.1 D4, then 2.3 Q7, above). `grant --revoke --read-section` and `grant --revoke --write` now remove every entry equal to the key, exact or case-only, and run on a file invalid only for them; the revoke parses with duplicates allowed and writes the file back property by property, keeping everything else as it was.
  evidence: `SettingsWriter.Revoke`; `SettingsDuplicateKeyTests` (`Writer_AnExactDuplicateSectionKey_IsRevoked_BothEntriesGone_AndTheFileIsValid` replaces `Writer_AnExactDuplicateGrantKey_RefusesTheRevoke_AndLeavesTheFile`).
- source_plan: same
  summary: Resolved: retro R4 (story 1.14 sweep, above). `uninstall --purge` warns with the process ID of every other running `OneNoteMcp.exe` before its question, and removes `settings.json` and its temporary files only while holding `Local\OneNoteMcp.Settings` (never across the question); a lock timeout keeps those files, says so and exits 1, and the other files are still removed.
  evidence: `UninstallCommand.Run`, `UninstallCommand.OtherServerProcesses`, `SettingsWriter.RunLocked`; `UninstallCommandTests` (R4 section).

## Story 2.7: write gateway, timeouts and error map (2026-11-11)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-write-gateway-timeouts-and-error-map-plan.md`
  summary: Resolved: the HRESULT triage entry (story 1.3, "Epic 2: triage the write-side deliberately-Internal codes … and the 23 untriaged OneNote codes"). Every named HRESULT, all 49 OneNote codes included, now has an explicit row in `HResultMap.Triaged`; none falls to the default arm. No code changed (plan Q2): the triaged ones stay `Internal` with a specific `userAction`, and those whose action ends "then retry" (`0x8004200B`, `0x8004200C`, `0x80042003`, `0x8004201E`, `0x80042024`) carry `retryable: true` through the exception's own flag. Three remaps are agreed for the next spine story (entry below).
  evidence: `src/OneNoteMcp.Com/HResultMap.cs` (`Triaged`, `UserAction`, `Retryable`, `ToException`). Tests: `HResultMapTests.EveryNamedHResult_HasAnExplicitTriageRow`, `EveryOneNoteHResult_IsTriaged_NotLeftToTheDefault`, `DesignNotesRow_IsInternal_WithItsOwnUserAction_AndRetryFlag`, `ToException_CarriesTheRowsUserActionAndRetryFlag`, `ARetryableRow_ReadsRetryableOnAReadAndAWrite_ButNotWhenTheOutcomeIsUnknown`.
- source_plan: same
  summary: Resolved: the AD-3 write-side timeout rules (story 1.5's entry, left partly open by 2.4). A write (`CreateNewPage`, `UpdatePageContent`, `DeletePageContent`, or the raw seam with `retryOnDisconnect: false`) that started in OneNote and whose caller stopped waiting (the gateway's timeout, or a cancel while it ran) is outstanding until it returns. Meanwhile every non-read gateway call (writes and `NavigateTo`) fails `ComBusy` before COM; reads still run. After a timeout they run on a fresh worker; after a cancel the worker isn't poisoned until the call's deadline, so a read queues behind the running write and can time out waiting for it, as before this story. A write abandoned while still queued never ran and isn't tracked. Busy (`RPC_E_CALL_REJECTED`, `RETRYLATER`) stays retried with backoff for writes: COM's message filter refuses the call before dispatch, so it never ran (confirmed, plan Boundaries).
  evidence: `ComGateway` (`CallKind`, `TrackAbandonedWrite`, `ThrowIfWriteOutstanding`, `WaitForAbandonedWritesAsync`). Tests: `ComGatewayLifecycleTests.AWriteThatTimesOut_IsOutstanding_OtherNonReadCallsAreComBusy_ReadsRun_UntilItSettles`, `AWriteCancelledWhileRunning_IsOutstanding_UntilItReturns`, `AWriteAbandonedWhileQueued_NeverRan_AndIsNotTracked`, `PageWrite_Busy_IsRetriedWithBackoff`, `PageWrites_AreNotRetriedAfterADisconnect`.
- source_plan: same
  summary: Resolved: the 2.4 review's lock entry. After a step-7 failure, `WritePipeline` keeps `Local\OneNoteMcp.Write` until the gateway reports no abandoned write outstanding or the pipeline's deadline runs out, whichever is first. The release happens in the background, so the tool's result isn't delayed; the written pages are dropped from the search cache again just before it.
  evidence: `WritePipeline.RunAsync` (step 8), `ReleaseWhenSettledAsync`. Tests: `CreatePageToolTests.AfterAStep7Timeout_WithAnAbandonedWriteOutstanding_TheLockIsKeptUntilItSettles_ButTheResultIsNotHeldBack`, `AfterAStep7Cancel_WithAnAbandonedWriteOutstanding_TheLockIsKeptUntilItSettles`, `AfterAStep7Timeout_AnAbandonedWriteThatNeverSettles_KeepsTheLockOnlyUntilTheDeadline`, `AFailureBeforeStep7_ReleasesTheLockAtOnce_EvenWithAnAbandonedWriteOutstanding`.
- source_plan: same
  summary: Resolved in `src/`, live check pending: spike 2.2's crash bug (a reconnect starting a hidden `ONENOTE.EXE -Embedding` while Windows Error Reporting dumps a crashed OneNote). `ProcessProbe` no longer counts an `ONENOTE.EXE` that a `WerFault.exe`/`WerFaultSecure.exe` in this session targets (`-p <pid>`); a live reference to it is dropped per call, and the not-running error says OneNote crashed and Windows is still closing it. A WER process whose command line can't be read makes every OneNote count as crashing only when connecting (activation or a reconnect: `IsOneNoteRunning()`); a live reference's per-call check (`IsOneNoteStillRunning()`) ignores it (Phill, 2026-11-12, plan Q3). Phill's live check: after a forced reconnect, no hidden `-Embedding` instance appears. Risk to watch: while a WER process whose command line can't be read is alive in the session (another app's report, an elevated `WerFaultSecure`, possibly a "stopped working" dialog left open), the server can't connect (first activation, the reconnect after OneNote restarts, or after the idle release), and those calls fail `OneNoteNotRunning` with the crash wording; a working connection keeps going, reads and writes included.
  evidence: `src/OneNoteMcp.Com/IProcessProbe.cs` (`Running(…, connecting)`, `IsOneNoteStillRunning`, `WerFaultTarget`, `IsOneNoteCrashing`, `CommandLine` via `NtQueryInformationProcess` class 60); `ComGateway.EnsureActivatedAsync`, `ComGateway.CrashingMessage`. Tests: `ComGatewayLifecycleTests.WerFaultTarget_ReadsThePidAfterP`, `Running_WhenConnecting_ExcludesTheOneNoteWerTargets_AndAnUnreadableWerLineExcludesEveryOne`, `Running_ForALiveReference_IgnoresAnUnreadableWerLine_ButStillExcludesANamedTarget`, `ACrashingOneNote_IsNotRunning_WithTheCrashWording_AndNothingIsActivated`, `AnUnreadableWerProcess_DoesNotDropALiveReference_ReadsAndWritesKeepGoing`, `AnUnreadableWerProcess_StillBlocksConnecting_WithTheCrashWording`, `AWerProcessNamingOurOneNote_DropsALiveReference_AndTheNextCallFailsNotRunning`.
- source_plan: same
  summary: Next spine story: sync AD-9 with the triage table (every OneNote code has a row; the "untriaged, default rule" sentence and the write-side deliberate-`Internal` list now describe `Triaged`, and the R rows' `retryable: true` override) and AD-3 with the WER crash rule in Activation (an unreadable WER process counts only when connecting; a live reference's per-call check ignores it, Phill 2026-11-12, plan Q3), and the write lock kept after a step-7 failure until the abandoned call settles or the deadline runs out. AD-9's `ComBusy` row already names "abandoned write outstanding". Forward note for stories 2.9 and 2.10 (basis-checked page writes): check for an abandoned write outstanding right after step 2 (the lock), so before step 3's read and step 6's snapshot, and fail `ComBusy` there, so no basis or snapshot is taken of a page an abandoned call may still be changing (one can outlive a lock released at its pipeline's deadline); today the gateway refuses only at step 7's call.
  evidence: this story's Never list (no spine edits); spine AD-3, AD-9 ("The remaining OneNote codes are untriaged …"); `IOneNoteGateway.WaitForAbandonedWritesAsync` (completes at once when none is outstanding) is the check 2.9/2.10 can use. Story 2.9 now uses it: the check after step 2 in every pipeline (forward note resolved).
- source_plan: same
  summary: Next spine story (Phill, 2026-11-12, plan Q2): amend AD-9 to remap three OneNote HRESULTs that fit other codes better: `0x80042017` hrInvalidName → `InvalidArgument`, `0x80042024` hrRecordingInProgress → `ComBusy`, `0x8004202E` hrRecycleBinEditFailed → `ObjectNotFound` (as 2.4 Q8 does for recycle-bin targets). The build that follows the amendment changes those three `HResultMap.Triaged` rows (code, and `userAction`/retryable flag where the new code's default fits) and adds them to `HResultMapTests.ExpectedNonInternal`. 2.7 keeps them `Internal`.
  evidence: plan Open Questions Q2; `src/OneNoteMcp.Com/HResultMap.cs` (`Triaged`); spine AD-9 (names `0x80042024` and `0x8004202E` deliberately `Internal`; `InvalidArgument` is the server's own refusal).
- source_plan: same
  summary: Epic 5: AD-3's 30 s call timeout stays for every write (plan Q1, confirmed by Phill 2026-11-12). 5.1's live runs (2026-11-11) saw OneDrive hierarchy creates take 3.4–13 s, once over 60 s, and a call the gateway abandoned still completed 2–3 s later. 2.7 makes that case safe (`outcomeUnknown`, no retry, lock held until it settles) rather than longer; a per-class or cloud limit would be a spine change for Phill.
  evidence: plan Implementation Notes, Q1; spike 2.2 found page writes not slow.
- source_plan: same
  summary: Live, pending (Phill): `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter PageWriteGatewayIntegrationTests` (update then delete an Outline on a fresh Seed page; a stale date → `StaleWrite`). Not run in the build lane.
  evidence: `tests/OneNoteMcp.Integration/PageWriteGatewayIntegrationTests.cs`; `TestNotebook.EnsureTestTargetAsync`.

## Story 2.9 update_page_content (2026-11-12)

- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-update-page-content-plan.md`
  summary: Spine sync (story 5.2 records it; no spine edits here), per Phill's 2026-11-12 answers: AD-16 settled for page writes, the 2013 XSD is a hard gate in AD-7 step 4 and `InvalidXml` names line, position and the element's local name only (Q3); AD-7 whole Outlines: the removal argument is `removeObjectIds`, and naming an OE removes its nested OEs too (Q1); AD-7 move rule: an OE sent under an Outline other than the one step 3 read it in is `InvalidArgument`, `userAction` re-create without the `objectID` and remove from the old Outline (Q4); AD-17: step 5 also compares the hash of each sent OE with an `objectID` outside any sent Outline, the title's OE in practice, with the partial-basis rule (Q5); AD-3: the abandoned-write `ComBusy` check runs right after step 2 in every non-read pipeline, `create_page` included; AD-9 (2.14 text): the input check also recurses into an object argument's members, and an object argument that declares its members is closed with `additionalProperties: false` (`basis`).
  evidence: plan Decisions Q1–Q5 and lane decisions; `src/OneNoteMcp.Xml/PageWriteCheck.cs`, `SchemaValidation.Problems`, `src/OneNoteMcp.Core/Writes/PageWrite.cs`, `WritePipeline.RunAsync`, `src/OneNoteMcp.Server/Tools/ArgumentCheck.cs` (`MemberMismatch`), `ServerSetup.Close`.
- source_plan: same
  summary: Live, pending (Phill): `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter UpdatePageContentIntegrationTests` on MCP Test (a fresh edit lands and its `meta.basis` carries a second edit; a stale basis on a page that isn't displayed → `StaleWrite`; one journal entry per write; the merged-table probe prints `RESULT merge kept` or `RESULT merge lost`), plus the plan's manual checks (an edit from Claude Code, a `quickStyleIndex` change → `readBackDiffers`, a write in the protected section with the write grant only, the stress harness at 0 mismatches since the hash change touches read bases). Not run in the build lane.
  evidence: `tests/OneNoteMcp.Integration/UpdatePageContentIntegrationTests.cs`; plan Verification, Manual checks.
- source_plan: same
  summary: Follow-up if the Q6 probe prints `RESULT merge lost`: amend AD-4 (editing inside a merged table in `xs2013` loses the merge) and decide the server's answer; until the probe runs, the spine's Deferred row "Editing inside a merged table in `xs2013`" stays open.
  evidence: plan Q6; spine AD-4 and Deferred.
- source_plan: same
  summary: Read-back details not in the spine (implementation choices, for 5.2 to confirm or drop): attribute values compare by number when both parse as numbers and by meaning for `true`/`1` and `false`/`0`, so `36` and `36.0` aren't reported; a sent `objectID` on an element of another kind than the page has is refused with `InvalidArgument` (`WrongElement`); a basis `lastModifiedTime` that isn't a date is `InvalidArgument`, not `StaleWrite`; a null `elementHashes` value counts as missing (`partialBasis`).
  evidence: `PageWriteCheck.SameValue`, `PageWriteProblemKind.WrongElement`; `UpdatePageContentWrite.CheckBasisAsync`; tests `PageWriteCheckTests.ReadBack_NumbersAndBooleans_CompareByValue`, `AnObjectIdOnAnotherKindOfElement_IsWrongElement`, `UpdatePageContentToolTests.ABasisDateThatIsntADate_IsInvalidArgument`.
- source_plan: same
  summary: Story 2.12: the guide skill's frontmatter still says the release is read-only, and its `WriteNotGranted` row names only `create_page`. 2.9 added the `update_page_content` tool row and the intro sentence only (Never: recipes beyond a tool row).
  evidence: `plugin/skills/guide/SKILL.md` (description, line 8, Errors table).
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/story-update-page-content-plan.md`
  summary: `ComGatewayLifecycleTests.AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked` is timing-sensitive under full-suite load: it failed once (`ComGatewayLifecycleTests.cs:867`, expected 0 abandoned writes tracked, actual 1) and passed on every rerun. Story 2.7 already noted that pinning the poison path without timing needs a clock seam in `ComGateway`.
  evidence: the 2.9 implementer's and the 2.9 quick reviewer's first full runs (1835 passed, 1 failed), then 1836/1836 on reruns; `ComGateway` isn't in 2.9's diff. A clock seam, or the 2.13 refactor sweep, would settle it.
- source_plan: same
  summary: Next spine sync (after 5.2), from 2.9's security-review build: (1) name the warning `readBackWithheld` (count-free; the protected no-read-grant read-back, with no `meta.basis`) in AD-7's read-back list and AD-21; (2) record that `pathCache` on `one:InsertedFile`/`one:MediaFile` is gated like `pathSource`, not stripped; (3) record that besides UNC and device paths, a mapped network drive, a link resolving to a network path, and anything not fully qualified (relative, drive-relative `C:x`, rooted `\x`, URLs) are `InvalidArgument` with no dialog; (4) a `one:File`'s path belongs to its parent object for the "same existing element" exemption; (5) broken XML is `InvalidXml` in step 1 too, before any grant check; (6) the step-4 refusal of a path not confirmed in step 1 is `WriteNotGranted`, retryable.
  evidence: plan "Follow-up: story 2.9 security review"; `PageWriteScan`, `FilePathGate`, `UpdatePageContentWrite.AuthorizeAsync`/`Validate`, `ToolWarnings.ReadBackWithheld`; tests `PageWriteSecurityTests`, `UpdatePageContentToolTests`.
- source_plan: same
  summary: File-path canonicalisation resolves each existing link or junction along the path to its final target, segment by segment, but not links inside a resolved target's own parent folders, and it doesn't expand 8.3 short names; and the target can change between the dialog and OneNote's read (time-of-check). Acceptable for v1 (the user confirms the path OneNote is told to read; a network target found during resolution is refused), but worth a look if file inserts become common.
  evidence: `FilePathGate.ResolveLinks`; test `PageWriteSecurityTests.Canonical_ResolvesAJunctionToItsTarget`.
- source_plan: same
  summary: Story 2.12: the skill should say how to add an image (inline `one:Data`), that a file path raises an accept/decline dialog, and the date-only rule for a protected section without a read grant (`ReadNotGranted`, `readBackWithheld`). 2.9 put these in the tool description only.
  evidence: `UpdatePageContentTool` description; `plugin/skills/guide/SKILL.md` has only the tool row and line 18.
- source_plan: same
  summary: Unverified, would be high (2.9 review pass 2): `one:HTMLBlock` with `one:Data` (an HTML string, XSD l.638/1097) isn't scanned for file references. If OneNote's HTML import fetches a local `src` (`<img src="file:///C:/…">`), a page write copies a local file into the notebook without the file-path dialog, the risk AD-7's user-gated paths close. Settle with a live probe on MCP Test (an `HTMLBlock` whose HTML references a local image; does the image land?); if it does, refuse `HTMLBlock` `one:Data` (or any HTML with a `src`/`href` to a local or network file) with `InvalidArgument`, and amend AD-7.
  evidence: `PageWriteScan.FilePaths` reads attributes only; `HtmlContent` choice `File`/`Data` in the vendored XSD. `one:T` CDATA also takes "basic html" (XSD comment), but `img` there is untested too.
- source_plan: same
  summary: Unverified, would be medium (2.9 review pass 2): `pathCache` on `one:InsertedFile`/`one:MediaFile` is gated by exact value on the same element. If it changes between reads (story 1.17 saw it change every read; spike 2.2 saw one value in 20 reads), every resend of an Outline holding an attachment raises the file-path dialog on a OneNote cache path, and fails `WriteNotGranted` (not retryable) on a client without elicitation. The follow-up live check "resending the page with that file unchanged raises no dialog" settles it; if it churns, exempt `pathCache` on an existing element (or strip it), and amend AD-7.
  evidence: `PageWriteScan.NewFilePaths`; spine AD-17 (`pathCache` excluded from the hash for that churn); deferred entries for story 1.17.
