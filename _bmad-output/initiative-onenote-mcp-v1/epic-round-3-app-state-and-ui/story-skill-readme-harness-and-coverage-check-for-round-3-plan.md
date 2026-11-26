---
title: 'Skill, README, harness and coverage check for round 3'
type: 'feature'
ticket: '10'
created: '2026-11-23'
status: 'done'
baseline_revision: '35bccae581ec848ebc377684579afa632b72201d'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
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

## Decisions (Phill, 2026-11-23, Inbox plan approval)

- **OQ1 (a):** a unit test parses the spine's coverage table, the `IApplication` slots (by reflection, as `InteropLayoutTests`) and `tool-contract.json`, and fails on any gap; it keeps holding at the release tag and after.
- **OQ2 (a):** the recipes go in a new `references/app-state.md`, with a "read before an app-state call" pointer in `SKILL.md`, as 3.10 did for exports.
- **OQ3 (a):** `navigate_to`'s description is fixed here (drop "takes keyboard focus" and the windowless sentence per AD-11), with the skill and README; the snapshot's description changes. 4.11 no longer carries it.
- **OQ4 (a):** the plan is kept whole.

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
- [x] `plugin/skills/guide/references/app-state.md` (new) and `SKILL.md` -- the six recipes; a "read before" pointer; description ≤1,024; intro; User-visible bullets.
- [x] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- `GuideSkill_DescriptionFitsTheLimit` (≤1,024 chars), red first.
- [x] `README.md` -- per Boundaries.
- [x] `NavigateToTool.cs`, `tool-contract.json` -- description per Decision OQ3; `NavigateToToolTests` if it asserts the text.
- [x] `spikes/stress-harness/{Program.cs,README.md}` -- wording and counts.
- [x] `tests/OneNoteMcp.Tests/CoverageTableTests.cs` (new, Decision OQ1) -- red against a table with a row removed, then green.
- [x] Spine: the sentence "Story 4.10 checks this table" names the test; memlog line. Own commit.
- [x] `deferred-work.md` -- close l.653.

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

**Sitting record (Phill, 2026-11-26, plugin loaded from the packed zip with the installed plugin disabled):**

- Step 1 passed: harness read-only on MCP Test, 844 comparisons, 0 mismatches, 39 large results reassembled, none over 100,000 chars.
- Step 2 passed: "which page am I looking at" was answered from `get_windows` and reads only; nothing was shown.
- Step 3 passed: Text Page landed in the first window, Table Page in the second (checked with `get_windows`). The agent's reply wrongly said a window had been closed; the tool did the right thing.
- Step 4 passed: `navigate_to_url` on the MCP Test Cloud link reported `landed` on Image Page. For the local MCP Test link the agent, from the skill, didn't call `navigate_to_url`; it found the page by name and called `navigate_to` directly instead of offering first (the user had asked to open it). The refusal itself is unit-tested. Side note: Phill's own click on the cloud link opened Text Page, not Image Page, in a browser-style OneNote; OneNote desktop's windows didn't change.
- Step 5 passed: the pick returned MCP Test > Probes; the second picker, cancelled, returned `pickerCancelled`.
- Step 6 passed: `syncRequested`; View Sync Status showed MCP Test Cloud synced at the current time.
- Step 7 passed: a fresh subagent given only README.md closed and reopened Throwaway b with no errors. The ID changed, the `writeNotebook` grant stayed active and unchanged, and the subagent cited the README's tool table, "Opening, closing and syncing notebooks" and "Write grants". Phill saw nothing odd on screen during it.
- The new "Missing images" step 3 wording (no "without an open window" claim): Phill kept it, 2026-11-26. New measurement: the reopen reset the notebook's colour (`#4DBCCA` to `none`); see the Plan Change Log.
- Step 8 passed: `close_notebook` on MCP Test Throwaway Cloud gave `notebookClosed` with the reopen-in-OneNote advice; Phill reopened it in OneNote and pressed Sync Now: no `0xE0000644`, and every notebook's sync status was the same as before. It came back with a new ID and kept its colour (`#4DBCCA`).
- Before step 7 Phill closed the OneNote app by hand (a misread of the step); OneNote ran its clean-up on restart and the test session's next calls worked with no error.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- no warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `pwsh -File scripts/pack-plugin.ps1` then `claude plugin validate --strict dist/stage/onenote` -- passes.
- The harness `--contract-only` against `contract-ok` -- exit 0.

**Manual checks:**
- The live sitting above.

## Implementation Notes

- 2026-11-25, built in worktree s410 on `35bccae` (4.7 and 4.9 merged). Their merge resolution checked: 35 contract tools, the skill intro's five user-visible tools, the harness's `Calls.cs` lists `quick_filing`.
  - Test-first: `GuideSkill_DescriptionFitsTheLimit` went red at 1,129 characters, green at 1,019 after the rewrite (app-state triggers added; "show my To Do items", "sort my sections", "permanently delete that section", "rename that section" and "open this .onepkg" dropped to fit). `CoverageTableTests` (new): the real table passes; its red cases are permanent tests (slot 27's row removed gives "slot 27" and "quick_filing" gaps; a renamed tool and an undated Decision each give a gap). Every backticked lowercase name in a Tool cell counts as a tool except the argument names `window` and `force` (a one-word name such as `export` replacing `publish` is a gap, tested); the declared getters drop `get_` to match the table (`Windows`, `Dummy1`).
  - `navigate_to`: description's first sentence now "changes what OneNote shows, and may bring OneNote forward"; the windowless sentence dropped (it was also missing its leading space). `NavigateToToolTests` checks the new sentence and that neither old claim returns. Premise correction: `tool-contract.json` holds no descriptions (`ToolContractTests` strips them), so the snapshot didn't change, and contract version stays `0.10`. The same focus claim fixed in the `binaryUnavailable` advice of `GetPageContentUseCase` and `GetBinaryPageContentUseCase`, and in comments (`NavigateToUseCase`, `IOneNoteGateway.NavigateTo`, `IApplication` slot 18). The README's Focus row also says a create (such as `create_notebook`) may bring OneNote forward (AD-11, spike 5.1 constraint 14).
  - Skill: new `references/app-state.md` (six recipes plus the shared rules); `SKILL.md` description, intro, When to use, the "read before" pointer, `navigate_to`'s row, Missing images (the windowless advice reworded), User-visible bullets (focus per AD-11, a `quick_filing` bullet); `exports-and-packages.md` now points to `close_notebook` for closing an opened copy.
  - README: status line, `navigate_to` row, Safety "Focus" row (five tools), Missing images, "What writes can't do" and `create_notebook`'s close advice, opened-package closing, the guide paragraph, a new "Opening, closing and syncing notebooks" section, and a How it works bullet on app-state calls.
  - Harness: `Program.cs` comment and refusal message; `README.md` exit 3 and never-calls lists gain `quick_filing`, counts 12 read-only and 23 not, and the probe's focus wording.
  - Spine: the Coverage sentence names `CoverageTableTests`; memlog line added. Not committed (the plan wants it in its own commit).
  - `deferred-work.md`: a Resolved entry for the `navigate_to` description item.
- Not run in the build session: the pack and `claude plugin validate`, and the live sitting. The I/O & Edge-Case Matrix rows are agent behaviour against real OneNote (the sitting's script); no unit test can exercise them, so the sitting covers them all.
- 2026-11-25, quick review: three findings, all patched (see the triage log). Final verification: build 0 warnings; `dotnet test tests/OneNoteMcp.Tests` 4201 passed, 3 skipped, 0 failed; harness `--contract-only` against `contract-ok` exited 0 with "contract ok: 35 tools".

## Plan Change Log

- 2026-11-26 (Phill, Inbox, during the sitting): measured at step 7 that reopening a local notebook reset its colour (`#4DBCCA` before the close, `none` after `open_notebook`). One clause was added to the README's Closing paragraph and to `close_notebook`'s `notebookClosed` warning: OneNote may reset its colour.
- 2026-11-25 (Phill, Inbox): put "open this .onepkg" back in the skill's description, since nothing else there names packages. To stay within 1,024 characters, three examples were shortened ("find my Trip plan page", "restore my deleted page", "reopen that notebook"). The description is now 1,023 characters.
## Review Triage Log

| # | Lens | Finding | Verdict | Route | Evidence |
|---|---|---|---|---|---|
| 1 | quick | `binaryUnavailable` advice in `GetPageContentUseCase`/`GetBinaryPageContentUseCase` (and the `IApplication` slot 18 comment) still said `navigate_to` takes focus | medium | patch | Strings are model-facing at run time; AD-11 and Decision OQ3 drop the claim. Reworded; no test asserted the old text. |
| 2 | quick | README Safety "Focus" row said only the five user-visible tools can bring OneNote forward | low | patch | Spine AD-11: an `OpenHierarchy` create took focus three times (spike 5.1 constraint 14). Row now says a create may too. |
| 3 | quick | `CoverageTableTests` ignored a one-word backticked tool name not in the contract | low | patch | Filter kept only underscored names or contract names, so `publish` → `export` drift passed. Now every backticked name except `window`/`force` counts; new test covers it. The aside-mention part (`close_notebook`'s sync on row 28) is not a defect: `close_notebook` does call `SyncHierarchy`. |
