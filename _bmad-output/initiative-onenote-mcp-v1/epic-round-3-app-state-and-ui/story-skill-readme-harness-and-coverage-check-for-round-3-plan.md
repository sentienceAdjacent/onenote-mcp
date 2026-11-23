---
title: 'Skill, README, harness and coverage check for round 3'
type: 'feature'
ticket: '10'
created: '2026-11-23'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-skill-readme-and-harness-for-round-2-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Stories 4.3–4.9 each added their own tool-map row, warning rows, README row and harness constant, so `PluginPackagingTests` already passes for every round-3 tool. What no story owned is still missing or wrong: the skill has no recipes for the app-state tools (`SKILL.md` Recipes, l.154–205, has none), and its frontmatter description is 1,129 characters, over the 1,024 the 2.12 convention set (measured on main `eb3a5d4`). The README has no section on opening, closing and syncing notebooks or reopening a cloud one, and still says "no tool closes a notebook" (l.209, epic 3 Q13), "Only `navigate_to` does" (Safety, l.81) and "close it in OneNote" (l.95). `navigate_to`'s description, the skill (l.296) and the README (l.30, l.91) still say it takes keyboard focus and shows nothing without a window, which spine AD-11 says descriptions drop (deferred-work l.653). The harness's startup message and comments name only `navigate_to` and `sync_hierarchy` (`Program.cs` l.96–106). Nothing checks the spine's Coverage of `IApplication` table against the tools (initiative Done when 4).

**Approach:** Documentation, a description fix, a harness tidy and one coverage test, on main after 4.7 (PR #131) and 4.9 (PR #132) merge, in 3.10's form. Then one batched live sitting with Phill runs every user-visible tool through the packed plugin, an agent following only the README closes and reopens throwaway b, and MCP Test Throwaway Cloud closes through `close_notebook` and reopens in OneNote.

## Boundaries & Constraints

**Always:**
- **Starts after both 4.7 and 4.9 are merged to main**; the branch is cut (or rebased) from that main. Whichever of them merges second resolves the tool list, snapshot, skill intro ("four user-visible tools" in each) and `Calls.cs`; this story checks that resolution and doesn't redo it.
- 2.12/3.10 conventions: `SKILL.md` keeps the safety rules, tool map, errors and warnings and says which reference to read before which call; recipes point to tool descriptions instead of copying them; full prefixed tool names; placeholders only ("notebook A", "MCP Test"). The frontmatter description at most 1,024 characters, rewritten (not appended to) to add the app-state triggers ("sync it now", "open this OneNote link", "let me pick the section", "close that notebook").
- Recipes (each: when it may be called, the call, what to tell the user, what can go wrong): **which page am I on** (`get_windows`, `current` window; then `get_page_content` only if asked); **show it in OneNote** (`navigate_to`, `window` from `get_windows` when the user names a window); **follow this link** (`navigate_to_url`; local links → `navigate_to` with the ID; the three landing warnings); **let me pick a section** (`quick_filing`, then the write the user asked for with the chosen ID; `pickerCancelled`, `pickerLeftOpen`, `ComBusy`); **sync now** (`sync_hierarchy`, requested not done, View Sync Status); **close and reopen a notebook** (`close_notebook` → `meta.path` → `open_notebook`; IDs change, grants kept; a cloud notebook the user reopens in OneNote; if it comes back with sections "not available yet", the user closes and reopens it in OneNote: spine Deferred, 4.4 sitting). Every one runs only when the user asks (AD-11), `get_windows` also when they say "this page" (Q5).
- Skill "User-visible actions and limits" gains a `quick_filing` bullet; every bullet and README line describes focus per AD-11 ("may bring OneNote forward"), never "takes keyboard focus".
- README: a new **Opening, closing and syncing notebooks** section (close: sync first, ~10 s, never forced, IDs change, grants kept; reopen: local by `open_notebook` or File > Open > Browse, cloud in OneNote under its account, list may lag a minute; sync: requested, not done); l.209 points to `close_notebook` (Q13); the Safety "Focus" row lists the five user-visible tools; l.95 and the status line (l.5) mention app state; "How it works" gets one bullet on the AppState pipeline (no grant; the write lock for open and close; 120 s deadline; never retried).
- Coverage per Decision OQ1. "Released" = in the contract snapshot on main; the test re-checks at Phill's release tag.
- Contract version stays `0.10`; a description change updates `tool-contract.json` (AD-14).

**Never:** new tools, arguments, warnings or error codes; behaviour changes; editing `tickets.toml`, `CLAUDE.md` or settings; a `<Version>` bump or release; touching OneNote or setting `ONENOTE_MCP_INTEGRATION` in the build session; closing MCP Test or MCP Test Cloud; real notebook names in any file.

## I/O & Edge-Case Matrix

What an agent does when it follows the skill: the sitting's script.

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Which page | "which page am I on?" | `get_windows`; names the current page; nothing moves | protected, no grant: says the section only |
| Show | "show page P in the other window" | `get_windows`, then `navigate_to` with that `window` | unknown handle: relays `InvalidArgument` |
| Follow link | a cloud `onenote:` link | `navigate_to_url`; reports `landed` | local link: refused, offers `navigate_to` |
| Pick | "let me pick the section" | `quick_filing`; uses the chosen ID | Cancel: `pickerCancelled`, doesn't reopen |
| Sync | "sync notebook A now" | `sync_hierarchy`; says requested, not done | — |
| Close and reopen local | "close b, then open it again" | `close_notebook`, `open_notebook` with `meta.path`; new ID | `alreadyExisted`: reports the current ID |
| Close cloud | "close Cloud" | `close_notebook`; relays the reopen-in-OneNote `userAction` | "open it by URL": refuses, says why |
| Unasked | a write task, nothing about windows | no app-state call | — |

</frozen-after-approval>

## Code Map

- `plugin/skills/guide/SKILL.md` -- l.3 description (1,129 chars); l.8 intro (count after the two merges: five user-visible tools); l.16–33 Writing, l.20–22 the "read before" pointers; l.35–77 tool map (rows exist; touch only `navigate_to`'s focus wording); l.154 Recipes; l.293–301 User-visible actions and limits (l.296 focus wording; add `quick_filing`).
- `plugin/skills/guide/references/` -- `page-writes.md`, `hierarchy-writes.md`, `exports-and-packages.md`: the pattern for a new `app-state.md` (Decision OQ2).
- Facts, cite don't copy: `src/OneNoteMcp.Server/Tools/{GetWindows,NavigateTo,NavigateToUrl,QuickFiling,SyncHierarchy,CloseNotebook,OpenNotebook}Tool.cs`; `CloseNotebookUseCase.cs` l.51–57 (`ReopenLocalUserAction`, `ReopenCloudUserAction`); `ToolWarnings.cs`; spine AD-7 `AppState`, AD-11, Deferred (cloud reopen stuck, 4.4).
- `README.md` -- l.5, l.26–38 tables, l.30, l.81, l.91, l.95, l.203–210 (l.209), l.353–363 How it works.
- `src/OneNoteMcp.Server/Tools/NavigateToTool.cs` -- description, first and fourth sentences (Decision OQ3); `tests/OneNoteMcp.Tests/Contract/tool-contract.json` its description.
- `spikes/stress-harness/Program.cs` l.96–106 (comment, refusal message: "but the AppState tools and the write tools"); `README.md` exit code 3 and the never-calls list (add `quick_filing` if the merge didn't); `FakeServer.cs` l.54 `contract-ok`.
- Coverage: spine `## Coverage of \`IApplication\`` (l.874–913, 29 rows); `InteropLayoutTests.cs` l.11–23 (`ExpectedSlots`, `DeclaredMethods()` by metadata token = slot order); `PluginPackagingTests.cs` l.248 `ContractToolNames()`, `RepoPaths`. Pre-check (2026-11-23, main + both branches' snapshots, 35 tools): every tool the table names is in a contract; only `list_grants` and `read_result` are in no row (neither calls COM).
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` l.653 -- close the `navigate_to` description entry.

## Tasks & Acceptance

**Execution:**
- [ ] `plugin/skills/guide/references/app-state.md` (new) and `SKILL.md` -- the six recipes; a "read before" pointer; description ≤1,024; intro; User-visible bullets.
- [ ] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- `GuideSkill_DescriptionFitsTheLimit` (≤1,024 chars), red first.
- [ ] `README.md` -- per Boundaries.
- [ ] `NavigateToTool.cs`, `tool-contract.json` -- description per Decision OQ3; `NavigateToToolTests` if it asserts the text.
- [ ] `spikes/stress-harness/{Program.cs,README.md}` -- wording and counts.
- [ ] `tests/OneNoteMcp.Tests/CoverageTableTests.cs` (new, Decision OQ1) -- red against a table with a row removed, then green.
- [ ] Spine: the sentence "Story 4.10 checks this table" names the test; memlog line. Own commit.
- [ ] `deferred-work.md` -- close l.653.

**Acceptance Criteria:**
- Given the branch, when `dotnet test tests/OneNoteMcp.Tests` runs, then every `IApplication` slot 7–35 has a table row with a contract tool or a dated "Not exposed" Decision, every tool a row names is in the contract, and every contract tool except `list_grants` and `read_result` is named in some row.
- Given the harness built from the branch, when it runs `--contract-only` against a `contract-ok` fake server, then it exits 0 with "contract ok: 35 tools".
- Given `SKILL.md`, its references and `README.md`, when searched for "keyboard focus", "no tool closes", "Only `navigate_to`", then nothing matches.

## Design Notes

- Considered a hand-kept coverage map inside the test; going with parsing the spine table, so there is one source and a drifting table fails CI.
- Considered putting the recipes inline in `SKILL.md` (72 KB already); going with a reference file, as 2.12 and 3.10 did.

## Live sitting (Phill, about 35 minutes; hitl)

Before: 4.7 and 4.9 merged, CI green. OneNote open on an MCP Test page, VS Code in front; MCP Test Throwaway b and MCP Test Throwaway Cloud open. The build session packs the plugin, stops any old `OneNoteMcp.exe`, and Phill starts `claude --plugin-dir dist/onenote-0.10.0-rc.1-win-x64.zip` (the installed `onenote` plugin disabled for that session if the names clash). Heads-up in the Inbox and the terminal before any dialog.

1. The harness runs read-only on MCP Test: 0 mismatches.
2. "Which page am I looking at?": the page is named; nothing moves.
3. "Show page P in OneNote", then Ctrl+M and "show page Q in the second window": each lands where the description says.
4. A link from `get_hyperlink_to_object` to an MCP Test Cloud page: "open this link" lands (`landed`). A local MCP Test link is refused and Claude offers `navigate_to`.
5. "Let me pick a section": he picks MCP Test > Probes; then again and Cancel.
6. "Sync MCP Test Cloud now": requested; he checks View Sync Status.
7. A fresh subagent given only `README.md` and the tools closes and reopens Throwaway b: new ID, its test grant active in `list_grants`, OneNote's view unchanged.
8. He notes every open notebook's Sync Status. "Close MCP Test Throwaway Cloud": `notebookClosed` with the reopen-in-OneNote advice. He reopens it in OneNote by the README's steps, presses Sync Now (reports `0xE0000644` if seen), and checks the other notebooks' Sync Status again. `get_hierarchy` lists it with a new ID.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.
- The harness `--contract-only` against `contract-ok` -- exit 0.

**Manual checks:**
- The live sitting above.

## Open Questions

**OQ1. How is the coverage table checked?**
- (a) A unit test parses the spine's table, the `IApplication` slots (by reflection, as `InteropLayoutTests`) and `tool-contract.json`, and fails on any gap; it keeps holding at the release tag and after. Couples CI to one spine section.
- (b) A one-off check by hand, recorded as a dated line under the table and in the memlog. No new test; nothing catches later drift.

Recommended: (a)

**OQ2. Where do the app-state recipes go?**
- (a) A new `references/app-state.md`, with a "read before an app-state call" pointer in `SKILL.md`, as 3.10 did for exports.
- (b) Inline in `SKILL.md`'s Recipes, always loaded; the file grows past 72 KB.

Recommended: (a)

**OQ3. `navigate_to`'s description (deferred-work l.653)?**
- (a) Fixed here: drop "takes keyboard focus" and the windowless sentence per AD-11, with the skill and README; the snapshot's description changes.
- (b) Left to the refactor sweep (4.11); the docs here keep matching today's description.

Recommended: (a)

**OQ4. Keep the plan whole?** It is about 3,300 tokens, over bmad-build's 1,600 target, for one goal.
- (a) Keep it whole.
- (b) Split the coverage test into its own story before 4.11.

Recommended: (a)

## Implementation Notes

## Plan Change Log

## Review Triage Log
