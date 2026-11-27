---
title: 'Refactor: shared pipeline core, ComGateway split, one notebook-folder check, smaller contract snapshot'
type: 'refactor'
ticket: '16'
created: '2026-11-27'
status: 'built'
baseline_revision: '38a797dda64799de7d00c526cba68fc0291cb68b'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The round-3 retrospective deferred four cleanups (F10-F13), each checked against main at `38a797d` on 2026-11-27 and all still holding: `AppStatePipeline` copies `WritePipeline`'s capped lock wait, abandoned-write wait, background release and deadline error (F10); `ComGateway.cs` is 1,749 lines (F11); `OpenNotebookUseCase.Names` maps `\\localhost\X$` but `create_notebook`'s open-folder check (`NotebookFolders.WithCanonical`) doesn't (F12); and all 35 tools in `tool-contract.json` carry the same 640-line `outputSchema`, 363 lines of it `meta` (F13).

**Approach:** One internal lock-and-deadline helper both pipelines call; `ComGateway` as partial-class files by concern; one `NotebookFolderPath.OpenForms` used by `open_notebook` and `create_notebook`; the snapshot keeps the shared output schema once. Approved by Phill with the story, 2026-11-26.

## Boundaries & Constraints

**Always:**
1. F10: `src/OneNoteMcp.Core/Writes/LockAndDeadline.cs` (internal static): the capped lock wait (a wait the deadline ended becomes the caller's deadline error), the abandoned-write wait (a throwing gateway counts as none outstanding), the outstanding-write `ComBusy` check, release now-or-in-background (with an optional before-release action for `WritePipeline`'s cache removal), and the `Timeout` exception shape (`retryable: unknown ? null : true`). Each pipeline keeps its own message text, step order and `Paused` deadline arithmetic.
2. F11: code motion only. `ComGateway` becomes `partial`; files `ComGateway.cs` (fields, ctors, dispatch, nested types, `GatewaySettings`), `ComGateway.Activation.cs` (activation, worker, abandoned-write tracking, idle), `ComGateway.Reads.cs`, `ComGateway.Windows.cs` (NavigateTo*, GetWindows, `ReadWindows`, `NavigateInWindow`), `ComGateway.QuickFiling.cs` (the picker hold), `ComGateway.Writes.cs` (sync, close, create, open, update, delete, publish). Each under 700 lines; visibility unchanged (AD-2).
3. F12: `NotebookFolderPath.OpenForms(notebookPath, links)` = the forms an open notebook's path names: its `NotebookKey`, its `\\localhost\X$` form read as `X:\`, and each made canonical; empty for a URL or blank. `MapLocalhostAdminShare` moves there. `OpenNotebookUseCase.Names` (kept, tests call it) = any form equals `NotebookKey(canonical)` ignoring case, as today. `CreateNotebook.ReadAsync` uses `NotebookFolders.WithCanonical(open.SelectMany(n => OpenForms(n.Path, null)))`.
4. F13: the snapshot is `{contractVersion, envelope: {outputSchema}, tools: [...]}`; a tool whose `outputSchema` deep-equals `envelope.outputSchema` stores `"outputSchema": "envelope"`, any other its full schema. The envelope is the most common output schema (ties: first by name). The test compares the compact forms and prints the replacement file on failure; one shared loader expands the snapshot for the other readers (`McpSdkBindingTests`, `CoverageTableTests`, `PluginPackagingTests`).
5. New tests: the admin-share `create_notebook` row (FileExists through `\\localhost\X$`, and InvalidArgument for a folder inside one); contract-layout tests showing a one-tool `meta` edit and a shared `meta` edit each fail the comparison; `OpenForms` rows.

**Never:** change any existing test's assertions (except the contract test's layout); change AD-7's steps, error codes, messages or retryability; change tool behaviour other than F12's admin-share case; touch real OneNote; spine edits.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Create over open notebook via admin share | open `\\localhost\c$\…\NB`, target `C:\…\NB` | `FileExists`, no COM create | not retryable |
| Create inside open notebook via admin share | open `\\localhost\c$\…`, target below it | `InvalidArgument` | unchanged message |
| Contract, one tool's meta edited | one tool's `outputSchema.properties.meta` differs | comparison fails | prints replacement |
| Contract, shared meta edited | all tools' meta differ | comparison fails | prints replacement |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/AppState/AppStatePipeline.cs` -- `AcquireAsync` (:157), `ThrowIfAbandonedWriteOutstanding` (:178), `AbandonedWrites` (:191), `ReleaseWhenSettledAsync` (:206), `DeadlineExpired` (:223); finally block (:138-154).
- `src/OneNoteMcp.Core/Writes/WritePipeline.cs` -- step 2 (:189-210), `AbandonedWrites` (:324), `ReleaseWhenSettledAsync` (:342, removes `writtenPageIds` from `cache` before dispose), `DeadlineExpired` (:367), finally (:288-318).
- `src/OneNoteMcp.Com/ComGateway.cs` -- reads :126-232; windows :233-432; sync :433-465; picker :466-699; writes :700-1097; core :1098-1306, activation :1307-1716, nested :1717-1749.
- `src/OneNoteMcp.Core/Writes/NotebookFolderPath.cs` (4.15) -- add `OpenForms`, `MapLocalhostAdminShare`.
- `src/OneNoteMcp.Core/UseCases/OpenNotebookUseCase.cs` -- `Names` :204, `MapLocalhostAdminShare` :231 + its `GeneratedRegex`.
- `src/OneNoteMcp.Core/UseCases/CreateHierarchyUseCase.cs` -- `CreateNotebook.ReadAsync` :387.
- `tests/OneNoteMcp.Tests/CreateHierarchyToolTests.cs` -- `Notebook_AnOpenNotebooksFolder_IsFileExists` (:571) pattern, `OpenNotebooksAre`, `Target`.
- `tests/OneNoteMcp.Tests/ToolContractTests.cs`, `Contract/tool-contract.json`, `McpSdkBindingTests.cs` (:89, :564), `CoverageTableTests.cs` (:164), `PluginPackagingTests.cs` (:249).

## Tasks & Acceptance

**Execution:**
- [x] `LockAndDeadline.cs`, `AppStatePipeline.cs`, `WritePipeline.cs` -- F10 (Always 1).
- [x] `src/OneNoteMcp.Com/ComGateway*.cs` -- F11 (Always 2), by moving line ranges, not retyping.
- [x] `NotebookFolderPath.cs`, `OpenNotebookUseCase.cs`, `CreateHierarchyUseCase.cs` + tests -- F12 (Always 3, 5).
- [x] `ToolContractTests.cs`, a `ContractSnapshot` test helper, `tool-contract.json`, the three readers + layout tests -- F13 (Always 4, 5).

**Acceptance Criteria:**
- Given main, when the admin-share create rows run, then they fail; given the change, they pass.
- Given the change, when the unit suite runs, then all pass, no existing assertion changed, every `ComGateway*.cs` is under 700 lines, and the expanded snapshot deep-equals the old file's `tools`.

## Implementation Notes

- Planning: about 1,900 tokens, over the 1,600 guide; kept whole, since the ticket bundles F10-F13 as one refactor story. No open questions. F13's unknown is settled by design: the comparison is per tool against the full schema, so a per-tool `meta` change can't hide. Approved by Phill with the story, 2026-11-26.

- Built in four sequential subagent runs, one per finding (4.15's single run was cut off). Files: new `LockAndDeadline.cs` (Core/Writes), both pipelines call it; `ComGateway` partial in six files (357, 128, 217, 263, 440, 439 lines; sorted non-trivial lines of the old file equal those of the new files); `NotebookFolderPath.OpenForms` and `MapLocalhostAdminShare` (moved; `OpenNotebookUseCase.MapLocalhostAdminShare` forwards to it so the existing assertion line is untouched), `OpenNotebookUseCase.Names` and `CreateNotebook.ReadAsync` use `OpenForms`; test helper `ContractSnapshot.cs` (compact/expand/load), `ToolContractTests` compares compact forms, the three other readers load through it; `tool-contract.json` 23,058 to 2,049 lines, all 35 tools on the envelope.
- F13 checks (2026-11-27): the expanded new snapshot deep-equals the old file (old copy kept in the session scratchpad, `f13/tool-contract.old.json`); new tests show a one-tool `meta` edit and an envelope `meta` edit each fail the comparison, and each was seen to fail its own fault (compactor marking every tool as envelope; comparison ignoring the envelope), the code restored afterwards.
- F12 red check (2026-11-27, reversible: the one `CreateHierarchyUseCase` line reverted and restored, diff checked identical): both admin-share rows fail on the old line; the quick-lens reviewer also ran them on an export of `38a797d` and saw both fail.
- Verification 2026-11-27: build (`--no-incremental`) 0 warnings; unit 4248 passed, 3 skipped, 0 failed (4251); integration without the variable 417 passed, 137 skipped, 0 failed (554). No existing assertion line removed (`git diff` of tests shows no `-` line with `Assert`).

## Plan Change Log

## Review Triage Log

**Pass 1 (quick lens, 2026-11-27):** 4 findings: high 0, medium 0, low 4, false 0, maybe-false 0; 3 patched, 1 rejected.

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | `OpenNotebookUseCase` keeps an unused `using System.Text.RegularExpressions` and `partial` after the regex moved | low | patch | Both removed; build 0 warnings. |
| 2 | No blank line before `OpenForms`' doc comment | low | patch | Added. |
| 3 | `WritePipeline.DeadlineExpired` keeps a retry comment whose code moved to `LockAndDeadline.Timeout` | low | patch | Comment removed (the helper has it). |
| 4 | The new layout tests index `outputSchema` on every tool, so a future tool without one would fail them | low | rejected | All 35 tools share the envelope through `ToolRunner`; no tool without an output schema exists, and the fix adds branches. |

After patches: build 0 warnings; unit 4248 passed, 3 skipped, 0 failed; integration (no variable) 417 passed, 137 skipped, 0 failed.

## Verification

**Commands:** (from `D:/Development/OneNote COM MCP/.claude/worktrees/s416`, each with `--artifacts-path "C:/Users/phill/AppData/Local/Temp/claude/2e954d91aa84c703/421e0750-1c29-4aff-a08b-1fe8f7985a3f/scratchpad/art416"`; never set `ONENOTE_MCP_INTEGRATION`)
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `dotnet test tests/OneNoteMcp.Integration` -- expected: none fail.
- `wc -l src/OneNoteMcp.Com/ComGateway*.cs` -- expected: each under 700.
