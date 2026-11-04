---
title: 'Plugin packaging and exe delivery'
type: 'feature'
ticket: '10'
created: '2026-11-04'
status: done
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
baseline_revision: 'e3dec472f80134c064fc34acce2a43660a84920a'
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-plugin-exe-delivery.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The server exists only as a local Debug build registered by hand. There is no plugin, no marketplace and no packaged artifact, so nothing can be installed the way AD-15 and AD-19 describe.

**Approach:** Make the repo a marketplace whose single plugin entry is an `archive` source (research note, Route A). Add a `plugin/` source folder, rename the exe to `OneNoteMcp.exe`, and add a pack script that publishes a self-contained exe, stages it with `plugin/` and zips it. CI validates the repo root and the staged plugin. The local loop is `claude --plugin-dir <zip>`.

## Boundaries & Constraints

**Always:**
- The plugin root in the zip holds `.claude-plugin/plugin.json`, `.mcp.json` (`command: "${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe"`) and the exe. There is no `bin/` folder anywhere in the plugin (AD-15).
- `version` is written into the staged `plugin.json` from `<Version>` at pack time. The checked-in `plugin/.claude-plugin/plugin.json` has no `version`, and the marketplace entry never carries one (research note, Release flow 2).
- The zip name is `onenote-<ver>-win-x64.zip`. The plugin and marketplace are both named `onenote-mcp`, and the MCP server key is `onenote`.
- Both validations run `--strict` and must exit 0.
- The rename is limited to `AssemblyName`. Namespaces, project names and `ServerSetup.ServerName` don't change.

**Never:**
- No GitHub release, tag or real `sha256` in this story; story 1.11 owns them.
- No skill content (story 1.12) and no README Install rewrite (story 1.13). The README changes only if a statement in it becomes false.
- No trimming or ReadyToRun: COM interop and the MCP SDK's reflection aren't trim-safe.
- No `LICENSE` file or `license` field: the licence is still undecided (before story 13).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Pack | clean checkout, `<Version>` 0.1.0 | `dist/onenote-0.1.0-win-x64.zip` with the plugin at its root and `version: "0.1.0"` in its `plugin.json` | No error expected |
| Pack, publish fails | build error | script exits non-zero and writes no zip | `$ErrorActionPreference = 'Stop'` plus a `$LASTEXITCODE` check after each native call |
| Stale staging | `dist/` left over from an earlier run | staging and the zip are recreated from scratch | delete before staging |
| Version drift | checked-in `plugin.json` gains a `version` | unit test fails | test names the file |

## Decisions

- Decision (2026-11-04, Phill): until story 1.11's first release, the marketplace entry is an archive entry for the v0.1.0 URL with an all-zero `sha256` placeholder. Story 1.11 replaces it, and an install before then fails on the hash mismatch.
- Decision (2026-11-04, Phill): the full plan is kept at about 2,000 tokens, with no split.
- Decision (2026-11-04, Phill): the repo stays private for this story. It goes public no earlier than story 1.13.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Server/OneNoteMcp.Server.csproj` -- add `<AssemblyName>OneNoteMcp</AssemblyName>` and pin `<RootNamespace>OneNoteMcp.Server</RootNamespace>`; publish settings (`RuntimeIdentifier` only on publish, `SelfContained`, `PublishSingleFile`, `EnableCompressionInSingleFile`, `IncludeNativeLibrariesForSelfExtract`).
- `src/OneNoteMcp.Server/ServerSetup.cs` -- reads `<Version>` from the assembly's informational version; it doesn't depend on the assembly name. Don't change it.
- `Directory.Build.props` -- `<Version>0.1.0</Version>`, the single version source; the pack script reads it (`dotnet msbuild -getProperty:Version`).
- `.github/workflows/ci.yml` -- windows-latest, setup-dotnet; add Node, `npm i -g @anthropic-ai/claude-code@<pinned>`, the pack step and two `claude plugin validate --strict` steps.
- `tests/OneNoteMcp.Tests/RepoPaths.cs` -- repo-root helper, reused by the new plugin-file test.
- `CLAUDE.md` line 16 -- the run path changes to `.../OneNoteMcp.exe`.
- `README.md` lines 52–79 -- already say `OneNoteMcp.exe`. Leave them.
- Local MCP registration: `onenote`, local scope, currently pointing at `...\bin\Debug\net10.0-windows\OneNoteMcp.Server.exe`.
- Trial run (scratchpad): with Claude Code 2.1.318, an archive entry with a zero `sha256` and the minimal plugin above both pass `claude plugin validate` with exit 0. The directory-profile notes (README, LICENSE, icon) don't affect the result.
- Caution: this session's `onenote` server runs from `src/OneNoteMcp.Server/bin/Debug`, which locks its DLLs. Build and test with `-c Release`, and publish to `dist/`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Server/OneNoteMcp.Server.csproj` -- AssemblyName, RootNamespace and publish properties -- one self-contained `OneNoteMcp.exe`.
- [ ] `plugin/.claude-plugin/plugin.json` -- name, displayName "OneNote MCP", description, author, homepage/repository; no version.
- [ ] `plugin/.mcp.json` -- the `onenote` stdio server.
- [ ] `.claude-plugin/marketplace.json` -- name `onenote-mcp`, owner, description, one archive entry (v0.1.0 URL, zero `sha256`; see Decisions).
- [ ] `scripts/pack-plugin.ps1` -- read the Version, publish win-x64 to `dist/publish`, stage `plugin/` and the exe in `dist/stage/onenote-mcp`, inject the version, zip to `dist/onenote-<ver>-win-x64.zip`, print the zip path and SHA-256. Story 1.11 reuses it.
- [ ] `.gitignore` -- `dist/`.
- [ ] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- `plugin.json` has no `version`, and its `name` equals the marketplace entry's; `.mcp.json`'s command ends with `/OneNoteMcp.exe`; the entry is an archive whose URL matches `releases/download/v{ver}/onenote-{ver}-win-x64.zip` for the props `<Version>`; there is no `plugin/bin`.
- [ ] `.github/workflows/ci.yml` -- pack and validate both, after the tests.
- [ ] `CLAUDE.md` -- the run path, plus a pack-and-local-loop line.

**Acceptance Criteria:**
- Given the repo, when `dotnet build -c Release -warnaserror` and the unit tests run, then both pass.
- Given the pack script has run, when `claude plugin validate --strict .` and `claude plugin validate --strict dist/stage/onenote-mcp` run, then both exit 0.
- Given the zip, when `claude -p --plugin-dir dist/onenote-0.1.0-win-x64.zip` asks for `get_hierarchy` on notebooks, then the plugin's `onenote` server answers with notebooks XML (read-only).
- Given the branch pushed, when CI runs, then the validate steps are green.
- Given the change, when Phill checks, then CLAUDE.md and the local `onenote` registration point at `OneNoteMcp.exe`.

## Implementation Notes

- Implemented by a subagent from this plan; files as in Tasks. Exe rename via AssemblyName only; publish props apply when a RuntimeIdentifier is set (publish defaults it to win-x64).
- Evidence: Release build 0 warnings; 446 unit tests pass; pack writes a 33.6 MB zip with `.claude-plugin/plugin.json` (version 0.1.0), `.mcp.json` and `OneNoteMcp.exe` at its root; both `--strict` validates exit 0; headless `claude -p --plugin-dir dist/onenote-0.1.0-win-x64.zip` called `mcp__plugin_onenote-mcp_onenote__get_hierarchy` (notebooks scope) and got 4 notebooks.
- Matrix rows 1-3 are script behaviour, covered by CI's pack step and by manual runs: a second pack recreated staging cleanly; a deliberate compile error made the script exit 1 with no zip. Row 4 is `PluginPackagingTests`.
- Surprise: the zip is not byte-reproducible (two packs of the same source hashed differently), so story 1.11 must hash the exact uploaded zip.
- Pending: CI (needs a push) and the local `onenote` re-registration (needs Phill's OK). After the rename, Debug builds write `OneNoteMcp.exe` and the old `OneNoteMcp.Server.exe` stays in `bin/Debug`, so the registration keeps starting the stale build until it's repointed.

## Plan Change Log

## Review Triage Log

Pass 1 (quick): high 0, medium 1, low 1, false 3, maybe-false 0. Both patches applied; build, 446 tests, pack and both validates re-run green.

| Finding | Verdict | Route | Evidence |
|---|---|---|---|
| `Invoke-Native` hides `dotnet publish` errors on failure | medium | patch | `$output = & $call` captures stdout, and the throw comes before `Out-Host`; MSBuild writes its errors to stdout, so a CI failure shows only the exit code. |
| Script needs PowerShell 7 but doesn't declare it | low | patch | `-AsHashtable` and `utf8NoBOM` don't exist in 5.1, and the failure comes after the publish. The fix is one `#Requires` line. |
| AC5: local registration not yet repointed | false | -- | The plan schedules it after Phill's OK (Verification, Manual checks). The stale-exe point is recorded in Implementation Notes. |
| AC3/AC4 have no evidence | false | -- | AC3 was run (see Implementation Notes). AC4 needs a push, which step 3 forbids. |
| The 1.3 plan's change to `done` is out of scope | false | -- | Phill said "1.3 done" and `tickets.py mark` wrote it. He agreed to carry it on this branch. |

## Design Notes

- Single-file with compression gives one exe for the zip and for "other MCP clients", without the hundreds of loose DLLs. It's self-contained, so no .NET runtime is needed on the target. COM `[ComImport]` interfaces work in single-file.
- Validate runs twice because a marketplace validate of an archive entry checks only the entry's fields (research note, Q2).
- Pin the CI's Claude Code version so a validator change can't break a green build unannounced; bump it deliberately.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests -c Release` -- expected: all pass.
- `pwsh -File scripts/pack-plugin.ps1` -- expected: zip path and SHA-256 printed.
- `claude plugin validate --strict .` and `claude plugin validate --strict dist/stage/onenote-mcp` -- expected: exit 0.

**Manual checks:**
- Headless `claude -p --plugin-dir <zip>` run that calls the plugin's `get_hierarchy` (notebooks scope only).
- After Phill's OK, re-register the local `onenote` server at the new Debug path, and check `claude mcp get onenote` shows it Connected.
