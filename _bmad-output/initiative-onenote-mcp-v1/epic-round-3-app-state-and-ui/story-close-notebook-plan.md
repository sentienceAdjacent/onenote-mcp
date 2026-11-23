---
title: 'close_notebook'
type: 'feature'
ticket: '4'
created: '2026-11-21'
status: 'done'
baseline_revision: 'a585a4d1cea1f83b3ae3115f24339e4a36a16dc3'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-3-app-state-and-ui/story-tracer-sync-hierarchy-through-the-appstate-pipeline-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Nothing closes a notebook from Claude: the user closes every notebook in OneNote, and a local `CloseNotebook` sent straight after a write lost the edit for good (spike 4.1 C2).

**Approach:** A `close_notebook` tool on story 4.3's `AppStatePipeline`, which gains the write-lock path (step 2, the abandoned-write check after it, step 8 holding the lock while an abandoned close settles). Under the lock it checks the ID against one read of the open notebooks, then runs `SyncHierarchy`, a ~10 s settle and `CloseNotebook(force: false)` (spine AD-7 `AppState` `close_notebook`).

## Boundaries & Constraints

**Always:**
- Notebook ID only, checked under the lock (step 3): not an open notebook but a known section, section group or page → `InvalidArgument`; unknown → `ObjectNotFound`; both before any state-changing call, each with a userAction to re-read the notebooks. Empty ID → `InvalidArgument` at step 1.
- Never `force` (Q1). No grant needed or given (Q2). Lock taken (OQ1 a); `ComBusy` right after step 2 while an abandoned write is outstanding.
- Step 7 order: `SyncHierarchy(notebookId)`, settle 10 s, `CloseNotebook`. A failure, timeout, deadline or cancel in the sync or the settle → not closed, that error, never `outcomeUnknown`, lock released at once. A `CloseNotebook` `Timeout`/`ComUnavailable`/`Cancelled`/`0x800706BE` → `outcomeUnknown`; it is sent as `CallKind.Write`, so it counts as an abandoned write and step 8 keeps the lock until it settles (AD-3, AD-18). Any other `CloseNotebook` failure keeps its AD-9 code with the userAction "let the notebook sync, or close it in OneNote".
- The settle counts against the 120 s deadline; the `CloseNotebook` call keeps the 30 s timeout (AD-3).
- The search cache is untouched (OQ6 a). Logs carry IDs only (AD-13).
- Description starts "User-visible" (AD-11), promises no focus behaviour. Annotations: not read-only, not destructive, not idempotent.

**Never:** `force: true`; an automatic retry; dropping grants; reopening anything; a URL or path argument; any spine change; changing `WritePipeline`; setting the integration env var in the build session.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Close ok | open local or cloud notebook ID | calls in order sync, settle, close; `ok` with the result below | — |
| Section/group/page ID | known non-notebook ID | no sync, no close | `InvalidArgument` |
| Unknown or old ID | not in the open notebooks, unknown to `GetHierarchy` | no sync, no close | `ObjectNotFound` |
| Abandoned write outstanding | FakeGateway `AbandonedWrites` pending | nothing read under the lock | `ComBusy`, retryable |
| Sync fails / times out | `SyncHierarchy` throws | no close | its code, not `outcomeUnknown`, lock released |
| Cancel or deadline in the settle | — | no close | `Cancelled` / `Timeout`, not `outcomeUnknown` |
| Close times out | `CloseNotebook` `Timeout` | — | `outcomeUnknown`, lock held until settled |
| Close refused | other HRESULT, e.g. `0x8004201D` | — | its AD-9 code, close userAction |

## Decisions (Phill, 2026-11-22, Inbox plan approval)

- **Q1 (a):** an `ok` result's `xml` is the closed notebook's `one:Notebook` element as read under the lock (name, path, ID), with `meta.objectId` and `meta.path`, plus the warning `notebookClosed` (this story names it, AD-9): it synced and waited ~10 s first, every ID changes on reopen, OneNote's view may go blank, and its grants survive; its userAction gives the reopen route (`open_notebook` for a local path, OneNote for a cloud one). No new meta field.
- **Q2 (a):** if the live sitting shows the edit made just before the close was lost, the build stops at `built` with a failed sitting and comes back to Phill (the settle length or a different save step is a spine Deferred row to reopen). It is a sitting rule, nothing to code.
- **Q3:** the plan is kept whole.

</frozen-after-approval>

## Code Map

- `s43:src/OneNoteMcp.Core/AppState/AppStatePipeline.cs`, `AppStateOperation.cs` (story 4.3, not yet merged) -- add the lock path here: the class doc says 4.4/4.5 add it. Model step 2 and step 8 on `Writes/WritePipeline.cs:194-210` (lock wait capped by deadline, abandoned check) and `:294-311, 342` (`settling`, `ReleaseWhenSettledAsync`); don't change `WritePipeline`.
- `src/OneNoteMcp.Core/Writes/WriteLock.cs:26,47` -- `AcquireAsync(correlationId, wait, ct)` → `WriteLockHold`.
- `src/OneNoteMcp.Com/Interop/IApplication.cs:51` -- slot 12 `CloseNotebook(bstrNotebookID, force)` already declared.
- `src/OneNoteMcp.Com/ComGateway.cs` -- `CallKind` (~1245), `TrackAbandonedWrite` (~1113, only `CallKind.Write`); model `CloseNotebookAsync` on `CreateNewPageAsync` (~258), never retried. s43's `SyncHierarchyAsync` (~265) is reused as is.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `CloseNotebookAsync(notebookId, ct)`.
- `src/OneNoteMcp.Xml/HierarchyNode.cs:35` `Notebooks(xml)` (Id, Name, Path); `HierarchyTarget.Read(selfXml).Kind` (`HierarchyTarget.cs:48`) for a non-notebook ID's kind.
- `src/OneNoteMcp.Core/Writes/CloudSettle.cs` -- pattern for an injected `Func<TimeSpan, CancellationToken, Task>` settle (`MoveContainerWrite.cs:106`, `MoveContainerToolTests.cs:200`).
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` (s43 adds `SyncRequested`), `ToolSuccess.cs`, `ToolMeta.cs` (`ObjectId`, `Path`).
- `src/OneNoteMcp.Server/Tools/` (s43's `SyncHierarchyTool.cs` as model), `ServerSetup.cs`.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- mirror s43's `SyncCalls`/failure/hook for `CloseCalls`; one ordered call log for the sync→settle→close order. `FakeApplication.cs:104` scripts `CloseNotebook`.
- `tests/OneNoteMcp.Integration/TestNotebook.cs:1357,1449` -- `CheckClose` already allows throwaways b, c and Cloud (spike 4.1) and refuses MCP Test/MCP Test Cloud; `ReopenThrowawayNotebookAsync` (:523), `OpenExistingAsync` (:584). Use-case integration tests wrap `ComGateway` in a `GuardedGateway` (`MergeSectionsIntegrationTests.cs:433`).
- Contract: `ToolContractTests.cs:81`, `Contract/tool-contract.json`, `McpSdkBindingTests.cs` count; `PluginPackagingTests` (skill and README name every tool); `spikes/stress-harness/Calls.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `Core/AppState/AppStatePipeline.cs`, `AppStateOperation.cs` -- `TakesWriteLock` (false by default) with an injected `WriteLock`; for those, the `ComBusy` check moves after step 2; step 8 as `WritePipeline`'s. A way for an operation to mark when its state-changing call is sent, so failures before it are never `outcomeUnknown` (also the deadline's).
- [x] Port, `ComGateway`, `FakeGateway`, `FakeApplication` -- `CloseNotebookAsync`, `CallKind.Write`, `force: false`.
- [x] `Core/UseCases/CloseNotebookUseCase.cs`, `Server/Tools/CloseNotebookTool.cs`, `ServerSetup.cs` -- the tool, its settle (injectable), its result (Decision Q1: the `one:Notebook` XML, `meta.objectId`/`meta.path`, warning `notebookClosed`); `ToolWarnings.NotebookClosed`.
- [x] Unit tests: `AppStatePipelineTests` (lock taken, released, kept while an abandoned close settles, `ComBusy` after step 2); `CloseNotebookToolTests` for every matrix row; `ComGatewayLifecycleTests` (`CloseNotebook` tracked as an abandoned write, never retried).
- [x] Contract snapshot, tool list, binding count.
- [x] `tests/OneNoteMcp.Integration/CloseNotebookIntegrationTests.cs` -- through the use case over a `GuardedGateway` (`CheckClose`): give throwaway b a test write grant in the run's own settings store, close b (not on screen), reopen with `ReopenThrowawayNotebookAsync`, `list_grants` shows the grant `active`; the guard refuses MCP Test.
- [x] `plugin/skills/guide/SKILL.md`, `README.md`, `spikes/stress-harness/Calls.cs` -- a `close_notebook` row (only when the user asks); the harness refuses it.

**Acceptance Criteria:**
- Given a close whose `CloseNotebook` timed out, when a write follows inside the deadline, then the write waits on the lock, and once the close settles it runs.
- Given the built server, when the tool list is read, then `close_notebook` is listed and the snapshot shows only that addition.

## Design Notes

- The lock goes into `AppStatePipeline`, not `WritePipeline`: the spine puts both tools on the `AppState` pipeline, and 4.3's plan reserved the lock path for 4.4 and 4.5. 4.5 reuses it.
- A non-notebook ID's kind needs one `GetHierarchy(id, self)` read; IDs don't encode kind reliably.
- Local vs cloud for the reopen hint: the notebook's `path` is an `https` URL for a cloud notebook.
- The result's notes are the warning `notebookClosed` (Decision Q1), as 4.3's `syncRequested` is a warning that informs; `ToolMeta` is unchanged.

## Live sitting (Phill, about 15 minutes; hitl)

Before: OneNote open in one window on an MCP Test page, online, with MCP Test, MCP Test Cloud, MCP Test Throwaway b and MCP Test Throwaway Cloud open. The build session stops any old `OneNoteMcp.exe` and re-points `onenote-dev` at the new build.

1. Phill runs `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter CloseNotebook`. It passes. Throwaway b may vanish from OneNote's list and come back (the list can lag about a minute).
2. In Claude Code he asks to add the line "settle check" to a page in Throwaway b, then straight away to close Throwaway b. He sees `ok` with b's name and path and the notes; b leaves the list.
3. He reopens b in OneNote: File > Open > Browse, then, in b's folder (tree below), `Open Notebook.onetoc2`. He opens that page: **expect the line there.** If it isn't, he says so and the story stops (Decision Q2).
   ```
   <throwaway root>
   └── MCP Test Throwaway … b      <- open this folder
       └── Open Notebook.onetoc2   <- pick this
   ```
4. File > Info > View Sync Status: he notes each notebook's status.
5. He asks to add a line to a page in Throwaway Cloud, then straight away to close Throwaway Cloud. He sees "OneNote is syncing changes" close by itself (he doesn't press Cancel), then `ok`.
6. He reopens Throwaway Cloud in OneNote straight after (File > Open, under his OneDrive), right-clicks it > Sync This Notebook Now, and checks the line is on the page.
7. View Sync Status again: he reports any notebook that changed, `0xE0000644` or "Incomplete sync".

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, snapshot included.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.

**Manual checks:**
- The live sitting above.

## Implementation Notes

- Pipeline (`AppStatePipeline`, `AppStateOperation`): `TakesWriteLock` (false by default) with a `WriteLock` and a lock wait (`timeouts.callSeconds`, read per call) given to the constructor; `RunAsync` takes the correlation ID for the lock's log line. For a lock taker the order is step 1, step 2 (wait capped by the deadline; a wait the deadline ended is `Timeout`), the `ComBusy` check, steps 3–4, step 7, step 8 (`ReleaseWhenSettledAsync`, as `WritePipeline`'s). The marked send is `MarksStateChange` + `MarkStateChangeSent()`; `OutcomeMayBeUnknown` decides `outcomeUnknown` for an ambiguous failure, the client's cancel and the deadline. `WritePipeline` is unchanged.
- Gateway: `IOneNoteGateway.CloseNotebookAsync` (default `NotSupported`); `ComGateway` sends `CloseNotebook(id, force: false)` as `CallKind.Write`, refusing an empty ID before COM.
- Use case: a sync failure keeps its code and message with "wasn't closed" added; a `Timeout`/`ComUnavailable`/`Cancelled` from the sync is marked retryable (nothing closed). An ambiguous close failure's advice ("let it sync, or close it in OneNote") goes in the message, since AD-9's envelope fixes `userAction` for `outcomeUnknown`; any other close failure gets `CloseUserAction`. The `notebookClosed` userAction names `open_notebook` "if this server lists that tool" (story 4.5 hasn't shipped it), else File > Open.
- Result XML: `HierarchyNode.NotebookElement` copies the matching `one:Notebook`'s attributes from the notebooks-scope read, with `xmlns:one` on it and no children.
- Integration test: the reopen uses `TestNotebook.OpenExistingAsync` (`cftNone`, b's recorded path), not `ReopenThrowawayNotebookAsync`, which refuses a notebook that isn't open (it reopens an open notebook's path, story 5.1). It skips when anything of b is on screen. Its guard refuses `SyncHierarchy` as well as `CloseNotebook` unless `CheckClose` passes, so the MCP Test case sends nothing.
- Contract: `close_notebook` only added to the snapshot (577 lines, no other change); no version bump, as 0.10.0-rc.1 isn't tagged (`git tag`: v0.9.0-rc.1 is the latest).
- Not run: the integration tests against real OneNote (the sitting's step 1).

## Plan Change Log

## Review Triage Log

Quick lens, iteration 0 (2026-11-22).

| # | Finding | Verdict | Route | Evidence |
|---|---|---|---|---|
| 1 | A client cancel in the sync or settle came back `Cancelled` with `retryable: false` | medium | patch (fixed) | `ToolErrors.IsRetryable(Cancelled, AppState)` is false and the pipeline's client-cancel branch set no `retryable`; spine AD-9's `Cancelled` row exempts a cancel before `CloseNotebook`. The branch now sets `retryable: true` when the call changes state but its outcome isn't unknown; the settle-cancel test asserts it. |
| 2 | On `outcomeUnknown` the close advice is in the message, not `userAction`; a deadline or client cancel during `CloseNotebook` gets only the pipeline's generic message | false (userAction) / low (cancel, deadline) | reject | AD-9 fixes `userAction` for every `outcomeUnknown` error ("re-read, then retry if the change is absent"), and AD-7 Merges already puts such advice in the message; the ambiguous `OneNoteException` path does. A client cancel or the 120 s deadline landing inside the sub-second `CloseNotebook` is rare, its `userAction` still says to re-read, and carrying the advice would add a pipeline hook. |
| 3 | The guide skill's trigger description names no close request | low | patch (fixed) | `plugin/skills/guide/SKILL.md` frontmatter lacked a close example; added "close that notebook" and "closing notebooks". |
- Live sitting (Phill, 2026-11-23), run on the 4.5 build (which contains 4.4): passed. Integration filter CloseNotebook 2 passed. Local: wrote "settle check" to a new page in throwaway b and closed b at once (ok, 10.5 s); Phill reopened b from its folder and the line was there (Q2 stop rule not triggered). Cloud: the first attempt was void: the write was refused WriteNotGranted (no write grant on this build; the grant prompt sat unseen in the terminal ~7 min), and the close with no edit before it returned ok in 10.4 s. Phill reopened Throwaway Cloud from File > Open > OneDrive and it came back stuck: a red "REFRESH NOTEBOOK: You can't edit this notebook because it's not syncing" bar, every section "isn't available yet. It was added from another device", COM listing only one empty section group for 3+ minutes, and Refresh notebook did nothing. Phill closed and reopened it again; the sections came back at once. Retry: wrote "cloud settle check 1123" to probe page (ok, 0.8 s), closed at once (ok, 10.5 s), no "syncing changes" box (sync icons only); after reopen and Sync This Notebook Now the line was there and every notebook was up to date. The stuck reopen was seen once and didn't recur; its cause (the close, the reopen, or OneDrive) is unknown.
