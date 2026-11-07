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
