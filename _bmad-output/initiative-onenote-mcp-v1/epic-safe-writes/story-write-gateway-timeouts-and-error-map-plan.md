---
title: 'Write gateway, timeouts and error map'
type: 'feature'
ticket: '7'
created: '2026-11-11'
status: 'built'
baseline_revision: '2a717a863c6842fac43ed2521c0466a0e645c8eb'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The port has no page-edit methods; AD-3's write-side rules are half built (an abandoned write isn't tracked, so other pipelines and other processes can overlap it, and the lock is released while it may still run); a crashed `ONENOTE.EXE` still listed during its WER dump lets a reconnect start a hidden `-Embedding` instance (spike 2.2 Failure modes); and 23 OneNote HRESULTs plus the write-side and policy codes fall to `Internal` untriaged (AD-9, Deferred "HRESULT triage").

**Approach:** Add `UpdatePageContentAsync`/`DeletePageContentAsync` to the port and both gateways (no caller yet: 2.9/2.10 build the tools); track abandoned writes in `ComGateway` (non-read calls get `ComBusy`, the pipeline keeps the lock until they settle, bounded by its deadline); make `ProcessProbe` count an `ONENOTE.EXE` that Windows Error Reporting is handling as not running; replace `HResultMap`'s switch with an explicit per-HRESULT triage table.

## Boundaries & Constraints

**Always:**
- Writes pass `force: false` and `XMLSchema.xs2013`; the date reaches the port as OneNote's exact string, and the gateway converts it to a `DateTimeKind.Utc` `DateTime` through a new `OneNoteMcp.Xml` helper (AD-17 "only Xml parses it"; same parse the probes used, `ProbeXml.ParseUtc`). An empty, unparsable or `DateTime.MinValue` date is refused before COM with `InvalidArgument` and a re-read `userAction`.
- Both write methods: `retryOnDisconnect: false`; busy (`0x80010001`, `0x8001010A`) still retried with backoff (a rejected call never ran: COM's message filter refuses it before dispatch).
- Abandoned write = a write call that started in OneNote and whose caller stopped waiting (gateway timeout, or the caller's cancel while running). While one is outstanding, every non-read gateway call (writes, `NavigateTo`, the raw seam with `retryOnDisconnect: false`) fails `ComBusy` before COM; reads run. A write abandoned while still queued never ran and isn't tracked.
- After a step-7 failure, `WritePipeline` releases the lock only when the gateway reports no outstanding abandoned write, or when its deadline runs out, whichever is first; the tool's result is never delayed by that wait.
- Crash rule (AD-3): an `ONENOTE.EXE` that a `WerFault.exe`/`WerFaultSecure.exe` in this session targets (`-p <pid>`) doesn't count as running; a WER process in this session whose command line can't be read makes every OneNote count as crashing (fail safe: never start a hidden instance) when connecting only (activation or reconnect); a live COM reference's per-call check ignores it (Phill, 2026-11-12, Q3). `allowSilentActivation` keeps its meaning.
- Every OneNote HRESULT (`0x80042000`–`0x80042031`) has an explicit triage row; only codes already in AD-9's closed list are used. Messages and `userAction`s carry no page content.

**Never:** tools, use cases or validation for page edits (2.9, 2.10); a new `ErrorCode`; changing AD-3's timeout values or the settings schema; touching the spine (`architecture-onenote-mcp.md`), `spikes/`, `Directory.Build.props` or the tool contract; running integration tests or `OneNoteMcp.exe` (a live probe sitting is using OneNote); killing or closing OneNote.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Update ok | page XML, page-XML date string | `UpdatePageContent(xml, utcDate, xs2013, false)` called once | — |
| Delete ok | pageId, objectId, date | `DeletePageContent(..., utcDate, false)` called once | — |
| Bad date | `""`, `"x"`, `0001-01-01T00:00:00Z` | COM not called | `InvalidArgument` |
| Busy then ok | first attempt `0x80010001` | retried, succeeds | — |
| Disconnect | `0x800706BA` | not retried | `ComUnavailable` (pipeline adds `outcomeUnknown`) |
| Write hangs | call outlives `timeouts.call` | worker poisoned | `Timeout`; outstanding until the call returns |
| Write outstanding | another write / `NavigateTo` | COM not called | `ComBusy`, retryable |
| Read while outstanding | `GetHierarchy` | runs on a fresh worker | — |
| Settle | hung call returns | count drops; non-read calls run again; wait task completes | — |
| Pipeline lock | step 7 `Timeout`/`Cancelled` with write outstanding | result returned at once; lock released when settled or at deadline | — |
| Crash | ONENOTE pid targeted by WerFault | `IsOneNoteRunning()` false; reconnect refused | `OneNoteNotRunning`, crash wording |
| Unreadable WER, connecting | WerFault in session, no command line; no live reference | treated as crashing; nothing activated | `OneNoteNotRunning`, crash wording |
| Unreadable WER, live reference | same, while a COM reference is live | reference kept; reads and writes run (Q3) | — |
| Crash, live reference | WerFault names our ONENOTE pid | reference released per call | `OneNoteNotRunning`, crash wording |
| Bad date, OLE range | `1899-12-30T00:00:00Z` (OADate 0) or earlier, years 1–99 | COM not called | `InvalidArgument` |

</intent-contract>

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct)`, `Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct)`, `Task WaitForAbandonedWritesAsync(CancellationToken ct)` (completes at once when none). Doc: `PageWrite`, pipeline only.
- `src/OneNoteMcp.Xml/` -- new `OneNoteDates.cs`: `public static DateTime? ToUtc(string? value)` (null on empty/unparsable/MinValue; `DateTimeStyles.AdjustToUniversal | AssumeUniversal`, invariant culture). Unit-tested in Tests.
- `src/OneNoteMcp.Com/ComGateway.cs` -- replace `bool retryOnDisconnect` inside `RunAsync`/`AttemptAsync`/`AwaitAsync` with a private `CallKind { Read, AppState, Write }` (reads retry on disconnect; `NavigateTo` = AppState; `CreateNewPage`, the two new methods and `InvokeAsync(retryOnDisconnect: false)` = Write). New guarded fields `outstandingWrites` + a `TaskCompletionSource` for settle; `TrackAbandonedWrite(Task)` called from `AwaitAsync`'s poison path and its cancel-after-start path when kind is Write; non-read check first in `RunAsync` (before `LiveWorker`'s `ComUnavailable`). Keep `IsBusy`/`IsReconnectTrigger` as they are; update the class summary and `InvokeAsync` doc.
- `src/OneNoteMcp.Com/IProcessProbe.cs` -- `ProcessProbe.IsOneNoteRunning()` excludes crashing PIDs; add default interface member `bool IsOneNoteCrashing() => false` (test probes compile unchanged). Testable statics: `WerFaultTarget(string commandLine) -> int?` (`-p`/`/p`, case-insensitive) and `Running(IReadOnlyList<int> oneNote, IReadOnlyList<string?> werCommandLines) -> IReadOnlyList<int>`. Command line via `NtQueryInformationProcess(..., 60 /*ProcessCommandLineInformation*/)` with `PROCESS_QUERY_LIMITED_INFORMATION`, `DllImport` + `DefaultDllImportSearchPaths(System32)` like the existing natives.
- `ComGateway.EnsureActivatedAsync` (~l.444) -- not-running message: when `probe.IsOneNoteCrashing()`, "OneNote crashed and Windows is still closing it, so the server didn't start a hidden copy. Start OneNote desktop once it has closed, then retry."; otherwise the existing text (tests assert it).
- `src/OneNoteMcp.Com/HResultMap.cs` -- `internal readonly record struct Triage(ErrorCode Code, string? UserAction = null, bool? Retryable = null)`; `FrozenDictionary<int, Triage> Triaged` with every current mapping, the Design Notes rows and the generic codes (`0x80080005` keeps `ServerExecFailureUserAction`); `ToErrorCode`, new `UserAction(int)` and `Retryable(int)` read it, default `Internal`/null/null; `ToException` passes both. `ToolErrors.From` already prefers the exception's own values.
- `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- constructor gains `IOneNoteGateway gateway` after `writeLock`; in `finally`, after a step-7 failure, if `gateway.WaitForAbandonedWritesAsync` isn't complete, hand the hold to a background release (`WaitAsync(remaining deadline, clock)`, then `Dispose`, exceptions swallowed). Callers: `ServerSetup.cs:102`, `CreatePageToolTests.cs:451`, `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs:246`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- record `UpdateCalls`/`DeleteCalls`, honour `Failure` and per-method failure hooks like `CreateFailure`; `AbandonedWrites` (`TaskCompletionSource?`) answers `WaitForAbandonedWritesAsync`. `FakeGatewayTests.cs:50` excludes non-`Task<string>` methods already; check.
- Other `IOneNoteGateway` test doubles get the three members (throw `NotSupportedException`, or delegate where they wrap `inner`): `CreatePageToolTests.cs:521`, `FindPagesCacheTests.cs:541`, `GetHierarchyToolTests.cs:258`, `HierarchyReadToolTests.cs:349`, `LargeResultTestKit.cs:60`, `PageContentToolTests.cs:520,565`, `ParentWalkTests.cs:191`, `ProtectedSectionKit.cs:165`, `SearchToolTests.cs:480`. `WaitForAbandonedWritesAsync` returns `Task.CompletedTask` in every non-wrapping double.
- `tests/OneNoteMcp.Tests/FakeApplication.cs:100,102` -- record update/delete calls with their arguments (`Answer`/`OnCall` pattern); `ComGatewayLifecycleTests.cs` (`TestProbe` at :648, `CreateNewPage` tests ~:505-535) is the model for the new gateway tests.
- `tests/OneNoteMcp.Tests/HResultMapTests.cs` -- `ExpectedNonInternal` unchanged (Q2); add: every `0x80042xxx` name has an explicit `Triaged` row; a theory per Design Notes row checking code, non-null `userAction` and retryable flag; `ToException` carries them; and a `ToolErrors.From` check that an R row reads `retryable: true` on a read and on a write, and an `outcomeUnknown` one still reads false.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- extract the guard from `WriteAsync` into `EnsureTestTargetAsync`; new `tests/OneNoteMcp.Integration/PageWriteGatewayIntegrationTests.cs`: on a fresh page created in MCP Test's Seed section via the guard, read it, `UpdatePageContentAsync` adding an Outline with the page-XML date, re-read, `DeletePageContentAsync` that Outline, re-read; plus a stale date → `StaleWrite`. Opt-in like the others; not run here.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- resolve l.14, l.22-23/232 (abandoned-write `ComBusy`, busy confirmed), l.196 (crash), l.241 (lock); add: spine sync for AD-9 (triage) and AD-3 (WER detection, lock hold), 5.1 timeout evidence for epic 5.
- Don't touch: the spine, `spikes/`, `Directory.Build.props`, `tool-contract.json`, the plugin skill (no code or tool changes the model sees).

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Xml/OneNoteDates.cs` + tests -- date conversion -- AD-17.
- [x] `IOneNoteGateway.cs`, `ComGateway.cs`, `FakeGateway.cs`, `FakeApplication.cs`, the test doubles -- port methods, `CallKind`, abandoned-write tracking -- AD-3, AD-7.
- [x] `IProcessProbe.cs` -- WER crash detection + not-running wording -- AD-3 Activation.
- [x] `HResultMap.cs`, `HResultMapTests.cs` -- triage table -- AD-9.
- [x] `WritePipeline.cs` + its three constructions -- lock kept until settle/deadline -- deferred l.241.
- [x] `tests/OneNoteMcp.Tests/` -- `ComGatewayLifecycleTests` (matrix rows: update/delete args and UTC kind, bad date, busy retry for a write, no disconnect retry, write timeout → outstanding → `ComBusy` for write and `NavigateTo`, read passes, settle clears and completes the wait, queued-abandoned write not tracked, cancel-while-running tracked); `ProcessProbe` statics (`-p 123`, `/P 123`, no pid, null line); `CreatePageToolTests` (lock still held after a step-7 `Timeout` while `FakeGateway.AbandonedWrites` is pending, released when it completes, released at the deadline; result returned before release).
- [x] `TestNotebook.cs`, `PageWriteGatewayIntegrationTests.cs` -- the ticket's integration test (live step later).
- [x] `deferred-work.md` -- entries above.

**Acceptance Criteria:**
- Given a write that timed out in OneNote and hasn't returned, when any other non-read gateway call is made, then it returns `ComBusy` without touching COM, and once the hung call returns the next one runs.
- Given a pipeline whose step 7 timed out, when another process's write waits on `Local\OneNoteMcp.Write`, then it can't acquire it until the abandoned call returns or the first pipeline's deadline passes.
- Given every OneNote HRESULT, when mapped, then its code and `userAction` match the triage table, and none falls to the default arm.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, when run, then both pass.

## Open Questions

- **Q1 (resolved, Phill, 2026-11-12): write timeouts.** Keep AD-3's 30 s call timeout for every write, local and cloud, relying on `outcomeUnknown`, no retry, and the write lock held until the abandoned call returns (or the deadline runs out). Already built; no code change.
- **Q2 (resolved, Phill, 2026-11-12): three HRESULTs that fit other codes better.** Remap them through an AD-9 amendment in the next spine story: `0x80042017` hrInvalidName → `InvalidArgument`, `0x80042024` hrRecordingInProgress → `ComBusy`, `0x8004202E` hrRecycleBinEditFailed → `ObjectNotFound`. This story keeps them `Internal` and doesn't touch the spine; deferred-work records the amendment.
- **Q3 (resolved, Phill, 2026-11-12): an unreadable WER process.** The "treat OneNote as crashing" rule for a `WerFault`/`WerFaultSecure` whose command line can't be read applies only when connecting (activation or a reconnect, i.e. creating the COM reference). A live, healthy reference isn't released because of it, and reads and writes on it don't fail `OneNoteNotRunning`. A readable WER process that names our `ONENOTE.EXE` is a real crash and still drops a live reference per call.

## Implementation Notes

- Assumption (unattended, 2026-11-11; Q1): AD-3's 30 s call timeout stays for writes, local and cloud. 5.1 live runs, 2026-11-11: OneDrive hierarchy creates took 3.4–13 s, once 60 s+, and a call the gateway abandoned still completed 2–3 s later. Page writes weren't measured slow (spike 2.2). This story makes that case safe (`outcomeUnknown`, no retry, lock held until it settles) rather than longer; a per-class or cloud limit is a spine change for Phill.
- Assumption (Q2, conservative): no HRESULT changes code. AD-9 names `0x80042024` and `0x8004202E` deliberately `Internal`, and `InvalidArgument` is defined as the server's own refusal, so a remap needs an AD-9 amendment. Every OneNote code gets an explicit row; the triaged ones keep `Internal` with a specific `userAction`, and those whose action ends "then retry" carry `retryable: true` through the exception's own flag (AD-9 allows it). Recommended to Phill instead: `0x80042017` → `InvalidArgument`, `0x80042024` → `ComBusy`, `0x8004202E` → `ObjectNotFound` (as 2.4 Q8 does for recycle-bin targets), with the AD-9 amendment.
- Assumption (Q3): crash detection reads WER's command line; an unreadable one fails safe.
- Process (build lane): don't commit and don't push; the lane commits after review. Never run integration tests or anything that talks to OneNote.
- Build (2026-11-11): implemented per the Code Map. The port takes OneNote's date string; `ComGateway` converts it with `OneNoteDates.ToUtc` and refuses a bad one with `InvalidArgument` before COM. `CallKind` replaces the internal `retryOnDisconnect` flag; `InvokeAsync(retryOnDisconnect: false)` counts as a write. `WritePipeline` hands the lock to a background release after a step-7 failure while a write is outstanding, and evicts the written pages from the cache again when it settles. `IProcessProbe.IsOneNoteCrashing()` is a default interface member, so the test probes compile unchanged.
- Build: the implementing subagent finished its edits but never handed back a report; the lane verified its diff itself and applied the review patches itself.
- Verification: `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 1517 passed, 0 failed (three consecutive runs after the review patches). Under load from parallel runs, two pre-existing timing/file tests flaked once each and passed on rerun: `ReadGrantSettingsTests.ConcurrentWriters_LoseNoUpdate` (`File.Replace` "Unable to remove the file to be replaced") and `CreatePageToolTests.LockHeldElsewhere_PastTheWait_IsComBusy_Retryable_NoWrite` (unchanged by this story). Integration tests and the server exe were not run (a live probe sitting is using OneNote).

- Fixes after Phill's answers and the review (2026-11-12):
  - Q3: `IProcessProbe.IsOneNoteStillRunning()` (default: `IsOneNoteRunning()`) is the per-call check for a live reference; `ProcessProbe` implements it with `Running(..., connecting: false)`, which ignores an unreadable WER command line but still excludes an `ONENOTE.EXE` a readable one names. `IsOneNoteRunning()`/`IsOneNoteCrashing()` keep the fail-safe connecting rule. In `ComGateway.EnsureActivatedAsync`, the per-call check uses `IsOneNoteStillRunning()`; inside the activation lock, a reference another call created while this one waited is live, so it too gets the per-call check before the connecting one (decision: otherwise a WER process appearing between the two calls would release a reference that was just made, which Q3 forbids).
  - F1: `OneNoteDates.ToUtc` returns null for any date on or before the OLE epoch 1899-12-30 00:00 UTC (`OleAutomationEpoch`), not only `DateTime.MinValue`. COM sends a `DateTime` as an OLE `DATE`: the epoch is OADate 0, which OneNote reads as "no date" and skips the check, and a year before 100 overflows `ToOADate` (`Internal 0x80131516`). The gateway refuses them with the existing `InvalidArgument` and re-read `userAction` (Boundaries' "bad date" rule); negative OADates are refused too, since OneNote never writes one.
  - F2: `ComGateway.AbandonedWritesTracked` counts every write ever tracked and never drops. A write that never ran settles at once even if tracked, so `OutstandingWrites == 0` couldn't fail; the queued-write tests now assert the counter stays 0, and the running-write tests assert it reaches 1. Checked by mutation: tracking a write abandoned while queued fails `AWriteStillQueuedAtItsOwnDeadline_…` and `AWriteCancelledWhileQueued_…`.
  - F3: `AWriteAbandonedWhileQueued_NeverRan_AndIsNotTracked` no longer depends on a 100 ms window: the write is queued halfway through the hung read's 1 s, and the test accepts `ComBusy` (the poisoned worker, expected) or `Timeout` (its own deadline, after a 0.5 s stall); both mean it never ran. Pinning the poison path with no timing at all would need a clock seam in `ComGateway`, which this story doesn't add. `AWriteCancelledWhileQueued_…` drops its 100 ms delay: with a live reference the write is queued before the call returns.
  - F4: `CreatePageToolTests.AfterAStep7Timeout_TheWrittenPages_AreDroppedFromTheCacheAgain_WhenTheAbandonedWriteSettles` drives `WritePipeline` with a page write that names its page (create_page names none on failure): a page re-cached while the call is outstanding stays until it settles, then is dropped before the lock goes; other pages stay.
  - Forward note (2.9/2.10): recorded in deferred-work's AD-3 sync entry.
  - F1 live evidence (lead, 2026-11-12, cloud re-run of 5.1's date probes, all `DeleteHierarchy` soft deletes): `DateTime.MinValue` skips the date check entirely. Audit: the only `src/` paths that send a date to OneNote are `ComGateway.UpdatePageContentAsync` and `DeletePageContentAsync`, and both take it only through `OneNoteDates.ToUtc`, which refuses `MinValue` and anything up to OADate 0 before COM (the gateway theory now also covers `0001-01-01T00:00:00` with no offset and the epoch written with an offset). `src/` has no `DeleteHierarchy` wrapper yet (epic 5) and no deliberate `MinValue` "no check". The deliberate `MinValue` writes are all test-side, through the raw seam or `ProbeSession`, for fixtures and probes: `BallastFixture.cs:220,265`, `FixtureCapture.cs:97,401,438`, `LifecycleScenarioTests.cs:153`, `PageContentToolsIntegrationTests.cs:51`, `SearchToolsIntegrationTests.cs:51`, `ProbeSession.cs:183`, and the probe rows in `WriteProbes/LocalWriteProbes.cs`, `CloudWriteProbes.cs` and `SearchProbes.cs`. Left unchanged.

## Plan Change Log

## Review Triage Log

### 2026-11-11 — Review pass
- verdicts: 6 findings — high 0, medium 1, low 5, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `HResultMap`: `0x80042002`, `0x80042017` and the five insert rows told the model to retry while `retryable` stayed false — reworded the three texts so none says "retry"; the deferred-work claim that only the R rows end "then retry" now holds.
  - `[medium]` `[patch]` `ProcessProbe`/`ComGateway`: an unreadable WER process in the session fails every call `OneNoteNotRunning` (reads included) and the per-call check drops a live reference, for as long as it lives — this is the intent's fail-safe rule (Boundaries, Q3), so the code stays; the deferred-work risk note understated it and now states it exactly, and it is an open question for Phill.
  - `[low]` `[reject]` `ProcessProbe.IsOneNoteRunning()` scans the process list three times per gateway call instead of once — real but a few ms per call, not seen in use; the fix is a restructuring (one enumeration, or caching), more than a direct correction.
  - `[low]` `[patch]` `ComGateway.EnsureActivatedAsync`: the crash error carried no `userAction`, so the envelope showed the generic "Start OneNote desktop, then retry." — added `CrashingUserAction` ("Wait until the crashed OneNote has closed, …") and asserted it; a plain not-running error keeps the default.
  - `[low]` `[patch]` deferred-work 2.7 entry said reads run "on a fresh worker" while a write is outstanding; after a cancel the worker isn't poisoned until the deadline, so a read queues behind the write — corrected the text (behaviour unchanged from before this story).
  - `[low]` `[patch]` the timeout-while-queued and cancel-while-queued abandon paths had no test — added `AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked` and `AWriteCancelledWhileQueued_NeverRan_AndIsNotTracked` (plus `ANotRunningOneNote_KeepsTheCodesDefaultUserAction`); 5 repeated runs green.

### 2026-11-12 — Follow-up review (lead), fixes approved by Phill
- findings: 4, all patched
  - `[patch]` F1 `OneNoteDates`: 1899-12-30 became OADate 0, which skips OneNote's date check, and years 1–99 overflowed to `Internal 0x80131516` — both now refused before COM with `InvalidArgument`.
  - `[patch]` F2 the queued-write tests' not-tracked assertions couldn't fail — now assert `AbandonedWritesTracked`; mutation-checked.
  - `[patch]` F3 `AWriteAbandonedWhileQueued_…` depended on a 100 ms window — made deterministic in pass/fail (see Implementation Notes).
  - `[patch]` F4 the second cache eviction after a settled abandoned write was untested — new pipeline test.

## Design Notes

Triage: every row below stays `Internal` (Q2); `userAction` gist from OneMore's `ErrorCodes.cs` descriptions; "R" = `retryable: true` override. Already-mapped rows keep their code and the code's default action.
- `0x8004200B` hrSectionReadOnly: R; OneNote reports the section read-only (e.g. view-only share); ask the user for edit access, then retry.
- `0x8004200C` hrPageReadOnly: R; page read-only in OneNote; ask the user to make it editable, then retry.
- `0x80042022` hrFutureContentLoss: the change would lose content from a newer OneNote; don't repeat it, edit that content in OneNote.
- `0x80042024` hrRecordingInProgress: R; recording in progress; retry after the user stops it.
- `0x8004202E` hrRecycleBinEditFailed: target is in the recycle bin; use a current ID or have the user restore it.
- `0x8004201C` hrDisabledByPolicy: an administrator policy disables this; retrying won't help.
- `0x80042017` hrInvalidName: OneNote refused the name; choose another.
- `0x80042002` hrCreatingSection: check the folder is writable and the name valid.
- `0x80042003` hrOpeningSection: R; the user opens the section in OneNote to see why (syncing, missing file), then retry.
- `0x8004201E` hrLegacySection: R; OneNote 2007-or-earlier section; the user upgrades the notebook, then retry.
- `0x80042007/08/09/0D/16` hrInserting{Image,Ink,Html,OutlineText,File}: Internal; re-read the page to see what was applied, then fix that element.
- `0x8004200A` hrNavigatingToPage: Internal; re-read to check the ID.
- `0x8004201F` hrMergeFailed, `0x8004202D` hrConvertFailed, `0x80042031` hrPublishFormatUnsupportedForLabels: Internal; operation-specific text (epics 3/5 refine).
- `0x80042013`, `0x80042025`–`0x8004202A`, `0x8004202B`, `0x8004202C`, `0x8004202F`: Internal, default action; no v1 call reaches them (selection, linked notes, unread, IM).

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

**Manual checks (if no CLI):**
- Live, later (Phill): `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter PageWriteGatewayIntegrationTests`; and a dry look that no hidden `ONENOTE.EXE -Embedding` appears after a forced reconnect.

## Auto Run Result

- **Status:** built (2026-11-11). Not marked done; not pushed.
- **Summary:** `UpdatePageContentAsync`, `DeletePageContentAsync` and `WaitForAbandonedWritesAsync` on the port and both gateways; AD-3's abandoned-write rules in `ComGateway` (non-read calls `ComBusy` while a write the caller stopped waiting for still runs; busy retries kept for writes); `WritePipeline` keeps `Local\OneNoteMcp.Write` after a step-7 failure until that write settles or the deadline runs out, without delaying the result; WER-based crash detection in `ProcessProbe` so a reconnect never starts a hidden OneNote; an explicit triage row for every named HRESULT in `HResultMap` (codes unchanged, specific `userAction`s, five retryable overrides).
- **Files:**
  - `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- three port members.
  - `src/OneNoteMcp.Xml/OneNoteDates.cs` -- OneNote date string to UTC `DateTime`.
  - `src/OneNoteMcp.Com/ComGateway.cs` -- page writes, `CallKind`, abandoned-write tracking, crash wording and userAction.
  - `src/OneNoteMcp.Com/IProcessProbe.cs` -- WER detection (`Running`, `WerFaultTarget`, `IsOneNoteCrashing`, command line via `NtQueryInformationProcess`).
  - `src/OneNoteMcp.Com/HResultMap.cs` -- `Triage` table, `UserAction`, `Retryable`.
  - `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- gateway parameter; lock kept until settle or deadline.
  - `src/OneNoteMcp.Server/ServerSetup.cs`, `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs` -- the new pipeline argument.
  - `tests/OneNoteMcp.Integration/TestNotebook.cs`, `tests/OneNoteMcp.Integration/PageWriteGatewayIntegrationTests.cs` -- guard extracted; the ticket's integration test (not run).
  - `tests/OneNoteMcp.Tests/*` -- gateway lifecycle, HRESULT, date and pipeline-lock tests; fakes and test doubles gain the members.
  - `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- four resolved entries, spine sync, epic 5 timeout evidence, the live step.
- **Review:** quick lens, 6 findings: 5 patched (1 medium, 4 low), 0 deferred, 1 rejected (triple process scan per call: a few ms per call, and the fix is a restructuring).
- **Follow-up review recommended:** false (patched: high 0, medium 1, low 4).
- **Verification:** build 0 warnings, 0 errors; unit tests 1517/1517 (three runs). Matrix audit: every row has a passing test. Integration tests not run (live step).
- **Residual risks:** WER detection is unproven live (WerFault could run outside the session, or its command line differ); an unreadable WER process blocks connecting (activation, reconnect) while it lives, though a live reference keeps working (Q3, narrowed 2026-11-12); writes keep the 30 s call timeout (Q1, confirmed); the spine's AD-3/AD-9 text lags this build until a spine story syncs it.
