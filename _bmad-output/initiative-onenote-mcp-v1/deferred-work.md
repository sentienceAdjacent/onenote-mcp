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
