---
title: 'README, uninstall --purge and clean-machine install'
type: 'feature'
ticket: '13'
created: '2026-11-07'
status: 'built'
baseline_revision: 'a622106cb4c14255deb9a9f58bd753bcdbd57ce4'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 1 has no way to remove what the server leaves on disk: settings with read grants, logs, and later the journal. The README still marks uninstall, the skill and the journal as "planned". Nobody has checked the install end to end on a clean profile, which is the epic's "Done when" #1 and #5.

**Approach:**
- Add `OneNoteMcp uninstall --purge` (AD-19). It removes only OneNoteMcp's own files.
- Bring the README up to date with what epic 1 ships.
- Name OneNote's add-in access option in the `sectionLocked` warning, the `SectionLocked` error's userAction and the README (Phill, 2026-11-08). With "Enable add-in programs to access password protected sections when they are unlocked" off, an unlocked protected section looks locked to COM.
- Run the clean-machine install as the epic's end-to-end check, with Phill.

## Boundaries & Constraints

**Always:**
- **What purge deletes, and nothing else:**
  - the settings file and any `.settings.*.tmp` leftovers beside it;
  - log files matching `onenote-mcp-*.log` in the log folder;
  - journal-named files (`{yyyyMMddTHHmmssfff}-{pid}-{guid}.json.gz`) in the journal folder.
- After deleting, it removes each folder only if it is now empty. It never recurses and never deletes anything else.
- **Root-like journal paths are refused**, and purge then deletes nothing from the journal folder. Root-like means any of:
  - a drive or UNC share root;
  - the user profile folder;
  - `%APPDATA%` or `%LOCALAPPDATA%` themselves;
  - Desktop or Documents;
  - the Windows folder or Program Files;
  - any path with fewer than two folders below the root.
- Paths come from the same resolvers the server uses: `SettingsStore.ResolvePath`, `FileLog.ResolveDirectory`, and the journal path in settings. If settings.json is invalid, purge uses the default journal folder and says so.
- Purge never touches Claude Code's `tool-results` files or the plugin's install folder. The README says both.
- The README must say the journal holds copies of note content (AD-19).
- Purge prints what it removed and what it refused, by path, and exits 0 when done, 1 when anything was refused or failed, and 2 on bad usage.
- It starts no COM and builds no host, like `grant` and `hook` in Program.cs.

- **Decisions (Phill, 2026-11-07):**
  - **Confirmation:** when interactive, purge asks the user to type `purge`; otherwise it needs `--yes`. The plugin hook doesn't block it.
  - **Clean environment:** a VMware Windows 11 VM with OneNote installed and signed in. A snapshot taken before any OneNoteMcp install is restored for each route.
  - **Release:** after Phill merges 1.20 and 1.13, Claude pushes tag `v0.7.0-rc.2` and runs the marketplace sync on Phill's go.

**Never:**
- Removing the exe or the plugin. That is `/plugin uninstall`, or deleting the exe for a manual install.
- Writing the journal itself, which is epic 2.
- Code signing.
- Committing anything from the clean-profile run except counts and pass/fail.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Purge | settings, logs, journal files present | those files removed; empty folders removed; exit 0 | — |
| Foreign files | a non-journal file in the journal folder; a non-log file in the logs folder | kept, along with their folder | — |
| Root-like journal | `journal.path` = `C:\` or the profile folder | journal skipped and named as refused; settings and logs still purged | exit 1 |
| Nothing there | no files | "nothing to remove", exit 0 | — |
| Invalid settings | settings.json unparsable | the file is still removed; journal uses the default folder | — |
| No `--purge` | `uninstall` alone | usage text, exit 2, nothing deleted | — |
| In use | a log file locked | the others removed; that one reported | exit 1 |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Server/Program.cs:15-47`: `hook` and `grant` branch on `args[0]` before the host is built. Add `uninstall` the same way. It needs no COM.
- `src/OneNoteMcp.Server/Cli/GrantCommand.cs`: the pattern to follow. Injected `TextWriter`, `const Usage`, exit codes 0/1/2. Put the new command in `Cli/UninstallCommand.cs`.
- `src/OneNoteMcp.Core/Settings/SettingsStore.cs:15,31-39`: settings path and `ONENOTE_MCP_SETTINGS_PATH`. `:143-150` parses `journal.path`. `RequireAbsolutePath` (:328) has no root-like check; put the purge-side check in the command, so the settings validation stays unchanged.
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:30-33`: `JournalSettings`, default `%LOCALAPPDATA%\OneNoteMcp\journal`.
- `src/OneNoteMcp.Core/Settings/SettingsWriter.cs:192`: the temp-file name `.settings.<pid>.<guid>.tmp`.
- `src/OneNoteMcp.Server/Logging/FileLog.cs:36,42-53`: the log folder (`ONENOTE_MCP_LOG_DIR`, else `%LOCALAPPDATA%\OneNoteMcp\logs`) and the `onenote-mcp-*.log` pattern.
- `README.md`. Stale:
  - :43 (skill "planned");
  - :70 and :173 (journal "planned"): keep planned, but say it holds note content and that purge removes it;
  - :151-155 (Uninstall): replace the planned text;
  - :157-165 (agent checklist): add the manual and source routes and the uninstall step.
  - Already current: the tools table (:11-28), protected sections (:32-39), large results, missing images.
- Tests:
  - `tests/OneNoteMcp.Tests/GrantCommandTests.cs` and `TempDirectory.cs` for the command.
  - `PluginPackagingTests.cs:245-289`, which checks the skill against `Contract/tool-contract.json`: add a README test that every contract tool is named and no "planned" purge text is left.
- `epic-read-only-release.md:35,39,61` (Done when #1 and #5; the clean-machine check is the epic's end-to-end test). `story-release-pipeline-plan.md:91`: check Mark-of-the-Web on Claude Code's marketplace download during the clean run.
- **Release:** `.github/workflows/release.yml` runs on a `v*` tag; the marketplace sync is `gh workflow run sync -R sentienceAdjacent/plugins` (AD-15).

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Server/Cli/UninstallCommand.cs`, `Program.cs`: the purge per Boundaries.
- [x] `tests/OneNoteMcp.Tests/UninstallCommandTests.cs`: every matrix row, on temp folders through the path overrides.
- [x] `README.md`: Uninstall section, journal note, the "planned" fixes, the agent checklist for all three routes. Add a README packaging test.
- [x] `scripts/clean-install-check.ps1`: snapshot `%APPDATA%\OneNoteMcp`, `%LOCALAPPDATA%\OneNoteMcp` and their parents' listing before and after purge, and diff them. It prints counts only, never notebook content.
- [x] `src/OneNoteMcp.Core/Results/ToolErrors.cs` (SectionLocked userAction) and the `sectionLocked` warning text in `GetHierarchyUseCase.cs`, plus any other `sectionLocked` warning sites: add that if the section is already unlocked in OneNote, the option "Enable add-in programs to access password protected sections when they are unlocked" (File > Options > Advanced > Passwords) is off. README protected-sections paragraph: the option keeps protected sections from every add-in, this server included. Update the tests that pin those texts.
- [ ] Clean-machine run with Phill (hitl), in the VM, after the release.

**Acceptance Criteria:**
- Given a clean Windows profile with OneNote signed in, when an agent follows only the README, then the plugin installs from the release and `get_hierarchy` lists notebooks.
- Given the same profile, when the manual-client and build-from-source routes are followed, then each lists notebooks.
- Given the server has run, when `OneNoteMcp uninstall --purge` runs, then the snapshot diff shows only OneNoteMcp files removed.

## Implementation Notes

- **Paths.** `UninstallPaths.Resolve` takes the settings path from `SettingsStore.ResolvePath` and the log folder from `FileLog.ResolveDirectory`, so `ONENOTE_MCP_SETTINGS_PATH` and `ONENOTE_MCP_LOG_DIR` apply. The default journal folder (`JournalSettings.Default()`) is passed in, so tests never reach the real `%LOCALAPPDATA%`. A settings file with no `journal.path` reads as the store's default, which the command maps back to the injected default. A `NewerSchema` file is treated like an invalid one: the default journal folder, and a note saying so.
- **File matching** is by regex on the file name, top level only, so Win32 wildcard quirks can't widen it: `.settings.*.tmp`, `onenote-mcp-*.log` (rolled `_001` files included), and `{8 digits}T{9 digits}-{pid}-{guid}.json.gz` with the GUID in `N` or `D` form.
- **Folders.** After the files go, the settings, log and journal folders are removed if empty, and so is the parent of each when it is named `OneNoteMcp` and empty (the default `%LOCALAPPDATA%\OneNoteMcp`, which would otherwise be left behind empty). A root-like folder is never removed. A non-empty folder is reported as kept.
- **Root-like** (`UninstallCommand.RootLikeReason`): a drive or share root; an exact match of the profile, `%APPDATA%`, `%LOCALAPPDATA%`, Desktop, Documents, Windows, Program Files or Program Files (x86); or fewer than two folders below the root (`C:\x`, `\\server\share\x`). The check is in the command; `SettingsStore` validation is unchanged.
- **Confirmation.** Purge lists the files and folders first, then asks for `purge` (case-sensitive, trimmed). `--yes` skips the question, interactive or not. Without a terminal and without `--yes` it removes nothing and exits 2 (the command line is incomplete). Declining exits 1. Nothing to remove exits 0 without asking (1 if the journal was refused).
- **README uninstall order.** A running server holds its log open, and starting Claude Code starts the server and a new log. So the README says: quit the clients, purge from a terminal by the exe's full path, then `claude plugin uninstall onenote@sentienceadjacent` from the same terminal. The agent checklist installs with `claude plugin marketplace add` and `claude plugin install` (an agent can't type `/plugin`), adds the manual route with a SHA-256 check and `claude mcp add`, and the source route via `dotnet publish ... -o publish` (the README's source command gained `-o publish`). Its step 6 covers uninstall, including that purge run from inside a session reports that session's log as in use.
- **Add-in access option.** One shared text, `ToolErrors.AddInAccessOptionNote`, is appended to the `SectionLocked` userAction and to the `sectionLocked` warning's userAction (`GetHierarchyUseCase`, the only warning site). The README has a new paragraph after the grant paragraph. The guide skill's two `SectionLocked`/`sectionLocked` rows now tell Claude to pass on the userAction when the user says the section is already unlocked, and never to change the option itself; this skill edit goes slightly past the task's list, for consistency.
- **Version.** `Directory.Build.props` bumped to `0.7.0-rc.2`: rc.1 was loaded in Phill's live 1.20 checks, and the release workflow refuses a tag that differs from `<Version>`.
- **Clean-install script.** `-Phase Before` and `-Phase After` around the purge; prints counts, PASS/FAIL and a Mark-of-the-Web yes/no only. "Left behind" and "removed but not owned" count only purge's exact top-level targets at the default locations (settings file and `.settings.*.tmp` beside it, `logs\onenote-mcp-*.log`, journal-named files in `journal\`); same-named files elsewhere count as foreign. `-AppDataRoot`, `-LocalAppDataRoot` and `-PluginRoot` exist for rehearsal on a temp tree.

## Plan Change Log

- 2026-11-08, before review (Phill folded it in; frozen Approach amended by him):
  - **Added:** name OneNote's add-in access option in the `SectionLocked` and `sectionLocked` texts and the README. Source: `deferred-work.md`, the 2026-11-08 entry.
  - **KEEP:** everything else.

## Review Triage Log

Pass 1 (quick lens): 7 findings: 1 rejected, 1 fixed by me, 5 sent to the implementer as patches.
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | AC 1–3 not met | — | rejected | They need the release and Phill's VM run (hitl), after the PR. Fixing it would mean editing this plan. |
| 2 | Mark-of-the-Web check missing from the clean-run checklist | medium | patch | Moved here by `story-release-pipeline-plan.md:91`. Add it to the script and to Manual checks. |
| 3 | The check script fails a correct purge: it matches file names recursively | medium | patch | Purge deletes only at the top level of the folders it targets. Check exactly those locations. |
| 4 | The README's refused-folder list says "AppData" | low | patch | Only `%APPDATA%` and `%LOCALAPPDATA%` are refused. Name them. |
| 5 | `claude mcp add` without a scope registers for one project only | medium | patch | Use `--scope user` for both add and remove. |
| 6 | The deferred-work entry for the add-in option is left open | low | patch (mine) | Appended a "resolved" entry. |
| 7 | No test for the NewerSchema purge path | low | patch | Add one. |

## Verification

- 2026-11-08 (build session): `dotnet build OneNoteMcp.slnx -warnaserror` passes with 0 warnings. `dotnet test tests/OneNoteMcp.Tests`: 1207 passed. `pack-plugin.ps1` packed `0.7.0-rc.2`, and `claude plugin validate --strict dist/stage/onenote` passes. The README purge test fails on the old README and passes on the new one.
- Rehearsal with the Debug exe and the path overrides on a temp tree: `uninstall` alone exits 2; `--purge` without a terminal or `--yes` exits 2 and removes nothing; `--purge --yes` removed the 4 files and 4 folders and exited 0. `clean-install-check.ps1` gave PASS on that run; FAIL, as intended, when another app's folder vanished and the plugin exe changed; exit 2 for After without Before.
- The name grep on the diff (notebook, section and employer names from the live hierarchy) is clean.
- Not done yet: the interactive `purge` prompt in a real terminal (unit-tested only), and the clean-machine run (AC 1-3) with Phill after the release.

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`; `dotnet test tests/OneNoteMcp.Tests`: both pass.
- `pwsh -File scripts/pack-plugin.ps1`; `claude plugin validate --strict dist/stage/onenote`: both pass.

**Manual checks:**
- The clean-machine run (AC 1–3) with Phill. The snapshot diff contains only OneNoteMcp paths.
- Mark-of-the-Web on Claude Code's marketplace download (moved here from `story-release-pipeline-plan.md:91`): after the plugin route installs, `clean-install-check.ps1` reports yes/no for a `Zone.Identifier` stream on the plugin's installed `OneNoteMcp.exe`. Record the answer.
- The name grep on the diff finds nothing.
