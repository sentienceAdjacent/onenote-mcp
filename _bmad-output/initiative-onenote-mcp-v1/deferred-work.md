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
- source_plan: `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-gateway-lifecycle-read-side-port-and-integration-harness-plan.md`
  summary: Remove `FormatDate` from the spine's AD-7 `Read` row, from R1 and from story 7, through `bmad-correct-course`. Decision (Phill, 2026-11-04): the port has nine Read methods and no `FormatDate`.
  evidence: `FormatDate` isn't in the OneNote 15 type library: the 29-slot hand-declared `IApplication` and the PIA both lack it. Nothing needs it, because dates are UTC ISO-8601 at the tool boundary (Consistency Conventions).
