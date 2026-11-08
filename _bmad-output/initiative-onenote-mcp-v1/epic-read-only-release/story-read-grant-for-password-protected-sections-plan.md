---
title: 'Read grant for password-protected sections'
type: 'feature'
ticket: '20'
created: '2026-11-07'
status: 'built'
baseline_revision: '0b400575f1ded112a223bda04066c1dc2faad8d1'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 1
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** A password-protected section is fully readable by every read tool as soon as the user unlocks it in OneNote. The server guards only `locked`, never `encrypted` (spike 1.15).

**Approach:** Implement spine AD-21 as written, with the AD-6 machinery it needs:
- `EncryptedSections` and `SectionKey`;
- a Core access check run by every read use case and by `read_result`;
- `protectedSections{}` in `settings.json`, written under the settings mutex;
- the elicitation dialog;
- the `OneNoteMcp grant --read-section` / `--revoke --read-section` CLI;
- `list_grants`.

## Boundaries & Constraints

**Always:**
- The spine (AD-6, AD-9, AD-10, AD-12, AD-20, AD-21) is the specification. Where this plan and the spine differ, the spine wins.
- No tool can create a grant. The dialog asks the user to type the section's name, and only a match grants. The CLI refuses to run without an interactive console.
- Broad reads never raise the dialog. Withheld pages never go in `meta.filtered`, are never cached (AD-12) and never stored (AD-20). Messages carry section IDs only.
- The read-grant check runs before any content COM call and before the `SectionLocked` check.
- `once` grants live in memory only. A `once` grant covers every read of its section (target-scoped and broad) for `readGrants.onceMinutes` (default 5, 1–60) from when the answer arrives (Phill, 2026-11-07, AD-21 as amended). `list_grants` shows this process's live ones. Pages in protected sections are never cached, even under a grant. A `read_result` entry made under one expires `ttlMinutes` after the original call, and reads never extend it (AD-20).
- The dialog's typed-name field gets a new name on every request, so the client has nothing to pre-fill it from (Phill, 2026-11-07).
- New tool, codes and meta make a contract change: 0.6 → 0.7 (AD-14).
- Phill's real notebooks are read only. Integration tests touch only MCP Test's "Ballast Vault", and only read it.

**Never:** AD-6 write grants or write tools (epic 2). `navigate_to` is AppState and returns no content, so it is out of scope.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Target read, no grant, no elicitation | `get_page_content` on a Vault page | — | `ReadNotGranted`, userAction names the CLI |
| Target read, dialog | client declares elicitation; user types the name, picks `once` | the page is returned; nothing saved; reads of that section within the window (default 5 minutes) need no dialog | decline, cancel or mismatch → `ReadNotGranted` |
| Hours / until revoked | dialog or CLI | saved in `protectedSections{}`; later calls pass until expiry or revoke | settings invalid → `grantNotSaved` warning; works like a `once` window |
| Broad hierarchy | `get_hierarchy(notebook, pages)` | the Vault's `one:Section` kept, its pages removed; `meta.withheld` + `readNotGranted` warning | — |
| Search | `find_pages` / `find_meta` over the notebook | Vault candidates dropped before filtering, reading or the cache; `meta.withheld`; not in `filtered` | — |
| Allowed | `get_hierarchy(vault, self)`, `get_hierarchy_parent(vault)`, link tools on the Vault | normal result | — |
| read_result | part from a result made under a lapsed grant | — | `ReadNotGranted`, no dialog |
| Section renamed or moved | its `path` changes | the old grant no longer matches; asked again | — |

</frozen-after-approval>

## Code Map

The grant machinery doesn't exist yet. There is no NotebookKey, access check, settings writer, mutex, CLI, `list_grants` or elicitation.

- **Xml:**
  - `src/OneNoteMcp.Xml/LockedSections.cs:13-20` is the pattern for `EncryptedSections`: `encrypted` "true"/"1", returning ID, `name` and `path`.
  - `SearchResultFilter.cs:34,75-153`: add the enclosing section ID to `SearchResultPage` (the scanner already tracks a recycle-bin stack).
  - Add a span cut for hierarchy `one:Page` elements, generalised from `Remove` (:53-73).
- **Core settings:**
  - `SettingsSnapshot.cs:87-153` and `SettingsStore.Validate` (:92-190): add a `ProtectedSections` map. It is empty on Invalid or NewerSchema, so stored grants count as absent.
  - Add a new writer type: the `Local\OneNoteMcp.Settings` mutex; re-read, change, write a temp file, `File.Replace`; never held across a dialog (AD-10). It also drops expired `protectedSections{}` entries.
  - `SettingsStore.cs:47` already keeps readers compatible with `File.Replace`.
- **Core access:**
  - `SectionKey` uses `NotebookKey`'s transform (AD-6 defines it; implement it here, as the one key function).
  - Add an access-check service. Target-scoped reads resolve the owning section with a `GetHierarchyParent` chain (`IOneNoteGateway.cs:14`: empty string ends the chain), then `GetHierarchy` for its `encrypted` and `path`.
  - Add an `IGrantPrompt` port on `ToolCallContext` (`UseCases/ToolCallContext.cs`), so Core stays free of the SDK.
- **Use cases** (`src/OneNoteMcp.Core/UseCases/`), check before the first content call:
  - GetPageContent :26
  - GetBinaryPageContent :43, plus its XPS confirm read :72-77
  - GetHierarchy :14-37
  - GetHierarchyParent, both hyperlink use cases
  - FindPages: the drop goes before the recycle-bin step at :62, so before the cache at :77
  - FindMeta :29
  - ReadResult: right after `store.Get` (:69), using `entry.Grants`
  - `SearchResults.Result` (:39-55): keep withheld separate from `removals`.
- **Large results:**
  - `ToolSuccess` gains the allowing grants, so `ResultDelivery.cs:58-65` stores them instead of `ResultGrants.None`.
  - `ResultStore.Add/Get` (:71-120): `once` entries get an absolute expiry.
- **Errors and meta:**
  - `ErrorCode.cs`, `ToolErrors.cs:31-67`: `ReadNotGranted` (retryable after the user grants).
  - `ToolWarnings.cs`: `readNotGranted`, `grantNotSaved`.
  - `ToolMeta.cs`: `withheld`.
- **Server:**
  - Tools take an `McpServer` parameter, which the SDK binds. Elicitation through `server.ElicitAsync(ElicitRequestParams)` with a `StringSchema` for the typed name, a titled enum for the lifetime and a `NumberSchema` 1–168 for hours.
  - Supported means `server.ClientCapabilities?.Elicitation != null`, and an `InvalidOperationException` counts as unsupported.
  - Add the new `ListGrantsTool`.
  - `ServerSetup.cs:67`.
- **Plugin guard (AD-6):**
  - `plugin/hooks/hooks.json`: a `PreToolUse` hook, matcher `Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell`, command `"${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe" hook guard-settings`. Check the plugin hook schema; `claude plugin validate --strict` must pass.
  - `Program.cs`: a `hook` branch before the host, like `grant`. It reads the hook JSON from stdin.
    - It denies (PreToolUse `permissionDecision: deny` plus a reason) when the file path resolves, ignoring case, to `SettingsStore`'s resolved path, or to any `settings.json` in a folder named `OneNoteMcp`.
    - It also denies a shell command whose text contains either of those.
    - Reads are never blocked. Unparsable input, or anything else, allows silently.
    - It writes nothing but the decision JSON on stdout, starts no COM and builds no host.
  - The decision logic lives in a testable class with unit tests. The packaging test requires `hooks/hooks.json` in the staged plugin.
  - AD-6 Limits: this is best effort against an obfuscated shell command. The README says so, and the false sentence at README:68 is fixed.
  - The skill tells Claude never to edit `settings.json` and never to run `OneNoteMcp grant`.
- **Program.cs:10-36:** branch on `args[0] == "grant"` before the host is built. Check that the console is interactive (`Console.IsInputRedirected`) and refuse otherwise.
- **Tests:**
  - Use scripted gateways as in `PageContentToolTests.cs:471` and `SearchToolTests.cs:410`, with locked-section XML samples from `HierarchyReadToolTests.cs:32-35`.
  - There is no in-process MCP client, so fake `IGrantPrompt`.
  - Integration: Ballast Vault (`BallastFixture.cs:94-130`). Skip when it is absent or not encrypted. Use temp settings through `ONENOTE_MCP_SETTINGS_PATH`.

## Tasks & Acceptance

**Execution:**
- [x] Xml: `EncryptedSections`; section ID on search pages; the hierarchy page cut.
- [x] Core settings: `protectedSections{}` read, and the writer (mutex, temp file, `File.Replace`, expiry pruning).
- [x] Core access:
  - `SectionKey`, the grant check, `IGrantPrompt`, and the `once`/`hours`/`untilRevoked` handling;
  - `ReadNotGranted`, the warnings and `meta.withheld`;
  - grants carried into the result store, with `once` expiry.
- [x] Every read use case and `read_result`, per AD-21's per-tool table.
- [x] Server: the elicitation adapter, `list_grants`, and the `grant` CLI in Program.cs. Contract 0.7; version 0.7.0-rc.1.
- [x] Skill and README: what protected sections do, how to grant (dialog or CLI) and how to revoke.
- [x] Unit tests for every matrix row and per tool. Integration tests on the Vault: locked and unlocked, no grant, then a CLI-style grant written to temp settings.
- [x] Stress harness on MCP Test: 0 mismatches. The Vault's content is withheld, and the harness expects that.
- [x] Plugin guard (AD-6 "the plugin blocks the model from editing `settings.json`"; added by the Plan Change Log entry of 2026-11-07): a `PreToolUse` hook in `plugin/hooks/hooks.json` that denies the model's file writes and shell commands aimed at `settings.json`. README and skill say so.

**Acceptance Criteria:**
- Given the Vault unlocked in OneNote and no grant, when each read tool targets it, then none returns its content.
- Given Phill types the section name in Claude Code's dialog, when he picks `once`, then that call returns the page, reads of the section within the window don't ask, and a read after that asks again.
- Given `OneNoteMcp grant --revoke --read-section "Ballast Vault"`, when a granted read is repeated, then it is withheld again.

## Implementation Notes

- **Calls made beyond the plan:**
  - An unknown page ID now fails `ObjectNotFound` from the parent lookup, because the access check runs first.
  - A broad `get_hierarchy` lists `meta.withheld` only at scope `pages`, the only scope where pages can appear.
  - `list_grants` on an invalid or newer `settings.json` lists the entries with a `settingsInvalid` warning, and makes no error (AD-10: reads continue).
  - An `hours` grant revoked through the CLI also stops `read_result` at once, not only at its `expiresAt`.
  - `read_result` refuses any part that overlaps a section whose grant has lapsed, an ancestor's `expand` included.
  - CLI revoke matches the section's current name when the section is open, otherwise the stored name.
  - userActions name the running exe's full path, because a plugin install isn't on PATH.
  - The stress harness runs the server on a temporary `settings.json` with no grants.
- **Plugin guard:**
  - `plugin/hooks/hooks.json` runs `OneNoteMcp.exe hook guard-settings` on Edit/Write/MultiEdit/NotebookEdit/Bash/PowerShell.
  - It denies writes to `settings.json` and shell commands that name it.
  - It is best effort (AD-6 Limits). It costs one exe start per matched tool call.
- **Observed on MCP Test (2026-11-07):**
  - The search XML does mark the Vault `encrypted="true"`. AD-21's extra `GetHierarchy` is kept anyway.
  - The recycle bin's "Deleted Pages" section is not `encrypted`. Resolved (Phill, 2026-11-07): OneNote refuses to move a page from a password-protected section to the recycle bin and offers only a permanent delete. The open cases are in the spine's Deferred rows.
  - Section `path` stability isn't captured.
- **Locked run:** a locked section lists 0 pages, so the locked integration run reads no Vault page. "ReadNotGranted before SectionLocked" is proven on fakes; the unlocked run covers the real page path.
- **Verification (2026-11-07, after review pass 1):**
  - Build: clean.
  - Unit tests: 1145 passed (after pass 2: 1156).
  - Integration, Vault locked: 87 passed, 7 skipped.
  - Stress harness on MCP Test: 617 comparisons, 0 mismatches, 2 unstableRead (the known table widths), 0 text blocks over 100,000. Withheld: `readNotGranted` 20 on find_pages, 5 on find_meta, 1 on get_hierarchy.
  - Packed `0.7.0-rc.1`; `claude plugin validate --strict` passes. The name grep on the diff is clean.
- **Verification after the once-window change (2026-11-07):**
  - Unit tests: 1173 passed.
  - Integration: 87 passed, 7 skipped.
  - Stress harness on MCP Test: 620 comparisons, 0 mismatches.
  - Packed; `claude plugin validate --strict` passes.
  - Not yet live-checked: the once window and the per-request field name. That check is the Deferred row in the spine.
- **Second live check (Phill, 2026-11-07 23:29–23:40, 0.7.0-rc.1, per the server log):**
  - One dialog: the Vault listing waited 66 s on it, then the page read passed under the window.
  - Within the window, the second page needed no dialog, and `list_grants` showed the live `once` grant expiring 5 minutes after the grant.
  - After the window, the dialog asked again with an empty name field, so the pre-fill fix works. Declined, it gave `ReadNotGranted`.
- **Third live check (Phill, 2026-11-08):** the dialog showed one "Allow for" list with no Hours field, and the name hint read `Type "Ballast Vault" to confirm.` It worked as designed.
- **OneNote setting found (2026-11-08):** File > Options > Advanced > Passwords has "Enable add-in programs to access password protected sections when they are unlocked" (on here). Its effect on COM reads isn't captured yet; follow-up.
- **Integration, Vault unlocked (2026-11-07):**
  - 87 passed, 7 skipped.
  - The Vault test read with `locked=False` and 3 pages visible: withheld without a grant, readable with one, withheld again after the revoke.
- **Live check (Phill, 2026-11-07, Claude Code with the 0.7.0-rc.1 server):**
  - **AC2 passed:**
    - The dialog named the section and notebook. With "This read only", the page came back. The next call asked again.
    - Finding the first page took two dialogs: `get_hierarchy` on the Vault, then `get_page_content`. The server log confirms both. A `once` grant covers one call (AD-21).
  - **AC1:** with no grant, `find_pages` withheld the Vault with `readNotGranted`. A declined dialog gave `ReadNotGranted`.
  - **AC3 passed:**
    - The CLI `hours` grant was listed active by `list_grants`, and the next read needed no dialog.
    - After `--revoke`, `protectedSections` was empty and the next read asked again. Declined, it gave `ReadNotGranted`.
  - **Hook:** with the plugin loaded, Claude refused on its own both to run `grant` and to edit `settings.json`, citing the skill. So the hook never fired live; it is covered by unit tests and an exe smoke test.
  - **Found:** Claude Code pre-filled the second dialog's section name with the previous answer. The form sends no default, so the client remembers answers. A repeat grant is then an Enter on Accept rather than typing the name. Raised with Phill.

## Plan Change Log

- 2026-11-07, after the live check (Phill renegotiated the frozen intent):
  - **Change 1, `once` covers the section for 5 minutes, not one call.** Finding then reading a Vault page took two dialogs. Phill: once he has granted the section, reading it is fine, and individual pages carry no extra protection.
  - **Change 2, new field name per request.** Claude Code pre-filled the typed name from the previous answer.
  - **Amended:** the frozen Boundaries, the matrix row and AC2. Spine AD-21 is amended too, with the Fable review.
  - **Change 3, from the Fable review of the amendment (Phill's answers):**
    - The window is the setting `readGrants.onceMinutes`.
    - `list_grants` shows live `once` grants.
    - Protected pages are never cached.
    - The dialog's option names the window.
    - The typed name is read only from that request's field.
  - **Change 4, from the second live check:**
    - Phill found the separate Hours field confusing. The dialog now offers one lifetime list: 5-minute window, 1 h, 8 h, 24 h, 7 days, until revoked. Other hour counts go through the CLI.
    - The name field's hint names the section, so it can be copied.
  - **KEEP:** everything else in the implementation.

- 2026-11-07, review pass 1, finding 1 (bad_plan):
  - **Trigger:** AD-6's rule "The plugin blocks the model from editing `settings.json`" was missing from the Code Map. This story is the first to store grants in that file, so without the block the model can grant itself a read with Edit/Write. That is the bypass AD-6 and AD-21 exist to prevent.
  - **Amended:** Code Map (Plugin guard) and Tasks.
  - **Known-bad state avoided:** a stored-grant gate the model can write for itself; a README saying the skill forbids editing `settings.json` when it doesn't.
  - **KEEP:** the whole current implementation. The review found nothing wrong in it that the guard changes. The guard is additive, so the code was not reverted; reverting and re-deriving the same code would only cost time. Only the guard and the patches from pass 1 are added.

## Review Triage Log

Pass 3 (quick lens, on the once-window change after Phill's renegotiation): 8 findings, all patched. All were text or docs: the `list_grants` settingsInvalid warning and the tool description; the README and skill on `read_result` after the window; the README's pre-fill claim; two stale doc comments in the elicitation prompt; this plan's matrix row; the spine Storage bullet hard-coding 5 minutes; AD-10's `schemaVersion` list; and the README settings docs for `readGrants`. The spine and plan fixes were mine; the rest went to the implementer.

Pass 1 (quick lens): high 1, medium 4, low 2, rejected 1.
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | No plugin block on editing `settings.json`, and README:68 says the skill forbids it when SKILL.md never mentions it | high | bad_plan | Spine AD-6 (line 129). `plugin/` has no hooks. Plan amended (Change Log): add the guard. |
| 2 | Spine Deferred items for 1.20 (recycle-bin `encrypted`, search XML `encrypted`, path stability) aren't recorded, and the recycle bin may leak | medium | defer | The implementer saw the search XML carry `encrypted` and "Deleted Pages" not carry it. Whether a deleted Vault page lands there needs a delete in the Vault (hands-on, Phill). Recorded in Implementation Notes and deferred-work, for Phill to decide on an AD-21 amendment. |
| 3 | The locked integration run never reads a Vault page (a locked section lists 0 pages), so ReadNotGranted-before-SectionLocked is proven only on fakes | low | rejected | True (spike 1: 0 pages when locked), but a fix needs an ID captured while unlocked, which is hands-on. The unlocked run covers the real page path. Noted in Implementation Notes. |
| 4 | `list_grants` fails on an invalid file with a message about writes; AD-10 says reads continue, and case-duplicate entries can't be seen | medium | patch | `ListGrantsUseCase` calls `EnsureWritable`; `ProtectedSectionEntries` holds the entries. |
| 5 | An empty section `path` would store a `""` key and make the whole `settings.json` Invalid | medium | patch | `GrantKeys.SectionKey("")` returns `""`, and `SettingsStore` rejects an empty key. Guard: refuse to grant (dialog and CLI) when the path is empty. |
| 6 | Search withholding fails open when the start node is the protected section and the search XML doesn't wrap pages in its `one:Section` | medium | patch | `CheckSearchScopeAsync` sets `WithholdAll` only for a page start. Set it too when the root is a withheld section. |
| 7 | userActions name `OneNoteMcp grant`, which isn't on PATH for plugin installs | medium | patch | Plugin exe path is `${CLAUDE_PLUGIN_ROOT}`. Name the running exe's full path (`Environment.ProcessPath`) in the userActions; README says where the plugin's exe is. |
| 8 | Two name matchers (`GrantCommand.Same` trims both sides; `ReadAccess` trims only the typed name) | low | patch | AD-6/AD-21: "the same name matcher". One Core matcher, used by both. |

Pass 2, from the automated security review of commit 23333e0: 4 findings. 2 were fixed, 1 was accepted as a limit and 1 was already deferred.
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | Search fails open for a page with an empty SectionId in a scope that withholds a section | medium | patch | `BroadAccess.IsWithheld` now withholds such a page whenever any section is withheld. Test added. |
| 2 | The hook's path check can be bypassed with 8.3 short names, NTFS streams or links | low | rejected | AD-6 Limits: best effort against casual misuse; any shell can write the file indirectly. The README says so. |
| 3 | The grant CLI's interactive check (non-redirected console) could be beaten with a pseudo-console | medium | patch | The hook now also denies shell commands that run `OneNoteMcp[.exe] grant`. Known gap: `dotnet run --project ... -- grant`, accepted as best effort. |
| 4 | Recycle-bin pages from a protected section may be unprotected | medium | defer | Already in `deferred-work.md`. The fail-closed interim (withhold recycle-bin pages in notebooks that have a protected section) is Phill's call. |

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`; `dotnet test tests/OneNoteMcp.Tests`; `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` (with the Vault locked, and again unlocked) -- all pass.
- Stress harness on MCP Test -- 0 mismatches.

**Manual checks:**
- Live check with Phill in Claude Code: the dialog, `once`, the CLI grant and the revoke.
- The name grep on the diff finds nothing.
