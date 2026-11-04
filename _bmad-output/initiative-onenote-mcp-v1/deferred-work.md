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
- Epic 2: triage the write-side deliberately-Internal codes (0x8004200B, 0x8004200C, 0x80042022, 0x80042024, 0x8004202E) and the 23 untriaged OneNote codes (for example hrInvalidName 0x80042017, hrOpeningSection, hrLegacySection) when the write tools land (AD-9).
