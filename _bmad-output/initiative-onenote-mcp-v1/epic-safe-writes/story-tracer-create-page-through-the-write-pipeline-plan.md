---
title: 'Tracer: create_page through the write pipeline'
type: 'feature'
ticket: '4'
created: '2026-11-10'
status: 'built'
baseline_revision: '9ccedd691f7d2edcb126f9792928a3efd01a5c80'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** No write path exists. Every later write story (2.5–2.11) needs the AD-7 pipeline, the AD-18 write lock, write grants in `settings.json` and a first write tool to hang them on.

**Approach:** Build the AD-7 step runner for the `Create` class only, the `Local\OneNoteMcp.Write` mutex, the `notebooks{}` settings model, `grant --write`, `CreateNewPage` on the port and both gateways, and a thin `create_page` tool. A write that fails at the COM call with `Timeout`, `ComUnavailable`, `Cancelled` or `0x800706BE` reports `outcomeUnknown`.

## Boundaries & Constraints

**Always:**
- AD-7 order: (1) `EnsureWritable`, then the write grant of the notebook that owns `sectionId`, resolved through the gateway (`GetHierarchyParent` chain, then the notebook's live `path` → `NotebookKey`), settings re-read at every check; (2) lock; (3) `GetHierarchy(sectionId, Self)`; (4) validate; (5–6) none for `Create`; (7) `CreateNewPage`; drop written page IDs from the page-text cache whatever the outcome; (8) release.
- Pipeline deadline 120 s; a deadline expiry is `Timeout`. Lock wait expiry is `ComBusy` (retryable). `AbandonedMutexException` counts as acquired. Reads never take the lock.
- The mutex is acquired and released on one dedicated thread per hold (a named `Mutex` is thread-affine; the pipeline awaits between steps).
- A page write in a password-protected section needs only the write grant and never raises the read dialog (AD-6, AD-21). Locked section → `SectionLocked`.
- `notebooks{}` entries: `{name, idAtGrant, write, permanentDelete, grantedAt, grantedVia}`; `grantedVia` ∈ `elicitation|cli|created`. Two keys equal ignoring case → `SettingsInvalid`, counted like the `protectedSections{}` duplicate (revoke still runs, 2.6). Granting again keeps an existing `permanentDelete`.
- Messages and logs carry IDs, never names or content (AD-9, AD-13). `WriteNotGranted`'s `userAction` names `grant --write "<notebook name>"` and says never to run it.
- Integration tests write only to MCP Test, with grants in a temporary `settings.json`. Contract snapshot and `<Version>` updated per AD-14.

**Never:** the write-grant dialog, `grant --revoke` for notebooks, `list_grants` write rows (2.6); unknown-argument rejection or the `InvalidArgument` code (2.5); abandoned-write `ComBusy`, crash detection, HRESULT triage (2.7); journal (2.8); any `NavigateTo`; any change to the spine.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Granted | section in a notebook with `write: true` | `ok`, new page ID in `meta` | — |
| Ungranted | no `notebooks{}` entry, or `write: false` | no COM write | `WriteNotGranted`, retryable, CLI `userAction` |
| Settings invalid / newer | `Invalid` or `NewerSchema` | no COM write | `SettingsInvalid`; newer schema gets update advice |
| Locked section | `locked="true"` at step 3 | no COM write | `SectionLocked` |
| Protected, unlocked, no read grant | write grant only | `ok`, no dialog | — |
| Unknown ID | `GetHierarchyParent` fails | — | `ObjectNotFound` |
| COM call fails ambiguously | `Timeout`/`ComUnavailable`/`Cancelled`/`0x800706BE` at step 7 | — | `outcomeUnknown: true`, `retryable: false` |
| Lock held elsewhere past the wait | other process holds it | no COM write | `ComBusy` |

## Decisions (Phill, 2026-11-10, plan review `review/2.4-plan-questions.md`)

- **Q1 (b):** `create_page(sectionId, style?)`, `style` ∈ `default|blankPageWithTitle|blankPageNoTitle`, default `blankPageWithTitle`.
- **Q2 (a):** the new ID is `meta.pageId`; epic 5 adds `meta.sectionId` for its creates.
- **Q3 (a):** `<Version>` `0.9.0-rc.1`, contract `0.9`; 2.5 rides on it unless a release ships between.
- **Q4 (a):** `grant --write` shows the matched notebook's displayed name and path and grants only when the user types `yes`.
- **Q5 (a):** the lock wait is `timeouts.callSeconds` (30 s default), capped by the remaining pipeline deadline.
- **Q6 (b):** the two-process test puts its pages in a dedicated `Create tests` section of MCP Test and soft-deletes them at the end (`DeleteHierarchy`, `deletePermanently: false`). Constraints: delete only the page IDs this run's `create_page` calls returned; before each delete a fresh `GetHierarchy` must show the ID is a `one:Page` inside MCP Test (never the notebook or a section; `TestNotebookGuard.Contains` alone isn't enough); the clean-up helper lives in the test's own file, not `TestNotebook.cs`; a failed clean-up is logged and never fails the test; the output records what the soft delete did (the page's ID afterwards, whether it's in the recycle bin) as evidence for epic 5.
- **Q7 (a):** `NewerSchema` → `SettingsInvalid`, `retryable: false`, `userAction`: update OneNoteMcp (the plugin) and restart the client; don't edit `settings.json`.
- **Q8 (a):** a non-section ID, or a section with `isInRecycleBin`/`isDeletedPages`, → `ObjectNotFound` before COM, with a `userAction` asking for a section's ID.
- **Q9 (a):** keep the plan whole (~2,600 tokens). Skill: a `create_page` row, call it only when the user asks for a page, intro no longer says read-only. README: a tool row and a `grant --write` line.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `CreateNewPageAsync(sectionId, NewPageStyle, ct)`; Core enum `NewPageStyle` beside `PageInfo.cs`.
- `src/OneNoteMcp.Com/ComGateway.cs:218-229` -- copy `NavigateToAsync`'s shape, `retryOnDisconnect: false`; `Interop/Enums.cs:48` and `IApplication.cs:45` already declare it. Map styles like `MapPageInfo`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- records creates, returns a synthetic ID, honours `Failure`; `FakeApplication.cs:93` scriptable `CreateNewPage` for gateway tests.
- `src/OneNoteMcp.Core/Access/HierarchyWalk.cs` -- reuse `AncestorsAsync` for a notebook-owner walk; `ReadAccess.cs` is the model for a `WriteAccess` sibling. `GrantKeys.NotebookKey`, `GrantKeys.NameMatches`, `GrantCli.Run` reuse as is.
- `src/OneNoteMcp.Core/Settings/SettingsStore.cs:98-133,275-318` -- add `ReadNotebooks` before `ReadProtectedSections`, same field-naming errors; `SettingsSnapshot.cs` gains `Notebooks` (valid files only) and `NotebookEntries`; `EnsureWritable` (`:233`) gets the NewerSchema advice via `OneNoteException.UserAction/Retryable`.
- `src/OneNoteMcp.Core/Settings/SettingsWriter.cs:340-396` -- `Change` is hard-wired to `protectedSections`; generalise to the root and add `SaveWriteGrant`. Keep the mutex, re-read and `File.Replace` path.
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs` -- add `--write [name]`; reuse `Pick`, `Ask`; matching via a new Core `OpenNotebooks.Find(xml)` (Server must not import Xml, A3) over an Xml helper listing `one:Notebook` nodes (`HierarchyNode` has `DisplayName`).
- `src/OneNoteMcp.Server/ToolRunner.cs:63-79` -- unchanged mapping; `ToolErrors.IsRetryable` already makes Timeout/ComUnavailable/Cancelled non-retryable for writes.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs` -- add the new-ID field (Q2).
- `src/OneNoteMcp.Core/UseCases/PageTextCache.cs:127` -- `Remove(pageId)` is the eviction.
- `src/OneNoteMcp.Server/ServerSetup.cs` -- register the lock (with a logging callback), `WriteAccess`, pipeline, use case, tool.
- `tests/OneNoteMcp.Tests/NavigateToToolTests.cs:98-115`, `spikes/stress-harness/Program.cs:88-99`, `Calls.cs:264` and its README -- read-only checks exempt the write tools; the harness refuses to call them.
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs:86` (count 12), `PluginPackagingTests` (skill and README must name every tool), `Directory.Build.props:4`.
- `tests/OneNoteMcp.Integration/TestNotebook.cs` -- guarded `WriteAsync`, `EnsureAsync` (seeds a section via `OpenHierarchy`); `ProtectedSectionIntegrationTests.cs:33-50` is the temp-settings precedent.

## Tasks & Acceptance

**Execution:**
- [x] Port, `NewPageStyle`, `ComGateway`, `FakeGateway`, `FakeApplication` -- `CreateNewPage` with no disconnect retry.
- [x] `src/OneNoteMcp.Core/Settings/*` -- `notebooks{}` read, validate, write; NewerSchema advice. Tests extend `SettingsStoreTests`, `SettingsDuplicateKeyTests`.
- [x] `src/OneNoteMcp.Core/Writes/WriteLock.cs` -- dedicated-thread hold, `ComBusy` on wait expiry, abandoned = acquired, release callback with UTC acquire/release times. Unit tests use a unique mutex name per test: two holds exclude, a wait expires as `ComBusy`, an abandoned holder counts as acquired. Cross-process exclusion is the integration test.
- [x] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- step 1 as in Always.
- [x] `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- the step runner: deadline, steps 1–8, `outcomeUnknown` wrapping at step 7 only, OCE from the deadline → `Timeout`, from the client at step 7 → `Cancelled` + `outcomeUnknown`, cache eviction in `finally`.
- [x] `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` (`Create` class) and `src/OneNoteMcp.Server/Tools/CreatePageTool.cs` -- thin; annotations readOnly false, destructive false, idempotent false, openWorld false.
- [x] `GrantCommand.cs` + Core `OpenNotebooks` + Xml helper -- `grant --write`; usage text updated.
- [x] `tests/OneNoteMcp.Tests/CreatePageToolTests.cs` -- every matrix row against `FakeGateway`, plus: lock released after every outcome; cache entry for the page dropped; deadline expiry; protected section never asks the prompt.
- [x] `GrantCommandTests.cs` -- `--write`: single match, ambiguous pick, no match, empty path, non-interactive refusal.
- [x] Harness, `NavigateToToolTests`, contract snapshot, `<Version>`, binding count -- per Code Map.
- [x] `plugin/skills/guide/SKILL.md`, `README.md` -- minimal mentions (Q8).
- [x] `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs` -- on MCP Test: granted create through the real gateway; ungranted → `WriteNotGranted` with no page added; two `OneNoteMcp.exe` processes over stdio firing concurrent `create_page`, their lock-hold log lines never overlap (Q6).
- [x] `deferred-work.md` -- mark the NewerSchema advice and the outcomeUnknown entries resolved with evidence.

**Acceptance Criteria:**
- Given a hold in progress, when a second hold is requested in the same process, then it waits until the first releases.
- Given any step-7 failure in the list, when the envelope is built, then `outcomeUnknown: true`, `retryable: false` and the AD-9 userAction; a failure before step 7 never sets it.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and the unit suite, when run, then both pass.

## Implementation Notes

- The runner is `WritePipeline` over an abstract `WriteOperation<T>` (steps 1, 3, 4, 7 abstract; 5–6 virtual no-ops); `Create`'s steps are the private `CreatePageUseCase.CreatePage`.
- Lock wait capped by the deadline: when the cap applied and the wait expires, it reports `Timeout` (nothing written), not `ComBusy`, so both Always rules hold.
- `HierarchyNode` gained `InRecycleBin` (`isInRecycleBin` or `isDeletedPages`) and `Notebooks(xml)`; `HierarchyWalk` gained `OwningNotebookAsync`. A notebook ID fails step 1 with Q8's `ObjectNotFound`.
- `SettingsSnapshot` gained `DuplicateSectionKeys` and `DuplicateNotebookKeys` beside `DuplicateGrantKeysOnly`; `list_grants`' revoke advice now keys on the section duplicate only, so a notebook duplicate gets the generic fix-the-file advice until 2.6.
- The lock log line also carries `abandoned=`. `SettingsStore` requires every `notebooks{}` field (a missing `permanentDelete` makes the file invalid, failing closed).
- Two-process test: the exe is `src/OneNoteMcp.Server/bin/<config>/net10.0-windows/OneNoteMcp.exe`, or `ONENOTE_MCP_TEST_EXE`; each server logs to its own temp `ONENOTE_MCP_LOG_DIR`. The soft delete passes the fresh read's `lastModifiedTime` (AD-17 forbids `MinValue`); a `StaleWrite` there is logged as evidence, not a failure.
- Not run (safety limits): the integration suite, the stress harness, `grant`, the server exe. Built only.

## Plan Change Log

## Review Triage Log

Pass 1 (workflow: acceptance, concurrency, security and edge-case lenses, each verified by a skeptic), 2026-11-10. Security lens: no findings. Verdicts: high 0, medium 0, low 5 (two lenses filed the same lock-wait issue, merged as A), false 1. Phill chose all four routes (Inbox, 2026-11-11).

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| A | A lock wait capped by the deadline expires as `Timeout` with `retryable: false`, though nothing was written (`WritePipeline.cs:81`) | low | patch | Real when `callSeconds` ≥ 120 or step 1 runs past 90 s. `DeadlineExpired` now passes `retryable: true` when `written` is false; test updated |
| B | No test for a deleted section carrying only `isInRecycleBin` | low | patch | Code already refuses it (`HierarchyNode.InRecycleBin`). Added `BinnedId` to `NotALiveSection_IsObjectNotFound_AskingForASectionId_NoWrite` |
| C | The write lock is released while a timed-out or cancelled step-7 call may still be running on the STA worker | low | defer | Harmless for `create_page` (worst case two pages); matters once basis-checked writes ship (2.9). Added to 2.7's scope in deferred-work.md |
| D | `create_page` departs from AD-14's snake_case-of-COM-method rule (`create_new_page`) | low | defer | Phill chose the name (Q1); AD-21 already uses it. One line in AD-14 recording the exception, in the next spine story (deferred-work.md) |
| E | `outcomeUnknown` set on step-7 failures that never reached COM | false | rejected | AD-7/AD-9 set it on those codes "whatever the source"; a precise flag needs an AD-9 amendment first |

## Design Notes

The runner owns order, lock, deadline and outcome; a class supplies its steps (`Create`: notebook target, section read, validation, call, written IDs). Steps 5–6 are hooks `Create` leaves empty, so 2.9 adds them without reshaping the runner. The lock log line (`write lock correlationId=… acquiredUtc=… releasedUtc=… waitMs=…`) is IDs and timings only (AD-13) and is what the two-process test reads. The new page's ID is returned even in a protected section without a read grant: the model made the page, and it reveals nothing inside the section.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet build spikes/stress-harness -c Release` -- builds.

**Manual checks (Phill, live):**
- `grant --write "MCP Test"`; `create_page` from Claude Code adds a page in MCP Test; a create in an ungranted notebook returns `WriteNotGranted`.
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` -- all pass, two-process test included.
- Stress harness on MCP Test -- 0 mismatches.
- Security review of the diff before merge (epic 2 breakdown: 2.4 controls access).
