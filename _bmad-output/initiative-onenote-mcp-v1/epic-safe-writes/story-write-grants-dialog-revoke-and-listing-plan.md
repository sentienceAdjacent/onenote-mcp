---
title: 'Write grants: dialog, revoke and listing'
type: 'feature'
ticket: '6'
created: '2026-11-11'
status: 'built'
baseline_revision: '4e6533d184659036608cd66fbb1f7f4f930a71b6'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A write grant can only be made with `grant --write` in a terminal, can't be revoked, and doesn't show in `list_grants`. `grant --revoke` can't clear an exact duplicate key (deferred-work, 2.3 Q7). `uninstall --purge` doesn't take the settings mutex or notice running servers (retro R4).

**Approach:** Complete AD-6 for notebooks. On `WriteNotGranted`, AD-7 step 1 raises an elicitation dialog where the user types the notebook's name; dialog time is excluded from the pipeline deadline. Add `grant --revoke --write`, `writeNotebook` rows in `list_grants`, Q7's duplicate-key revoke, and R4.

## Boundaries & Constraints

**Always:**
- Dialog order: `EnsureWritable` first, then the owning-notebook walk. Raise the dialog only for a missing grant on a notebook with a non-empty path, and only when `context.Prompt` is set. No STA call, write lock or settings mutex is held while the user decides (AD-6, AD-10, AD-18), and the wait uses the client's token, never the deadline.
- The form names the notebook (displayed name and path) and says the grant lasts until revoked, naming `grant --revoke --write`. It has one typed-name field, renamed on every request (`notebookName_` + 8 hex digits), and the answer is read only from that field (as AD-21). There is no lifetime field: write grants have none (AD-10).
- Answer check: after the dialog, re-read the open notebooks (`GetHierarchy("", Sections)` → `OpenNotebooks.Find`) and match the typed text with `GrantKeys.NameMatches` against each displayed name. 0 matches = mismatch; more than 1 = ambiguous. Either way no grant is made.
- A match saves `{write: true, grantedVia: elicitation}` through `SettingsWriter.SaveWriteGrant` (keeps `permanentDelete`). Then step 1 runs once more, with no second dialog. A failed save keeps its own error (`SettingsInvalid`/`ComBusy`). There is no `once` fallback for writes.
- Unsupported, declined, cancelled, mismatch and ambiguous all give `WriteNotGranted`, retryable, with the existing `WriteAccess.NotGrantedUserAction`. The message says why and carries IDs only (AD-9).
- `grant --revoke --write [name]` matches the stored grants by live displayed name (an open notebook with the same key), else by stored name. It lists several matches with their paths, and needs no `yes`. It runs on a file that is invalid only for duplicate grant keys, and removes every entry equal to the key: exact or case-only, in either area (Q7).
- `list_grants`: one row per `notebooks{}` entry with `write: true`: `{kind, key, name, notebookKey: key, idAtGrant, grantedAt, grantedVia, lifetime: untilRevoked, status: active|notebookNotOpen}`. It makes one `GetHierarchy` at Sections scope only when there are grants. The `settingsInvalid` advice names `--revoke --write` for notebook duplicates.
- Purge (R4): before the confirm prompt, warn with the PIDs of every other process running `OneNoteMcp.exe`. Remove `settings.json` and its temp files while holding `Local\OneNoteMcp.Settings`, never across the prompt. A lock timeout skips the settings files, reports it and exits 1. The other files are still removed.
- Grants stay keyed by path: a notebook whose ID changed (close and reopen, spike 5.1) keeps its grant and shows `active`.
- No contract change is expected (`kind` is a string; descriptions aren't snapshotted). `<Version>` stays `0.9.0-rc.1`. Public repo: MCP Test and made-up names and IDs only.

**Never:** a write-grant lifetime; the permanent-delete grant or its rows (epic 5); a dialog for read tools on writes (AD-21); spine edits; the journal or 2.7's gateway rules; running OneNote in unit tests.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Granted via dialog | ungranted, client elicits, types the name | grant saved, page created | — |
| Decline / cancel | dialog answered so | no grant, no lock | `WriteNotGranted` |
| Mismatch | typed text matches no open notebook | no grant | `WriteNotGranted` |
| Ambiguous | two open notebooks share the name | no grant | `WriteNotGranted` (CLI lists both) |
| Other notebook | typed text matches one notebook, not the target | no grant (Phill, Q2) | `WriteNotGranted` |
| No elicitation | client lacks the capability | no dialog | `WriteNotGranted` |
| Settings invalid | invalid / newer | no dialog | `SettingsInvalid` |
| Slow answer | user answers after more than the deadline | write still runs | — |
| Revoke | `grant --revoke --write "MCP Test"` | entry removed; next write asks | — |
| Exact duplicate key | two identical keys | revoke removes both | — |
| ID changed | same path, new ID | still granted, `active` | — |
| Purge, server running | another OneNoteMcp PID | warns, then asks | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- `CheckAsync` throws plain `WriteNotGranted`. Add an internal `WriteGrantMissing : OneNoteException` (same code, message and userAction) that carries the resolved notebook (ID, displayed name, path, key). The no-path case stays plain.
- `src/OneNoteMcp.Core/Access/GrantDialog.cs` -- the read dialog. Model the new `WriteGrantDialog.cs` on it (catch OCE→Cancelled, status switch, `NameMatches`). Leave `GrantDialog` itself unchanged.
- `src/OneNoteMcp.Core/Access/GrantPrompt.cs` -- `IGrantPrompt`, `GrantPromptResult`, `GrantedVia.Elicitation`. Add `WriteGrantQuestion` and `AskWriteAsync`. Fakes to update: `tests/.../ProtectedSectionKit.cs:263` `FakePrompt`, `ParentWalkTests.cs:130`.
- `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs` -- copy `AskAsync`/`Request`/`Read`/`NewSectionNameField` as `AskWriteAsync`/`WriteRequest`/`ReadWrite`/`NewNotebookNameField`.
- `src/OneNoteMcp.Core/Writes/WritePipeline.cs:62-86` -- the deadline CTS is made with `clock`. Pause with `CancelAfter(Timeout.InfiniteTimeSpan)`, resume with `CancelAfter(remaining)`, and subtract the paused span in the lock-wait sum (`:79`).
- `src/OneNoteMcp.Server/Tools/CreatePageTool.cs` -- add `McpServer? server`, pass `ElicitationGrantPrompt.For(server)` (as `GetPageContentTool.cs:37-45`). `ToolRunner.RunAsync` already takes a prompt; leave 2.5's argument check unchanged.
- `src/OneNoteMcp.Server/ServerSetup.cs:101-102` -- build `WriteGrantDialog` (gateway, `SettingsWriter`) into `WritePipeline`.
- `src/OneNoteMcp.Core/Settings/SettingsWriter.cs` -- `RevokeReadGrant`/`RemoveKey`/`Change` pattern. `Load` parses with `allowDuplicateProperties: false` (`:200`), which is why exact duplicates can't be revoked. Load with duplicates allowed when `allowDuplicateKeys`. If `JsonObject` won't hold duplicates, rebuild the area from a `JsonDocument`, skipping every property whose key matches. Add `RevokeWriteGrant(key)` and `RunLocked(Action)` (`Change`'s acquire/abandoned/`ComBusy`). `SettingsJson.cs:9-15` doc comment.
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs` -- `NotebookEntries`, `Notebooks`, `DuplicateNotebookKeys` (from 2.4). Read only.
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs` -- `RevokeAsync` (read) is the model. The `write && revoke` refusal goes. Add `Usage` and doc lines.
- `src/OneNoteMcp.Core/UseCases/ListGrantsUseCase.cs` -- add the rows. The open keys come from the same Sections XML via `OpenNotebooks.Find` (`Server` must not import Xml). `Tools/ListGrantsTool.cs` description.
- `src/OneNoteMcp.Server/Cli/UninstallCommand.cs:150-200` (`Plan`, `Run`) and `Program.cs:51-55` -- inject a running-servers probe and a `SettingsWriter`-style lock.
- Tests to extend: `CreatePageToolTests` (harness `UseCase`/`Tool`, `:448`), `GrantCommandTests`, `SettingsDuplicateKeyTests` (flip `Writer_AnExactDuplicateGrantKey_RefusesTheRevoke_AndLeavesTheFile`), `UninstallCommandTests`, a list_grants test beside `ReadGrantSettingsTests`.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Access/GrantPrompt.cs`, `ElicitationGrantPrompt.cs` -- write question, port method, form and reader -- the Server adapts MCP, Core stays SDK-free.
- [x] `src/OneNoteMcp.Core/Access/WriteGrantDialog.cs` (new), `WriteAccess.cs` -- ask, re-read, match, save; `WriteGrantMissing` -- AD-6 creation path.
- [x] `src/OneNoteMcp.Core/Writes/WritePipeline.cs`, `ServerSetup.cs`, `CreatePageTool.cs` -- step-1 dialog with deadline pause and one re-check -- AD-7 step 1.
- [x] `src/OneNoteMcp.Core/Settings/SettingsWriter.cs`, `SettingsJson.cs` -- `RevokeWriteGrant`, duplicate-tolerant revoke, `RunLocked` -- Q7, R4.
- [x] `src/OneNoteMcp.Server/Cli/GrantCommand.cs` -- `--revoke --write`.
- [x] `ListGrantsUseCase.cs`, `ListGrantsTool.cs` -- `writeNotebook` rows and advice.
- [x] `UninstallCommand.cs`, `Program.cs` -- R4.
- [x] Tests -- every matrix row. Plus: the dialog form (field renamed per request, hint, no lifetime) and the reader; a deadline shorter than a fake prompt's delay still writes; the write lock isn't taken until the dialog has ended (a second process can take it meanwhile); a client cancel during the dialog is `Cancelled` with no grant.
- [x] `README.md`, `skills/` -- one `grant --revoke --write` line, a sentence that `create_page` can raise the dialog, and the purge warning -- `PluginPackagingTests` stays green.

**Acceptance Criteria:**
- Given an ungranted notebook and a client that elicits, when `create_page` runs and the user types the notebook's name, then the grant is stored with `grantedVia: elicitation`, the page is created, and `list_grants` shows a `writeNotebook` row with status `active`.
- Given a stored write grant, when the user runs `grant --revoke --write` and picks it, then it is gone, and the next `create_page` asks again.
- Given a `settings.json` with an exact duplicate notebook or section key, when the matching revoke runs, then both entries are gone and the file is valid.
- Given another OneNoteMcp process, when `uninstall --purge` runs, then it warns with that PID before asking, and deletes `settings.json` only under the settings mutex.

## Design Notes

Why the pipeline raises the dialog, not `WriteAccess`: step 1 runs on the deadline-linked token, but the dialog must use the client's token and pause the deadline. So `WritePipeline.RunAsync` catches `WriteGrantMissing` from `AuthorizeAsync`, pauses, awaits `WriteGrantDialog.AskAsync(context.Prompt, missing, ct)`, resumes, and calls `AuthorizeAsync` again. The grant check, the save and the re-check stay in Core. Every later write class (2.9–2.11, epic 5) gets the dialog without extra code.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass; `tool-contract.json` unchanged.

**Manual checks (Phill, live, Claude Code with the dev build registered as `onenote-dev`):**
1. Revoke MCP Test with `grant --revoke --write "MCP Test"`; `list_grants` no longer lists it.
2. `create_page` on an MCP Test section: the dialog names the notebook and path. Note whether Claude Code's tool-permission prompt comes first, and whether the field is pre-filled. Type the name: the page is created, and `list_grants` shows `writeNotebook`, `active`, `elicitation`.
3. Revoke again. Then decline, cancel (Esc) and type a wrong name, one at a time: each returns `WriteNotGranted` and creates no page.
4. Wait more than 2 minutes before answering a dialog: the write still goes through (no `Timeout`), and Claude Code doesn't time the call out.
5. Close and reopen MCP Test in OneNote: `list_grants` still shows `active`, and `create_page` needs no dialog.
6. Purge with a session running, using temporary `ONENOTE_MCP_SETTINGS_PATH` and `ONENOTE_MCP_LOG_DIR`: the warning lists the server's PID.

## Open Questions

None. Phill answered all four and approved the plan (2026-11-12, Inbox thread "2.6 write grants"):

1. **Size and scope:** keep the whole plan, R4 (purge) included.
2. **Typed name matches a different open notebook:** no grant. Refuse with `WriteNotGranted` ("the name typed didn't match this notebook"), and record a one-line AD-6 clarification for the next spine story (a deferred-work entry, not a spine edit in 2.6).
3. **`kind` for write rows in `list_grants`:** `writeNotebook`.
4. **Typed-name hint in the write dialog:** show the notebook's name, as the read dialog does.

## Implementation Notes

- Build lane constraints (2026-11-11, from the team lead): work only in this worktree (`D:\Development\OneNote COM MCP\.claude\worktrees\agent-a2127fef2e50a5c92`, branch `story/2.6-write-grants`). Verify with unit tests only (`dotnet build OneNoteMcp.slnx -warnaserror`, `dotnet test tests/OneNoteMcp.Tests`). Do not run the integration tests or anything that starts or touches OneNote/COM: a live probe sitting is using OneNote. The live manual checks above are left for a later sitting. Do not commit; the build lane commits after review.
- Q2's deferred-work entry: append one entry to `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` asking the next spine story to add a one-line AD-6 clarification (a typed name that matches a different open notebook gives no grant). No spine edit in 2.6.
- A decision the plan doesn't settle: pick the option most consistent with the plan and the spine, and record it here as a bullet.
- Decision: `OneNoteException` is no longer `sealed`, so `WriteGrantMissing : OneNoteException` (internal, in `WriteAccess.cs`) can exist as the Code Map says. Every other error is still a plain `OneNoteException`; uncaught, `WriteGrantMissing` reaches the tool runner as one (same code, message and `userAction`).
- Decision: `WriteGrantDialog` is public (the Server builds it in `ServerSetup`) with an internal `AskAsync`, and takes a non-null `SettingsWriter`: with nowhere to store the grant there is nothing to ask for. `WritePipeline` takes it as an optional last constructor argument; without it, or when the call has no `context.Prompt`, step 1's refusal stays the plain "has no write grant from the user." (no dialog).
- Decision: the dialog's grant stores the matched open notebook's displayed name as `name` and its live ID as `idAtGrant`, as `grant --write` does.
- Decision: the duplicate-tolerant revoke doesn't go through `JsonObject` at all (it throws on a duplicate key even with `AllowDuplicateProperties`). Both revokes (`RevokeReadGrant`, `RevokeWriteGrant`) parse a `JsonDocument` with duplicates allowed and write the file back with `Utf8JsonWriter`, property by property: in the revoked area every key equal to the revoked one (exact or case-only) is dropped, expired read grants are pruned as every grant writer does, and everything else, duplicates elsewhere included, is copied unchanged. A revoke that removes nothing leaves the file byte for byte. Saves keep the strict `JsonObject` path.
- Decision: `list_grants` row order is stored read grants, then live `once` grants, then write grants, each sorted by key, so existing read-grant output is unchanged. The `settingsInvalid` advice names `--revoke --write` for notebook duplicates, `--revoke --read-section` for section duplicates, and both when both areas have them.
- Decision: `UninstallCommand` takes the running-servers probe and the settings lock as required constructor arguments (`UninstallCommand.OtherServerProcesses`, `UninstallCommand.SettingsLock(path)` in `Program.cs`, fakes in tests). The warning is printed with `--yes` too (before the removal, where there is no question). With no settings files to remove, the lock isn't taken.
- Decision: after the dialog, if the paused deadline has nothing left, the pipeline cancels it at once rather than re-arming a zero-length timer, so the re-check and lock wait see it expired.
- Decision (not in the plan, found while verifying): `SettingsWriter` now retries `File.Replace` up to 4 times, 50 ms apart, on `IOException`, still inside the settings mutex. Back-to-back grant saves in the unit tests intermittently failed with "Unable to remove the file to be replaced" (a scanner holding the just-written file); the same can hit a user who grants twice quickly. It is a file-system retry, not a OneNote write retry (AD-7/AD-17 unaffected).
- Small additions: `grant --write`'s success line now names `grant --revoke --write "<name>"`, as the read grant's does; `create_page`'s and `list_grants`' tool descriptions mention the dialog and the `writeNotebook` rows (descriptions aren't in `tool-contract.json`, which is unchanged).
- Verification (2026-11-11): `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings; `dotnet test tests/OneNoteMcp.Tests` 1496 passed, run 8 times in a row with no failure after the `File.Replace` retry. No integration tests and nothing touching OneNote/COM were run (lane constraint); the live manual checks are still open.
- Decision (review fix): a write revoke never touches the permanent-delete grant (the Never list keeps it to epic 5). `grant --revoke --write` offers only entries with `write: true`. `SettingsWriter.RevokeWriteGrant` drops every entry equal to the key as before, except that when any of them has `"permanentDelete": true`, the first such entry is written back under its own key spelling with `"write": false` and every other member copied, so exactly one entry with that key remains and the file is valid. The read-grant revoke is unchanged.
- Review fixes: `uninstall --purge` lists `settings.json` and its temp files again under the settings mutex (taken whenever the settings folder exists), so a file a running server created during the question goes too. `ReplaceWithRetry` (bug 2.15) now tries about half a second (10 x 50 ms) and, after a replace that removed the target and failed (1176), moves the temp file into place; `Write` never deletes the temp file when `settings.json` is gone. `OneNoteMcp.Core` gains `InternalsVisibleTo` for the unit tests, for that seam.

## Plan Change Log

## Review Triage Log

Quick lens (review loop 0). Findings were relayed by the team lead.

| # | Finding | Verdict | Route | Evidence |
|---|---|---|---|---|
| 1 | Purge lists the settings files before the question, so a settings.json created meanwhile survives and purge reports success | medium | patch | `UninstallCommand.Plan` fills `SettingsFiles` before the prompt; `Run` deletes only that list and skips the lock when it's empty. The dialog's `SaveWriteGrant` can create the file during the prompt. |
| 2 | `grant --revoke --write` matches entries with `write: false` and deletes `permanentDelete` | medium | patch | `RevokeWriteAsync` builds its candidates from `NotebookEntries` with no `Write` filter; `Revoke` drops the whole entry. The plan's Never list leaves the permanent-delete grant out of 2.6, and `SaveWriteGrant` keeps it. |
| 3 | A failed `File.Replace` (1176) can lose settings.json, and the retry window is shorter than bug 2.15's | medium | patch | With no backup name, ReplaceFile's ERROR_UNABLE_TO_MOVE_REPLACEMENT leaves the target deleted and the new content in temp. Each retry then throws, and `Write`'s catch deletes temp. The single-attempt path had the same loss before 2.6. |
| 4 | The `ToolCallContext.Prompt` doc says read grants only | low | patch | `ToolCallContext.cs:9`. The prompt now also drives the AD-6 dialog. |
| 5 | Plan dates 2026-11-12 are "future" | false | rejected | Phill's answers came in on 2026-11-12 UTC (team lead). |
