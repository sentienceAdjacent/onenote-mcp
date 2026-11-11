---
title: 'Write grants: dialog, revoke and listing'
type: 'feature'
ticket: '6'
created: '2026-11-11'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
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
| Other notebook | typed text matches one notebook, not the target | per Q2 | `WriteNotGranted` |
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
- [ ] `src/OneNoteMcp.Core/Access/GrantPrompt.cs`, `ElicitationGrantPrompt.cs` -- write question, port method, form and reader -- the Server adapts MCP, Core stays SDK-free.
- [ ] `src/OneNoteMcp.Core/Access/WriteGrantDialog.cs` (new), `WriteAccess.cs` -- ask, re-read, match, save; `WriteGrantMissing` -- AD-6 creation path.
- [ ] `src/OneNoteMcp.Core/Writes/WritePipeline.cs`, `ServerSetup.cs`, `CreatePageTool.cs` -- step-1 dialog with deadline pause and one re-check -- AD-7 step 1.
- [ ] `src/OneNoteMcp.Core/Settings/SettingsWriter.cs`, `SettingsJson.cs` -- `RevokeWriteGrant`, duplicate-tolerant revoke, `RunLocked` -- Q7, R4.
- [ ] `src/OneNoteMcp.Server/Cli/GrantCommand.cs` -- `--revoke --write`.
- [ ] `ListGrantsUseCase.cs`, `ListGrantsTool.cs` -- `writeNotebook` rows and advice.
- [ ] `UninstallCommand.cs`, `Program.cs` -- R4.
- [ ] Tests -- every matrix row. Plus: the dialog form (field renamed per request, hint, no lifetime) and the reader; a deadline shorter than a fake prompt's delay still writes; the write lock isn't taken until the dialog has ended (a second process can take it meanwhile); a client cancel during the dialog is `Cancelled` with no grant.
- [ ] `README.md`, `skills/` -- one `grant --revoke --write` line, a sentence that `create_page` can raise the dialog, and the purge warning -- `PluginPackagingTests` stays green.

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

1. **Size and scope.** The plan is about 3,300 tokens (the target is under 1,600), and R4 (purge) could ship on its own. (a) Keep the whole plan, as the ticket agreed at inception. (b) Split R4 into a deferred-work entry or its own ticket.
2. **The typed name matches a different open notebook than the write's target.** (a) No grant: `WriteNotGranted`, "the name typed didn't match this notebook", and record a one-line AD-6 clarification for the next spine story. (b) Grant the notebook it matched (a literal reading of AD-6's matcher); the write is still refused.
3. **`kind` for write rows in `list_grants`** (a wire value the skill may quote). (a) `writeNotebook`, which mirrors `readSection`. (b) `notebook`. (c) `write`.
4. **The typed-name hint in the write dialog.** (a) Show the notebook's name, as the read dialog does. Typing proves a person answered; AD-6 Limits already say it doesn't prove they knew the name. (b) Don't show it, so the user must know or look up the name. That is stronger against a reflexive Accept, but the message names the notebook anyway.

## Implementation Notes

## Plan Change Log

## Review Triage Log
